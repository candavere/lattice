using System.Collections.Concurrent;
using System.Diagnostics;
using Lattice.Environment;
using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// An <see cref="IAgent"/> backed by a child process speaking the external-agent
/// protocol of spec §1-§9.
/// </summary>
/// <remarks>
/// <para>
/// <b>One process per match</b> (U-8, §3). The process is started in the
/// constructor, sent <c>hello</c> exactly once, required to answer with a valid
/// <c>hello_ack</c>, and terminated by <see cref="Dispose"/> — which kills it if
/// it has not exited. Nothing here is reusable across matches, seeds, pairings,
/// or the mirrored seatings of one seed, and there is deliberately no API for
/// reusing an instance: a fresh instance is the only way to obtain one, so
/// carryover of any accumulated agent state is impossible by construction rather
/// than by convention.
/// </para>
/// <para>
/// <b>The seam.</b> This plugs into the existing in-process seam, <see cref="IAgent"/>,
/// <b>unchanged</b>: <see cref="Decide"/> is synchronous, so it blocks on the
/// child's pipe, and returns the same <see cref="AgentAction"/> an in-process
/// agent returns. Because the episode is driven by the same
/// <see cref="ScenarioRunner"/> that drives in-process agents, a match played with
/// this agent goes through the identical step loop, contention accounting, and
/// terminal-tick handling, so the recorded trajectory is byte-identical to an
/// in-process one (§10.2). Failure has no action to return, so
/// <see cref="Decide"/> throws <see cref="ExternalAgentFaultException"/> and
/// <see cref="ExternalMatchRunner"/> turns the throw into a recorded result.
/// </para>
/// <para>
/// <b>Three pumps, three jobs.</b> stdout is read as lines by a dedicated reader
/// that enforces <c>max_line_bytes</c> as it reads
/// (<see cref="ProtocolFraming.ReadLine"/>), so an over-long line is refused
/// without ever being buffered whole; stdin is written through
/// <see cref="ProtocolWriter"/>; and stderr is drained continuously on its own
/// reader into a <see cref="StderrRing"/> for the whole life of the process. The
/// stderr drain is the one that must not be deferred: an undrained pipe is a
/// deadlock, so a chatty agent would otherwise be recorded as stalling when in
/// fact Lattice had stopped reading (§1.1).
/// </para>
/// <para>
/// <b>Monotonic only.</b> Every timeout is measured on a
/// <see cref="Stopwatch"/> started when <c>hello</c> is written. No wall clock is
/// read anywhere in this type, so a clock adjustment mid-match cannot
/// manufacture or suppress a timeout (§7).
/// </para>
/// <para>
/// <b>The budget is end-to-end, both directions.</b> The read side always had a
/// deadline; the write side originally did not, so a child that stopped reading
/// stdin blocked the host in a full pipe before any budget applied. Writes and
/// flushes now run under the same binding budget the read would get, measured
/// from the same clock, and a write that expires kills the child — which is
/// what unblocks the pipe — before reporting the timeout. The best-effort error
/// notification is bounded the same way and never restarts the clock. A
/// disposed agent is cancellation, not a fault: it raises
/// <see cref="OperationCanceledException"/> rather than inventing a §8 code.
/// </para>
/// </remarks>
public sealed class ExternalAgent : IAgent, IDisposable
{
    /// <summary>The <c>hello.scenario</c> value of the v3.0 <c>standard</c> family (§3.1).</summary>
    public const string StandardScenario = "standard";

    /// <summary>The <c>hello.scenario</c> value of the v3.0 <c>bottleneck</c> family (§3.1).</summary>
    public const string BottleneckScenario = "bottleneck";

    private const byte LineFeed = 0x0A;

    /// <summary>
    /// Wakeup granularity for a blocked read. The deadline itself always comes
    /// from the <see cref="Stopwatch"/>; this only bounds how long the thread
    /// sleeps before re-checking it, so a coarse timer can never shorten a
    /// timeout.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The stdout read buffer. Fixed and small on purpose: it exists to stop
    /// <see cref="ProtocolFraming.ReadLine"/>'s byte-at-a-time reads costing a
    /// syscall each, and it must not become a place a whole over-cap line can sit
    /// before the cap is applied.
    /// </summary>
    private const int StdoutBufferSize = 64 * 1024;

    /// <summary>
    /// How long to wait for the child to exit after its stdout closed, so a
    /// terminating child can be classified as <c>agent_exited</c> or
    /// <c>agent_crashed</c> from its exit code. Bounded, never an unbounded
    /// <c>WaitForExit()</c>: a child that closed stdout but never exits is a
    /// stall, and is reported as one.
    /// </summary>
    private static readonly TimeSpan ExitProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a child gets to exit on its own after stdin closes, before it is
    /// killed. A conforming agent sees the EOF immediately, so this only has to
    /// cover process teardown; it is deliberately short because it is paid by
    /// every match, including the ones whose child is hung and never will exit.
    /// </summary>
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the kill to be observed before giving up on a clean join.</summary>
    private static readonly TimeSpan KillConfirmTimeout = TimeSpan.FromSeconds(2);

