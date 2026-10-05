using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The real command runner: what each command actually does when the Launchpad
/// starts it, and that the bytes it puts on stdout are the bytes the same command
/// puts there from a shell.
/// </summary>
/// <remarks>
/// <para>
/// The frozen surface of this stage is the eight commands' stdout bytes and exit
/// codes. The strongest evidence available is differential rather than declarative:
/// each case here runs the command through the runner and the same command through
/// <c>CliApp.Run</c>, and compares the two streams byte for byte. A runner that
/// reworded a line, reordered two, or dropped a lifecycle line would fail here.
/// </para>
/// <para>
/// Nothing here starts the cockpit: replay and live simulate need a real terminal
/// and a live episode, and the pty check in stage 5 covers them on one.
/// </para>
/// </remarks>
public class LaunchpadCliTests
{
    [Theory]
    [InlineData("generate", new[] { "--seed", "42" })]
    [InlineData("analyze", new[] { "--trajectory", "site/demo.jsonl" })]
    [InlineData("render", new[] { "--trajectory", "site/demo.jsonl" })]
    [InlineData("validate-scenario", new[] { "scenarios/dungeon-infiltration.json" })]
    public void ACommandRunFromTheLaunchpadIsByteIdenticalToTheSameCommandFromAShell(
        string command,
        string[] arguments)
    {
        var run = RunThrough(command, arguments);
        var shell = RunDirect(command, arguments);

        Assert.Equal(shell.Exit, run.Exit);
        Assert.Equal(shell.Stdout, run.Stdout);
    }

    /// <summary>
    /// <c>replay</c> and <c>simulate</c> are not in the comparison above, because they
    /// are the two commands the Launchpad opens as a screen rather than running. Their
    /// routing is asserted by the two cockpit tests below, and their stdout and exit
    /// codes are frozen by the suite's own golden and parity tests, which drive
    /// <c>lattice tui</c> and <c>lattice simulate</c> directly.
    /// </summary>
    [Fact]
    public void TheTwoScreenCommandsAreTheOnlyTwoNotRunAsACommand()
    {
        foreach (var command in new[] { "generate", "simulate", "render", "analyze", "replay", "benchmark", "evaluate", "validate-scenario" })
        {
            Assert.Equal(
                command is "replay" or "simulate",
                CliAppLaunchpadRunner.IsCockpit([command]));
        }
    }

    /// <summary>
    /// <c>benchmark</c> cannot be compared byte for byte even against itself: its
    /// artifact carries a capture timestamp and per-case timings, both of which
    /// differ between two runs of the same command. What is compared instead is the
    /// shape of what it produced and its status, which are the parts a reader depends
    /// on.
    /// </summary>
    [Fact]
    public void BenchmarkProducesItsArtifactAndItsOwnStatus()
    {
        var run = RunThrough("benchmark", ["--runs", "1", "--warmup", "1", "--steps", "4"]);

        Assert.Equal(0, run.Exit);

        using var artifact = System.Text.Json.JsonDocument.Parse(run.Stdout);
        Assert.True(artifact.RootElement.TryGetProperty("Workloads", out _), "the artifact has no workloads.");
        Assert.True(artifact.RootElement.TryGetProperty("Metadata", out _), "the artifact does not name its host.");
    }

    /// <summary>
    /// A command's exit status is the command's, passed through rather than mapped.
    /// A reader who runs a command that fails needs the shell's own number.
    /// </summary>
    [Fact]
    public void AFailingCommandsStatusIsItsOwn()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lattice-launchpad-missing-{Guid.NewGuid():N}.jsonl");

        var run = RunThrough("analyze", ["--trajectory", missing]);

