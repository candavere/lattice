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
            var limits = ExternalAgentTestHost.Budget(stepTimeoutMs: 500, maxTicks: 2, slackMs: 30_000);
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
