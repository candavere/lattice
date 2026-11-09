using System.Diagnostics;
using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// Run-control repair, bug A: stdin backpressure must not bypass deadlines.
///
/// The stub mode <c>ack-then-stop-reading</c> answers the handshake and then
/// never reads stdin again, so the next observation write blocks on a full
/// pipe while the child stays alive. That is backpressure, not a silent agent:
/// a silent agent lets the write complete and never answers, while this one
/// never lets the write complete. Every test here bounds the wait with an
/// explicit outer deadline (so a regression fails instead of hanging the host)
/// and cleans up only the process it started, by PID recorded from the agent
/// it constructed. No scan-and-kill, no stale PID reuse: the PID comes from
/// <see cref="ExternalAgent.ProcessId"/> on the live instance being tested.
/// </summary>
/// <remarks>
/// Outer budgets: step 500 ms plus a bounded teardown allowance of 12 s
/// (kill confirm 2 s + pump join 5 s, both pre-existing constants, plus margin
/// for child startup under parallel load). A case that has not settled in that
/// window has bypassed its deadline by definition.
/// </remarks>
public class ExternalAgentBackpressureTests
{
    private const ulong Seed = 9001;

    /// <summary>
    /// Reaps the fixture's worker by the PID the stub recorded in the test-owned
    /// file, where the platform's tree kill cannot (the audit's primitive probe:
    /// <c>Kill(entireProcessTree: true)</c> on an exited parent is accepted but
    /// does not reliably reap an already-reparented descendant on macOS). The
    /// PIDs come only from this test's own file — never a scan, never a name
    /// match. PID reuse inside the file's seconds-long lifetime is accepted and
    /// documented: the file is created fresh per test and read back only by the
    /// test that owns it.
    /// </summary>
    private static void ReapOwnedWorkers(string? pidFile)
    {
        if (pidFile is null || !File.Exists(pidFile))
        {
            return;
        }

        var pids = File.ReadAllLines(pidFile)
            .Select(line => int.TryParse(line.Trim(), out var pid) ? pid : -1)
            .Where(pid => pid > 0)
            .Distinct();
        foreach (var pid in pids)
        {
            try
            {
                using var worker = Process.GetProcessById(pid);
                if (!worker.HasExited)
                {
                    worker.Kill(entireProcessTree: true);
                    worker.WaitForExit(5_000);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Already gone, or the platform refused; nothing this test owns
                // is left to reap.
            }
        }
    }

    /// <summary>A fresh test-owned file for the stub's worker-PID records.</summary>
    private static string NewWorkerPidFile() =>
        Path.Combine(Path.GetTempPath(), $"lattice-worker-pids-{Guid.NewGuid():N}.txt");

    /// <summary>The stub's own name for the test-only environment variable.</summary>
    private const string WorkerPidFileVariableName = "LATTICE_TEST_WORKER_PIDFILE";

    /// <summary>
    /// Handshake succeeds, then a permitted large observation meets a child
    /// that no longer reads. The exchange must settle within step budget plus
    /// teardown allowance with a step timeout, and leave no process behind.
    /// Pre-fix: the synchronous stdin write blocks forever (pipe_write) and
    /// the outer deadline fires.
    /// </summary>
    /// <remarks>
    /// The match budget carries slack so the <em>step</em> budget is the one
    /// that binds: with match == step, the handshake's own elapsed time makes
    /// the match budget the smaller one from the first wait onward, and the
    /// same blocked write would correctly report timeout_match — which is the
    /// separate case the next test pins.
    /// </remarks>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task BlockedWriteSettlesWithinStepBudgetPlusTeardown()
    {
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 500, maxTicks: 1, slackMs: 30_000);
        using var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("ack-then-stop-reading"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 1, agentCount: 2, limits);

        // The handshake barrier: the constructor returned without a pending
        // fault, so the child proved it reads (it answered hello_ack) before
        // it stopped reading. A failure here is a different bug than the one
        // under test.
        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;
        Assert.True(childPid > 0);

        var observation = ObservationFor(LargeButPermittedMap(), slot: 0);
        var clock = Stopwatch.StartNew();
        try
        {
            var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                () => Task.Run(() => agent.Decide(observation))
                    .WaitAsync(TimeSpan.FromSeconds(12)));
            clock.Stop();

            Assert.Equal(ProtocolReason.TimeoutStep, fault.Fault.Reason);
            Assert.Equal(0, fault.Fault.Step);
            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(12),
                $"blocked write took {clock.Elapsed}; the 500 ms step budget was bypassed.");
            AssertStderrTailAttached(fault.Fault);
        }
        finally
        {
            agent.Dispose();
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        }
    }

    /// <summary>
    /// The whole-match budget expires first: with match == step (one tick, no
    /// slack) the elapsed handshake time makes the match budget the smaller
    /// one from the first wait onward, so the same blocked write must report
    /// <c>timeout_match</c>, not <c>timeout_step</c>. This pins the precedence
    /// the pre-fix code never reached (it blocked before any budget applied).
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task WholeMatchBudgetExpiringFirstReportsTimeoutMatch()
    {
        var limits = new ExternalTimeLimits(stepTimeoutMs: 500, matchTimeoutMs: 500, maxTicks: 1);
        using var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("ack-then-stop-reading"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 1, agentCount: 2, limits);
        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;

        var observation = ObservationFor(LargeButPermittedMap(), slot: 0);
        try
        {
            var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                () => Task.Run(() => agent.Decide(observation))
                    .WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.Equal(ProtocolReason.TimeoutMatch, fault.Fault.Reason);
        }
        finally
        {
            agent.Dispose();
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        }
    }

    /// <summary>
    /// Reporting the error must not start a second unbounded write or restart
    /// the budget: the fault above already attempts a best-effort error line on
    /// the same full pipe. The exchange must still settle within the single
    /// budget plus teardown, not twice it.
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task ErrorNotificationDoesNotRestartTheBudget()
    {
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 500, maxTicks: 1, slackMs: 2_000);
        using var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("ack-then-stop-reading"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 1, agentCount: 2, limits);
        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;

        var observation = ObservationFor(LargeButPermittedMap(), slot: 0);
        var clock = Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAsync<ExternalAgentFaultException>(
                () => Task.Run(() => agent.Decide(observation))
                    .WaitAsync(TimeSpan.FromSeconds(12)));
            clock.Stop();

            // One budget plus teardown, not two budgets: an error line that
            // restarted the clock would push settlement past step + match.
            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(12),
                $"fault plus error notification took {clock.Elapsed}; the error path restarted the budget.");
        }
        finally
        {
            agent.Dispose();
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        }
    }

    /// <summary>
    /// Cancelling during a blocked write is distinct from timing out. Disposing
    /// the agent from a second thread (the only pre-token cancellation path)
    /// must unblock the writer promptly; the throw must read as cancellation,
    /// never as a step timeout for an agent that was not given its budget.
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task ConcurrentDisposeDuringBlockedWriteReadsAsCancellation()
    {
        // Step budget must comfortably exceed the 500 ms dispose delay: under
        // full parallel load the writer's budget clock can otherwise expire
        // before the beside-thread dispose is observed, reading a step timeout
        // for an agent the test itself cancelled. 30 s is far above the delay
        // so cancellation ordering is deterministic; the budget never binds.
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 30_000, maxTicks: 2, slackMs: 2_000);
        var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("ack-then-stop-reading"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 2, agentCount: 2, limits);
        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;

        var observation = ObservationFor(LargeButPermittedMap(), slot: 0);
        var decided = Task.Run(() =>
        {
            try
            {
                agent.Decide(observation);
                return "returned";
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
            catch (ExternalAgentFaultException faulted)
            {
                return "fault:" + faulted.Fault.Reason.ToWireString();
            }
            catch (ObjectDisposedException)
            {
                return "cancelled";
            }
        });

        // Let the writer block on the full pipe (handshake proved the pipe
        // works; nothing reads now), then cancel from beside it.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        agent.Dispose();

        var outcome = await decided.WaitAsync(TimeSpan.FromSeconds(12));
        ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        Assert.Equal(
            "cancelled", outcome);
    }

    /// <summary>
    /// Teardown is repeatable and late I/O cannot double-report: dispose twice
    /// and decide-after-dispose throw without a second result or an unobserved
    /// pump failure. Passes before and after the fix; retained as the guard
    /// that the bounded teardown did not break the normal path.
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task RepeatedTeardownIsIdempotent()
    {
        var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("conform"),
            agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
            maxTicks: 2, agentCount: 2,
            ExternalAgentTestHost.Budget(stepTimeoutMs: 2_000, maxTicks: 2, slackMs: 2_000));
        var childPid = agent.ProcessId;

        var observation = ObservationFor(ExternalAgentTestHost.TwoZoneMap(), slot: 0);
        var action = await Task.Run(() => agent.Decide(observation)).WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(ActionKind.Wait, action.Kind);

        agent.Dispose();
        agent.Dispose();
        Assert.Throws<ObjectDisposedException>(() => agent.Decide(observation));
        ExternalAgentTestHost.AssertNoProcessLeft(childPid);
    }

    /// <summary>
    /// The audit's per-match teardown tax: an agent that plays its whole match
    /// while a spawned worker holds the capture pipes must not cost a fixed
    /// stall at dispose, and the direct child must be gone. The worker's own
    /// fate is platform-documented (the tree kill reaps it where the platform
    /// can; an already-reparented orphan is recorded, not asserted).
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task TeardownWithWorkerHoldingPipesIsBoundedAndLeavesNoChild()
    {
        const int maxTicks = 4;
        var pidFile = NewWorkerPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, pidFile);
            var clock = Stopwatch.StartNew();
            var result = await Task.Run(() => ExternalMatchRunner.Run(
                ExternalAgentTestHost.TwoZoneMap(),
                ExternalAgentTestHost.Config(maxTicks),
                ExternalAgentTestHost.Launch("spawn-hold"),
                externalSlot: 0,
                baseline: new AlwaysWaitAgent(1),
                Seed,
                maxSteps: maxTicks,
                limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 2_000, maxTicks, slackMs: 2_000)))
                .WaitAsync(TimeSpan.FromSeconds(20));
            clock.Stop();

            Assert.Null(result.Fault);
            Assert.True(result.Completed);
            Assert.True(result.ChildExited, "the direct child was not observed to have exited.");
            if (result.ChildProcessId is { } childId)
            {
                ExternalAgentTestHost.AssertNoProcessLeft(childId);
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, null);
            ReapOwnedWorkers(pidFile);
        }
    }

    /// <summary>
    /// The audit's misclassification: the agent answers every step, then exits
    /// cleanly while its worker holds both pipes. The failure must be
    /// <c>agent_exited</c> — observed from the process — never
    /// <c>timeout_step</c>, which would blame a live match for a stall that
    /// was an exit.
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task CleanExitWithWorkerHoldingPipesIsAgentExited()
    {
        // Exits at step 1 of 4: one exchange completed, so this is a mid-match
        // clean exit exactly as exit-at-step produces, with the pipe held.
        var pidFile = NewWorkerPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, pidFile);
            var result = await Task.Run(() => ExternalMatchRunner.Run(
                ExternalAgentTestHost.TwoZoneMap(),
                ExternalAgentTestHost.Config(maxTicks: 4),
                ExternalAgentTestHost.Launch("spawn-exit", "1"),
                externalSlot: 0,
                baseline: new AlwaysWaitAgent(1),
                Seed,
                maxSteps: 4,
                limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000)))
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotNull(result.Fault);
            Assert.Equal(ProtocolReason.AgentExited, result.Fault.Reason);
            Assert.Equal(1, result.Fault.Step);
            Assert.True(result.ChildExited);
            if (result.ChildProcessId is { } childId)
            {
                ExternalAgentTestHost.AssertNoProcessLeft(childId);
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, null);
            ReapOwnedWorkers(pidFile);
        }
    }

    /// <summary>
    /// The audit's per-match teardown tax, asserted where it is actually paid:
    /// the agent self-exits while its worker holds the pipes, and Dispose used
    /// to wait out a 5 s pump join for a pipe that will never reach EOF. The
    /// bound is on Dispose alone — no child startup inside the measurement —
    /// so it discriminates the join tax from parallel-run load.
    /// </summary>
    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task DisposeAfterSelfExitWithWorkerHoldingPipesIsBounded()
    {
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
        var pidFile = NewWorkerPidFile();
        System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, pidFile);
        ExternalAgent agent;
        try
        {
            agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-exit", "1"),
                agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVariableName, null);
        }

        Assert.Null(agent.PendingFault);
        var childPid = agent.ProcessId;

        try
        {
            // Step 0 answered; the stub exits at step 1 with the worker holding the
            // pipes. The exit must surface as agent_exited, not as a timeout.
            var observation = ObservationFor(ExternalAgentTestHost.TwoZoneMap(), slot: 0);
            _ = await Task.Run(() => agent.Decide(observation)).WaitAsync(TimeSpan.FromSeconds(10));
            var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                () => Task.Run(() => agent.Decide(observation)).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(ProtocolReason.AgentExited, fault.Fault.Reason);

            var disposeClock = Stopwatch.StartNew();
            agent.Dispose();
            disposeClock.Stop();

            Assert.True(
                disposeClock.Elapsed < TimeSpan.FromSeconds(3),
                $"Dispose took {disposeClock.Elapsed}; teardown still pays the descendant pump-join tax.");
        }
        finally
        {
            agent.Dispose();
            ReapOwnedWorkers(pidFile);
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
        }
    }

    /// <summary>
    /// A map large enough to fill the pipe but small enough to stay legal: the
    /// observation must pass the §7 host gate (otherwise the match is voided
    /// before any byte is written and the test proves nothing) while exceeding
    /// a 64 KiB pipe buffer (otherwise the write completes and the test
    /// measures a read timeout instead of backpressure).
    /// </summary>
    private static MapGraph LargeButPermittedMap()
    {
        var map = new MapGraph(
            [.. Enumerable.Range(0, 5_000).Select(i => new Zone(i, new GridPoint(i, 0)))],
            [],
            []);
        var observation = ObservationFor(map, slot: 0);
        Assert.Null(
            ExternalAgent.CheckOutbound(step: 0, observation));

        var payload = ProtocolWriter.Encode(ExternalWire.ToWire(step: 0, observation));
        Assert.True(
            payload.Length < ProtocolLimits.MaxLineBytes,
            $"observation is {payload.Length} bytes; the host gate would void it and the test would prove nothing.");
        Assert.True(
            payload.Length > 64 * 1024,
            $"observation is only {payload.Length} bytes; it would not fill the pipe and the test would measure a read timeout.");
        return map;
    }

    private static Observation ObservationFor(MapGraph map, int slot)
    {
        var state = Simulation.CreateInitial(map, new SimulationConfig(AgentCount: 2, MaxTicks: 1), DynamicMapRuleSet.None);
        return new Observation(slot, state.Map, state.Agents, state.Claims, state.StepCount);
    }

    private static void AssertStderrTailAttached(ExternalAgentFault fault)
    {
        Assert.Contains("stub: mode=", fault.StderrTail, StringComparison.Ordinal);
    }
}