        Assert.Equal(1, run.Exit);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>
    /// A usage error is still a usage error: the runner hands the argument vector to
    /// <c>CliApp.Run</c> unchanged, so the status is 2 and the message is the parser's.
    /// </summary>
    [Fact]
    public void AUsageErrorIsStillAUsageError()
    {
        var run = RunThrough("generate", ["--seed", "not-a-number"]);

        Assert.Equal(2, run.Exit);
        Assert.Contains("expects an unsigned integer", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("", run.Stdout);
    }

    /// <summary>
    /// Replay is the one command that does not simply run: it opens the cockpit. The
    /// runner is asked through the cockpit path, which needs a terminal, so a
    /// redirected run is refused rather than run silently — the same rule the
    /// cockpit itself applies.
    /// </summary>
    [Fact]
    public void ReplayIsTheOneCommandTheCockpitIsAskedFor()
    {
        var asked = 0;
        var runner = CliAppRunner(new CapturingCockpit(() => asked++));

        var run = runner.Run(new LaunchpadRunRequest(
            ["replay", "site/demo.jsonl"],
            "lattice replay site/demo.jsonl",
            TextWriter.Null,
            TextWriter.Null));

        Assert.Equal(1, asked);
        Assert.Equal(0, run);
    }

    [Fact]
    public void LiveSimulateIsAlsoAskedThroughTheCockpit()
    {
        var asked = 0;
        var runner = CliAppRunner(new CapturingCockpit(() => asked++));

        runner.Run(new LaunchpadRunRequest(
            ["simulate", "--seed", "42", "--steps", "2"],
            "lattice simulate --seed 42 --steps 2",
            TextWriter.Null,
            TextWriter.Null));

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// The cockpit is reached through its own command line: <c>tui</c> in front of the
    /// same arguments, not a re-parsed set. A reader who configured replay in the
    /// Launchpad gets the same refusal, the same flags and the same frames as one who
    /// typed <c>lattice tui replay</c>.
    /// </summary>
    [Fact]
    public void TheCockpitIsReachedThroughItsOwnCommandLine()
    {
        Assert.Equal(
            ["tui", "replay", "site/demo.jsonl"],
            CliAppLaunchpadRunner.CockpitArguments(["replay", "site/demo.jsonl"]));

        // Already prefixed: not prefixed twice.
        Assert.Equal(
            ["tui", "replay", "site/demo.jsonl"],
            CliAppLaunchpadRunner.CockpitArguments(["tui", "replay", "site/demo.jsonl"]));
    }

    /// <summary>
    /// Everything else goes to <c>CliApp.Run</c> and nowhere else. There is no second
    /// code path per command: one runner, one argument vector, so a command cannot
    /// behave differently here than it does at a shell.
    /// </summary>
    [Fact]
    public void EveryOtherCommandGoesThroughCliAppRun()
    {
        var asked = 0;
        var runner = CliAppRunner(new CapturingCockpit(() => asked++));

        runner.Run(new LaunchpadRunRequest(
            ["generate", "--seed", "42"],
            "lattice generate --seed 42",
            TextWriter.Null,
            TextWriter.Null));

        Assert.Equal(0, asked);
    }

    private static ILaunchpadRunner CliAppRunner(ICockpitRunner cockpit) =>
        new CliAppLaunchpadRunner(cockpit, ConsoleCapabilities);

    /// <summary>The terminal a test host reports, which is non-interactive by definition.</summary>
    private static CliTerminal ConsoleCapabilities =>
        CliTerminal.For(TextWriter.Null);

    /// <summary>What one command put on the two streams.</summary>
    private readonly record struct Streams(int Exit, string Stdout, string Stderr)
    {
        /// <summary>
        /// The timestamp a benchmark artifact carries, read out of its own stdout so
        /// the comparison above removes exactly that field and nothing else.
        /// </summary>
        internal string Timestamp => System.Text.RegularExpressions.Regex
            .Match(Stdout, @"\d{4}-\d{2}-\d{2}T[0-9:.]+(\\u002B|\+)[0-9:]+")
            .Value;
    }

    private static Streams RunThrough(string command, string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runner = new CliAppLaunchpadRunner(new RefusingCockpit(), ConsoleCapabilities);

        var exit = runner.Run(new LaunchpadRunRequest(
            [command, .. arguments],
            $"lattice {command} {string.Join(' ', arguments)}",
            stdout,
            stderr));

        return new Streams(exit, stdout.ToString(), stderr.ToString());
    }

    private static Streams RunDirect(string command, string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run([command, .. arguments], stdout, stderr, ConsoleCapabilities, TuiConsole.Default);

        return new Streams(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The cockpit, stood in for: a test host's streams are redirected, so a real
    /// cockpit run would be refused, and these cases are about the argument vector
    /// rather than about what the cockpit draws.
    /// </summary>
    private sealed class CapturingCockpit(Action asked) : ICockpitRunner
    {
        public int Run(LaunchpadRunRequest request)
        {
            asked();
            return 0;
        }
    }

    /// <summary>A cockpit that refuses, the way a redirected one does.</summary>
    private sealed class RefusingCockpit : ICockpitRunner
    {
        public int Run(LaunchpadRunRequest request) => UsageError.ExitCode;
    }
}
