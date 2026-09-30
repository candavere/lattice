using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using Lattice.Cli;
using Xunit;
using Xunit.Abstractions;

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

    /// <summary>
    /// Ceiling for the churn worker to reach readiness. Readiness is a completed
    /// real write/delete cycle, which is two syscalls, so this only has to absorb
    /// a slow or heavily loaded machine. It is finite so a worker that faults or
    /// dies before its first cycle fails the test instead of hanging the run.
    /// </summary>
    private const int ChurnReadyTimeoutMs = 30_000;

    /// <summary>How long the churn worker is given to notice cancellation and exit.</summary>
    private const int ChurnStopTimeoutMs = 10_000;

    private readonly ITestOutputHelper _output;

    public ValidateScenarioCommandTests(ITestOutputHelper output) => _output = output;

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

        RunWithTeardown(
            body: () =>
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
            },
            teardown: new (string, Action)[] { ("delete the sandbox", () => DeleteSandbox(sandbox)) });
    }

    [Fact]
    public void UnrelatedActivityInTheSharedSystemTempRoot_CannotFailThisAssertion()
    {
        // The regression guard for the race this file used to have. It reproduces
        // the original trigger exactly — files appearing and disappearing in the
        // shared Path.GetTempPath() while the command runs — and asserts that the
        // command-owned surfaces are now immune to it. If someone reintroduces a
        // shared-temp snapshot, this fails; today it passes with the churn worker
        // having completed real work, established by the readiness rendezvous and
        // the counter increase described below.
        var sandbox = CreateSandbox();
        using var churn = new SharedTempChurnWorker();

        RunWithTeardown(
            body: () =>
            {
                var descriptor = SeedCollectionSkirmish(sandbox);
                var before = Snapshot(sandbox);

                // Ordering is established by an explicit rendezvous, not by hoping
                // the thread pool got round to the churn loop. A dedicated worker
                // thread signals readiness only after it has actually completed a
                // real create/delete cycle in the shared system temp root, so the
                // CLI below cannot be launched against zero activity.
                Assert.True(
                    churn.WaitUntilReady(ChurnReadyTimeoutMs),
                    $"the churn worker did not complete a real write within {ChurnReadyTimeoutMs} ms.");

                // A worker that faults before signalling must say so rather than
                // leave this waiting until the ceiling above.
                var faultBeforeReady = churn.Fault;
                Assert.True(
                    faultBeforeReady is null,
                    "the churn worker faulted before it became ready: " + faultBeforeReady);

                var createdAtLaunch = churn.Created;
                Assert.True(
                    createdAtLaunch > 0,
                    "the churn created no files, so this proved nothing about isolation.");

                var (exit, stdout, stderr) = RunIsolated(sandbox, descriptor);

                // The proof is the worker's own state, not elapsed time. Readiness
                // guarantees one completed real churn cycle before the child is
                // launched. Sampled before it is stopped, the worker must be alive
                // and unfaulted; sampled after it is stopped, it must be strictly
                // further along than the pre-invocation sample. The counter increase
                // therefore proves at least one additional completed cycle between
                // the pre-invocation sample and the post-shutdown sample. That
                // sampling interval spans, but extends beyond, the child's
                // launch-to-return interval: it does not prove continuous churn, nor
                // that a cycle completed strictly inside the child invocation.
                // Repeated green runs are not a universal scheduling guarantee, and
                // do not prove a spurious scheduling failure impossible.
                var aliveAcrossInvocation = churn.IsAlive;
                var fault = churn.Fault;
                StopChurnWorker(churn);
                var createdAcrossInvocation = churn.Created;

                Assert.True(aliveAcrossInvocation, "the churn worker had already exited while the CLI was running.");
                Assert.True(fault is null, "the churn worker faulted during the CLI invocation: " + fault);
                Assert.True(
                    createdAcrossInvocation > createdAtLaunch,
                    $"the churn worker completed no further activity during the CLI invocation " +
                    $"({createdAtLaunch} -> {createdAcrossInvocation}), so it was not provably still running.");

                var unexpected = Unexpected(before, Snapshot(sandbox));

                Assert.Equal(0, exit);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains("is valid", stderr, StringComparison.Ordinal);
                Assert.True(
                    unexpected.Count == 0,
                    "unrelated shared-temp activity leaked into the sandbox: " +
                    string.Join("; ", unexpected));
            },
            teardown: new (string, Action)[]
            {
                // Idempotent: the body may already have stopped the worker, in
                // which case this returns immediately instead of faulting.
                ("stop the shared-temp churn worker", () => StopChurnWorker(churn)),
                ("delete the sandbox", () => DeleteSandbox(sandbox)),
            });
    }

    [Fact]
    public void ANewFileOrDirectoryInsideTheMonitoredSandboxIsDetected()
    {
        // The other half of the guard: isolation must not become vacuity. If the
        // detector could not see a write, then a real write would pass too, so
        // this plants one file and one directory and requires both to be
        // reported. This is what keeps the assertion above meaningful.
        var sandbox = CreateSandbox();

        RunWithTeardown(
            body: () =>
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
            },
            teardown: new (string, Action)[] { ("delete the sandbox", () => DeleteSandbox(sandbox)) });
    }

    // ---------------------------------------------------------------------
    // Failure preservation for teardown.
    //
    // These four tests pin the behaviour that the helpers below implement, so
    // the guarantee is enforced by passing tests rather than by a comment. They
    // inject a teardown fault through a delegate rather than by changing
    // permissions on a real directory, because the latter is OS-dependent and
    // behaves differently per platform and per filesystem.
    // ---------------------------------------------------------------------

    [Fact]
    public void ATeardownFaultDoesNotReplaceAnAlreadyFailingTest()
    {
        var reported = new List<string>();
        var primary = new InvalidTimeZoneException("the body failed first");

        var caught = Record.Exception(() => RunWithTeardown(
            body: () => throw primary,
            teardown: new (string, Action)[] { ("delete the sandbox", () => throw new UnauthorizedAccessException("the OS refused")) },
            report: reported.Add,
            caller: nameof(ATeardownFaultDoesNotReplaceAnAlreadyFailingTest)));

        Assert.NotNull(caught);

        // Identity: the very same exception object, so nothing about the original
        // diagnosis was rewritten.
        Assert.Same(primary, caught);
        Assert.Contains("the body failed first", caught.Message, StringComparison.Ordinal);

        // Stack: rethrown through ExceptionDispatchInfo, so the original throw
        // site is still on it rather than the helper's rethrow site.
        Assert.NotNull(caught.StackTrace);
        Assert.Contains(
            nameof(ATeardownFaultDoesNotReplaceAnAlreadyFailingTest),
            caught.StackTrace,
            StringComparison.Ordinal);

        // Secondary: reported beside the primary rather than in place of it.
        Assert.Contains(
            reported,
            line => line.Contains("delete the sandbox", StringComparison.Ordinal)
                 && line.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal));
    }

    [Fact]
    public void ATeardownFaultFailsAnOtherwiseSuccessfulTest()
    {
        var reported = new List<string>();
        var bodyRan = false;

        var caught = Record.Exception(() => RunWithTeardown(
            body: () => bodyRan = true,
            teardown: new (string, Action)[] { ("delete the sandbox", () => throw new UnauthorizedAccessException("the OS refused")) },
            report: reported.Add,
            caller: nameof(ATeardownFaultFailsAnOtherwiseSuccessfulTest)));

        Assert.True(bodyRan, "the body did not run.");
        Assert.NotNull(caught);

        var aggregate = Assert.IsType<AggregateException>(caught);
        Assert.Contains("1 teardown step(s) failed", aggregate.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ATeardownFaultFailsAnOtherwiseSuccessfulTest), aggregate.Message, StringComparison.Ordinal);

        var step = Assert.IsType<TeardownStepFault>(Assert.Single(aggregate.InnerExceptions));
        Assert.Equal("delete the sandbox", step.Step);
        Assert.IsType<UnauthorizedAccessException>(step.InnerException);
        Assert.Contains(
            reported,
            line => line.Contains("delete the sandbox", StringComparison.Ordinal)
                 && line.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal));
    }

    [Fact]
    public void OneTeardownFaultDoesNotSkipTheRemainingCleanup()
    {
        var attempted = new List<string>();
        var reported = new List<string>();
        var primary = new TimeoutException("the body failed first");

        var caught = Record.Exception(() => RunWithTeardown(
            body: () => throw primary,
            teardown: new (string, Action)[]
            {
                ("stop the shared-temp churn worker", () =>
                {
                    attempted.Add("stop the shared-temp churn worker");
                    throw new IOException("the OS refused");
                }),
                ("delete the sandbox", () => attempted.Add("delete the sandbox")),
            },
            report: reported.Add,
            caller: nameof(OneTeardownFaultDoesNotSkipTheRemainingCleanup)));

        // The primary survives untouched, even though the first teardown step threw.
        Assert.Same(primary, caught);

        // Every step was still attempted: a fault stopping the worker must not
        // prevent the sandbox from being cleaned up too.
        Assert.Equal(
            new[] { "stop the shared-temp churn worker", "delete the sandbox" },
            attempted);

        // Exactly one step's fault is reported, with that step named and its type
        // intact; the step that succeeded is not reported as a fault. The helper
        // also writes a trailing count line, so the assertion is on content
        // rather than on the number of lines written.
        Assert.Equal(2, reported.Count);
        Assert.Contains("stop the shared-temp churn worker", reported[0], StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), reported[0], StringComparison.Ordinal);
        Assert.Contains("1 teardown step(s) failed", reported[1], StringComparison.Ordinal);
        Assert.DoesNotContain(
            reported,
            line => line.Contains("delete the sandbox", StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingAnAlreadyDeletedSandboxIsNotACleanupFailure()
    {
        // Harmless absence is not a cleanup failure. This is the idempotent case
        // teardown relies on: the body may already have deleted the surface, and
        // running the same step again must not manufacture a fault.
        var sandbox = CreateSandbox();
        DeleteSandbox(sandbox);
        Assert.False(Directory.Exists(sandbox));

        var reported = new List<string>();

        RunWithTeardown(
            body: () => { },
            teardown: new (string, Action)[] { ("delete the sandbox", () => DeleteSandbox(sandbox)) },
            report: reported.Add,
            caller: nameof(DeletingAnAlreadyDeletedSandboxIsNotACleanupFailure));

        Assert.Empty(reported);
    }

    /// <summary>
    /// Runs <paramref name="body"/>, then runs every teardown step, and decides
    /// which failure the caller sees.
    /// </summary>
    /// <remarks>
    /// The rule is asymmetric on purpose, because the two cases want different
    /// things:
    ///
    /// <list type="bullet">
    /// <item>If the body, an assertion, readiness or the worker already failed,
    /// that exception is rethrown as the same object, with its original stack,
    /// because it is the diagnosis. A teardown fault that happened afterwards is
    /// written to the test's output and onto the primary's
    /// <see cref="Exception.Data"/> — beside the failure, never in place of it.
    /// A teardown exception that escaped here instead would have overwritten the
    /// real error and left the reader debugging the wrong problem.</item>
    ///
    /// <item>If the body succeeded and a teardown step genuinely failed, the test
    /// fails. Swallowing it would turn a broken cleanup into a green run, which
    /// is how leaked sandboxes and orphaned child processes stay invisible.</item>
    /// </list>
    ///
    /// Every step is attempted even after one fails, so a fault stopping the owned
    /// worker cannot also strand the sandbox. Each fault is reported separately
    /// and honestly; nothing is aggregated away silently.
    ///
    /// The catch here is broad on purpose: this is the diagnostic boundary, and
    /// every caught exception is either rethrown or reported. No path here turns
    /// a failure into a pass.
    ///
    /// <paramref name="report"/> defaults to this test's output helper so a
    /// secondary fault is visible in the retained run output instead of being
    /// dropped into an unused local. The regression tests above pass their own
    /// collector so they can assert on what was reported.
    /// </remarks>
    private void RunWithTeardown(
        Action body,
        IReadOnlyList<(string Description, Action Teardown)> teardown,
        Action<string>? report = null,
        [CallerMemberName] string caller = "")
    {
        var write = report ?? _output.WriteLine;

        Exception? primary = null;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            primary = ex;
        }

        var faults = new List<(string Description, Exception Error)>();
        foreach (var (description, step) in teardown)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                faults.Add((description, ex));
            }
        }

        if (primary is not null)
        {
            foreach (var (description, error) in faults)
            {
                write(
                    $"[{caller}] teardown step '{description}' failed after the test had already failed; " +
                    $"reported here, not substituted for the test's own failure: {error}");
                primary.Data[$"Lattice.Teardown.{description}"] = error.ToString();
            }

            if (faults.Count > 0)
            {
                write($"[{caller}] {faults.Count} teardown step(s) failed; the test's own failure is preserved below.");
            }

            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        if (faults.Count > 0)
        {
            foreach (var (description, error) in faults)
            {
                write($"[{caller}] teardown step '{description}' failed: {error}");
            }

            throw new AggregateException(
                $"{faults.Count} teardown step(s) failed in '{caller}' after the body succeeded.",
                faults.ConvertAll(fault => new TeardownStepFault(fault.Description, fault.Error)));
        }
    }

    /// <summary>One teardown step's failure, tagged with the step that produced it.</summary>
    private sealed class TeardownStepFault : Exception
    {
        public TeardownStepFault(string step, Exception error)
            : base($"teardown step '{step}' failed: {error.Message}", error)
        {
            Step = step;
        }

        public string Step { get; }
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
    /// Deletes the sandbox, or does nothing if it is already gone.
    /// </summary>
    /// <remarks>
    /// Absence is treated as success, not as a fault: teardown is expected to be
    /// runnable more than once, and a surface this test owns is not a resource
    /// anyone else can be holding open.
    ///
    /// Everything else propagates. An earlier version caught <see cref="IOException"/>
    /// here and called that "one refusal from the OS", on the grounds that an
    /// indexer or a virus scanner can hold a handle briefly on Windows. That was
    /// two mistakes at once: it turned a real cleanup fault into a green run on
    /// the success path, and because this runs from a <c>finally</c>, a fault of
    /// any other type — <see cref="UnauthorizedAccessException"/> and
    /// <see cref="ArgumentException"/> both derive from
    /// <see cref="SystemException"/>, not <see cref="IOException"/> — escaped the
    /// catch and replaced whatever the test was already reporting. Letting it
    /// propagate into <see cref="RunWithTeardown"/> fixes both: the original
    /// failure survives, and a genuine cleanup fault is now visible on the success
    /// path too.
    ///
    /// The cost of that is honest and worth stating: a transient scanner lock on
    /// Windows is no longer absorbed, and will now surface as a named teardown
    /// fault naming this path. That is a visible test failure rather than a
    /// silently undeleted directory, which is the trade this file wants. The
    /// deletion is still a single attempt with no retry and no wait.
    /// </remarks>
    private static void DeleteSandbox(string sandbox)
    {
        if (!Directory.Exists(sandbox))
        {
            return;
        }

        Directory.Delete(sandbox, recursive: true);
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
    /// Asks the worker to stop and waits, with a finite ceiling, for it to
    /// notice. Safe to call more than once: the second call is a no-op that
    /// returns immediately, which is what lets the body and the teardown both
    /// guarantee the worker is stopped.
    /// </summary>
    private static void StopChurnWorker(SharedTempChurnWorker churn)
    {
        if (!churn.RequestStopAndJoin(ChurnStopTimeoutMs))
        {
            throw new TimeoutException(
                $"the shared-temp churn worker did not stop within {ChurnStopTimeoutMs} ms.");
        }
    }

    /// <summary>
    /// Creates and deletes uniquely named files in the shared system temp root
    /// until cancelled — the behaviour of the sibling tests that used to be able
    /// to fail the shared-snapshot version of this file's assertion.
    /// </summary>
    /// <remarks>
    /// This runs on a thread it creates and owns, not on a thread-pool callback,
    /// and that is the whole point of the rewrite. The previous version used
    /// <c>Task.Run</c>, which queues on the global thread pool — the same pool
    /// this suite's 850-odd parallel collections occupy, several of which block
    /// inside real child-process waits. The churn work item could therefore sit
    /// queued for the entire duration of the CLI invocation, get cancelled before
    /// it ever ran, and complete with a count of zero, failing the very guard
    /// that exists to prove the churn was real. A dedicated thread cannot be
    /// starved that way, so the guarantee rests on the rendezvous below instead
    /// of on scheduling luck.
    ///
    /// Readiness means one real create/delete cycle has completed. It is never
    /// signalled by a queued work item, an entered callback, or a primed counter,
    /// so a test that reaches its assertions has already observed genuine shared
    /// -temp-root activity.
    /// </remarks>
    private sealed class SharedTempChurnWorker : IDisposable
    {
        private const string FilePrefix = "lattice-validate-churn-";

        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _finished = new(false);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly Thread _thread;

        private int _created;
        private Exception? _fault;

        public SharedTempChurnWorker()
        {
            // Background so a worker that somehow refused to stop can never wedge
            // the test runner's exit; every path that owns it still joins it.
            _thread = new Thread(Run) { IsBackground = true, Name = "lattice-shared-temp-churn" };
            _thread.Start();
        }

        /// <summary>Completed create/delete cycles so far.</summary>
        public int Created
        {
            get { lock (_gate) { return _created; } }
        }

        /// <summary>The worker's own fault, if it faulted. Checked before and after the CLI run.</summary>
        public Exception? Fault
        {
            get { lock (_gate) { return _fault; } }
        }

        public bool IsAlive => _thread.IsAlive;

        /// <summary>
        /// Blocks until a real activity cycle has completed, the worker has
        /// faulted, or the ceiling expires. Returns whether readiness was
        /// actually acknowledged.
        /// </summary>
        public bool WaitUntilReady(int timeoutMs) => _ready.Wait(timeoutMs);

        /// <summary>
        /// Signals cancellation and waits a bounded time for the worker to exit.
        /// Idempotent: once the worker has exited this returns true immediately.
        /// </summary>
        public bool RequestStopAndJoin(int timeoutMs)
        {
            _stop.Cancel();
            return _finished.Wait(timeoutMs);
        }

        /// <summary>
        /// Last-resort teardown. It never throws, because throwing here would
        /// escape a <c>finally</c> and displace whatever the test was already
        /// reporting; the bounded, throwing check belongs in the teardown list
        /// via <see cref="StopChurnWorker"/> instead.
        /// </summary>
        public void Dispose() => RequestStopAndJoin(ChurnStopTimeoutMs);

        private void Run()
        {
            var root = Path.GetTempPath();
            var acknowledged = false;

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var path = Path.Combine(root, $"{FilePrefix}{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllText(path, "unrelated sibling activity");
                        File.Delete(path);
                    }
                    finally
                    {
                        // Never strand a test-owned file in a directory shared with
                        // the whole suite, even when unwinding from a fault. The
                        // broad catch is safe here because it runs while another
                        // exception is in flight, where replacing it would lose
                        // the real diagnosis; the in-flight fault itself is still
                        // recorded below and reported.
                        try
                        {
                            if (File.Exists(path))
                            {
                                File.Delete(path);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                        {
                        }
                    }

                    var announce = false;
                    lock (_gate)
                    {
                        _created++;
                        if (!acknowledged)
                        {
                            acknowledged = true;
                            announce = true;
                        }
                    }

                    // Only ever signalled from here, which is only ever reached
                    // after a completed real cycle.
                    if (announce)
                    {
                        _ready.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _fault = ex;
                }
            }
            finally
            {
                // Signalled on the fault path too, so a worker that dies early
                // releases the waiting test immediately instead of making it sit
                // out the full readiness ceiling before learning why.
                _ready.Set();
                _finished.Set();
            }
        }
    }

    [Fact]
    public void TheCommandIsDispatchedByName()
    {
        var (exit, stdout, _) = Run("--help");

        Assert.Equal(0, exit);
        Assert.Contains("validate-scenario", stdout, StringComparison.Ordinal);
    }
}
