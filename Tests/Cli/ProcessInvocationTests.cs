using System.Diagnostics;
using System.Text.Json.Nodes;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage 4: the console runs every command as a child process of the same program.
/// Invocation resolution (host vs apphost), argument fidelity through
/// <c>ProcessStartInfo.ArgumentList</c>, byte parity with a plain shell run, the result
/// buffer bound, watch-screen inheritance, and whole-tree cancel with no leftover agent.
/// These tests really spawn children (not dry runs) and run on all three CI operating
/// systems; every install-adjacent assertion here uses temp directories and never the
/// real profile, PATH or tools.
/// </summary>
public class ProcessInvocationTests
{
    [Fact]
    public void HostFormRunsDotnetWithTheDllFirst()
    {
        var invocation = ProcessInvocationResolver.ForPaths("/usr/local/bin/dotnet", "/app/lattice.dll");

        Assert.Equal("/usr/local/bin/dotnet", invocation.FileName);
        Assert.Equal(["/app/lattice.dll"], invocation.PrefixArguments);
    }

    [Fact]
    public void WindowsHostBackslashesDecideTheSameWay()
    {
        var invocation = ProcessInvocationResolver.ForPaths("C:\\Program Files\\dotnet\\dotnet.exe", "C:\\app\\lattice.dll");

        Assert.Equal("C:\\Program Files\\dotnet\\dotnet.exe", invocation.FileName);
        Assert.Equal(["C:\\app\\lattice.dll"], invocation.PrefixArguments);
    }

    [Fact]
    public void ApphostFormRunsItselfWithNoPrefix()
    {
        var invocation = ProcessInvocationResolver.ForPaths("/home/u/.lattice/bin/lattice", "/home/u/.lattice/bin/lattice.dll");

        Assert.Equal("/home/u/.lattice/bin/lattice", invocation.FileName);
        Assert.Empty(invocation.PrefixArguments);
    }

    [Fact]
    public void MissingProcessPathFallsBackToTheAssembly()
    {
        Assert.Equal("/app/lattice.dll", ProcessInvocationResolver.ForPaths(null, "/app/lattice.dll").FileName);
        Assert.Equal("/app/lattice.dll", ProcessInvocationResolver.ForPaths("", "/app/lattice.dll").FileName);
    }

    [Fact]
    public void ShortOutputIsKeptWhole()
    {
        Assert.Equal(("abc", false), SlashConsoleProcessRunner.BoundOutput("abc"));
    }

    [Fact]
    public void OutputAtExactlyTheBoundIsNotTruncated()
    {
        var text = new string('x', SlashConsoleProcessRunner.CapturedMaxCharsPerStream);

        Assert.Equal((text, false), SlashConsoleProcessRunner.BoundOutput(text));
    }

    [Fact]
    public void OutputPastTheBoundIsDroppedAndReported()
    {
        var text = new string('x', SlashConsoleProcessRunner.CapturedMaxCharsPerStream + 1);
        var (bounded, truncated) = SlashConsoleProcessRunner.BoundOutput(text);

        Assert.True(truncated);
        Assert.Equal(SlashConsoleProcessRunner.CapturedMaxCharsPerStream, bounded.Length);
    }

    [Theory]
    [InlineData("generate", "--seed", "42")]
    [InlineData("--help")]
    [InlineData("frobnicate")]
    public void CapturedBytesAndExitEqualAPlainInProcessRun(params string[] argv)
    {
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();
        var invocation = ChildInvocation();

        var child = runner.StartCaptured(new CapturedRunRequest(argv, "lattice " + string.Join(' ', argv), temp.Path, ChildInvocation()));
        var finished = child.Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
        Assert.False(finished.Cancelled);
        Assert.False(finished.Truncated);
        Assert.True(invocation.FileName.Length > 0);
    }

