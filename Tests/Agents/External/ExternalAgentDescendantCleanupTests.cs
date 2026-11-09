using System.Diagnostics;
using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// B3 product-owned descendant cleanup (red phase).
/// A real agent spawns a worker that holds stdout/stderr and outlives it;
/// the agent exits naturally (no cancel, no timeout, no test-side kill).
/// Product assertion (evaluated BEFORE harness reap): Lattice itself ended
/// the worker and none remains. Harness safety cleanup (by owned PID, in
/// finally) is mandatory so a failing test never leaves orphans, and must
/// never make the product assertion pass.
/// </summary>
public class ExternalAgentDescendantCleanupTests
{
    private readonly ITestOutputHelper _output;
    public ExternalAgentDescendantCleanupTests(ITestOutputHelper output) => _output = output;

    private const ulong Seed = 9100;
    private const string WorkerPidFileVar = "LATTICE_TEST_WORKER_PIDFILE";

    [Fact(Timeout = 30_000)]
    public async Task NaturalExitEndsReparentedWorker()
    {
        var pidFile = NewPidFile();
        bool? harnessReaped = null;
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-exit", "1"),
                agentId: 0, runSeed: Seed, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;
            Assert.True(childPid > 0);
            try
            {
                var obs = Obs();
                var first = await Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(ActionKind.Wait, first.Kind);

                var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                    () => Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(ProtocolReason.AgentExited, fault.Fault.Reason);
                Assert.Equal(1, fault.Fault.Step);

                var workersBefore = ReadPids(pidFile);
                Assert.NotEmpty(workersBefore);
                LogReparentEvidence(workersBefore, "after-natural-exit-before-dispose");

                agent.Dispose();

                var workersAfter = ReadPids(pidFile);
                var productClean = workersAfter.All(p => !IsAlive(p));
                _output.WriteLine($"PRODUCT-ASSERT natural-exit: workers=[{string.Join(",", workersAfter)}] clean={productClean}");
                ExternalAgentTestHost.AssertNoProcessLeft(childPid);
                Assert.True(productClean, $"Lattice did not end reparented worker(s) [{string.Join(",", workersAfter.Where(IsAlive))}]; product cleanup failed (harness has not reaped yet).");
            }
            finally
            {
                agent.Dispose();
            }
        }
        finally
        {
            harnessReaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP natural-exit: reaped-any={harnessReaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MislabelledExitAssertsAgentExitedNotTimeoutStep()
    {
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-exit", "1"),
                agentId: 0, runSeed: Seed + 1, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;
            try
            {
                var obs = Obs();
                _ = await Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10));
                var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                    () => Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(ProtocolReason.AgentExited, fault.Fault.Reason);
                Assert.NotEqual(ProtocolReason.TimeoutStep, fault.Fault.Reason);
                Assert.Equal(1, fault.Fault.Step);
                agent.Dispose();
                ExternalAgentTestHost.AssertNoProcessLeft(childPid);
                var workers = ReadPids(pidFile);
                var clean = workers.All(p => !IsAlive(p));
                _output.WriteLine($"PRODUCT-ASSERT mislabel: clean={clean} workers=[{string.Join(",", workers)}]");
                Assert.True(clean, "worker remains after agent_exited classification.");
            }
            finally
            {
                agent.Dispose();
            }
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP mislabel: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task StepTimeoutEndsWorkerHoldingPipes()
    {
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 500, maxTicks: 2, slackMs: 2_000);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-hang-at-step", "1"),
                agentId: 0, runSeed: Seed + 2, scenario: ExternalAgent.StandardScenario,
                maxTicks: 2, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;
            try
            {
                var obs = Obs();
                _ = await Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10));
                var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                    () => Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(12)));
                Assert.Equal(ProtocolReason.TimeoutStep, fault.Fault.Reason);
                agent.Dispose();
                var workers = ReadPids(pidFile);
                var clean = workers.All(p => !IsAlive(p));
                _output.WriteLine($"PRODUCT-ASSERT step-timeout: clean={clean} workers=[{string.Join(",", workers)}]");
                ExternalAgentTestHost.AssertNoProcessLeft(childPid);
                Assert.True(clean, "worker remains after step timeout.");
            }
            finally
            {
                agent.Dispose();
            }
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP step-timeout: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MatchTimeoutEndsWorkerHoldingPipes()
    {
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var diag = System.Diagnostics.Stopwatch.StartNew();
            void Diag(string tag) =>
                _output.WriteLine($"DIAG {tag}: t={diag.ElapsedMilliseconds}ms utc={DateTimeOffset.UtcNow:HH:mm:ss.fff}");
            Diag("ctor-start");
            var limits = new ExternalTimeLimits(stepTimeoutMs: 500, matchTimeoutMs: 500, maxTicks: 1);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-stall-match", "5000"),
                agentId: 0, runSeed: Seed + 3, scenario: ExternalAgent.StandardScenario,
                maxTicks: 1, agentCount: 2, limits);
            Diag("ctor-returned");
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;
            try
            {
                var fault = await Assert.ThrowsAsync<ExternalAgentFaultException>(
                    () => Task.Run(() => agent.Decide(Obs())).WaitAsync(TimeSpan.FromSeconds(12)));
                Assert.Equal(ProtocolReason.TimeoutMatch, fault.Fault.Reason);
                Diag($"fault reason={fault.Fault.Reason}");
                Diag("dispose-entry");
                agent.Dispose();
                Diag($"dispose-returned unreaped=[{string.Join(",", agent.UnreapedDescendants)}]");

                var workers = ReadPids(pidFile);
                var clean = workers.All(p => !IsAlive(p));
                _output.WriteLine($"PRODUCT-ASSERT match-timeout: clean={clean} workers=[{string.Join(",", workers)}]");
                try
                {
                    ExternalAgentTestHost.AssertNoProcessLeft(childPid);
                    Assert.True(clean, "worker remains after match timeout.");
                }
                finally
                {
                    // DIAG (post-assertion, best-effort): the stub records worker PIDs in
                    // this file right after Process.Start returns, so the mtime is the
                    // stub-recorded spawn completion - not the exact process birth, and
                    // the filesystem clock is coarse. Observed only after the original
                    // assertions so the read cannot interfere with the kill path or the
                    // assertion timing; a failure here is reported and never replaces
                    // the original failure or cleanup.
                    try
                    {
                        var pidContent = File.Exists(pidFile) ? File.ReadAllText(pidFile).Trim() : "<absent>";
                        var pidMtimeUtc = File.Exists(pidFile) ? File.GetLastWriteTimeUtc(pidFile).ToString("HH:mm:ss.fff") : "<absent>";
                        _output.WriteLine($"DIAG pid-file-post-dispose: pids=[{pidContent}] mtime-utc={pidMtimeUtc} now-utc={DateTimeOffset.UtcNow:HH:mm:ss.fff}");
                    }
                    catch (Exception diagError)
                    {
                        _output.WriteLine($"DIAG pid-file-read-error: {diagError.GetType().Name}: {diagError.Message}");
                    }

                    // DIAG (post-assertion, best-effort, observational only): liveness at
                    // two fixed offsets after the original assertions. Runs on pass and
                    // fail; a failure here is reported and never masks the original
                    // result. Two samples, then done: no watcher, no polling loop.
                    try
                    {
                        Thread.Sleep(250);
                        _output.WriteLine($"DIAG liveness +250ms-after-asserts: alive=[{string.Join(",", workers.Where(IsAlive))}]");
                        Thread.Sleep(750);
                        _output.WriteLine($"DIAG liveness +1000ms-after-asserts: alive=[{string.Join(",", workers.Where(IsAlive))}]");
                    }
                    catch (Exception diagError)
                    {
                        _output.WriteLine($"DIAG liveness-error: {diagError.GetType().Name}: {diagError.Message}");
                    }
                }
            }
            finally
            {
                agent.Dispose();
            }
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP match-timeout: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UserCancelEndsWorkerHoldingPipes()
    {
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-hold"),
                agentId: 0, runSeed: Seed + 4, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;
            try
            {
                var obs = Obs();
                var first = await Task.Run(() => agent.Decide(obs)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(ActionKind.Wait, first.Kind);
                agent.Dispose();
                var workers = ReadPids(pidFile);
                var clean = workers.All(p => !IsAlive(p));
                _output.WriteLine($"PRODUCT-ASSERT cancel: clean={clean} workers=[{string.Join(",", workers)}]");
                ExternalAgentTestHost.AssertNoProcessLeft(childPid);
                Assert.True(clean, "worker remains after user cancel (Dispose).");
            }
            finally
            {
                agent.Dispose();
            }
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP cancel: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkerSpawnedAfterTheLastExchangeIsReapedAtDispose()
    {
        // Regression: the dispose-time descendant capture used to be dead code
        // (NoteDescendants refuses to run once the teardown claim is staked, and
        // Dispose staked the claim first), so the last effective snapshot was
        // the previous exchange's. A worker born after that exchange was
        // invisible: the graceful stdin close let the conforming child exit on
        // EOF, the worker orphaned to PID 1, and the tree kill requested on the
        // exited handle no-op'd. The capture is now forced exactly once, after
        // the claim and before the kill passes.
        //
        // The fixture barrier is the PROTOCOL, not a sleep: the stub spawns its
        // worker only after answering step 0, and the host captures descendants
        // before it writes an observation - so the worker is deterministically
        // born after the last capture, however long the constructor's
        // post-handshake scan took. (A fixed post-handshake sleep could
        // false-green when that scan stalled past the sleep under load.)
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-hold-after-step", "0"),
                agentId: 0, runSeed: Seed + 4, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;

            // One ordinary exchange: the stub answers step 0 correctly and only
            // then spawns its worker. No further exchange runs, so no later
            // capture can see the worker before the dispose.
            _ = await Task.Run(() => agent.Decide(Obs())).WaitAsync(TimeSpan.FromSeconds(10));

            // Owned synchronization, bounded: the worker certainly exists
            // before the disposal - no fixed sleep.
            var workers = await WaitForOwnedWorkers(pidFile, wantAlive: 1);
            await Task.Run(() => agent.Dispose());

            var clean = workers.All(p => !IsAlive(p));
            _output.WriteLine($"PRODUCT-ASSERT dispose-capture: clean={clean} workers=[{string.Join(",", workers)}]");
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
            Assert.True(clean, "worker remains after dispose despite the dispose-time capture.");
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP dispose-capture: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkerOrphanedBeforeAnyCaptureOutlivesDispose_HarnessReaps()
    {
        // Characterization, not a passing grade: the relay spawns the worker
        // and exits BEFORE the hello_ack is written, so the worker has
        // reparented past the child before the host's first descendant
        // snapshot. No capture ever sees it, the tree kill cannot reach it,
        // and it is not even listed in UnreapedDescendants - the pre-existing
        // orphan blind spot, pinned here so a future fix flips this test.
        // The harness owns the worker via the pid file and reaps it after the
        // product assertions.
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-relayed-worker"),
                agentId: 0, runSeed: Seed + 6, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;

            var workers = await WaitForOwnedWorkers(pidFile, wantAlive: 1);
            await Task.Run(() => agent.Dispose());

            var surviving = workers.Where(IsAlive).ToList();
            _output.WriteLine($"CHARACTERIZATION orphan-before-capture: surviving=[{string.Join(",", surviving)}] unreaped=[{string.Join(",", agent.UnreapedDescendants)}]");
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
            Assert.Single(surviving);
            Assert.DoesNotContain(surviving[0], agent.UnreapedDescendants);
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP orphan-before-capture: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkerBornInTheGraceWindowOutlivesDispose_HarnessReaps()
    {
        // Characterization of the residual race the dispose-time capture does
        // not close: the stub plays correctly until the host closes stdin at
        // dispose, then spawns its worker in the dying breath - AFTER the
        // dispose-time snapshot - and exits cleanly within the grace,
        // orphaning the worker. Closing this window means killing before the
        // graceful close, a protocol-contract change (every conforming agent
        // would be SIGKILLed at dispose instead of exiting on EOF), so it is
        // pinned here rather than silently widened. The harness owns the
        // worker via the pid file and reaps it after the product assertions.
        var pidFile = NewPidFile();
        try
        {
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, pidFile);
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
            using var agent = new ExternalAgent(
                ExternalAgentTestHost.Launch("spawn-on-eof"),
                agentId: 0, runSeed: Seed + 7, scenario: ExternalAgent.StandardScenario,
                maxTicks: 4, agentCount: 2, limits);
            Assert.Null(agent.PendingFault);
            var childPid = agent.ProcessId;

            _ = await Task.Run(() => agent.Decide(Obs())).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() => agent.Dispose());

            // The spawn happens before the child exits, and Dispose waited for
            // that exit, so the pid file is written; the bounded poll only
            // covers file-flush latency.
            var workers = await WaitForOwnedWorkers(pidFile, wantAlive: 1);
            var surviving = workers.Where(IsAlive).ToList();
            _output.WriteLine($"CHARACTERIZATION grace-window-birth: surviving=[{string.Join(",", surviving)}] unreaped=[{string.Join(",", agent.UnreapedDescendants)}]");
            ExternalAgentTestHost.AssertNoProcessLeft(childPid);
            Assert.Single(surviving);
            Assert.DoesNotContain(surviving[0], agent.UnreapedDescendants);
        }
        finally
        {
            var reaped = ReapOwned(pidFile);
            _output.WriteLine($"HARNESS-CLEANUP grace-window-birth: reaped-any={reaped}");
            System.Environment.SetEnvironmentVariable(WorkerPidFileVar, null);
        }
    }

    private async Task<List<int>> WaitForOwnedWorkers(string pidFile, int wantAlive)
    {
        for (var waited = 0; waited < 10_000; waited += 50)
        {
            var pids = ReadPids(pidFile);
            if (pids.Count(IsAlive) >= wantAlive) { return pids; }
            await Task.Delay(50);
        }

        throw new InvalidOperationException("fixture never recorded a live worker pid; the test setup itself is broken.");
    }

    private static string NewPidFile() =>
        Path.Combine(Path.GetTempPath(), $"lattice-b3-pids-{Guid.NewGuid():N}.txt");

    private static Observation Obs()
    {
        var map = ExternalAgentTestHost.TwoZoneMap();
        var state = Simulation.CreateInitial(map, new SimulationConfig(AgentCount: 2, MaxTicks: 1), DynamicMapRuleSet.None);
        return new Observation(0, state.Map, state.Agents, state.Claims, state.StepCount);
    }

    private static List<int> ReadPids(string pidFile)
    {
        if (!File.Exists(pidFile)) return [];
        return File.ReadAllLines(pidFile)
            .Select(l => int.TryParse(l.Trim(), out var p) ? p : -1)
            .Where(p => p > 0).Distinct().ToList();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void LogReparentEvidence(List<int> pids, string phase)
    {
        foreach (var pid in pids)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = "ps", RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ppid=");
                psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(pid.ToString());
                using var ps = Process.Start(psi);
                if (ps is null) continue;
                var outp = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit(2000);
                _output.WriteLine($"REPARENT-EVIDENCE {phase}: worker={pid} alive={IsAlive(pid)} ppid=[{outp.Trim()}] (1=launchd on macOS)");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"REPARENT-EVIDENCE {phase}: worker={pid} ps failed: {ex.Message}");
            }
        }
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    [Fact]
    public void ProcessTree_KillById_DoesNotKillWhenIdentityMismatched()
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var actual = ProcessTree.TryGetStartTime(p.Id);
        Assert.NotNull(actual);
        Assert.False(ProcessTree.KillById(p.Id, actual.Value.AddSeconds(1)));
        Assert.True(IsAlive(p.Id));
        try { p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(5000); } catch { }
    }

    [Fact]
    public void ProcessTree_KillById_AbsentPidIsComplete()
    {
        Assert.True(ProcessTree.KillById(int.MaxValue));
    }

    [Fact(Timeout = 30_000)]
    public async Task UnverifiedWorkerAlreadyDeadIsExcludedFromUnreaped_LiveUnverifiedIsListedNotKilled()
    {
        // Integration evidence for the null-start branch of
        // KillKnownDescendants. A null start time means identity was never
        // captured, so the PID is never killed by bare id - that invariant is
        // unchanged. What changed: a null-start PID whose process is already
        // gone is no longer reported unreaped ("dead before we could verify"
        // is not "could not verify, may be alive"); a null-start PID whose
        // process is still live is still listed and left running.
        //
        // The null-start state cannot be produced deterministically from the
        // public surface (it needs TryGetStartTime to fail on a live capture),
        // so the precondition is injected into the private bookkeeping with
        // reflection; everything asserted is real: real processes, a real
        // child, a real Dispose, the public UnreapedDescendants report.
        var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 4_000, maxTicks: 4, slackMs: 2_000);
        using var agent = new ExternalAgent(
            ExternalAgentTestHost.Launch("conform"),
            agentId: 0, runSeed: Seed + 8, scenario: ExternalAgent.StandardScenario,
            maxTicks: 4, agentCount: 2, limits);
        Assert.Null(agent.PendingFault);

        using var live = Process.Start(new ProcessStartInfo
        {
            FileName = "sleep", ArgumentList = { "30" }, UseShellExecute = false, CreateNoWindow = true,
        })!;
        using var dead = Process.Start(new ProcessStartInfo
        {
            FileName = "sleep", ArgumentList = { "0" }, UseShellExecute = false, CreateNoWindow = true,
        })!;
        dead.WaitForExit(10_000);

        var field = typeof(ExternalAgent).GetField("_knownDescendants",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("bookkeeping field renamed; update this test");
        var known = (System.Collections.Generic.IDictionary<int, DateTime?>)field.GetValue(agent)!;
        known[dead.Id] = null;
        known[live.Id] = null;

        await Task.Run(() => agent.Dispose());

        _output.WriteLine($"PRODUCT-ASSERT null-start: unreaped=[{string.Join(",", agent.UnreapedDescendants)}] dead={dead.Id} live={live.Id}");
        Assert.DoesNotContain(dead.Id, agent.UnreapedDescendants);
        Assert.Contains(live.Id, agent.UnreapedDescendants);
        Assert.True(IsAlive(live.Id), "a null-start PID must never be killed by bare id.");
        try { live.Kill(entireProcessTree: true); } catch { }
        try { live.WaitForExit(5_000); } catch { }
    }

    private bool ReapOwned(string pidFile)
    {
        var pids = ReadPids(pidFile);
        var reapedAny = false;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    reapedAny = true;
                    _output.WriteLine($"harness reaping owned worker {pid}");
                    try { p.Kill(entireProcessTree: true); } catch (NotSupportedException) { p.Kill(); }
                    try { p.WaitForExit(5000); } catch { }
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        var deadline = Stopwatch.StartNew();
        while (pids.Any(IsAlive) && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(100);
        try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
        return reapedAny;
    }
}
