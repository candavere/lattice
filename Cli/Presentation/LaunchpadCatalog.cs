using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>Whether the reader asked to watch a command or to run it.</summary>
public enum RunMode
{
    /// <summary>
    /// Watch it: the command becomes a screen. A live episode runs in the cockpit
    /// under the reader's own controls, and nothing is written to a file.
    /// </summary>
    Screen,

    /// <summary>
    /// Record it: the command runs as typed, on an ordinary terminal, writing
    /// whatever artifact its <c>--out</c> names.
    /// </summary>
    Run,
}

/// <summary>One field of one command's form.</summary>
/// <param name="Label">The flag's own name, or the field's own name where there is no flag.</param>
/// <param name="Kind">What the field holds.</param>
/// <param name="Required">Whether the command refuses to run without it.</param>
/// <param name="Default">The text the field starts with, or <c>null</c> to start empty.</param>
/// <param name="ValueFormat">How the text is read when the form is validated.</param>
/// <param name="Choices">The accepted values, for a <see cref="LaunchpadValueFormat.Choice"/> field.</param>
/// <param name="Help">One line saying what the field is for.</param>
public sealed record LaunchpadField(
    string Label,
    LaunchpadFieldKind Kind,
    bool Required = false,
    string? Default = null,
    LaunchpadValueFormat ValueFormat = LaunchpadValueFormat.Text,
    string[]? Choices = null,
    string? Help = null);

/// <summary>One command the setup screen can start.</summary>
/// <param name="Name">The command's own name, as it is typed.</param>
/// <param name="Summary">One line saying what the command does.</param>
/// <param name="Fields">The form, in the order the reader meets it.</param>
/// <param name="AllowedFlags">
/// Every flag the command's parser accepts, including any the form has no field for.
/// </param>
/// <param name="RequiredFlags">The flags whose absence is a usage error.</param>
/// <param name="TakesPositionalPath">Whether the command takes a bare path token.</param>
/// <param name="RunModes">
/// The ways this command can be started, in the order the form offers them. A
/// command with one is not a choice the reader has to make.
/// </param>
public sealed record LaunchpadCommand(
    string Name,
    string Summary,
    IReadOnlyList<LaunchpadField> Fields,
    string[] AllowedFlags,
    string[] RequiredFlags,
    bool TakesPositionalPath = false,
    string[]? RunModes = null)
{
    /// <summary>
    /// The ways this command can be started. Watch first where there is a choice: a
    /// screen is the reason a reader came to the Launchpad at all, and a command
    /// that can only be run has one way and no field to choose it.
    /// </summary>
    public IReadOnlyList<string> Ways =>
        RunModes is { Length: > 0 } ? RunModes : ["run"];

    /// <summary>
    /// The run mode a way's name names, defaulting to the first. A name this catalog
    /// does not know is a run rather than a screen: an unrecognised word must not
    /// silently open a full-screen viewer.
    /// </summary>
    public RunMode ModeNamedBy(string? way) =>
        string.Equals(way, "screen", StringComparison.OrdinalIgnoreCase) ? RunMode.Screen : RunMode.Run;

    /// <summary>
    /// The text a field starts with: its own default when it has one, and the empty
    /// <summary>
    /// The text a field starts with: its own default when it has one, and the empty
    /// string otherwise. Exposed so a caller building a form does not re-decide
    /// what "no default" means.
    /// </summary>
    public string DefaultFor(string label) =>
        Fields.FirstOrDefault(field => field.Label == label)?.Default ?? string.Empty;
}

/// <summary>
/// The commands the setup screen offers, with the flags each one really takes.
/// </summary>
/// <remarks>
/// <para>
/// The flags here are the flags in <c>CliApp</c>. A field is a way of typing one,
/// not a way of describing a different one: the form's job is to make the command
/// line reachable, and the parser's job is still to be the thing that decides
/// whether a command line is runnable. So a field marks a flag as required
/// because the command refuses without it, and pre-fills a default because the
/// command has one — never because the screen thought it was a good idea.
/// </para>
/// <para>
/// Every command carries an <see cref="LaunchpadFieldKind.ExtraArguments"/>
/// field, which is what keeps the promise that no flag is unreachable: the tail is
/// passed through unexamined for anything the catalog does not model.
/// </para>
/// </remarks>
public static class LaunchpadCatalog
{
    /// <summary>The raw tail field's label, the one every command carries.</summary>
    public const string ExtraArgumentsLabel = "extra arguments";

