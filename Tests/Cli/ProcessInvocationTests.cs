using System.Diagnostics;
using System.Text;
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
/// real profile, PATH or tools. Child processes launch the CLI copy MSBuild deploys
/// beside the test assembly (the build under test) rather than whatever a previous
/// build left in the CLI project's own bin folder, which in-suite builds can rewrite.
/// </summary>
public class ProcessInvocationTests
{
    [Fact]
    public void ChildLaunchesFromTheTestOutputDirectory()
    {
        // Structural guard: the child must launch from the CLI copy MSBuild
        // deploys beside the test assembly, not from the CLI project's own
        // bin tree that in-suite builds can rewrite.
        var invocation = ChildInvocation();
        var dll = Assert.Single(invocation.PrefixArguments);
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        var fullDll = Path.GetFullPath(dll);
        Assert.StartsWith(baseDir, fullDll, StringComparison.Ordinal);
        Assert.True(File.Exists(fullDll), $"expected a built CLI at {fullDll}.");
    }

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

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
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

        Assert.True(finished.ExitCode == 0, $"expected exit 0 but got {finished.ExitCode}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
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

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 1, $"expected exit 1 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
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
        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 2, $"expected exit 2 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
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

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 2, $"expected exit 2 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
    }

    [Fact]
    public void MissingNonBmpPathMatchesAPlainRedirectedRun()
    {
        // A missing non-BMP path: the exit code and stdout are encoding-free and
        // must match the in-process run. Stderr is compared against a CONTROL run
        // (the same invocation started plainly, same redirects, same ArgumentList,
        // no encoding overrides) — that is what "bytes equal a plain redirected
        // run" means. Argv fidelity itself is proven losslessly by
        // ArgumentListTransmitsEveryByteLosslessly and
        // GenerateWritesUnicodeNamedArtifactsByteIdentical; replacement of
        // characters the code page cannot represent on a redirected Windows
        // console is existing CLI behaviour, not changed by this console.
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Path, "täst-😀.json");
        string[] argv = ["validate-scenario", missing];

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 1, $"expected exit 1 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
        Assert.Equal(stdout.ToString(), finished.Stdout);

        var control = ControlRun(argv, temp.Path);
        Assert.True(control.ExitCode == finished.ExitCode, $"control/child exit mismatch: control={control.ExitCode} child={finished.ExitCode} in-process={exit}\n--- control stdout ---\n{control.Stdout}\n--- control stderr ---\n{control.Stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
        Assert.Equal(control.Stdout, finished.Stdout);
        Assert.Equal(control.Stderr, finished.Stderr);
    }

    [Fact]
    public void BmpUnicodePathMatchesInProcessRun()
    {
        // BMP umlauts are representable on the redirected consoles of the CI
        // matrix (rows 437/1252 on en-US Windows, UTF-8 elsewhere), so the full
        // stderr bytes must match the in-process run here.
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Path, "täst-ü.json");
        string[] argv = ["validate-scenario", missing];