    private readonly ExternalTimeLimits _limits;
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StderrRing _stderr = new();
    private readonly BlockingCollection<ReadOutcome> _inbound = new(boundedCapacity: 1);
    private readonly List<Task> _pumps = [];
    private readonly Stopwatch _clock = new();

    /// <summary>
    /// PIDs of descendants ever observed while the parent was alive. A worker
    /// that outlives its parent is reparented (PPID 1 on macOS) and can no
    /// longer be enumerated from the exited parent, so the only product-owned
    /// handle to it is a PID captured before the exit. Accumulated on every
    /// exchange while the parent is known alive, then killed explicitly by PID
    /// at teardown. Test-only pidfiles are never read here; this is the
    /// product mechanism.
    /// </summary>
    private readonly Dictionary<int, DateTime?> _knownDescendants = [];
    private readonly object _descendantsLock = new();

    /// <summary>
    /// Cancelled by <see cref="Dispose"/> so a wait that is bounded by a budget
    /// rather than a pipe can be woken early instead of served its whole budget.
    /// </summary>
    private readonly CancellationTokenSource _cancelled = new();

    private ExternalAgentFault? _pendingFault;
    private int _step;

    /// <summary>0 = live, 1 = disposed. Interlocked, because a mid-exchange
    /// cancellation runs the full <see cref="Dispose"/> from the writer's wait
    /// thread while the caller may be disposing concurrently: exactly one of
    /// them may run the teardown.</summary>
    private int _disposeState;

    /// <summary>
    /// How many writes are in flight. Closing stdin while a write is blocked
    /// inside it blocks the closer — the close waits for the in-flight write,
    /// and a blocked pipe write never ends on its own — so a close that would
    /// sit on top of one is skipped and the tree kill is what ends the write.
    /// </summary>
    private int _writesInFlight;

    /// <summary>The write in flight, so teardown can join it bounded after the kill.</summary>
    private volatile Task? _writeInFlight;

