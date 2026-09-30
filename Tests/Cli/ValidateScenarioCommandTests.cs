using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-1 coverage for the <c>validate-scenario</c> subcommand itself: the
/// command's exit status, its success report, its per-fault diagnostics, and —
/// the property that makes it safe to run in a pipeline — that it has no
/// simulation or write side effect even when it succeeds.
/// </summary>
public class ValidateScenarioCommandTests
{
    /// <summary>
    /// Ceiling for one isolated child invocation. It is a backstop, not the
    /// normal path: the launch plus a descriptor read is milliseconds, and this
    /// only has to be large enough that a loaded CI machine does not trip it.
    /// A child that overruns is killed here rather than left to hang the run.
    /// </summary>
    private const int ChildTimeoutMs = 60_000;

    /// <summary>How long the churn control waits for its own task to notice cancellation.</summary>
    private const int ChurnStopTimeoutMs = 10_000;

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Committed(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
    }

    [Theory]
    [InlineData("scenarios/collection-skirmish.json")]
    [InlineData("scenarios/bottleneck-contention.json")]
    [InlineData("scenarios/dungeon-infiltration.json")]
    [InlineData("scenarios/gated-vault-duel.json")]
    [InlineData("scenarios/skeleton-with-override.json")]
    public void AValidDescriptor_ExitsZeroAndReportsWhatItDeclares(string relative)
    {
        var (exit, stdout, stderr) = Run("validate-scenario", Committed(relative));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("is valid (schema v1)", stderr, StringComparison.Ordinal);
        Assert.Contains("sha256", stderr, StringComparison.Ordinal);
        Assert.Contains("simulation", stderr, StringComparison.Ordinal);
        Assert.Contains("victory", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidDescriptor_PrintsItsDigestOnStdErr()
    {
        var path = Committed("scenarios/collection-skirmish.json");
        var (_, _, stderr) = Run("validate-scenario", path);
        var (_, digest) = ScenarioLoader.LoadFile(path);

        Assert.Contains(digest, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOutFlag_IsAcceptedAsThePathToo()
    {
        var path = Committed("scenarios/collection-skirmish.json");
        var positional = Run("validate-scenario", path);
        var flagged = Run("validate-scenario", "--out", path);

        Assert.Equal(0, positional.ExitCode);
        Assert.Equal(0, flagged.ExitCode);
        Assert.Equal(positional.Stderr, flagged.Stderr);
    }

    [Fact]
    public void AnInvalidDescriptor_ExitsNonZeroAndNamesTheField()
    {
        var (exit, _, stderr) = Run(
            "validate-scenario", Committed("Tests/fixtures/scenarios/invalid/unknown_field.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("scenario error: Weather:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPath_ExitsNonZero()
    {
        var (exit, _, stderr) = Run("validate-scenario");

        Assert.NotEqual(0, exit);
        Assert.Contains("missing scenario path", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedExtraPositional_ExitsNonZero()
    {
        // Only the last extra positional is named, and it is named rather than
        // silently dropped: a caller that passed two paths meant something.
        var (exit, _, stderr) = Run(
            "validate-scenario", Committed("scenarios/collection-skirmish.json"), "extra.json");

        Assert.NotEqual(0, exit);
        Assert.Contains("extra.json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFlag_IsRejected()
    {
        var (exit, _, stderr) = Run("validate-scenario", "--strict", Committed("scenarios/collection-skirmish.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("unknown flag '--strict'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentFile_ExitsNonZeroWithoutAnUnhandledFault()
    {
        var (exit, _, stderr) = Run("validate-scenario", Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulValidation_WritesNoArtifactAndNoTrajectory()
    {
        // The command reads a descriptor and reports on stderr. It must not emit a
        // trajectory on stdout, and it must leave no trace behind.
        //
        // What this assertion actually proves, stated plainly so it is not
        // over-claimed: the command wrote no file, created no directory, and
        // modified no seeded input anywhere inside the two surfaces it was
        // actually given — its working directory and its temporary-file root.
        // Neither this nor the version it replaces proves there were no writes
        // anywhere on the machine or on the filesystem. What it does prove is
        // stronger than a global guess in the only sense that matters here: the
        // monitored surfaces are ones this test owns, exclusively and by name.
        //
        // The previous version snapshotted Path.GetTempPath() around an in-process
        // call. That directory is shared with every other test in the suite, and
        // at least a dozen test classes create files in it concurrently, so the
        // assertion could fail on an unrelated sibling's temp file — and it did.
        // It was measuring the machine, not this command.
        var sandbox = CreateSandbox();
        try
        {
            var descriptor = SeedCollectionSkirmish(sandbox);
            var before = Snapshot(sandbox);

            var (exit, stdout, stderr) = RunIsolated(sandbox, descriptor);

            var unexpected = Unexpected(before, Snapshot(sandbox));

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("is valid", stderr, StringComparison.Ordinal);
            Assert.True(
                unexpected.Count == 0,
                "validate-scenario changed its sandbox: " + string.Join("; ", unexpected));
        }
        finally
        {
            DeleteSandbox(sandbox);
        }
    }

    [Fact]
    public async Task UnrelatedActivityInTheSharedSystemTempRoot_CannotFailThisAssertion()
    {
        // The regression guard for the race this file used to have. It reproduces
        // the original trigger exactly — files appearing and disappearing in the
        // shared Path.GetTempPath() while the command runs — and asserts that the
        // command-owned surfaces are now immune to it. If someone reintroduces a
        // shared-temp snapshot, this fails; today it passes while the churn below
        // is provably still running.
        var sandbox = CreateSandbox();
        try
        {
            var descriptor = SeedCollectionSkirmish(sandbox);
            var before = Snapshot(sandbox);

            using var stop = new CancellationTokenSource();
            var churned = Task.Run(() => ChurnSharedSystemTempRoot(stop.Token));

            var (exit, stdout, stderr) = RunIsolated(sandbox, descriptor);

            stop.Cancel();
            var stopped = await Task.WhenAny(churned, Task.Delay(ChurnStopTimeoutMs));
            Assert.True(
                ReferenceEquals(churned, stopped),
                "the churn task did not stop after cancellation.");
            var churnCount = await churned;

            var unexpected = Unexpected(before, Snapshot(sandbox));

            // The control is only meaningful if the churn really happened.
            Assert.True(
                churnCount > 0,
                "the churn created no files, so this proved nothing about isolation.");
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("is valid", stderr, StringComparison.Ordinal);
            Assert.True(
                unexpected.Count == 0,
                "unrelated shared-temp activity leaked into the sandbox: " +
                string.Join("; ", unexpected));
        }
        finally
        {
            DeleteSandbox(sandbox);
        }
    }

    [Fact]
    public void ANewFileOrDirectoryInsideTheMonitoredSandboxIsDetected()
    {
        // The other half of the guard: isolation must not become vacuity. If the
        // detector could not see a write, then a real write would pass too, so
        // this plants one file and one directory and requires both to be
        // reported. This is what keeps the assertion above meaningful.
        var sandbox = CreateSandbox();
        try
        {
            SeedCollectionSkirmish(sandbox);
            var before = Snapshot(sandbox);

            File.WriteAllText(Path.Combine(sandbox, "unexpected.jsonl"), "{}\n");
            Directory.CreateDirectory(Path.Combine(sandbox, "unexpected-directory"));

            var unexpected = Unexpected(before, Snapshot(sandbox));

            Assert.True(
                unexpected.Any(entry => entry.Contains("unexpected.jsonl", StringComparison.Ordinal)),
                "a new file inside the sandbox was not detected: " + string.Join("; ", unexpected));
            Assert.True(
                unexpected.Any(entry => entry.Contains("unexpected-directory", StringComparison.Ordinal)),
                "a new directory inside the sandbox was not detected: " + string.Join("; ", unexpected));
        }
        finally
        {
            DeleteSandbox(sandbox);
        }
    }

    /// <summary>
    /// Creates the command's private world: a unique working directory with a
    /// <c>tmp</c> subtree inside it. Both are named by this test alone, which is
    /// the property the old shared snapshot lacked — no other process, and no
    /// other test, has a path into them.
    /// </summary>
    private static string CreateSandbox()
    {
        var sandbox = Directory.CreateTempSubdirectory("lattice-validate-scenario-sandbox").FullName;
        Directory.CreateDirectory(Path.Combine(sandbox, "tmp"));
        return sandbox;
    }

    /// <summary>
    /// Deletes the sandbox, tolerating one refusal from the OS.
    /// </summary>
    /// <remarks>
    /// The catch is not a retry and adds no wait. The child has already exited by
    /// the time this runs, so a refusal means a transient lock — an indexer or a
    /// virus scanner on Windows, which is why the original test could not simply
    /// assert a clean directory. The path is unique to this test, so the next run
    /// starts from a fresh one and nothing accumulates within or across runs. Any
    /// other exception propagates: a permissions or path fault is a real problem,
    /// not noise. Assertions run before this in the caller's <c>finally</c>, so a
    /// swallowed cleanup fault can never mask a failed assertion.
    /// </remarks>
    private static void DeleteSandbox(string sandbox)
    {
        try
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
        catch (IOException)
        {
            // One attempt refused by the OS; the directory stays and the next run
            // uses a different path, so nothing accumulates within a run.
        }
    }

    /// <summary>
    /// Copies the committed descriptor into the sandbox and returns its absolute
    /// path. Seeding before the baseline snapshot is what keeps an input file
    /// from being reported later as if the command had created it.
    /// </summary>
    private static string SeedCollectionSkirmish(string sandbox)
    {
        var source = Committed("scenarios/collection-skirmish.json");
        var seeded = Path.Combine(sandbox, "scenario.json");
        File.Copy(source, seeded);
        return seeded;
    }

    /// <summary>
    /// Runs the real CLI as a child process whose working directory and
    /// temporary-file root are both inside the sandbox.
    /// </summary>
    /// <remarks>
    /// A child process is what makes the isolation real. Setting TMPDIR, TMP and
    /// TEMP on the child's environment gives the command a temporary root of its
    /// own without mutating this test runner's process-wide environment, which
    /// would leak into every parallel test. The working directory does the same
    /// for relative output. All three variables are set because the three
    /// supported platforms disagree about which one they honour.
    ///
    /// The program is the dotnet host rather than the <c>lattice</c> apphost, so
    /// the launch does not depend on the host being able to locate a runtime by
    /// itself — the same launch shape the external-agent tests already use.
    ///
    /// Both streams are drained by concurrent asynchronous reads before the exit
    /// wait, so a chatty child cannot fill a pipe buffer and deadlock against a
    /// sequential ReadToEnd.
    /// </remarks>
    private static (int ExitCode, string Stdout, string Stderr) RunIsolated(string sandbox, string scenarioPath)
    {
        var childTemp = Path.Combine(sandbox, "tmp");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = sandbox,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(CliAssemblyPath());
        startInfo.ArgumentList.Add("validate-scenario");
        startInfo.ArgumentList.Add(scenarioPath);

        startInfo.Environment["TMPDIR"] = childTemp;
        startInfo.Environment["TMP"] = childTemp;
        startInfo.Environment["TEMP"] = childTemp;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ChildTimeoutMs))
        {
            // Only the child this test started, and only because this test started
            // it. Nothing else on the machine is touched.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(ChildTimeoutMs);
            Assert.Fail($"`validate-scenario` did not exit within {ChildTimeoutMs} ms.");
        }

        return (
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    /// <summary>
    /// The built CLI, resolved from the test assembly's own output directory so
    /// it is the build under test rather than whatever a previous build left in
    /// the CLI project's own bin folder.
    /// </summary>
    private static string CliAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lattice.dll");
        Assert.True(File.Exists(path), $"the CLI was not found at '{path}'.");
        return path;
    }

    /// <summary>
    /// A recursive listing of everything under <paramref name="root"/>, keyed by
    /// the path relative to it. Files are keyed by size and SHA-256 so an
    /// in-place edit is visible, not just a create or a delete; directories are
    /// keyed separately so one appears at all.
    /// </summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Relative(root, file)] = DescribeFile(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Relative(root, directory) + "/"] = "directory";
        }

        return snapshot;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string DescribeFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = Convert.ToHexString(sha256.ComputeHash(stream));
        return $"file:{new FileInfo(path).Length}:{hash}";
    }

    /// <summary>
    /// Everything that appeared, vanished or changed between two snapshots.
    /// </summary>
    /// <remarks>
    /// Modifications are part of the result on purpose. A command that rewrote
    /// the descriptor it was asked to read would leave the file set identical, so
    /// a set-only comparison would call that clean.
    /// </remarks>
    private static IReadOnlyList<string> Unexpected(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var problems = new List<string>();

        foreach (var (path, descriptor) in after)
        {
            if (!before.TryGetValue(path, out var prior))
            {
                problems.Add($"new {path}");
            }
            else if (!string.Equals(prior, descriptor, StringComparison.Ordinal))
            {
                problems.Add($"modified {path}");
            }
        }

        foreach (var path in before.Keys)
        {
            if (!after.ContainsKey(path))
            {
                problems.Add($"removed {path}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Creates and deletes files in the shared system temp root until cancelled,
    /// returning how many it made — the behaviour of the sibling tests that used
    /// to be able to fail the shared-snapshot version of this file's assertion.
    /// </summary>
    private static int ChurnSharedSystemTempRoot(CancellationToken token)
    {
        var root = Path.GetTempPath();
        var created = 0;

        while (!token.IsCancellationRequested)
        {
            var path = Path.Combine(root, $"lattice-validate-churn-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, "unrelated sibling activity");
            File.Delete(path);
            created++;
        }

        return created;
    }

    [Fact]
    public void TheCommandIsDispatchedByName()
    {
        var (exit, stdout, _) = Run("--help");

        Assert.Equal(0, exit);
        Assert.Contains("validate-scenario", stdout, StringComparison.Ordinal);
    }
}
