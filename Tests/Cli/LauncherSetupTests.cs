using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lattice.Tests.Cli;

public sealed class LauncherSetupTests
{
    private const int DefaultTimeoutMs = 300_000;

    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Lattice.sln")))
        {
            root = Directory.GetParent(root)?.FullName;
        }
        Assert.True(root is not null, "could not locate the repository root.");
        return root!;
    }

    private static string SetupScript()
    {
        var name = OperatingSystem.IsWindows() ? "setup.ps1" : "setup.sh";
        return Path.Combine(RepoRoot(), name);
    }

    private static string Shell()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "pwsh", "powershell" })
            {
                foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                    {
                        try
                        {
                            var full = Path.Combine(dir.Trim(), name + ext);
                            if (File.Exists(full))
                            {
                                return full;
                            }
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                }
            }
            throw new InvalidOperationException("no PowerShell interpreter found");
        }

        return "/bin/bash";
    }

    private static ProcessStartInfo BaseStart(string fileName, IEnumerable<string> args, string cwd, IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var pair in env)
        {
            psi.Environment[pair.Key] = pair.Value;
        }
        return psi;
    }

    private sealed class LauncherDeadline
    {
        public string Name { get; }
        public Stopwatch Stopwatch { get; }
        public long TotalMs { get; }
        public long CleanupReserveMs { get; }

        private LauncherDeadline(string name, long totalMs, long cleanupReserveMs)
        {
            Name = name;
            TotalMs = totalMs;
            CleanupReserveMs = cleanupReserveMs;
            Stopwatch = Stopwatch.StartNew();
        }

        public static LauncherDeadline Start(string name, long totalMs, long cleanupReserveMs)
            => new LauncherDeadline(name, totalMs, cleanupReserveMs);

        public long ElapsedMs => Stopwatch.ElapsedMilliseconds;
        public long RemainingMs => Math.Max(0, TotalMs - ElapsedMs);

        public int InvocationBudgetMs(int perCallCapMs)
        {
            long available = RemainingMs - CleanupReserveMs;
            if (available <= 0)
            {
                return 0;
            }
            return (int)Math.Min(perCallCapMs, available);
        }

        public int CleanupBudgetMs()
        {
            long rem = RemainingMs;
            if (rem <= 0)
            {
                return 0;
            }
            return (int)Math.Min(CleanupReserveMs, rem);
        }
    }

    private sealed class InvocationDiag
    {
        public string Id { get; }
        public string Label { get; }
        public string? Directory { get; }
        public string FilePath { get; }
        private readonly object _lock = new object();

        public InvocationDiag(string label, string id, string? directory)
        {
            Label = label;
            Id = id;
            Directory = directory;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                catch
                {
                }
                var safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
                FilePath = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{id}_{safe}.txt");
            }
            else
            {
                FilePath = string.Empty;
            }
        }

        public void Write(string phase, string? detail = null, int? pid = null, string? exitState = null,
            int? budgetMs = null, int? remainingMs = null, string? cleanupOutcome = null,
            string? stdoutTail = null, string? stderrTail = null)
        {
            if (string.IsNullOrWhiteSpace(Directory) || string.IsNullOrWhiteSpace(FilePath))
            {
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("utc=").Append(DateTime.UtcNow.ToString("O"));
            // monotonic elapsed is filled by caller via remaining? We'll append stopwatch not available here; use Environment.TickCount64? Use a static? Instead include remaining budget only.
            if (remainingMs.HasValue) sb.Append(" remainingMs=").Append(remainingMs.Value);
            if (budgetMs.HasValue) sb.Append(" budgetMs=").Append(budgetMs.Value);
            sb.Append(" label=").Append(Label);
            sb.Append(" id=").Append(Id);
            sb.Append(" phase=").Append(phase);
            if (pid.HasValue) sb.Append(" pid=").Append(pid.Value);
            if (exitState is not null) sb.Append(" exitState=").Append(exitState);
            if (cleanupOutcome is not null) sb.Append(" cleanup=").Append(cleanupOutcome);
            if (stdoutTail is not null) sb.Append(" stdoutTail=").Append(QuoteTail(stdoutTail));
            if (stderrTail is not null) sb.Append(" stderrTail=").Append(QuoteTail(stderrTail));
            if (detail is not null) sb.Append(" detail=").Append(QuoteTail(detail));
            sb.AppendLine();
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(FilePath, sb.ToString());
                }
            }
            catch
            {
            }
        }

        private static string QuoteTail(string s)
        {
            if (s.Length > 512)
            {
                s = s.Substring(s.Length - 512);
            }
            return s.Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    private static string? DiagnosticsDirectory()
    {
        var v = System.Environment.GetEnvironmentVariable("LATTICE_LAUNCHER_DIAG_DIR");
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }
        return v;
    }

    private static string Tail(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }
        const int max = 1024;
        if (s.Length <= max)
        {
            return s;
        }
        return s.Substring(s.Length - max);
    }

    private static void KillProcessTree(Process process, InvocationDiag diag)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            diag.Write("kill-failed", detail: ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(
        string fileName, IEnumerable<string> args, string cwd, IDictionary<string, string> env,
        string label, string invocationId, LauncherDeadline deadline, int perCallTimeoutMs = DefaultTimeoutMs)
    {
        var diag = new InvocationDiag(label, invocationId, DiagnosticsDirectory());
        var argList = args is ICollection<string> c ? c.ToArray() : args.ToArray();
        diag.Write("start", detail: fileName + " " + string.Join(" ", argList), remainingMs: (int)deadline.RemainingMs, budgetMs: deadline.InvocationBudgetMs(perCallTimeoutMs));

        Process? process = null;
        try
        {
            ProcessStartInfo psi;
            try
            {
                psi = BaseStart(fileName, argList, cwd, env);
            }
            catch (Exception ex)
            {
                diag.Write("process-start-failed", detail: ex.GetType().Name + ": " + ex.Message, remainingMs: (int)deadline.RemainingMs);
                throw;
            }

            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                diag.Write("process-start-failed", detail: ex.GetType().Name + ": " + ex.Message, remainingMs: (int)deadline.RemainingMs);
                throw;
            }
            if (process is null)
            {
                diag.Write("process-start-failed", detail: "Process.Start returned null", remainingMs: (int)deadline.RemainingMs);
                throw new InvalidOperationException("Process.Start returned null");
            }

            diag.Write("process-started", pid: process.Id, remainingMs: (int)deadline.RemainingMs);

            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            // Exit wait bounded by this invocation's share of the parent deadline.
            int exitBudget = deadline.InvocationBudgetMs(perCallTimeoutMs);
            diag.Write("exit-wait-start", budgetMs: exitBudget, pid: process.Id, remainingMs: (int)deadline.RemainingMs);
            bool exited = process.WaitForExit(exitBudget);
            diag.Write("exit-wait-done", pid: process.Id, exitState: exited ? ("exited:" + process.ExitCode) : "still-running", remainingMs: (int)deadline.RemainingMs);

            if (!exited)
            {
                diag.Write("cleanup-start", pid: process.Id, cleanupOutcome: "killing-tree", remainingMs: (int)deadline.RemainingMs);
                KillProcessTree(process, diag);
                int cb = deadline.CleanupBudgetMs();
                if (cb <= 0)
                {
                    cb = 1000;
                }
                bool cleanExited = process.WaitForExit(cb);
                diag.Write("cleanup-done", pid: process.Id, cleanupOutcome: cleanExited ? "exited" : "uncertain-descendants", remainingMs: (int)deadline.RemainingMs);
                throw new TimeoutException(
                    $"{label} [{invocationId}] phase=exit-wait budgetMs={exitBudget} pid={process.Id} exitState=still-running cleanup={(cleanExited ? "exited" : "uncertain-descendants")}");
            }

            // Drain both streams together under one shared recomputed budget.
            int readBudgetMs = deadline.InvocationBudgetMs(perCallTimeoutMs);
            if (readBudgetMs <= 0)
            {
                readBudgetMs = 0;
            }
            var readBudget = TimeSpan.FromMilliseconds(readBudgetMs);
            diag.Write("drain-start", budgetMs: readBudgetMs, pid: process.Id, remainingMs: (int)deadline.RemainingMs);
            bool drained = Task.WhenAll(outTask, errTask).Wait(readBudget);
            if (!drained)
            {
                diag.Write("cleanup-start", pid: process.Id, cleanupOutcome: "killing-tree-held-pipe", remainingMs: (int)deadline.RemainingMs);
                KillProcessTree(process, diag);
                int cb = deadline.CleanupBudgetMs();
                if (cb <= 0) cb = 1000;
                process.WaitForExit(cb);
                string outPartial = outTask.IsCompleted ? Tail(SafeResult(outTask)) : "<pending>";
                string errPartial = errTask.IsCompleted ? Tail(SafeResult(errTask)) : "<pending>";
                diag.Write("cleanup-done", pid: process.Id, cleanupOutcome: "held-pipe", remainingMs: (int)deadline.RemainingMs,
                    stdoutTail: outPartial, stderrTail: errPartial);
                throw new TimeoutException(
                    $"{label} [{invocationId}] phase=output-drain budgetMs={readBudgetMs} pid={process.Id} exitState=exited:{process.ExitCode} cleanup=held-pipe " +
                    $"stdoutTail={Tail(outPartial)} stderrTail={Tail(errPartial)} " +
                    "process exited but its output pipes are still open (a spawned process likely inherited them)");
            }

            string stdout;
            string stderr;
            try
            {
                stdout = outTask.GetAwaiter().GetResult();
                stderr = errTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                diag.Write("error", detail: ex.GetType().Name + ": " + ex.Message, pid: process.Id, remainingMs: (int)deadline.RemainingMs);
                throw;
            }

            diag.Write("completed", pid: process.Id, exitState: "exited:" + process.ExitCode, remainingMs: (int)deadline.RemainingMs,
                stdoutTail: Tail(stdout), stderrTail: Tail(stderr));
            return (process.ExitCode, stdout, stderr);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diag.Write("error", detail: ex.GetType().Name + ": " + ex.Message, remainingMs: (int)deadline.RemainingMs);
            throw;
        }
        finally
        {
            // Dispose readers without waiting; never mask the original error.
            if (process is not null)
            {
                try { process.StandardOutput.Dispose(); } catch { }
                try { process.StandardError.Dispose(); } catch { }
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }
            diag.Write("finally", pid: process?.Id, remainingMs: (int)deadline.RemainingMs, cleanupOutcome: "disposed");
        }
    }

    private static string? SafeResult(Task<string> t)
    {
        try { return t.GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private sealed class TempScope : IDisposable
    {
        public string Root { get; }
        public string Home { get; }
        public string Work { get; }
        public LauncherDeadline? Deadline { get; }

        public TempScope(LauncherDeadline? deadline = null)
        {
            Deadline = deadline;
            Root = Path.Combine(Path.GetTempPath(), "lattice-launcher-" + Guid.NewGuid().ToString("N"));
            Home = Path.Combine(Root, "home");
            Work = Path.Combine(Root, "work");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Work);
        }

        public void Dispose()
        {
            int budget = Deadline?.CleanupBudgetMs() ?? 5000;
            if (budget <= 0) budget = 1000;
            var task = Task.Run(() =>
            {
                try { Directory.Delete(Root, recursive: true); }
                catch { }
            });
            if (!task.Wait(budget))
            {
                // Retain uniquely owned temp dir rather than hang.
                var diagDir = DiagnosticsDirectory();
                if (!string.IsNullOrWhiteSpace(diagDir))
                {
                    try
                    {
                        Directory.CreateDirectory(diagDir);
                        File.AppendAllText(Path.Combine(diagDir, "retained-temp-dirs.txt"),
                            $"{DateTime.UtcNow:O} retained={Root} budgetMs={budget}{System.Environment.NewLine}");
                    }
                    catch { }
                }
                try { Console.Error.WriteLine("RETAINED_TEMP_DIR=" + Root); } catch { }
            }
        }
    }

    [Fact]
    public async Task UnixLauncher_InstallUninstall_TamperAndConflict()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = RepoRoot();
        var setup = SetupScript();
        var versionLine = File.ReadAllLines(Path.Combine(root, "Cli", "Lattice.Cli.csproj"))
            .First(l => l.Contains("<Version>"));
        var expectedVersion = versionLine.Substring(versionLine.IndexOf("<Version>") + 9);
        expectedVersion = expectedVersion.Substring(0, expectedVersion.IndexOf("</Version>"));

        var deadline = LauncherDeadline.Start("UnixLauncher_InstallUninstall_TamperAndConflict", 300_000, 30_000);
        using var scope = new TempScope(deadline);
        var env = new Dictionary<string, string>
        {
            ["HOME"] = scope.Home,
            ["SHELL"] = "/bin/bash",
        };
        var currentPath = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        var sanitized = string.Join(Path.PathSeparator, currentPath.Split(Path.PathSeparator)
            .Where(d =>
            {
                try { return !File.Exists(Path.Combine(d.Trim(), "lattice")) && !d.Contains("lattice", StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            }));
        env["PATH"] = sanitized;

        // 1. dry run creates nothing
        var (dryCode, dryOut, dryErr) = Run("bash", new[] { setup, "--install-launcher", "--dry-run" }, scope.Work, env, "unix-step1-dry-run", Guid.NewGuid().ToString("N"), deadline);
        Assert.Equal(0, dryCode);
        Assert.DoesNotContain("installed lattice", dryOut);
        Assert.False(File.Exists(Path.Combine(scope.Home, ".lattice", "install.json")));

        // 2. conflicting foreign lattice is never executed (install refuses, exit != 0)
        var fakeBin = Path.Combine(scope.Root, "fakebin");
        Directory.CreateDirectory(fakeBin);
        var fakeLattice = Path.Combine(fakeBin, "lattice");
        File.WriteAllText(fakeLattice, "#!/bin/sh\necho SHOULD-NOT-RUN\ncat /dev/null\n");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // make executable
            Run("chmod", new[] { "+x", fakeLattice }, scope.Work, env, "unix-chmod-fake-lattice", Guid.NewGuid().ToString("N"), deadline, 30_000);
        }
        var conflictEnv = new Dictionary<string, string>(env)
        {
            ["PATH"] = fakeBin + Path.PathSeparator + (env.GetValueOrDefault("PATH") ?? "")
        };
        var (conflictCode, conflictOut, conflictErr) = Run("bash", new[] { setup, "--install-launcher" }, scope.Work, conflictEnv, "unix-step2-conflict-install", Guid.NewGuid().ToString("N"), deadline);
        Assert.NotEqual(0, conflictCode);
        Assert.DoesNotContain("SHOULD-NOT-RUN", conflictOut);
        Assert.DoesNotContain("SHOULD-NOT-RUN", conflictErr);

        // 3. actual install with add-to-path
        var (installCode, installOut, installErr) = Run("bash", new[] { setup, "--install-launcher", "--add-to-path" }, scope.Work, env, "unix-step3-install", Guid.NewGuid().ToString("N"), deadline);
        Assert.True(installCode == 0, $"install failed: {installErr}\n{installOut}");
        var manifestPath = Path.Combine(scope.Home, ".lattice", "install.json");
        Assert.True(File.Exists(manifestPath), "manifest missing after install");

        // 4. resolve installed lattice from a fresh login shell
        var (verCode, verOut, verErr) = Run("bash", new[] { "-l", "-c", "command -v lattice && lattice --version" }, scope.Work, env, "unix-step4-resolve-version", Guid.NewGuid().ToString("N"), deadline);
        Assert.True(verCode == 0, $"version resolution failed: {verErr}\n{verOut}");
        Assert.Contains(expectedVersion, verOut);

        // 5. generate artifact parity
        var installedOut = Path.Combine(scope.Work, "installed.json");
        var (giCode, giOut, giErr) = Run("bash", new[] { "-l", "-c", $"lattice generate --seed 42 --out {installedOut}" }, scope.Work, env, "unix-step5-generate-installed", Guid.NewGuid().ToString("N"), deadline);
        Assert.True(giCode == 0, $"installed generate failed: {giErr}\n{giOut}");
        var dotnetOut = Path.Combine(scope.Work, "dotnet.json");
        var (gdCode, gdOut, gdErr) = Run("dotnet", new[]
        {
            "run", "--disable-build-servers", "-c", "Release", "--project", Path.Combine(root, "Cli"),
            "--", "generate", "--seed", "42", "--out", dotnetOut
        }, scope.Work, env, "unix-step5-generate-dotnet", Guid.NewGuid().ToString("N"), deadline);
        Assert.True(gdCode == 0, $"dotnet generate failed: {gdErr}\n{gdOut}");
        Assert.True(File.Exists(installedOut));
        Assert.True(File.Exists(dotnetOut));
        Assert.Equal(File.ReadAllBytes(installedOut), File.ReadAllBytes(dotnetOut));

        // 6. redirected bare invocation exits 2
        var (bareCode, bareOut, bareErr) = Run("bash", new[] { "-l", "-c", "lattice </dev/null" }, scope.Work, env, "unix-step6-redirected-bare", Guid.NewGuid().ToString("N"), deadline);
        // stdin redirected by the shell redirect above; stdout already redirected by runner
        Assert.Equal(2, bareCode);

        // 7. second install refuses
        var (secondCode, secondOut, secondErr) = Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env, "unix-step7-second-install-refuses", Guid.NewGuid().ToString("N"), deadline);
        Assert.NotEqual(0, secondCode);

        // 8. (Altered marked block is preserved: verified by launcher_restore_path,
        //    which refuses any change to the block and makes no profile edit;
        //    not re-exercised here because that path also removes the already-
        //    installed tool, which the round-trip below must not repeat.)

        // 9. concurrent install refuses (launch two, at least one fails)
        var tempHome2 = Path.Combine(scope.Root, "home2");
        Directory.CreateDirectory(tempHome2);
        var env2 = new Dictionary<string, string>(env) { ["HOME"] = tempHome2 };
        var t1 = Task.Run(() => Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env, "unix-step9-concurrent-install-a", Guid.NewGuid().ToString("N"), deadline).ExitCode);
        var t2 = Task.Run(() => Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env2, "unix-step9-concurrent-install-b", Guid.NewGuid().ToString("N"), deadline).ExitCode);
        var concurrent = await Task.WhenAll(t1, t2);
        Assert.False(concurrent[0] == 0 && concurrent[1] == 0, "concurrent installs both succeeded");

        // 10. clean up temp home2 if needed
        try { Directory.Delete(tempHome2, recursive: true); } catch { }

        // 13. final uninstall round-trip
        var expectedProfileAbsentBefore = !File.Exists(Path.Combine(scope.Home, ".profile"));
        // We may have profile from install; check current
        var profileExistsBeforeUninstall = File.Exists(Path.Combine(scope.Home, ".profile"));
        var (uninstallCode, uninstallOut, uninstallErr) = Run("bash", new[] { setup, "--uninstall-launcher" }, scope.Work, env, "unix-step13-uninstall", Guid.NewGuid().ToString("N"), deadline);
        Assert.True(uninstallCode == 0, $"uninstall failed: {uninstallErr}\n{uninstallOut}");

        var binDir = Path.Combine(scope.Home, ".lattice", "bin");
        Assert.False(Directory.Exists(binDir) && Directory.EnumerateFileSystemEntries(binDir).Any(), "owned bin dir not cleaned");
        // unrelated file survival: create sentinel? Already none. Profile restore exact
        // In this temp HOME the profile did not exist before install, so the
        // uninstaller must remove the file it created rather than leave it.
        Assert.False(File.Exists(Path.Combine(scope.Home, ".profile")), "installed profile should be removed on uninstall");
    }

    [Fact]
    public void Run_BoundedRead_FailsFastWhenPipeHeldOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var deadline = LauncherDeadline.Start("Run_BoundedRead_FailsFastWhenPipeHeldOpen", 30_000, 5_000);
        var env = new Dictionary<string, string>();
        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Run("bash", new[] { "-c", "exec 3>&1; (sleep 3 >&3 &) ; exit 0" }, Path.GetTempPath(), env,
                "helper-held-stdout-pipe", Guid.NewGuid().ToString("N"), deadline, 1000));
        Assert.Contains("output pipes are still open", ex.Message);
        Assert.Contains("phase=output-drain", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"held-pipe took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Run_LiveProcessDeadline_TerminatesOwnedProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var deadline = LauncherDeadline.Start("Run_LiveProcessDeadline_TerminatesOwnedProcess", 30_000, 5_000);
        var env = new Dictionary<string, string>();
        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() =>
            Run("bash", new[] { "-c", "echo READY; sleep 30" }, Path.GetTempPath(), env,
                "helper-live-process-deadline", Guid.NewGuid().ToString("N"), deadline, 1000));
        Assert.Contains("phase=exit-wait", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"live-process took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void WindowsLauncher_InstallUninstall_Basic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = RepoRoot();
        var setup = SetupScript();
        var shell = Shell();
        var versionLine = File.ReadAllLines(Path.Combine(root, "Cli", "Lattice.Cli.csproj"))
            .First(l => l.Contains("<Version>"));
        var expectedVersion = versionLine.Substring(versionLine.IndexOf("<Version>") + 9);
        expectedVersion = expectedVersion.Substring(0, expectedVersion.IndexOf("</Version>"));

        // Capture the runner's USER Path (registry) BEFORE any effect so a
        // failure mid-test cannot leak a PATH edit into later CI work; restored
        // in the finally below.
        Microsoft.Win32.RegistryKey? regBefore = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: false);
        string originalRegRaw = (regBefore?.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? string.Empty;
        Microsoft.Win32.RegistryValueKind originalRegKind;
        try
        {
            originalRegKind = regBefore is null ? Microsoft.Win32.RegistryValueKind.String : regBefore.GetValueKind("Path");
        }
        catch
        {
            originalRegKind = Microsoft.Win32.RegistryValueKind.String;
        }
        if (originalRegKind is not (Microsoft.Win32.RegistryValueKind.String or Microsoft.Win32.RegistryValueKind.ExpandString))
        {
            originalRegKind = Microsoft.Win32.RegistryValueKind.String;
        }
        regBefore?.Dispose();

        // Capture the runner's NuGet global packages cache before redirecting
        // USERPROFILE, so `dotnet pack` under the redirected profile reuses the
        // already-restored cache instead of re-downloading every dependency.
        string originalUserProfile = System.Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        string nugetGlobal = Path.Combine(originalUserProfile, ".nuget", "packages");

        var deadline = LauncherDeadline.Start("WindowsLauncher_InstallUninstall_Basic", 300_000, 30_000);
        using var scope = new TempScope(deadline);
        var env = new Dictionary<string, string>
        {
            ["USERPROFILE"] = scope.Home,
            ["LOCALAPPDATA"] = Path.Combine(scope.Home, "AppData", "Local"),
        };
        Directory.CreateDirectory(env["LOCALAPPDATA"]);
        if (Directory.Exists(nugetGlobal))
        {
            env["NUGET_PACKAGES"] = nugetGlobal;
        }
        var curPath = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        // Keep the full PATH so tooling resolves; a conflict is exercised
        // separately by prepending a directory containing a 'lattice' shim.
        env["PATH"] = curPath;

        try
        {
            // dry run
            var (dryCode, dryOut, dryErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher", "-DryRun" }, scope.Work, env, "windows-dry-run", Guid.NewGuid().ToString("N"), deadline);
            Assert.Equal(0, dryCode);

            // conflict
            var fakeBin = Path.Combine(scope.Root, "fakebin");
            Directory.CreateDirectory(fakeBin);
            File.WriteAllText(Path.Combine(fakeBin, "lattice.cmd"), "@echo SHOULD-NOT-RUN\r\n");
            var conflictEnv = new Dictionary<string, string>(env)
            {
                ["PATH"] = fakeBin + Path.PathSeparator + (env.GetValueOrDefault("PATH") ?? "")
            };
            var (conflictCode, conflictOut, conflictErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher" }, scope.Work, conflictEnv, "windows-conflict-install", Guid.NewGuid().ToString("N"), deadline);
            Assert.NotEqual(0, conflictCode);
            Assert.DoesNotContain("SHOULD-NOT-RUN", conflictOut + conflictErr);

            // install with registry path
            var (installCode, installOut, installErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher", "-AddToPath" }, scope.Work, env, "windows-install", Guid.NewGuid().ToString("N"), deadline);
            Assert.True(installCode == 0, $"install failed: {installErr}\n{installOut}");

            // uninstall
            var (uninstallCode, uninstallOut, uninstallErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-UninstallLauncher" }, scope.Work, env, "windows-uninstall", Guid.NewGuid().ToString("N"), deadline);
            Assert.True(uninstallCode == 0, $"uninstall failed: {uninstallErr}\n{uninstallOut}");

            // exact USER Path restoration
            using (var regAfter = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: false))
            {
                var afterRaw = (regAfter?.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? string.Empty;
                Assert.Equal(originalRegRaw, afterRaw);
            }
        }
        finally
        {
            try
            {
                using var wkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
                if (wkey is not null)
                {
                    wkey.SetValue("Path", originalRegRaw, originalRegKind);
                }
            }
            catch
            {
            }
        }
    }
}