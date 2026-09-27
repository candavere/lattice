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

    private ExternalAgentFault? _pendingFault;
    private int _step;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pendingFault is not null)
        {
            throw new ExternalAgentFaultException(_pendingFault);
        }

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        TryCloseStdin();

        if (!_process.WaitForExit((int)GracefulExitTimeout.TotalMilliseconds))
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the wait and the kill; nothing to do.
            }

            _process.WaitForExit((int)KillConfirmTimeout.TotalMilliseconds);
        }

        LatchExit();

        // Releasing the pump lets a reader blocked handing over a line finish.
        _inbound.CompleteAdding();
        try
        {
            Task.WaitAll(_pumps.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // A pump that faults while the pipe is being torn down is not a match
            // failure: the match has already been decided. Swallowed rather than
            // allowed to mask that decision.
        }

        _inbound.Dispose();
        _process.Dispose();
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
        WriteLine(payload);

        // The handshake waits step_timeout_ms, on the same knob as a step (§3.1).
        var outcome = ReadWithin(_limits.StepTimeout, ProtocolReason.TimeoutHandshake, out var timeout);
        if (outcome is null)
        {
            _pendingFault = new ExternalAgentFault(
                timeout!.Value,
                $"no hello_ack within step_timeout_ms {_limits.StepTimeoutMs}.",
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

    private byte[]? Exchange(byte[] payload, int step)
    {
        WriteLine(payload);

        var outcome = ReadWithin(_limits.StepTimeout, ProtocolReason.TimeoutStep, out var timeout);
        if (outcome is null)
        {
            throw Fail(
                timeout!.Value,
                timeout == ProtocolReason.TimeoutMatch
                    ? $"the match exceeded match_timeout_ms {_limits.MatchTimeoutMs} before step {step} was answered."
                    : $"no action within step_timeout_ms {_limits.StepTimeoutMs} for step {step}.",
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

        var matchRemaining = _limits.MatchTimeout - _clock.Elapsed;
        if (matchRemaining <= TimeSpan.Zero)
        {
            timeout = ProtocolReason.TimeoutMatch;
            return null;
        }

        // When the match budget is the smaller of the two it is the one that binds,
        // so its expiry is a timeout_match; otherwise the exchange budget binds and
        // its expiry is the caller's code. §8.4 puts timeout_step first when both
        // hold, and this ordering is what decides which of them actually held.
        var matchBinds = matchRemaining < stepBudget;
        var budget = matchBinds ? matchRemaining : stepBudget;
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

    private ExternalAgentFaultException Fail(
        ProtocolReason reason,
        string detail,
        int step,
        bool sendError = true)
    {
        // §3/§8.1: an error line before terminating whenever a failure was
        // detected, and never on a normal termination. Best-effort, because a
        // process-level failure may already have taken the stream with it.
        if (sendError && !_disposed)
        {
            try
            {
                var error = ProtocolWriter.WriteLine(new ErrorMessage
                {
                    Reason = reason,
                    Detail = detail,
                });
                WriteLine(error);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The peer is gone; §8.1 makes sending `error` permitted, not
                // required, where the stream has already failed.
            }
        }

        var fault = new ExternalAgentFault(reason, detail, step, StderrTail());
        _pendingFault ??= fault;
        return new ExternalAgentFaultException(fault);
    }

    private void TryCloseStdin()
    {
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
