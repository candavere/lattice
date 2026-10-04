using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the contract that makes the working indicator safe to add to commands
/// whose output is already pinned by golden tests: nothing at all is written
/// unless stderr is the real, interactive terminal.
/// </summary>
/// <remarks>
/// Every test here drives the real <see cref="CliApp.Run"/> with an ordinary
/// <see cref="StringWriter"/>, which is exactly what the repository's existing
/// CLI tests do. So the whole class doubles as the regression net for the rule
/// that a redirected stderr gets no spinner, no success line, and no failure
/// line — the bytes a script captures are the bytes it got before this existed.
/// </remarks>
public class CommandLifecycleCliTests : IDisposable
{
    private readonly string directory;
    private readonly string trajectoryPath;

    public CommandLifecycleCliTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"lattice-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        trajectoryPath = Path.Combine(directory, "trajectory.jsonl");

        var turns = new AgentAction[8][];
        for (var tick = 0; tick < turns.Length; tick++)
        {
            turns[tick] =
            [
                new AgentAction(ActionKind.Wait),
                new AgentAction(ActionKind.Wait),
            ];
        }

        using (var sink = new StreamWriter(trajectoryPath))
        {
            TrajectoryWriter.Record(
                MapGenerator.Generate(123, new GeneratorConfig(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap)),
                new SimulationConfig(AgentCount: 2, MaxTicks: turns.Length),
                123,
                turns,
                sink);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void AnalyzeWritesNothingToStderrBeyondItsOwnOutput()
    {
        var result = Run("analyze", "--trajectory", trajectoryPath);

        Assert.Equal(0, result.Exit);
        // Byte-for-byte what it wrote before this feature existed: the compact
        // report goes to stdout and stderr is empty.
        Assert.Equal(string.Empty, result.Err);
        Assert.Contains("Lattice Tactical Report", result.Out);
    }

    [Fact]
    public void AnalyzeWritesOnlyItsOwnLineWhenGivenAnOutPath()
    {
        var result = Run("analyze", "--trajectory", trajectoryPath, "--out", Path.Combine(directory, "r.md"));

        Assert.Equal(0, result.Exit);
        Assert.Equal($"wrote {Path.Combine(directory, "r.md")}{System.Environment.NewLine}", result.Err);
    }

    [Fact]
    public void SimulateWritesNoLifecycleOutputToARedirectedStderr()
    {
        var result = Run("simulate", "--seed", "7", "--steps", "5", "--quiet");

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain('\u001b', result.Err);
        Assert.DoesNotContain("ok", result.Err);
    }

    [Fact]
    public void ReplayVerifyWritesNoLifecycleOutputToARedirectedStderr()
    {
        var result = Run("replay", trajectoryPath, "--verify");

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain('\u001b', result.Err);
        Assert.DoesNotContain("exit 0", result.Err);
    }

    [Fact]
    public void AFailingCommandStillReportsOnlyItsExistingStderrLine()
    {
        var result = Run("analyze", "--trajectory", Path.Combine(directory, "no-such-file.jsonl"));

        Assert.Equal(1, result.Exit);
        // The pre-existing error line, and nothing resembling a lifecycle line.
        Assert.StartsWith("error:", result.Err);
        Assert.DoesNotContain('\u001b', result.Err);
        Assert.DoesNotContain("exit 1", result.Err);
    }

    [Fact]
    public void StdoutIsUnaffectedByTheLifecycleWork()
    {
        // The byte-for-byte promise, checked on the command whose stdout is a
        // pure artifact: same input twice, same bytes out.
        var first = Run("simulate", "--seed", "11", "--steps", "4", "--quiet");
        var second = Run("simulate", "--seed", "11", "--steps", "4", "--quiet");

        Assert.Equal(first.Out, second.Out);
        Assert.NotEqual(string.Empty, first.Out);
    }

    [Fact]
    public void ExitCodesAreUnaffectedByTheLifecycleWork()
    {
        Assert.Equal(2, Run("simulate").Exit);
        Assert.Equal(2, Run("nonsense").Exit);
        Assert.Equal(0, Run("--version").Exit);
    }

    [Fact]
    public void BenchmarkWritesNoLifecycleOutputToARedirectedStderr()
    {
        var result = Run("benchmark", "--runs", "1", "--warmup", "1", "--steps", "2");

        Assert.DoesNotContain('\u001b', result.Err);
        Assert.DoesNotContain("exit 0", result.Err);
    }

    /// <summary>
    /// Runs the command as if it were attached to a real 24-bit terminal, which
    /// is the only way to reach the lifecycle code from a test: the default
    /// <see cref="CliApp.Run(string[], TextWriter, TextWriter)"/> overload
    /// deliberately treats a <see cref="StringWriter"/> as a redirected stderr.
    /// </summary>
    private static (int Exit, string Out, string Err) RunInteractively(params string[] args) =>
        RunWith(InteractiveTerminal, args);

    private static (int Exit, string Out, string Err) RunWith(CliTerminal terminal, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr, terminal);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static CliTerminal InteractiveTerminal => new(
        CapabilityDetector.Detect(new TerminalEnvironment(
            "truecolor", "xterm-256color", null, "en_US.UTF-8",
            InputRedirected: false, OutputRedirected: false, 120, 40)),
        InteractiveStderr: true);

    /// <summary>
    /// An interactive terminal whose repaint pump does nothing at all, so no
    /// callback can ever run.
    /// </summary>
    /// <remarks>
    /// This is the point of the repair. These assertions used to depend on a
    /// <see cref="System.Threading.Timer"/> callback being scheduled inside the
    /// command's lifetime, which is not guaranteed and is exactly what failed on
    /// a loaded CI runner. With an inert pump they hold only because attaching a
    /// real counter paints the line synchronously — so they now test the
    /// production behaviour instead of the thread pool.
    /// </remarks>
    private static CliTerminal InertPumpTerminal => InteractiveTerminal with
    {
        RepaintIntervalMs = 60_000,
        PumpFactory = static (_, _) => new NoOpPump(),
    };

    /// <summary>
    /// A terminal whose repaints are driven synchronously by a pump that ticks a
    /// fixed number of times the moment it is created, so an animation assertion
    /// measures the animation rather than the scheduler.
    /// </summary>
    /// <param name="ticks">How many repaints the pump performs.</param>
    private static CliTerminal ManualPumpTerminal(int ticks) => InteractiveTerminal with
    {
        RepaintIntervalMs = 1,
        PumpFactory = (tick, _) =>
        {
            for (var i = 0; i < ticks; i++)
            {
                tick();
            }

            return new NoOpPump();
        },
    };

    /// <summary>A pump handle that never schedules anything.</summary>
    private sealed class NoOpPump : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public void AnInteractiveStderrGetsTheSuccessLine()
    {
        var result = RunInteractively("analyze", "--trajectory", trajectoryPath, "--out", Path.Combine(directory, "r.md"));

        Assert.Equal(0, result.Exit);
        Assert.Contains("analyze", result.Err);
        Assert.Contains("ok", result.Err);
        Assert.Contains('\u001b', result.Err);
    }

    [Fact]
    public void AnInteractiveStderrStillLeavesStdoutByteIdentical()
    {
        var redirected = Run("analyze", "--trajectory", trajectoryPath);
        var interactive = RunInteractively("analyze", "--trajectory", trajectoryPath);

        Assert.Equal(redirected.Out, interactive.Out);
        Assert.Equal(redirected.Exit, interactive.Exit);
    }

    [Fact]
    public void AnInteractiveStderrGetsTheFailureLineWithTheRealExitCode()
    {
        var result = RunInteractively("analyze", "--trajectory", Path.Combine(directory, "no-such-file.jsonl"));

        Assert.Equal(1, result.Exit);
        Assert.Contains("exit 1", result.Err);
    }

    [Fact]
    public void AUsageErrorOnAnInteractiveStderrReportsExitTwo()
    {
        var result = RunInteractively("analyze");

        Assert.Equal(2, result.Exit);
        Assert.Contains("exit 2", result.Err);
    }

    [Fact]
    public void QuietSuppressesTheLineEvenOnAnInteractiveTerminal()
    {
        var result = RunInteractively("simulate", "--seed", "7", "--steps", "4", "--quiet");

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain("ok", result.Err);
    }

    [Fact]
    public void NoColorKeepsPlainStatusAndDropsEveryEscapeSequence()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var noColor = new CliTerminal(
            CapabilityDetector.Detect(new TerminalEnvironment(
                "truecolor", "xterm-256color", "1", "en_US.UTF-8",
                InputRedirected: false, OutputRedirected: false, 120, 40)),
            InteractiveStderr: true);

        var exit = CliApp.Run(
            ["analyze", "--trajectory", trajectoryPath, "--out", Path.Combine(directory, "r.md")],
            stdout,
            stderr,
            noColor);

        Assert.Equal(0, exit);
        Assert.DoesNotContain('\u001b', stderr.ToString());
        // Plain status is still allowed, so the words survive the styling.
        Assert.Contains("ok", stderr.ToString());
    }