        var runner = new SlashConsoleProcessRunner();
        var finished = runner.StartCaptured(new CapturedRunRequest(argv, "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(argv, stdout, stderr);

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 1, $"expected exit 1 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
        Assert.Equal(stdout.ToString(), finished.Stdout);
        Assert.Equal(stderr.ToString(), finished.Stderr);
        Assert.Contains("täst-ü.json", finished.Stderr, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ProbeCases()
    {
        yield return new object[] { new[] { "" } };
        yield return new object[] { new[] { "--flag=value with spaces" } };
        yield return new object[] { new[] { "a\"b" } };
        yield return new object[] { new[] { "dir\\sub dir\\file.json" } };
        yield return new object[] { new[] { "täst-😀.json" } };
        yield return new object[] { new[] { "日本語テスト.json" } };
        yield return new object[] { new[] { "\U0001D4B3abc" } };
        yield return new object[] { new[] { "C:\\Program Files\\agent\\bin\\" } };
        yield return new object[] { new[] { "alpha", "", "beta gamma", "täst-😀", "--flag=value with spaces" } };
    }

    [Theory]
    [MemberData(nameof(ProbeCases))]
    public void ArgumentListTransmitsEveryByteLosslessly(string[] argv)
    {
        // No console encoding anywhere in this path: the probe writes the UTF-8
        // hex of each argument it received into a result file, and the file is
        // read back and compared element by element. An empty argument stays the
        // distinct empty string and a dropped or re-split argument changes the
        // array length, so any ArgumentList mangling fails here on any OS.
        var python = FindPython();
        Assert.False(string.IsNullOrEmpty(python), "a python on PATH is required for the probe.");
        RequirePython3(python!);

        using var temp = new TempDir();
        var probe = Path.Combine(temp.Path, "probe.py");
        var result = Path.Combine(temp.Path, "result.json");
        File.WriteAllText(probe,
            "import sys, json\n" +
            "out = sys.argv[1]\n" +
            "arr = [a.encode(\"utf-8\", errors=\"strict\").hex() for a in sys.argv[2:]]\n" +
            "open(out, \"w\", encoding=\"utf-8\").write(json.dumps(arr))\n");

        var runner = new SlashConsoleProcessRunner();
        var args = new[] { probe, result }.Concat(argv).ToArray();
        var finished = runner.StartCaptured(new CapturedRunRequest(
            args, "probe", temp.Path, new ProcessInvocation(python!, []))).Finish();

        Assert.Equal(0, finished.ExitCode);
        var actual = JsonNode.Parse(File.ReadAllText(result))!.AsArray();
        Assert.Equal(argv.Length, actual.Count);
        for (var i = 0; i < argv.Length; i++)
        {
            Assert.Equal(
                Convert.ToHexString(Encoding.UTF8.GetBytes(argv[i])),
                actual[i]!.GetValue<string>(),
                ignoreCase: true);
        }
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

    [Theory]
    [InlineData("täst-😀.json")]
    [InlineData("sp ace-😀 ü.json")]
    public void GenerateWritesUnicodeNamedArtifactsByteIdentical(string name)
    {
        // The artifact bytes (LF-normalized JSON written by the child) prove the
        // --out path reached the child intact, without any console text in the
        // comparison. Baseline bytes come from an in-process run in a separate
        // directory; child stderr text is deliberately not asserted here.
        using var childDir = new TempDir();
        using var baselineDir = new TempDir();
        var runner = new SlashConsoleProcessRunner();

        var finished = runner.StartCaptured(new CapturedRunRequest(
            ["generate", "--seed", "42", "--out", name], "lattice generate", childDir.Path, ChildInvocation())).Finish();
        Assert.True(finished.ExitCode == 0, $"expected exit 0 but got {finished.ExitCode}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");

        var entries = Directory.EnumerateFileSystemEntries(childDir.Path).ToList();
        var actualName = Path.GetFileName(Assert.Single(entries));
        if (!string.Equals(actualName, name, StringComparison.Ordinal))
        {
            // A filesystem may return a different Unicode normalisation form
            // (macOS): compare FormC on both sides, still requiring the non-BMP
            // code point to survive.
            Assert.Equal(name.Normalize(NormalizationForm.FormC), actualName.Normalize(NormalizationForm.FormC));
        }

        Assert.Contains("😀", actualName, StringComparison.Ordinal);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var baselineOut = Path.Combine(baselineDir.Path, "baseline.json");
        var exit = CliApp.Run(["generate", "--seed", "42", "--out", baselineOut], stdout, stderr);
        Assert.Equal(0, exit);
        Assert.Equal(
            File.ReadAllBytes(baselineOut),
            File.ReadAllBytes(Path.Combine(childDir.Path, actualName)));
    }

    [Fact]
    public void ValidateScenarioUnicodePathMatchesOriginalExitCode()
    {
        // An existing valid fixture copied to a Unicode name: the child must
        // report the same exit code on both paths. No console text compared.
        using var temp = new TempDir();
        var original = Committed("scenarios", "collection-skirmish.json");
        var unicode = Path.Combine(temp.Path, "s cenario-😀.json");
        File.Copy(original, unicode);

        var runner = new SlashConsoleProcessRunner();
        var unicodeRun = runner.StartCaptured(new CapturedRunRequest(
            ["validate-scenario", unicode], "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();
        var originalRun = runner.StartCaptured(new CapturedRunRequest(
            ["validate-scenario", original], "lattice validate-scenario", temp.Path, ChildInvocation())).Finish();

        Assert.True(originalRun.ExitCode == unicodeRun.ExitCode, $"exit mismatch: original={originalRun.ExitCode} unicode={unicodeRun.ExitCode}\n--- original stdout ---\n{originalRun.Stdout}\n--- original stderr ---\n{originalRun.Stderr}\n--- unicode stdout ---\n{unicodeRun.Stdout}\n--- unicode stderr ---\n{unicodeRun.Stderr}");
        Assert.True(unicodeRun.ExitCode == 0, $"expected exit 0 but got {unicodeRun.ExitCode} (original={originalRun.ExitCode})\n--- unicode stdout ---\n{unicodeRun.Stdout}\n--- unicode stderr ---\n{unicodeRun.Stderr}\n--- original stdout ---\n{originalRun.Stdout}\n--- original stderr ---\n{originalRun.Stderr}");
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

        Assert.True(exit == finished.ExitCode, $"exit mismatch: in-process={exit} child={finished.ExitCode}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}");
        Assert.True(finished.ExitCode == 0, $"expected exit 0 but got {finished.ExitCode} (in-process={exit})\n--- child stdout ---\n{finished.Stdout}\n--- child stderr ---\n{finished.Stderr}\n--- in-process stdout ---\n{stdout}\n--- in-process stderr ---\n{stderr}");
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
        // The built CLI, resolved from the test assembly's own output directory so
        // it is the build under test rather than whatever a previous build left in
        // the CLI project's own bin folder, which in-suite builds can rewrite.
        var dll = Path.Combine(AppContext.BaseDirectory, "lattice.dll");
        Assert.True(File.Exists(dll), $"the CLI was not found at '{dll}'.");
        return dll;
    }

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

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());

    private static void RequirePython3(string python)
    {
        var start = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; assert sys.version_info[0]==3");
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"expected Python 3 at {python}.");
    }

    private static (int ExitCode, string Stdout, string Stderr) ControlRun(string[] argv, string cwd)
    {
        // The same invocation started plainly: same redirects, same ArgumentList,
        // no encoding overrides. What "a plain redirected run" prints, byte for byte.
        var invocation = ChildInvocation();
        var start = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        foreach (var prefix in invocation.PrefixArguments)
        {
            start.ArgumentList.Add(prefix);
        }

        foreach (var argument in argv)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
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
