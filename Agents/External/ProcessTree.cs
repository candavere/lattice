using System.Diagnostics;

namespace Lattice.Agents.External;

/// <summary>
/// Best-effort descendant enumeration + explicit PID kill, so a worker that
/// was reparented past its parent's exit (PPID 1 on macOS launchd) is still
/// ended by a PID Lattice saw while the parent was alive.
/// Killing the process tree from an exited parent handle is accepted but does
/// not reliably reap such an orphan on macOS; a PID captured before the exit
/// does, because it no longer goes through the parent.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// All live descendants of <paramref name="rootPid"/>, recursively.
    /// Unix: parsed from `ps -e -o pid=,ppid=`. Windows: best-effort empty
    /// (unverified here); the caller's tree kill remains the only mechanism.
    /// Never throws: returns empty on any failure.
    /// </summary>
    public static IReadOnlyList<int> CollectDescendants(int rootPid)
    {
        if (rootPid <= 0) return [];
        if (OperatingSystem.IsWindows()) return CollectWindows(rootPid);
        return CollectViaPs(rootPid);
    }

    /// <summary>
    /// Kills one PID's tree, tolerating already-gone and platform refusal.
    /// When <paramref name="expectedStartTime"/> is supplied, the kill is
    /// skipped unless the live process's start time matches it, mitigating PID
    /// reuse. The check and the kill are not atomic, so residual TOCTOU remains;
    /// on any uncertainty the kill is skipped (fail closed).
    /// </summary>
    public static bool KillById(int pid, DateTime? expectedStartTime = null)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            if (expectedStartTime.HasValue)
            {
                DateTime actual;
                try
                {
                    actual = p.StartTime;
                }
                catch (Exception exception) when (exception is NotSupportedException
                    or InvalidOperationException
                    or PlatformNotSupportedException
                    or System.ComponentModel.Win32Exception)
                {
                    // Cannot verify identity; fail closed. If it has exited, done.
                    try { if (p.HasExited) return true; } catch { }
                    return false;
                }
                if (actual != expectedStartTime.Value)
                {
                    return false;
                }
            }
            try { p.Kill(entireProcessTree: true); }
            catch (NotSupportedException) { p.Kill(); }
            catch (InvalidOperationException) { }
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception)
        {
            // Lookup failed; cannot confirm. Treat as unverifiable rather than
            // claiming success, but do not throw.
            return false;
        }
    }

    /// <summary>
    /// True only when the process is positively gone: it does not exist, or it
    /// has exited. Every uncertainty (lookup failure, unsupported platform query)
    /// answers false, so callers that report an unreaped descendant fail closed:
    /// a process that may be alive is reported, a process that is certainly dead
    /// is not. A PID whose start time could not be captured because the process
    /// had already exited is reaped by definition; reporting it as unreaped
    /// conflates "could not verify, may be alive" with "dead before we could
    /// verify" (observed 2026-11-09: non-empty UnreapedDescendants with the
    /// worker already dead).
    /// </summary>
    internal static bool IsGone(int pid)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; } // no process with that id: positively gone
        // InvalidOperationException is not a positive "gone" signal: fail closed
        // (report unverifiable) instead of claiming the process exited.
        catch (Exception exception) when (exception is NotSupportedException
            or PlatformNotSupportedException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the start time of a live process, or null if it cannot be
    /// determined. Used to record identity alongside a captured PID.
    /// </summary>
    public static DateTime? TryGetStartTime(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            return p.StartTime;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<int> CollectViaPs(int rootPid)
    {
        Dictionary<int, List<int>> children = new();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("pid=,ppid=");
            using var ps = Process.Start(psi);
            if (ps is null) return [];
            var output = ps.StandardOutput.ReadToEnd();
            if (!ps.WaitForExit(2000)) { try { ps.Kill(); } catch { } return []; }
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0], out var pid)) continue;
                if (!int.TryParse(parts[1], out var ppid)) continue;
                if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = [];
                list.Add(pid);
            }
        }
        catch { return []; }

        List<int> result = [];
        Queue<int> queue = new();
        queue.Enqueue(rootPid);
        HashSet<int> seen = [rootPid];
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid)) continue;
                result.Add(kid);
                queue.Enqueue(kid);
            }
        }
        return result;
    }

    private static IReadOnlyList<int> CollectWindows(int rootPid)
    {
        // Unverified here (no Windows run). Story: query Win32_Process
        // ParentProcessId and BFS as above, then KillById. Returning empty
        // keeps the existing tree-kill behavior on Windows until exercised.
        return [];
    }
}