    [Fact]
    public void ACommandWithNoRealCounterShowsNoRatio()
    {
        // analyze reads a file and reports; there is no counter behind it, so the
        // line must not imply one.
        var result = RunInteractively("analyze", "--trajectory", trajectoryPath, "--out", Path.Combine(directory, "r.md"));

        Assert.DoesNotContain("steps", result.Err);
    }

    [Fact]
    public void EvaluateReportsRealMatchProgressFromTheSeedsItActuallyRuns()
    {
        // Two seeds, two mirrored seatings each: four matches. The line must show
        // that ratio and never a percentage or a bar.
        var result = RunInteractively(
            "evaluate", "--seed-set", "dev", "--seeds", "2", "--rollouts", "1",
            "--out", Path.Combine(directory, "study.json"));

        Assert.Equal(0, result.Exit);

        var line = result.Err.Split('\n').FirstOrDefault(text => text.Contains("evaluate", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.DoesNotContain("%", line);
    }

    [Fact]
    public void EvaluatePutsTheRealMatchRatioOnTheLine()
    {
        // This is the assertion that the counter is actually connected to the
        // render path. If the wiring were severed the line would fall back to
        // showing no ratio at all, which every other test here would still pass.
        //
        // The budget is 3 seeds x 2 mirrored seatings = 6 matches. The pump never
        // ticks, so the line can only carry a ratio if attaching the real
        // counter painted it synchronously; asserting the budget rather than the
        // numerator keeps the test independent of how many matches had started.
        var result = RunWith(
            InertPumpTerminal,
            "evaluate", "--seed-set", "dev", "--seeds", "3", "--rollouts", "1",
            "--out", Path.Combine(directory, "study.json"));

        Assert.Equal(0, result.Exit);
        Assert.Contains("matches", result.Err);
        Assert.Contains("/6 matches", result.Err);
    }

    [Fact]
    public void TheMatchBudgetFollowsTheSeedCapRatherThanTheSuiteSize()
    {
        // --seeds caps the suite at 1, so the budget is 1 x 2 seatings = 2
        // matches, not the 50 seeds the held-out suite nominally holds.
        var result = RunWith(
            InertPumpTerminal,
            "evaluate", "--seed-set", "dev", "--seeds", "1", "--rollouts", "1",
            "--out", Path.Combine(directory, "study.json"));

        Assert.Equal(0, result.Exit);
        Assert.Contains("/2 matches", result.Err);
    }

    [Fact]
    public void TheWorkingLineIsAnimatedWhileALongCommandRuns()
    {
        // The plan requires a live spinner, not one static line. Every frame of
        // the animation must be a distinct glyph drawn on the same row, and all
        // of them must be erased before the closing line lands. The pump ticks
        // three times synchronously, so the frames exist because the indicator
        // animated them, not because a thread-pool callback was scheduled.
        var result = RunWith(
            ManualPumpTerminal(ticks: 3),
            "benchmark", "--runs", "2", "--warmup", "2", "--steps", "4");

        var frames = CommandLifecycle.SpinnerFrameCount;
        var seen = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            if (result.Err.Contains(CommandLifecycle.Spinner(frame, true), StringComparison.Ordinal))
            {
                seen++;
            }
        }

        Assert.True(seen >= 2, $"expected an animated spinner, saw {seen} of {frames} frames.");
    }

    [Fact]
    public void EveryWorkingFrameIsErasedBeforeTheClosingLine()
    {
        // Without this the terminal keeps one row of spinner debris per frame. The
        // pump ticks synchronously at Start, so every frame is drawn before the
        // command writes anything: with a real timer, a tick landing after the
        // command's own stderr write would legitimately repaint on the next row
        // and this assertion would measure the scheduler instead of the erase.
        var result = RunWith(
            ManualPumpTerminal(ticks: 3),
            "benchmark", "--runs", "2", "--warmup", "2", "--steps", "4");

        var rows = result.Err.Split('\n');
        var spinnerRows = rows.Count(row => row.Contains("benchmark", StringComparison.Ordinal));

        // The working frames all share one row, so the verb appears once for the
        // whole animation plus once on the closing line.
        Assert.True(
            spinnerRows <= 2,
            $"the spinner left {spinnerRows} rows behind instead of repainting one.");
    }

    [Fact]
    public void TheAnimationStopsOnceTheCommandHasReturned()
    {
        // Nothing may repaint after the closing line: a spinner ticking over the
        // prompt is the classic way a progress indicator outlives its work.
        //
        // This one deliberately keeps the REAL timer. Its assertion holds whether
        // or not any callback happens to fire during the command, so it never
        // depended on the scheduler — and a manually-driven pump would make it
        // vacuous, since that pump cannot tick at all once Start has returned.
        var result = RunInteractively("benchmark", "--runs", "2", "--warmup", "2", "--steps", "4");

        var lines = result.Err.Split('\n');
        var closing = Array.FindLastIndex(lines, line => line.Contains("ok", StringComparison.Ordinal));

        Assert.True(closing >= 0, "no closing line was written.");
        Assert.DoesNotContain(
            CommandLifecycle.ReturnToLineStart,
            string.Concat(lines.Skip(closing + 1)),
            StringComparison.Ordinal);
    }
}