    /// <summary>
    /// Starts the agent process, sends <c>hello</c> exactly once, and requires a
    /// valid <c>hello_ack</c> — the whole of the §3 handshake sequence.
    /// </summary>
    /// <param name="launch">The §3.2 launch contract: a program and an argv.</param>
    /// <param name="agentId">The slot this process plays (§3.1 <c>agent_slot</c>).</param>
    /// <param name="runSeed">The run seed (§3.1 <c>seed</c>).</param>
    /// <param name="scenario">The scenario family (§3.1 <c>scenario</c>).</param>
    /// <param name="maxTicks">The match's tick budget (§3.1 <c>max_ticks</c>).</param>
    /// <param name="agentCount">The number of agents in the match (§3.1 <c>agent_count</c>).</param>
    /// <param name="limits">The two named time limits (§3.1 <c>limits</c>).</param>
    /// <exception cref="ExternalAgentLaunchException">
    /// The process could not be started at all. That is a host-side launch
    /// problem, not an agent fault, and the closed §8 reason set has no code for
    /// it — so it is reported as an exception rather than being forced into a code
    /// it does not belong to.
    /// </exception>
    /// <remarks>
    /// A handshake <b>failure</b> does not throw here: it is recorded and raised
    /// from the first <see cref="Decide"/>, so that every protocol failure reaches
    /// the caller through one path and one type. A caller that wants to know
    /// before playing a step can read <see cref="PendingFault"/>.
    /// </remarks>
    public ExternalAgent(
        ExternalAgentLaunch launch,
        int agentId,
        ulong runSeed,
        string scenario,
        int maxTicks,
        int agentCount,
        ExternalTimeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegative(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(agentCount, 2);

        AgentId = agentId;
        Scenario = scenario;
        _limits = limits;

        _process = StartProcess(launch, out _stdin, out _stdout);
        ProcessId = SafeProcessId(_process);

        // Both pumps start before the first byte is written, so stderr is drained
        // for the whole life of the process and stdout is already being read when
        // the agent's first line arrives.
        //
        // Dedicated threads, not thread-pool work items, and that is load-bearing
        // rather than incidental. Both pumps block for the whole life of the
        // match: stdout in a byte-at-a-time read, stderr waiting for the agent to
        // produce something. On the thread pool that is two permanently occupied
        // workers per live match, and the stderr drain -- a single sequential
        // reader that must keep up with whatever the agent writes -- ends up
        // queued behind them. Under a parallel test run that starves, the child
        // blocks on a full stderr pipe, and a perfectly correct agent is recorded
        // as having timed out. LongRunning gives each pump a thread of its own.
        _pumps.Add(StartPump(PumpStdout));
        _pumps.Add(StartPump(DrainStderr));

        Handshake(runSeed, maxTicks, agentCount);
        NoteDescendants();
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <summary>The <c>scenario</c> this process was told about in <c>hello</c>.</summary>
    public string Scenario { get; }

    /// <summary>The OS process id of the child, for asserting that it is really gone.</summary>
    public int ProcessId { get; }

    /// <summary>
    /// True once the child has been observed to have exited. Latched before the
    /// <see cref="Process"/> is disposed, so it stays readable afterwards — which
    /// is what makes "no process left running" assertable after
    /// <see cref="Dispose"/>.
    /// </summary>
    public bool ProcessExited { get; private set; }

    /// <summary>The child's exit code, or <see langword="null"/> if it had not exited.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// The failure detected during construction (a handshake failure), or
    /// <see langword="null"/>. It is re-raised from the first
    /// <see cref="Decide"/> rather than thrown here, so that every protocol
    /// failure has exactly one path to the caller.
    /// </summary>
    public ExternalAgentFault? PendingFault => _pendingFault;

    /// <summary>The stderr ring, for a test or a report to read the tail from.</summary>
    public StderrRing Diagnostics => _stderr;

    /// <summary>The last 64 KiB the child wrote to stderr (§1.1). Never parsed.</summary>
    public string StderrTail() => _stderr.Tail();

    /// <summary>
    /// Both agents' scores as they stood at the start of the most recent step
    /// Lattice drove, or <see langword="null"/> before the first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the partial state of a match that failed, and it comes from the
    /// observation itself rather than from a second copy of the step loop: a wire
    /// observation carries every agent's score (§5.3), and the last one this
    /// process was handed is the state the failing step started from. A match that
    /// died on step 12 therefore reports the scores as of step 12, which is the
    /// only number that means anything to somebody reading the row.
    /// </para>
    /// <para>
    /// It stays <see langword="null"/> for a handshake failure, correctly: no
    /// observation was ever written, so no step ever began and the match's scores
    /// really are 0-0 rather than unrecorded.
    /// </para>
    /// </remarks>
    public (int Slot0Score, int Slot1Score)? LastKnownScores { get; private set; }

    /// <summary>
    /// Writes one <c>observation</c> and reads back exactly one <c>action</c> —
    /// one exchange, in the direction Lattice drives (§1.2).
    /// </summary>
    /// <exception cref="ExternalAgentFaultException">
    /// The exchange failed, carrying exactly one §8 reason code and, where the
    /// spec requires it, the child's stderr tail. Thrown rather than returning a
    /// default action, because a substituted default would let an agent improve
    /// its score by crashing (§9.2).
    /// </exception>
    public AgentAction Decide(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);

        if (_pendingFault is not null)
        {
            throw new ExternalAgentFaultException(_pendingFault);
        }

        // The state this step starts from, kept so a failure here can report the
        // scores as they stood rather than as nothing. Taken before anything can
        // throw, so every failure below it has the number.
        LastKnownScores = (observation.AgentStates[0].Score, observation.AgentStates[1].Score);

        NoteDescendants();

        var step = _step;
        var message = ExternalWire.ToWire(step, observation);

        // §7's host-side gate, before a single byte of the observation is
        // written. A refusal here is a void run, never a loss for the agent.
        byte[] payload;
        try
        {
            payload = ProtocolWriter.EncodeChecked(message);
        }
        catch (ProtocolViolation violation) when (violation.Reason == ProtocolReason.HostLimit)
        {
            throw Fail(ProtocolReason.HostLimit, violation.Detail, step, sendError: false);
        }

        var outcome = Exchange(payload, step);

        // Framing, discriminator, unknown-member, schema, and step-echo codes all
        // come out of the parser, already in §8.4 precedence order.
        ActionMessage action;
        try
        {
            action = ProtocolParser.ParseAction(outcome, step);
        }
        catch (ProtocolViolation violation)
        {
            throw Fail(violation.Reason, violation.Detail, step);
        }

        // §6.3: legal shape for *this* map. The parser reports what the line said;
        // this reports what the map allows.
        try
        {
            ProtocolActionGuard.RequireWithinActionSpace(
                action,
                observation.Map.Zones.Length,
                observation.Map.Resources.Length);
        }
        catch (ProtocolViolation violation)
        {
            throw Fail(violation.Reason, violation.Detail, step);
        }

        var mapped = ExternalWire.ToAction(action);

        // §6.3 makes ActionSpace the authority at receipt. The guard above is the
        // wire-side pre-check; this is the gate, and the two are asserted to agree
        // over a seeded sample.
        var problems = ActionSpace.Validate(mapped, observation.Map);
        if (problems.Count > 0)
        {
            throw Fail(ProtocolReason.IllegalAction, string.Join(" ", problems), step);
        }

        _step = step + 1;
        return mapped;
    }

    /// <summary>
    /// Closes stdin, lets the child exit, and kills it if it has not — so a
    /// match can never leave a process behind (§3, and the kill-on-dispose
    /// guarantee the tests assert).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        // Wake any bounded wait first, so a write or read that is waiting out
        // its budget is released by the cancellation rather than served it.
        _cancelled.Cancel();

        NoteDescendants();

        if (Volatile.Read(ref _writesInFlight) > 0)
        {
            // A write is blocked in the pipe: stdin cannot be closed gracefully
            // until it ends, and the kill is what ends it. Kill first, then the
            // close is safe.
            KillAndJoinWrite(_writeInFlight);
        }
        else
        {
            TryCloseStdin();

            if (!_process.WaitForExit((int)GracefulExitTimeout.TotalMilliseconds))
            {
                KillEntireTree();
                _process.WaitForExit((int)KillConfirmTimeout.TotalMilliseconds);
            }
        }

        // Always attempt the whole-tree kill, even when the child exited on its
        // own within the grace: a descendant the child spawned — a worker that
        // inherited stdout or stderr — outlives its parent's exit. The tree kill
        // reaps what the platform can; a descendant already reparented past the
        // parent (PPID 1 on macOS) escapes it, so the PIDs accumulated while the
        // parent was alive are killed explicitly by PID below. The pumps' join
        // stays bounded regardless.
        KillEntireTree();
        KillKnownDescendants();

        LatchExit();

        // Releasing the pump lets a reader blocked handing over a line finish.
        // Bounded, and short: the join exists to let a mid-line handover
        // complete (sub-millisecond normally), not to wait out a pipe a
        // descendant is holding — waiting that out here was a per-match stall
        // paid by every match whose agent spawned a worker.
        _inbound.CompleteAdding();
        try
        {
            Task.WaitAll(_pumps.ToArray(), PumpJoinTimeout);
        }
        catch (AggregateException)
        {
            // A pump that faults while the pipe is being torn down is not a match
            // failure: the match has already been decided. Swallowed rather than
            // allowed to mask that decision.
        }

        _inbound.Dispose();
        _process.Dispose();
        _cancelled.Dispose();
    }

