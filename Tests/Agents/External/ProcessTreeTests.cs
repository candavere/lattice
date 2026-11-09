using System.Diagnostics;
using Lattice.Agents.External;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// Unit coverage for the PID-identity helpers behind descendant reaping.
/// These exist because <see cref="ExternalAgent.UnreapedDescendants"/> once
/// listed PIDs whose identity read had failed <em>because the process was
/// already dead</em> (observed 2026-11-09: non-empty unreaped list with the
/// worker verified dead by an independent liveness probe), conflating
/// "could not verify, may be alive" with "dead before we could verify".
/// The decision lives in <see cref="ProcessTree.IsGone"/>, tested here
/// deterministically with real owned processes - no races, no sleeps.
/// </summary>
public class ProcessTreeTests
{
    [Fact]
    public void IsGone_CurrentProcess_IsFalse()
    {
        using var self = Process.GetCurrentProcess();
        Assert.False(ProcessTree.IsGone(self.Id));
    }

    [Fact]
    public void IsGone_ExitedProcess_IsTrue()
    {
        using var process = StartShortLived();
        process.WaitForExit();
        Assert.True(ProcessTree.IsGone(process.Id));
    }

    [Fact]
    public void IsGone_NonExistentPid_IsTrue()
    {
        // No supported platform hands out int.MaxValue as a PID (Linux pid_max
        // caps at 2^22, macOS below 100000, Windows far below), so this is a
        // deterministic "no such process" lookup, not a race against reuse.
        Assert.True(ProcessTree.IsGone(int.MaxValue));
    }

    [Fact]
    public void KillById_IdentityMismatch_FailsClosedAndDoesNotKill()
    {
        // PID-reuse guard: a live process whose start time does NOT match the
        // captured identity must survive untouched (fail closed - the PID may
        // belong to someone else's reused process), and the miss is reported
        // as false so the caller can list it as unreaped.
        using var worker = StartHoldingWorker();
        var actualStart = worker.StartTime;
        try
        {
            var killed = ProcessTree.KillById(worker.Id, actualStart.AddMinutes(1));

            Assert.False(killed);
            worker.Refresh();
            Assert.False(worker.HasExited, "identity-mismatched kill must not touch the process");
        }
        finally
        {
            // Owned PID, verified identity: the exact cleanup path under test.
            ProcessTree.KillById(worker.Id, actualStart);
        }
    }

    [Fact]
    public void KillById_MatchingIdentity_KillsOwnedProcess()
    {
        using var worker = StartHoldingWorker();
        var actualStart = worker.StartTime;

        var killed = ProcessTree.KillById(worker.Id, actualStart);

        Assert.True(killed);
        var exited = worker.WaitForExit(10_000);
        Assert.True(exited, "owned worker did not exit after an identity-matched kill");
    }

    [Fact]
    public void KillById_ExitedProcess_ReturnsTrue()
    {
        // Deterministic barrier: the child blocks on its own stdin until the
        // parent closes the pipe, so its identity is captured while it is
        // guaranteed alive - no scheduling window exists. Release is EOF, not
        // a signal, so the fixture itself cannot hit the stopped-process kill
        // races the descendant tests live next to.
        using var process = StartBarrierHeld(out var start);
        try
        {
            process.StandardInput.Close(); // release: child sees EOF and exits 0
            Assert.True(
                process.WaitForExit(10_000),
                "barrier-held child did not exit after release");
            Assert.True(ProcessTree.KillById(process.Id, start));
        }
        finally
        {
            if (!process.HasExited)
            {
                // Owned PID, verified identity: the exact cleanup path under test.
                ProcessTree.KillById(process.Id, start);
            }
        }
    }

    /// <summary>A process that exits on its own immediately, with no pipes held.</summary>
    private static Process StartShortLived()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        return process;
    }

    /// <summary>
    /// A child that cannot exit before the parent releases it: the fixture
    /// stub in hold-until-eof mode, blocked reading its own redirected stdin.
    /// StartTime is captured before returning, while the child is alive by
    /// construction.
    /// </summary>
    private static Process StartBarrierHeld(out DateTime start)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList, not a joined string: a stub path containing spaces
        // must survive without any quoting convention of our own.
        startInfo.ArgumentList.Add(ExternalAgentTestHost.StubPath);
        startInfo.ArgumentList.Add("hold-until-eof");
        var process = Process.Start(startInfo)!;
        try
        {
            start = process.StartTime;
            return process;
        }
        catch
        {
            // The child is ours and still barrier-held: release it via EOF and
            // dispose, never a kill against an identity we failed to verify.
            try { process.StandardInput.Close(); } catch { }
            try { process.WaitForExit(10_000); } catch { }
            process.Dispose();
            throw;
        }
    }


    /// <summary>
    /// The fixture stub in worker-hold mode: a real, owned process that stays
    /// alive until killed. Every test using it is responsible for the kill -
    /// it owns the PID, and the identity-matched cleanup here is what the
    /// fail-closed tests verify does not happen by accident.
    /// </summary>
    private static Process StartHoldingWorker()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{ExternalAgentTestHost.StubPath} worker-hold",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            // Force the runtime to have observed the start before the tests
            // read StartTime: on some platforms the first access is what throws
            // for a failed start, and we want that here, not inside an assertion.
            _ = process.StartTime;
            return process;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