    [Fact]
    public void OutArtifactsWithSpacesMatchAndLandInTheWorkingDirectory()
    {
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();
        var childOut = "map file.json";
        var directOut = Path.Combine(temp.Path, "map direct.json");

        var child = runner.StartCaptured(new CapturedRunRequest(
            ["generate", "--seed", "42", "--out", childOut], "lattice generate", temp.Path, ChildInvocation()));
        var finished = child.Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(["generate", "--seed", "42", "--out", directOut], stdout, stderr);

        Assert.Equal(0, finished.ExitCode);
        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal("", stdout.ToString());
        Assert.Equal("", finished.Stdout);
        Assert.Equal($"wrote {childOut}{System.Environment.NewLine}", finished.Stderr);
        Assert.StartsWith("wrote ", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temp.Path, childOut)), "the child resolves a relative --out in its working directory.");
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(temp.Path, childOut)),
            File.ReadAllBytes(directOut));
    }

    [Fact]
    public void ValidateScenarioErrorParity()
    {
        using var temp = new TempDir();
        var bad = Path.Combine(temp.Path, "bad.json");
        File.WriteAllText(bad, "{ invalid");
        var argv = new[] { "validate-scenario", bad };

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(1, finished.ExitCode);
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--flag=value with spaces")]
    [InlineData("--we\"ird")]
    public void AwkwardValuesReachTheChildUnchanged(string awkward)
    {
        using var temp = new TempDir();
        string[] argv = ["generate", "--seed", "42", awkward];

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice generate", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        // An empty seed, a --flag=value with spaces, and a quote all fail parsing the
        // same way in both: any ArgumentList mangling (dropped empties, re-split spaces,
        // stripped quotes) would parse differently and break this equality.
        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(2, finished.ExitCode);
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
    }

    [Fact]
    public void EmptySeedValueIsNotDropped()
    {
        using var temp = new TempDir();
        string[] argv = ["generate", "--seed", "", "--out", "x.jsonl"];

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice generate", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(2, finished.ExitCode);
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
    }

    [Fact]
    public void UnicodePathsReachTheChildUnchanged()
    {
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Path, "täst-😀.json");
        string[] argv = ["validate-scenario", missing];

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
        Assert.Contains("täst-😀.json", finished.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchRunsInheritedAndReportsTheExit()
    {
        // --version works on redirected streams too, so this proves the inherited
        // plumbing (no redirect, shared working directory) without needing a TTY.
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();

        var result = runner.RunWatch(new WatchRunRequest(["--version"], "lattice --version", temp.Path, ChildInvocation()));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void EvaluateMatchesExceptCreatedAtUtc()
    {
        using var temp = new TempDir();
        string[] argv = ["evaluate", "--seed-set", "dev", "--seeds", "2", "--rollouts", "1", "--scenario", "standard"];
        var runner = new SlashConsoleProcessRunner();

        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice evaluate", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.Equal(exit, finished.ExitCode);
        Assert.Equal(0, finished.ExitCode);
        Assert.Equal(NormalizeArtifact(stdout.ToString()), NormalizeArtifact(finished.Stdout));
    }

    [Fact]
    public void CancellingEvaluateLeavesNoAgentProcess()
    {
        var python = FindPython();
        Assert.False(string.IsNullOrEmpty(python), "a python on PATH is required for the agent helper.");

        using var temp = new TempDir();
        var helper = Path.Combine(temp.Path, "sleeper.py");
        var pids = Path.Combine(temp.Path, "pids.txt");
        File.WriteAllText(helper,
            "import sys, os, time\n" +
            "open(sys.argv[1], \"a\").write(str(os.getpid()) + \"\\n\")\n" +
            "time.sleep(300)\n");

        var agentCmd = $"\"{python}\" \"{helper}\" \"{pids}\"";
        string[] argv = ["evaluate", "--seed-set", "dev", "--seeds", "50", "--agent-cmd", agentCmd, "--agent-step-timeout-ms", "2000"];
        var runner = new SlashConsoleProcessRunner();
        var run = runner.StartCaptured(new CapturedRunRequest(argv, "lattice evaluate", temp.Path, ChildInvocation()));

        try
        {
            Assert.True(WaitForFile(pids, TimeSpan.FromSeconds(60)), "the agent never started; cannot prove tree-kill.");
            var agentPids = File.ReadAllLines(pids).Select(line => int.Parse(line.Trim())).ToList();
            Assert.NotEmpty(agentPids);
            foreach (var pid in agentPids)
            {
                AssertAgentAlive(pid);
            }

            Thread.Sleep(TimeSpan.FromSeconds(1));
            run.Cancel();
            var finished = run.Finish();

            Assert.True(finished.Cancelled, "cancelling must report cancelled.");
            foreach (var pid in File.ReadAllLines(pids).Select(line => int.Parse(line.Trim())))
            {
                AssertAgentDead(pid);
            }
        }
        finally
        {
            if (!run.IsDone)
            {
                run.Cancel();
            }

            run.Finish();
        }
    }

    private static string NormalizeArtifact(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("CreatedAtUtc");
        return node.ToJsonString();
    }

    private static ProcessInvocation ChildInvocation() =>
        ProcessInvocationResolver.ForPaths("dotnet", LatticeDll());

    private static string LatticeDll()
    {
        // Tests/bin/<config>/net10.0 -> repo/Cli/bin/<config>/net10.0/lattice.dll.
        var baseDir = AppContext.BaseDirectory;
        var config = Directory.GetParent(baseDir)?.Parent?.Name ?? "Release";
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root, "Lattice.sln")))
        {
            root = Directory.GetParent(root)?.FullName;
        }

        Assert.True(root is not null, "could not locate the repository root.");
        var dll = Path.Combine(root!, "Cli", "bin", config, "net10.0", "lattice.dll");
        Assert.True(File.Exists(dll), $"expected a built CLI at {dll}; build in {config} first.");
        return dll;
    }

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }

                    if (OperatingSystem.IsWindows())
                    {
                        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                        {
                            if (File.Exists(full + ext))
                            {
                                return full + ext;
                            }
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static bool WaitForFile(string path, TimeSpan budget)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < budget)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(200));
        }

        return false;
    }

    private static void AssertAgentAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited, $"agent process {pid} died before cancel; tree-kill unproven.");
        }
        catch (ArgumentException)
        {
            Assert.Fail($"agent process {pid} died before cancel; tree-kill unproven.");
        }
    }

    private static void AssertAgentDead(int pid)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(200));
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"agent process {pid} survived the whole-tree kill.");
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class TempDir : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lattice-proc-" + Guid.NewGuid().ToString("N"));

        internal TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