    /// <summary>How long the pump join waits before abandoning a pump to its pipe.</summary>
    private static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Kills the whole process tree, confirms exit, and joins the write in
    /// flight, bounded — the one sequence every "the write must be ended now"
    /// path shares. The kill comes first because it is what unblocks the
    /// writer: closing stdin on top of a blocked write blocks the closer
    /// instead, and a blocked pipe write never ends on its own.
    /// </summary>
    private void KillAndJoinWrite(Task? write)
    {
        KillEntireTree();
        _process.WaitForExit((int)KillConfirmTimeout.TotalMilliseconds);

        if (write is null)
        {
            return;
        }

        try
        {
            write.Wait(KillConfirmTimeout);
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
            // The write died with the pipe, as intended; the match is already
            // decided and whatever fault raised this path is its record.
        }
    }

    /// <summary>Kills the whole process tree, tolerating every already-gone race.</summary>
    private void KillEntireTree()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the wait and the kill; the request below
            // on an exited handle is still what reaches surviving descendants.
        }
        catch (NotSupportedException)
        {
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }

        KillKnownDescendants();
    }

    /// <summary>
    /// Records live descendants of the child while it is alive. Called after
    /// the handshake and before every exchange, so a worker spawned at any
    /// point is captured before a later exit reparents it.
    /// </summary>
    private void NoteDescendants()
    {
        if (Volatile.Read(ref _disposeState) != 0) return;
        int pid = ProcessId;
        if (pid <= 0) return;
        try
        {
            if (_process.HasExited) return;
        }
        catch (InvalidOperationException) { return; }
        IReadOnlyList<int> found;
        try { found = ProcessTree.CollectDescendants(pid); }
        catch { return; }
        if (found.Count == 0) return;
        lock (_descendantsLock)
        {
            foreach (var id in found) _knownDescendants[id] = ProcessTree.TryGetStartTime(id);
        }
    }

    /// <summary>
    /// Kills PIDs captured by <see cref="NoteDescendants"/> explicitly by PID,
    /// so a worker already reparented past its parent is still ended. Bounded:
    /// each kill is fire-and-forget with a short settle, never an unbounded wait.
    /// </summary>
    private void KillKnownDescendants()
    {
        (int pid, DateTime? start)[] targets;
        lock (_descendantsLock) { targets = _knownDescendants.Select(kv => (kv.Key, kv.Value)).ToArray(); }
        foreach (var (pid, start) in targets)
        {
            if (pid == ProcessId) continue;
            ProcessTree.KillById(pid, start);
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            bool anyAlive = false;
            foreach (var (pid, _) in targets)
            {
                if (pid == ProcessId) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited) { anyAlive = true; break; }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (!anyAlive) break;
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// The §7 outbound gate, usable <b>before</b> the child is started.
    /// </summary>
    /// <param name="observation">The observation the match would open with.</param>
    /// <param name="step">The 0-based wire step, which is 0 at the start of a match.</param>
    /// <returns>
    /// The fault to record, or <see langword="null"/> when the observation is
    /// within both limits. Callers that can reach the observation before spawning
    /// — <see cref="ExternalMatchRunner"/> does — use this so that a match refused
    /// on a host limit never starts a process at all, which is what §8.4's "never
    /// reaches the wire" and "no agent behaviour for it to outrank" ask for.
    /// </returns>
    public static ExternalAgentFault? CheckOutbound(int step, Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        try
        {
            _ = ProtocolWriter.EncodeChecked(ExternalWire.ToWire(step, observation));
            return null;
        }
        catch (ProtocolViolation violation) when (violation.Reason == ProtocolReason.HostLimit)
        {
            return new ExternalAgentFault(ProtocolReason.HostLimit, violation.Detail, step, string.Empty);
        }
    }

    private static Process StartProcess(
        ExternalAgentLaunch launch,
        out Stream stdin,
        out Stream stdout)
    {
        Process process;
        try
        {
            process = Process.Start(launch.CreateStartInfo())
                ?? throw new ExternalAgentLaunchException(
                    $"'{launch.Program}' did not start and reported no process.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ExternalAgentLaunchException(
                $"could not start external agent '{launch.Program}': {e.Message}",
                e);
        }

        stdin = process.StandardInput.BaseStream;

        // Buffered because ProtocolFraming.ReadLine reads one byte at a time, and
        // an unbuffered pipe turns that into one syscall per byte: a legal
        // near-cap line then takes seconds to cross the pipe and is reported as a
        // timeout it never was. The buffer is fixed and small, so the §7 line cap
        // is still enforced by ReadLine against a stream holding only bounded
        // lookahead — buffering changes the syscall count, not the framing rules.
        stdout = new BufferedStream(process.StandardOutput.BaseStream, StdoutBufferSize);

        // stdin is a byte stream written one whole line at a time; the framework's
        // char writer would re-encode and could emit a platform newline.
        process.StandardInput.AutoFlush = false;
        return process;
    }

    private void Handshake(ulong runSeed, int maxTicks, int agentCount)
    {
        var hello = new HelloMessage
        {
            Protocol = ProtocolLimits.Version,
            Scenario = Scenario,
            Seed = unchecked((long)runSeed),
            AgentSlot = AgentId,
            MaxTicks = maxTicks,
            AgentCount = agentCount,
            Limits = new MatchLimitsWire
            {
                StepTimeoutMs = _limits.StepTimeoutMs,
                MatchTimeoutMs = _limits.MatchTimeoutMs,
            },
        };

        byte[] payload;
        try
        {
            payload = ProtocolWriter.EncodeChecked(hello);
        }
        catch (ProtocolViolation violation)
        {
            _pendingFault = new ExternalAgentFault(
                violation.Reason,
                violation.Detail,
                Step: -1,
                StderrTail: StderrTail());
            return;
        }

        _clock.Restart();
        WriteLineBounded(
            payload,
            Budget(_limits.StepTimeout).Budget,
            ProtocolReason.TimeoutHandshake,
            step: -1,
            subject: "the hello was not accepted");

        // The handshake waits step_timeout_ms, on the same knob as a step (§3.1).
        var outcome = ReadWithin(_limits.StepTimeout, ProtocolReason.TimeoutHandshake, out var timeout);
        if (outcome is null)
        {
            _pendingFault = new ExternalAgentFault(
                timeout!.Value,
                DescribeTimeout(timeout.Value, "no hello_ack"),
                Step: -1,
                StderrTail: StderrTail());
            return;
        }

        if (outcome.Value.Kind != ReadKind.Line)
        {
            _pendingFault = FromNonLine(outcome.Value, "the handshake ended before a hello_ack arrived", step: -1);
            return;
        }

        try
        {
            // Parses the framing, then compares `protocol` before the body, so a
            // string, a float, another integer, and a missing field are all
            // protocol_mismatch rather than a generic schema error (§2).
            _ = ProtocolParser.ParseHelloAck(outcome.Value.Line!);
        }
        catch (ProtocolViolation violation)
        {
            _pendingFault = new ExternalAgentFault(violation.Reason, violation.Detail, -1, StderrTail());
        }
    }

    private void WriteLine(byte[] payload)
    {
        _stdin.Write(payload, 0, payload.Length);
        _stdin.WriteByte(LineFeed);
        _stdin.Flush();
    }

    /// <summary>
    /// Writes one LF-terminated line, bounded by <paramref name="budget"/> on the
    /// monotonic clock and cancellable by <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the write is bounded at all.</b> A child that stops reading stdin
    /// fills the pipe, and a synchronous write into a full pipe blocks for as
    /// long as the child stays silent — indefinitely. The read side has always
    /// had a deadline; the write side bypassed it, so the deadlock moved one
    /// stage earlier and no budget ever applied. The write is therefore given
    /// the same budget the read would have had, measured from the same
    /// monotonic clock, so the deadline is end-to-end rather than per-direction.
    /// </para>
    /// <para>
    /// <b>Why the write runs on another thread.</b> A blocked pipe write cannot
    /// be interrupted in place on any of the three supported operating systems;
    /// waiting for it from a side thread with a budget is the only portable way
    /// to keep the deciding thread in control. The waiter never abandons the
    /// task: on expiry the child is killed, which is what actually unblocks the
    /// pipe, and the write is then joined bounded so its failure is observed
    /// rather than left to surface as an unobserved task exception. Killing on
    /// expiry is also the contract, not collateral: a match whose agent will
    /// not accept its observation is over, and the kill-on-dispose guarantee
    /// would end the same process moments later anyway.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <see cref="Dispose"/> was called while the write was in flight. A
    /// cancelled exchange is not a protocol fault: no §8 code describes it, and
    /// recording one would score the agent a loss for a match Lattice itself
    /// abandoned.
    /// </exception>
    private void WriteLineBounded(byte[] payload, TimeSpan budget, ProtocolReason expiryReason, int step, string subject)
    {
        if (_cancelled.IsCancellationRequested)
        {
            throw new OperationCanceledException("the external agent was disposed during the exchange.");
        }

        if (budget <= TimeSpan.Zero)
        {
            // The whole-match budget was already gone before a byte was written.
            throw Fail(expiryReason, DescribeTimeout(expiryReason, subject), step, sendError: false);
        }

        Task write;
        // The in-flight count is incremented synchronously, before the task is
        // queued: between Task.Run and the pool running the body, Dispose could
        // otherwise read zero and close stdin under an about-to-start write —
        // the exact hazard the count exists to prevent.
        Interlocked.Increment(ref _writesInFlight);
        try
        {
            write = Task.Run(() =>
            {
                try
                {
                    _stdin.Write(payload, 0, payload.Length);
                    _stdin.WriteByte(LineFeed);
                    _stdin.Flush();
                }
                finally
                {
                    Interlocked.Decrement(ref _writesInFlight);
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Interlocked.Decrement(ref _writesInFlight);
            throw Fail(expiryReason, $"the write could not be started: {exception.Message}", step);
        }

        _writeInFlight = write;

        try
        {
            write.Wait(budget, _cancelled.Token);
        }
        catch (OperationCanceledException) when (_cancelled.IsCancellationRequested)
        {
            DisposeNow();
            throw new OperationCanceledException("the external agent was disposed during the exchange.");
        }
        catch (AggregateException)
        {
            // The write itself faulted — the pipe is already broken. The first
            // fault is the one to record, not a wrapper around it: GetResult
            // rethrows the original IOException.
            write.GetAwaiter().GetResult();
        }

        if (!write.IsCompleted)
        {
            // The budget expired with bytes still in the pipe. Kill FIRST — the
            // kill is what unblocks the writer, and closing stdin on top of a
            // blocked write would block the closer instead — then observe the
            // write's failure, so nothing is left blocked or unobserved behind
            // this throw.
            KillAndJoinWrite(write);
            LatchExit();
            TryCloseStdin();
            throw Fail(expiryReason, DescribeTimeout(expiryReason, subject), step, sendError: false);
        }

        // Completed within budget: surface an in-flight pipe failure as the
        // fault it is, exactly as a synchronous write would have.
        write.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The budget one exchange direction gets, and whether the whole-match
    /// budget is the one that binds. Shared by the write and the read so both
    /// sides of one exchange decide from the same clock and the same rule.
    /// </summary>
    private (TimeSpan Budget, bool MatchBinds) Budget(TimeSpan stepBudget)
    {
        var matchRemaining = _limits.MatchTimeout - _clock.Elapsed;
        if (matchRemaining <= TimeSpan.Zero)
        {
            return (TimeSpan.Zero, true);
        }

        // When the match budget is the smaller of the two it is the one that
        // binds, so its expiry is a timeout_match; otherwise the exchange budget
        // binds and its expiry is the caller's code. §8.4 puts timeout_step
        // first when both hold, and this ordering is what decides which of them
        // actually held.
        var matchBinds = matchRemaining < stepBudget;
        return (matchBinds ? matchRemaining : stepBudget, matchBinds);
    }

    /// <summary>
    /// The teardown path a mid-exchange cancellation uses: the full
    /// <see cref="Dispose"/> sequence, from an arbitrary thread, idempotently.
    /// </summary>
    private void DisposeNow()
    {
        try
        {
            Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The source was disposed between the check and this call.
        }
    }

    private byte[]? Exchange(byte[] payload, int step)
    {
        // The write and the read share one monotonic budget: the write consumes
        // from the step budget first and the read gets whatever remains, so a
        // blocked write cannot buy a second full budget on the read side. Which
        // budget binds — step or whole-match — is decided from the same clock
        // and the same rule in both directions.
        var writeStart = _clock.Elapsed;
        var (writeBudget, matchBinds) = Budget(_limits.StepTimeout);
        var writeTimeout = matchBinds ? ProtocolReason.TimeoutMatch : ProtocolReason.TimeoutStep;
        WriteLineBounded(payload, writeBudget, writeTimeout, step, subject: $"the observation for step {step} was not accepted");

        var stepRemaining = _limits.StepTimeout - (_clock.Elapsed - writeStart);
        var outcome = ReadWithin(stepRemaining < TimeSpan.Zero ? TimeSpan.Zero : stepRemaining, ProtocolReason.TimeoutStep, out var timeout);
        if (outcome is null)
        {
            throw Fail(
                timeout!.Value,
                DescribeTimeout(
                    timeout.Value,
                    timeout == ProtocolReason.TimeoutMatch
                        ? $"the match budget expired before step {step} was answered"
                        : $"no action for step {step}"),
                step);
        }

        return outcome.Value.Kind == ReadKind.Line
            ? outcome.Value.Line
            : throw new ExternalAgentFaultException(
                FromNonLine(outcome.Value, $"the exchange for step {step} ended without an action", step));
    }

    /// <summary>
    /// Reads the next line, bounded by whichever of the step and match budgets is
    /// smaller, and says which budget ran out if none arrives.
    /// </summary>
    /// <param name="stepBudget">The per-exchange budget to wait.</param>
    /// <param name="exchangeTimeout">
    /// The code to report when the <em>exchange</em> budget is what ran out. The
    /// handshake and the step loop share this method and this budget but not this
    /// code: an agent that never starts is <c>timeout_handshake</c> and one that
    /// stalls mid-match is <c>timeout_step</c> (spec §2, §8.2), so the caller says
    /// which exchange it is waiting on rather than this method guessing.
    /// </param>
    private ReadOutcome? ReadWithin(TimeSpan stepBudget, ProtocolReason exchangeTimeout, out ProtocolReason? timeout)
    {
        timeout = null;

        // One rule decides which budget binds, shared with the write side of the
        // exchange so both directions agree on it.
        var (budget, matchBinds) = Budget(stepBudget);
        if (budget <= TimeSpan.Zero)
        {
            timeout = ProtocolReason.TimeoutMatch;
            return null;
        }

        var deadline = _clock.Elapsed + budget;

        while (true)
        {
            var remaining = deadline - _clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timeout = matchBinds ? ProtocolReason.TimeoutMatch : exchangeTimeout;
                return null;
            }

            if (_inbound.TryTake(out var outcome, remaining < PollInterval ? remaining : PollInterval))
            {
                return outcome;
            }

            // The child can exit without its stdout ever reaching EOF: a
            // descendant it spawned holds the pipe open. Exit is observable
            // directly, so the exchange ends here rather than at the budget,
            // and the caller classifies from the exit code — the difference
            // between agent_exited and a timeout_step that blames a live
            // process for a match that was already over.
            bool exited;
            try
            {
                exited = _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                exited = false;
            }

            if (exited)
            {
                // One poll interval of settle, so a line the child flushed on
                // its way out is still taken — the same granularity the loop
                // already waits at.
                if (_inbound.TryTake(out var final, PollInterval))
                {
                    return final;
                }

                return ReadOutcome.OfClosed();
            }
        }
    }

    private static Task StartPump(Action pump) =>
        Task.Factory.StartNew(
            pump,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private void PumpStdout()
    {
        try
        {
            while (true)
            {
                // The line cap is enforced here, as it reads, so an over-long line
                // is refused without ever being buffered whole (§7).
                var line = ProtocolFraming.ReadLine(_stdout);
                if (line is null)
                {
                    _inbound.Add(ReadOutcome.OfClosed());
                    return;
                }

                // Capacity 1 is deliberate: the pump blocks until the line is
                // consumed, so exactly one agent line is ever in flight and a
                // second line cannot be silently buffered ahead of its step.
                _inbound.Add(ReadOutcome.OfLine(line));
            }
        }
        catch (ProtocolViolation violation)
        {
            TryAdd(ReadOutcome.OfViolation(violation));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went away, or the collection was completed during teardown.
            // Either way there is no further line to read.
        }
    }

    private void DrainStderr()
    {
        // A blocking read on a thread of its own, which is the point: this loop is
        // what keeps a chatty agent from blocking on a full pipe, and it must not
        // be waiting for a thread-pool slot to get there. Deliberately not
        // buffered past the ring -- every byte is appended as it arrives, so the
        // tail is always current.
        var buffer = new byte[16 * 1024];
        var stream = _process.StandardError.BaseStream;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                _stderr.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // stderr closed, or the child was killed. The tail collected so far is
            // what the failure record carries.
        }
    }

    private void TryAdd(ReadOutcome outcome)
    {
        try
        {
            _inbound.Add(outcome);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Completed during teardown; the match is already decided.
        }
    }

    private ExternalAgentFault FromNonLine(ReadOutcome outcome, string detail, int step)
    {
        var reason = outcome.Kind switch
        {
            // A detected violation outranks the process-level consequence of it:
            // the reason is the violation, not the exit that followed it (§8.4).
            ReadKind.Violation => outcome.Violation!.Reason,
            ReadKind.Closed => ClassifyExit(),
            _ => ProtocolReason.AgentExited,
        };

        var text = outcome.Kind == ReadKind.Violation ? outcome.Violation!.Detail : detail;
        return new ExternalAgentFault(reason, text, step, StderrTail());
    }

    private ProtocolReason ClassifyExit()
    {
        if (!_process.WaitForExit((int)ExitProbeTimeout.TotalMilliseconds))
        {
            // stdout closed but the child is still running: it stalled, which is an
            // exit from the exchange's point of view, and is not a crash.
            return ProtocolReason.AgentExited;
        }

        LatchExit();

        // A clean exit, or an EOF on stdout, is agent_exited; a signal or a
        // non-zero code is agent_crashed. .NET surfaces a signal-terminated child
        // as 128 + signal, so the same comparison covers both.
        return ExitCode == 0 ? ProtocolReason.AgentExited : ProtocolReason.AgentCrashed;
    }

    /// <summary>
    /// The diagnostic for a wait that ran out, naming <b>the budget that actually
    /// expired</b> and its value.
    /// </summary>
    /// <param name="reason">
    /// The code <see cref="ReadWithin"/> settled on: <c>timeout_step</c> when the
    /// per-exchange budget bound, <c>timeout_match</c> when the whole-match budget
    /// did.
    /// </param>
    /// <param name="subject">What the agent had not done yet, e.g. "no hello_ack".</param>
    /// <remarks>
    /// <para>
    /// The text is derived from the reason rather than written per call site,
    /// because a diagnostic that names the wrong budget sends the reader to tune
    /// the wrong number: "no action within step_timeout_ms" on a match that ran out
    /// of <c>match_timeout_ms</c> invites a reader to raise a limit that was never
    /// the binding one, and the failure repeats unchanged.
    /// </para>
    /// <para>
    /// In the handshake the match budget cannot in fact be the smaller of the two
    /// — §7 requires <c>match_timeout_ms ≥ step_timeout_ms × max_ticks</c>, and
    /// the clock starts when <c>hello</c> is written — so the
    /// <c>timeout_match</c> branch there is defensive. It is still routed through
    /// this one formatter rather than a second string, so the two exchanges
    /// cannot drift apart if that ever stops being true.
    /// </para>
    /// </remarks>
    private string DescribeTimeout(ProtocolReason reason, string subject) =>
        reason == ProtocolReason.TimeoutMatch
            ? $"{subject}: the match exceeded match_timeout_ms {_limits.MatchTimeoutMs}."
            : $"{subject} within step_timeout_ms {_limits.StepTimeoutMs}.";

    private ExternalAgentFaultException Fail(
        ProtocolReason reason,
        string detail,
        int step,
        bool sendError = true)
    {
        // §3/§8.1: an error line before terminating whenever a failure was
        // detected, and never on a normal termination. Best-effort, because a
        // process-level failure may already have taken the stream with it — and
        // bounded, because the same non-reading child that caused the fault can
        // hold the error line in a full pipe exactly as it held the observation.
        // The notification may consume what is left of the match budget, never
        // more: it does not restart the clock, and when nothing is left it is
        // skipped, which §8.1 permits wherever the stream has already failed.
        if (sendError && Volatile.Read(ref _disposeState) == 0)
        {
            var remaining = _limits.MatchTimeout - _clock.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    var error = ProtocolWriter.WriteLine(new ErrorMessage
                    {
                        Reason = reason,
                        Detail = detail,
                    });
                    WriteLineBounded(
                        error,
                        remaining,
                        reason,
                        step,
                        subject: "the error notification was not accepted");
                }
                catch (Exception e) when (e is ExternalAgentFaultException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The peer is gone, or would not read the notification either;
                    // §8.1 makes sending `error` permitted, not required, where the
                    // stream has already failed. The original fault stands.
                }
            }
        }

        var fault = new ExternalAgentFault(reason, detail, step, StderrTail());
        _pendingFault ??= fault;
        return new ExternalAgentFaultException(fault);
    }

    /// <summary>
    /// Closes stdin when no write is in flight. A close issued while a write is
    /// blocked inside the stream blocks the closer until that write ends — and
    /// a blocked pipe write never ends on its own — so a close that would sit
    /// on top of one is skipped: the tree kill ends the write, and the stream
    /// is disposed with the process afterwards.
    /// </summary>
    private void TryCloseStdin()
    {
        if (Volatile.Read(ref _writesInFlight) > 0)
        {
            return;
        }

        try
        {
            _stdin.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Already closed, or the child is gone. Either is fine at teardown.
        }
    }

    private void LatchExit()
    {
        try
        {
            if (_process.HasExited)
            {
                ProcessExited = true;
                ExitCode ??= _process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            // The Process was already disposed; nothing left to latch.
        }
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private enum ReadKind
    {
        Line,
        Violation,
        Closed,
    }

    private readonly record struct ReadOutcome(
        ReadKind Kind,
        byte[]? Line = null,
        ProtocolViolation? Violation = null)
    {
        public static ReadOutcome OfLine(byte[] line) => new(ReadKind.Line, line);

        public static ReadOutcome OfViolation(ProtocolViolation violation) => new(ReadKind.Violation, Violation: violation);

        public static ReadOutcome OfClosed() => new(ReadKind.Closed);
    }
}
