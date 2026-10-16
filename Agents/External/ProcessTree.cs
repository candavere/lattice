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

    /// <summary>Kills one PID's tree, tolerating already-gone and platform refusal.</summary>
    public static void KillById(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;
            try { p.Kill(entireProcessTree: true); }
            catch (NotSupportedException) { p.Kill(); }
            catch (InvalidOperationException) { }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
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
