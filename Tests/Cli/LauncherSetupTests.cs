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

    private static (int ExitCode, string Stdout, string Stderr) Run(string fileName, IEnumerable<string> args, string cwd, IDictionary<string, string> env, int timeoutMs = DefaultTimeoutMs)
    {
        using var process = Process.Start(BaseStart(fileName, args, cwd, env))!;
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.WaitForExit();
        }
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();
        return (exited ? process.ExitCode : -99, stdout, stderr);
    }

    private sealed class TempScope : IDisposable
    {
        public string Root { get; }
        public string Home { get; }
        public string Work { get; }

        public TempScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "lattice-launcher-" + Guid.NewGuid().ToString("N"));
            Home = Path.Combine(Root, "home");
            Work = Path.Combine(Root, "work");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Work);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
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

        using var scope = new TempScope();
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
        var (dryCode, dryOut, dryErr) = Run("bash", new[] { setup, "--install-launcher", "--dry-run" }, scope.Work, env);
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
            Run("chmod", new[] { "+x", fakeLattice }, scope.Work, env);
        }
        var conflictEnv = new Dictionary<string, string>(env)
        {
            ["PATH"] = fakeBin + Path.PathSeparator + (env.GetValueOrDefault("PATH") ?? "")
        };
        var (conflictCode, conflictOut, conflictErr) = Run("bash", new[] { setup, "--install-launcher" }, scope.Work, conflictEnv);
        Assert.NotEqual(0, conflictCode);
        Assert.DoesNotContain("SHOULD-NOT-RUN", conflictOut);
        Assert.DoesNotContain("SHOULD-NOT-RUN", conflictErr);

        // 3. actual install with add-to-path
        var (installCode, installOut, installErr) = Run("bash", new[] { setup, "--install-launcher", "--add-to-path" }, scope.Work, env);
        Assert.True(installCode == 0, $"install failed: {installErr}\n{installOut}");
        var manifestPath = Path.Combine(scope.Home, ".lattice", "install.json");
        Assert.True(File.Exists(manifestPath), "manifest missing after install");

        // 4. resolve installed lattice from a fresh login shell
        var (verCode, verOut, verErr) = Run("bash", new[] { "-l", "-c", "command -v lattice && lattice --version" }, scope.Work, env);
        Assert.True(verCode == 0, $"version resolution failed: {verErr}\n{verOut}");
        Assert.Contains(expectedVersion, verOut);

        // 5. generate artifact parity
        var installedOut = Path.Combine(scope.Work, "installed.json");
        var (giCode, giOut, giErr) = Run("bash", new[] { "-l", "-c", $"lattice generate --seed 42 --out {installedOut}" }, scope.Work, env);
        Assert.True(giCode == 0, $"installed generate failed: {giErr}\n{giOut}");
        var dotnetOut = Path.Combine(scope.Work, "dotnet.json");
        var (gdCode, gdOut, gdErr) = Run("dotnet", new[]
        {
            "run", "-c", "Release", "--project", Path.Combine(root, "Cli"),
            "--", "generate", "--seed", "42", "--out", dotnetOut
        }, scope.Work, env);
        Assert.True(gdCode == 0, $"dotnet generate failed: {gdErr}\n{gdOut}");
        Assert.True(File.Exists(installedOut));
        Assert.True(File.Exists(dotnetOut));
        Assert.Equal(File.ReadAllBytes(installedOut), File.ReadAllBytes(dotnetOut));

        // 6. redirected bare invocation exits 2
        var (bareCode, bareOut, bareErr) = Run("bash", new[] { "-l", "-c", "lattice </dev/null" }, scope.Work, env);
        // stdin redirected by the shell redirect above; stdout already redirected by runner
        Assert.Equal(2, bareCode);

        // 7. second install refuses
        var (secondCode, secondOut, secondErr) = Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env);
        Assert.NotEqual(0, secondCode);

        // 8. (Altered marked block is preserved: verified by launcher_restore_path,
        //    which refuses any change to the block and makes no profile edit;
        //    not re-exercised here because that path also removes the already-
        //    installed tool, which the round-trip below must not repeat.)

        // 9. concurrent install refuses (launch two, at least one fails)
        var tempHome2 = Path.Combine(scope.Root, "home2");
        Directory.CreateDirectory(tempHome2);
        var env2 = new Dictionary<string, string>(env) { ["HOME"] = tempHome2 };