    /// <summary>The run-mode field's label, on the commands that can be run two ways.</summary>
    public const string RunModeLabel = "run mode";

    /// <summary>
    /// The two ways a command that can be watched can also be recorded. In the
    /// order the form offers them: watching first, because a screen is the reason a
    /// reader came to the Launchpad at all.
    /// </summary>
    public static readonly string[] BothRunModes = ["screen", "run"];

    /// <summary>
    /// The one way a read-only viewer can be run. A replay has no artifact to write,
    /// so offering it a choice would be offering one option dressed as two.
    /// </summary>
    public static readonly string[] ScreenOnly = ["screen"];

    /// <summary>The path field's label, on the two commands that take a bare path.</summary>
    public const string PositionalPathLabel = "path";

    /// <summary>The command table, in the order the usage text lists the commands.</summary>
    public static readonly IReadOnlyList<LaunchpadCommand> Commands =
    [
        new(
            "generate",
            "Generate a valid map as JSON.",
            Form(
                Flag("--seed", Required: true, Format: LaunchpadValueFormat.UnsignedInteger,
                    Help: "The seed the map is generated from."),
                Flag("--min-fairness", Format: LaunchpadValueFormat.UnitInterval,
                    Help: "Retry generation until the spawn-bias index is at or below this."),
                Flag("--out", Help: "Write the map here instead of stdout.")),
            ["--seed", "--min-fairness", "--out"],
            ["--seed"]),
        new(
            "simulate",
            "Run an episode: watched live, or recorded as trajectory JSONL.",
            Watchable(
                Flag("--seed", Required: true, Format: LaunchpadValueFormat.UnsignedInteger,
                    Help: "The seed the episode runs from."),
                Flag("--steps", Format: LaunchpadValueFormat.PositiveInteger, Default: "100",
                    Help: "The tick budget."),
                Flag("--agent", Format: LaunchpadValueFormat.Choice, Default: "greedy",
                    Choices: ["greedy", "random", "mcts"],
                    Help: "The policy in seat 0."),
                Flag("--scenario", Help: "'infiltration', or a path to a scenario descriptor."),
                Flag("--out", Help: "Write the trajectory here instead of stdout."),
                Flag("--quiet", Help: "Suppress the dashboard and the lifecycle line."),
                Flag("--rules", Help: "A JSON DynamicMapRuleSet to run the episode under.")),
            ["--seed", "--steps", "--agent", "--scenario", "--out", "--quiet", "--rules"],
            ["--seed"],
            RunModes: BothRunModes),
        new(
            "render",
            "Render a recorded trajectory as ASCII or SVG.",
            Form(
                Flag("--trajectory", Help: "The trajectory to read."),
                Flag("--format", Format: LaunchpadValueFormat.Choice, Default: "ascii",
                    Choices: ["ascii", "svg"],
                    Help: "The output form."),
                Flag("--out", Help: "Write here instead of stdout.")),
            ["--trajectory", "--format", "--out"],
            []),
        new(
            "analyze",
            "Report contention and turning points for a trajectory.",
            Form(
                Flag("--trajectory", Help: "The trajectory to analyze."),
                Flag("--out", Help: "Write a Markdown report here instead of the terminal view.")),
            ["--trajectory", "--out"],
            []),
        new(
            "replay",
            "Play a recorded trajectory in the cockpit, or re-run it.",
            Watched(
                Path("The trajectory to re-run."),
                Flag("--trajectory", Help: "The same path, named explicitly."),
                Flag("--verify", Help: "Assert tick-by-tick equivalence instead of re-serializing."),
                Flag("--out", Help: "Write the re-serialized trajectory here.")),
            ["--trajectory", "--verify", "--out"],
            [],
            TakesPositionalPath: true,
            RunModes: ScreenOnly),
        new(
            "benchmark",
            "Measure the five-case workload matrix.",
            Form(
                Flag("--runs", Format: LaunchpadValueFormat.PositiveInteger,
                    Help: "Measured iterations per case."),
                Flag("--warmup", Format: LaunchpadValueFormat.PositiveInteger,
                    Help: "Warm-up ticks before measuring."),
                Flag("--steps", Format: LaunchpadValueFormat.PositiveInteger,
                    Help: "Per-iteration budget for the raw stepping cases."),
                Flag("--out", Help: "Write the JSON artifact here."),
                Flag("--commit", Help: "The commit the artifact claims."),
                Flag("--cpu", Help: "The CPU model the artifact claims.")),
            ["--runs", "--warmup", "--steps", "--out", "--commit", "--cpu"],
            []),
        new(
            "evaluate",
            "Run the paired MCTS-versus-Scout evaluation.",
            Form(
                Flag("--seed-set", Default: "heldout", Help: "The suite(s): dev, heldout, or both."),
                Flag("--rollouts", Format: LaunchpadValueFormat.PositiveInteger, Default: "32",
                    Help: "MCTS rollouts per action."),
                Flag("--seeds", Format: LaunchpadValueFormat.PositiveInteger, Default: "50",
                    Help: "Seeds per suite."),
                Flag("--out", Help: "Write the machine-readable artifact here."),
                Flag("--commit", Help: "The commit the artifact claims."),
                Flag("--scenario", Format: LaunchpadValueFormat.Choice, Default: "standard",
                    Choices: ["standard", "bottleneck"],
                    Help: "The map family, or a path to a descriptor."),
                Flag("--agent-cmd", Help: "An external agent command line to score."),
                Flag("--agent-step-timeout-ms", Format: LaunchpadValueFormat.PositiveInteger,
                    Help: "The external agent's per-step budget.")),
            ["--seed-set", "--rollouts", "--seeds", "--out", "--commit", "--scenario", "--agent-cmd", "--agent-step-timeout-ms"],
            []),
        new(
            "validate-scenario",
            "Check a scenario descriptor without running it.",
            Form(
                Path("The descriptor to check."),
                Flag("--out", Help: "The same path, named explicitly.")),
            ["--out"],
            [],
            TakesPositionalPath: true),
    ];