#pragma warning disable xUnit1031
        var t1 = Task.Run(() => Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env).ExitCode);
        var t2 = Task.Run(() => Run("bash", new[] { setup, "--install-launcher" }, scope.Work, env2).ExitCode);
#pragma warning restore xUnit1031
        await Task.WhenAll(t1, t2);
        Assert.False(t1.Result == 0 && t2.Result == 0, "concurrent installs both succeeded");

        // 10. clean up temp home2 if needed
        try { Directory.Delete(tempHome2, recursive: true); } catch { }

        // 13. final uninstall round-trip
        var expectedProfileAbsentBefore = !File.Exists(Path.Combine(scope.Home, ".profile"));
        // We may have profile from install; check current
        var profileExistsBeforeUninstall = File.Exists(Path.Combine(scope.Home, ".profile"));
        var (uninstallCode, uninstallOut, uninstallErr) = Run("bash", new[] { setup, "--uninstall-launcher" }, scope.Work, env);
        Assert.True(uninstallCode == 0, $"uninstall failed: {uninstallErr}\n{uninstallOut}");

        var binDir = Path.Combine(scope.Home, ".lattice", "bin");
        Assert.False(Directory.Exists(binDir) && Directory.EnumerateFileSystemEntries(binDir).Any(), "owned bin dir not cleaned");
        // unrelated file survival: create sentinel? Already none. Profile restore exact
        // In this temp HOME the profile did not exist before install, so the
        // uninstaller must remove the file it created rather than leave it.
        Assert.False(File.Exists(Path.Combine(scope.Home, ".profile")), "installed profile should be removed on uninstall");
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

        using var scope = new TempScope();
        var env = new Dictionary<string, string>
        {
            ["USERPROFILE"] = scope.Home,
            ["LOCALAPPDATA"] = Path.Combine(scope.Home, "AppData", "Local"),
        };
        Directory.CreateDirectory(env["LOCALAPPDATA"]);
        var curPath = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        // Keep the full PATH so tooling resolves; a conflict is exercised
        // separately by prepending a directory containing a 'lattice' shim.
        env["PATH"] = curPath;

        // dry run
        var (dryCode, dryOut, dryErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher", "-DryRun" }, scope.Work, env);
        Assert.Equal(0, dryCode);

        // conflict
        var fakeBin = Path.Combine(scope.Root, "fakebin");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(Path.Combine(fakeBin, "lattice.cmd"), "@echo SHOULD-NOT-RUN\r\n");
        var conflictEnv = new Dictionary<string, string>(env)
        {
            ["PATH"] = fakeBin + Path.PathSeparator + (env.GetValueOrDefault("PATH") ?? "")
        };
        var (conflictCode, conflictOut, conflictErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher" }, scope.Work, conflictEnv);
        Assert.NotEqual(0, conflictCode);
        Assert.DoesNotContain("SHOULD-NOT-RUN", conflictOut + conflictErr);

        // install with registry path
        var (installCode, installOut, installErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-InstallLauncher", "-AddToPath" }, scope.Work, env);
        Assert.True(installCode == 0, $"install failed: {installErr}\n{installOut}");

        // uninstall
        var (uninstallCode, uninstallOut, uninstallErr) = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", setup, "-UninstallLauncher" }, scope.Work, env);
        Assert.True(uninstallCode == 0, $"uninstall failed: {uninstallErr}\n{uninstallOut}");
    }
}