    /// <summary>The command with this name, or <c>null</c>.</summary>
    public static LaunchpadCommand? Find(string name) =>
        Commands.FirstOrDefault(command => command.Name == name);

    private static LaunchpadField Flag(
        string name,
        bool Required = false,
        LaunchpadValueFormat Format = LaunchpadValueFormat.Text,
        string? Default = null,
        string[]? Choices = null,
        string? Help = null) =>
        new(name, LaunchpadFieldKind.Flag, Required, Default, Format, Choices, Help);

    private static LaunchpadField Path(string? Help) =>
        new(PositionalPathLabel, LaunchpadFieldKind.PositionalPath, Help: Help);

    /// <summary>
    /// A command's fields: whatever the caller listed, then the raw tail every
    /// command carries. The tail is the reason no flag is unreachable — it is passed
    /// through unexamined for anything the catalog does not model.
    /// </summary>
    private static IReadOnlyList<LaunchpadField> Form(params LaunchpadField[] fields) =>
        WithModes(fields, ["run"]);

    /// <summary>
    /// The same list, for a command that can be watched as well as run: the run-mode
    /// field goes after the flags and before the tail.
    /// </summary>
    private static IReadOnlyList<LaunchpadField> Watchable(params LaunchpadField[] fields) =>
        WithModes(fields, BothRunModes);

    /// <summary>A command that can only be watched, so the field says so and is not a choice.</summary>
    private static IReadOnlyList<LaunchpadField> Watched(params LaunchpadField[] fields) =>
        WithModes(fields, ScreenOnly);

    /// <summary>
    /// The mode field is inserted only where there is a choice to make. A command
    /// with one way is not given a row that says "run" over and over: a form that
    /// asks a question with only one answer is a form with a row of noise on it.
    /// </summary>
    private static IReadOnlyList<LaunchpadField> WithModes(LaunchpadField[] fields, string[] ways) =>
        ways.Length > 1 ? [.. fields, Mode(ways), Tail()] : [.. fields, Tail()];

    /// <summary>The raw tail field, the one every command carries and never fills.</summary>
    private static LaunchpadField Tail() =>
        new(
            ExtraArgumentsLabel,
            LaunchpadFieldKind.ExtraArguments,
            Help: "Anything else, passed through as typed.");

    /// <summary>
    /// The run-mode field, for a command that can be started more than one way. It
    /// sits with the other fields and before the tail, so Tab reaches it in reading
    /// order rather than at the end.
    /// </summary>
    private static LaunchpadField Mode(string[] ways) =>
        new(
            RunModeLabel,
            LaunchpadFieldKind.RunMode,
            Default: ways[0],
            ValueFormat: LaunchpadValueFormat.Choice,
            Choices: ways,
            Help: ways.Length > 1
                ? "Watch it on screen, or run it and write what it produces."
                : "This command is watched, not recorded.");
}
