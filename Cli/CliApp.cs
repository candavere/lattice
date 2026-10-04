using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Analytics.Benchmarking;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Protocol;
using Lattice.Trajectories;
using Lattice.Tui;
using Lattice.Visualization;

namespace Lattice.Cli;

/// <summary>
/// The Lattice command-line driver: simulate, analyze, replay, render, and
/// benchmark subcommands over the seedable environment.
/// Turns plain `args` into one of eight subcommands — `generate`, `simulate`,
/// `render`, `analyze`, `replay`, `benchmark`, `evaluate`, `validate-scenario`
/// — and routes all
/// I/O through caller-supplied writers so it stays a pure function of its
/// inputs (no hidden state, no ambient reading of Console). The real entry
/// point (Program.cs) just forwards <see cref="Console.Out"/>/<see cref="Console.Error"/>;
/// keeping the app class separate is what lets the integration tests drive exit
/// codes and stdout deterministically.
/// <para>
/// The exit-status contract is three-way and is decided by <em>when</em> the
/// fault is known, not by which command hit it: <c>0</c> on success,
/// <see cref="UsageError.ExitCode"/> (<c>2</c>) when the command line itself is
/// not runnable, and <c>1</c> when the command was runnable and the work failed.
/// "Not runnable" is decidable from <c>args</c> alone — an unknown, duplicated,
/// bare or malformed flag, a missing required flag, an unexpected argument, an
/// unknown command, no command at all. Everything discovered by doing the work
/// (a file that is missing or unparseable, a descriptor that fails validation or
/// declares something this build cannot run, a verification divergence) is a
/// runtime failure and keeps <c>1</c>. §3.3's <c>--agent-cmd</c> usage errors are
/// a subset of the first kind, so the one documented exit status there is also
/// the one every other usage error reports.
/// </para>
/// </summary>
public static class CliApp
{
    private const int Success = 0;

    /// <summary>
    /// A runtime failure: the command line was runnable and the work it named
    /// could not be completed. Distinct from <see cref="UsageError.ExitCode"/>,
    /// which means the work never started because the invocation was wrong.
    /// </summary>
    private const int Failure = 1;

    /// <summary>
    /// The CLI's reported version. Kept in lockstep with the project
    /// <c>&lt;Version&gt;</c> elements by the release workflow's tag-parity gate.
    /// </summary>
    public const string Version = "3.1.0";

    private static readonly GeneratorConfig DefaultGeneratorConfig = new(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap);
    private const int DefaultSimulationSteps = 100;
    private const int DefaultEvaluationRollouts = 32;

    private const string DevelopmentSuite = "dev";
    private const string HeldOutSuite = "heldout";
    private const string EvaluationTargetPolicy = "MCTS";
    private const string EvaluationBaselinePolicy = "Scout";

    /// <summary>
    /// The baseline's index in the evaluation roster. The external path takes the
    /// candidate seat, so it needs the opponent by index rather than by building
    /// a second one: the baseline it plays must be the same agent the in-process
    /// study is published against, or the two results would not be commensurable.
    /// </summary>
    private const int EvaluationBaselineIndex = 1;

    /// <summary>
    /// The canonical versioned evaluation seed sets: seeds 1001..1050 are the
    /// development/validation suite (used while tuning), 2001..2050 are the
    /// held-out suite (used only to grade the locked decision rule). Neither
    /// set is derived from the other.
    /// </summary>
    private static readonly ulong[] DevelopmentEvaluationSeeds = SeedRange(1001, 50);
    private static readonly ulong[] HeldOutEvaluationSeeds = SeedRange(2001, 50);

    /// <summary>
    /// The simulation parameters the paired evaluation runs its mirrored
    /// matches under: two seats, transit timing enabled so positional
    /// advantage is priced, and a 200-tick budget — enough for any
    /// DefaultGeneratorConfig map to exhaust its resources.
    /// </summary>
    private static readonly SimulationConfig EvaluationSimulationConfig =
        new(AgentCount: 2, MaxTicks: 200, TransitSpeed: 4);

    /// <summary>
    /// The machine-readable evaluation artifact written by
    /// <c>evaluate --out</c>: the per-suite paired-study reports plus the
    /// host and provenance facts the numbers were produced on.
    /// </summary>
    /// <remarks>
    /// The last four fields carry the external-agent reporting of spec §9.3 and
    /// are <b>omitted entirely unless <c>--agent-cmd</c> was used</b>, so an
    /// in-process artifact stays byte-identical to the one that existed before
    /// this protocol did — a property the golden fixture pins by asserting the
    /// exact top-level field set rather than by tolerating new fields. Each is
    /// nullable for that reason alone: a null field does not mean "no external
    /// agent failed", it means "this run had no external agent to report on".
    /// </remarks>
    internal sealed record EvaluationArtifact(
        string? CommitSha,
        DateTime CreatedAtUtc,
        string Runtime,
        string Os,
        int Cores,
        string Architecture,
        PairedStudyReport[] Studies,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, int>? AgentFailures = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? VoidRuns = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string[]? AgentCommand = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        EvaluationAgentLimits? AgentLimits = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        EvaluationForfeit[]? AgentForfeits = null);

    /// <summary>
    /// One forfeited match: the scoreboard as it stood when the external agent's
    /// plumbing broke, beside the scores the study was actually scored from.
    /// </summary>
    /// <remarks>
    /// §9.3 scores an agent failure from 0 for the external side, so the row that
    /// reached the analyzer does not carry the lead the agent abandoned. Without
    /// this record that information would be gone, and a failure at step 200 would
    /// be indistinguishable from a handshake that never started — which is a
    /// difference a reader diagnosing a flaky agent very much needs.
    /// <para>
    /// <b>Report-only, and placed so that it cannot be otherwise.</b> The
    /// per-seed statistics are computed by <c>PairedStudy.Analyze</c> from
    /// <c>MatchResult</c> rows built before this record exists, and this array is
    /// attached to the artifact afterwards. There is no code path by which a
    /// partial score reaches the delta, the confidence interval, the outcome rates,
    /// or the decision rule.
    /// </para>
    /// <para>
    /// The scores are indexed by <em>slot</em>, like every row the runner writes,
    /// and <c>ExternalSeat</c> says which slot the external agent played — so the
    /// seat is never guessed from the numbers. An empty or absent array means no
    /// match was forfeited.
    /// </para>
    /// </remarks>
    internal sealed record EvaluationForfeit(
        ulong Seed,
        int ExternalSeat,
        string Reason,
        int PartialScoreAtSlot0,
        int PartialScoreAtSlot1,
        int ScoredExternalScore,
        int ScoredOpponentScore);

    /// <summary>
    /// The two spec §7 limits an external run was actually played under.
    /// </summary>
    /// <remarks>
    /// §3.1 requires both values in the run's output metadata, so a reported run
    /// states the time limits it played under rather than leaving them to be
    /// inferred from a version number. The JSON spelling is PascalCase to match
    /// every other field of the artifact; the names are the spec's two named
    /// limits, and <c>MatchTimeoutMs</c> is always the value computed from
    /// <c>StepTimeoutMs</c> rather than a second number somebody chose.
    /// </remarks>
    internal sealed record EvaluationAgentLimits(int StepTimeoutMs, int MatchTimeoutMs);

    /// <summary>
    /// One parsed <c>evaluate</c> invocation: the suite selection, the tick and
    /// rollout budgets, the map provider, the roster, and either the in-process
    /// candidate or the command line of an external one.
    /// </summary>
    /// <remarks>
    /// Both candidates arrive as the same request so that everything after
    /// parsing — the suite loop, the mirrored seatings, the artifact, the summary
    /// lines — is one code path. What differs between them is only who supplies
    /// the action for the candidate seat, which is exactly the difference §9.4
    /// says must not reach the statistics.
    /// </remarks>
    private sealed record EvaluateRequest(
        string[] Suites,
        int Rollouts,
        int SeedCap,
        string? Commit,
        string Scenario,
        Func<ulong, MapGraph> MapFactory,
        IReadOnlyList<IAgentFactory> Teams,
        string? AgentCommand,
        int AgentStepTimeoutMs);

    /// <summary>
    /// The simulation parameters the <c>--min-fairness</c> gate runs its
    /// mirrored greedy episodes under: two seats, transit timing enabled so
    /// positional advantage is priced, 200-tick budget — enough for any
    /// DefaultGeneratorConfig map to exhaust its resources.
    /// </summary>
    private static readonly SimulationConfig FairnessSimulationConfig =
        new(AgentCount: 2, MaxTicks: 200, TransitSpeed: 8);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, CliTerminal.For(stderr));

    /// <summary>
    /// The overload that takes the terminal as a value, so the lifecycle output
    /// and every other terminal-dependent decision can be driven from a test
    /// without a real terminal.
    /// </summary>
    /// <remarks>
    /// The two overloads exist so that the eight existing subcommands keep
    /// their exact stdout bytes and exit codes for every current caller: the
    /// three-argument form resolves the terminal from the writer it was given,
    /// and a <see cref="StringWriter"/> — what the tests and any redirecting
    /// script supply — resolves to a non-interactive terminal, which is silent.
    /// </remarks>
    /// <param name="args">The command line.</param>
    /// <param name="stdout">Where the command's own output goes, byte for byte as before.</param>
    /// <param name="stderr">Where diagnostics and the lifecycle lines go.</param>
    /// <param name="terminal">What the CLI believes about the attached terminal.</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CliTerminal terminal)
    {
        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return UsageError.ExitCode;
        }

        if (args[0] is "-h" or "--help")
        {
            WriteUsage(stdout);
            return Success;
        }

        if (args is ["--version"] or ["-v"])
        {
            stdout.WriteLine(Version);
            return Success;
        }

        if (IsKnownCommand(args[0]) && args[1..].Any(arg => arg is "-h" or "--help"))
        {
            WriteUsage(stdout);
            return Success;
        }

        // Every command that can take a while is wrapped, so the working line,
        // the success line and the failure line are decided in exactly one
        // place and cannot be forgotten by a future subcommand. The wrapper is
        // inert unless stderr is an interactive terminal, and it never touches
        // stdout.
        //
        // --quiet is read here rather than from each command's own flag parsing
        // because the scope has to be opened before the command runs, and
        // because a suppressed dashboard must suppress the lifecycle line too:
        // --quiet is the caller's statement that they want no incidental
        // output. Only the commands that accept it are considered, so a literal
        // "--quiet" appearing as another command's value cannot silence it.
        using var lifecycle = CommandLifecycleScope.Begin(
            args[0],
            stderr,
            terminal,
            quiet: AcceptsQuiet(args[0]) && args.Skip(1).Contains("--quiet", StringComparer.Ordinal));

        // The command's own diagnostics go through the indicator's writer when a
        // live line is being repainted, so a summary line cannot land in the
        // middle of a spinner frame. Inert otherwise, and byte-identical to
        // before: the same writer object reaches the same code.
        var commandErrors = lifecycle.Interleave(stderr);

        return args[0] switch
        {
            "generate" => lifecycle.Run(() => Generate(args[1..], stdout, commandErrors)),
            "simulate" => lifecycle.Run(() => Simulate(args[1..], stdout, commandErrors)),
            "render" => lifecycle.Run(() => Render(args[1..], stdout, commandErrors)),
            "analyze" => lifecycle.Run(() => Analyze(args[1..], stdout, commandErrors)),
            "replay" => lifecycle.Run(() => Replay(args[1..], stdout, commandErrors)),
            "benchmark" => lifecycle.Run(() => RunBenchmark(args[1..], stdout, commandErrors)),
            "evaluate" => lifecycle.Run(() => Evaluate(args[1..], stdout, commandErrors, lifecycle)),
            "validate-scenario" => lifecycle.Run(() => ValidateScenario(args[1..], stdout, commandErrors)),
            "tui" => lifecycle.Run(() => Tui(args[1..], stdout, commandErrors)),
            _ => UnknownCommand(args[0], stderr),
        };
    }

    /// <summary>
    /// The interactive terminal viewer. One subcommand today — <c>replay</c> — which
    /// plays a recorded trajectory in a read-only cockpit and runs no simulation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the command line can be wrong about is decided before anything is
    /// read: the subcommand, the arity, an unknown flag. Each of those is a
    /// <see cref="UsageError"/> and reports the usage status. A path that is missing
    /// or unreadable is a fact discovered while doing the work, so it keeps the
    /// runtime-failure status — the same split every other command here makes.
    /// </para>
    /// <para>
    /// The terminal itself is the host's business, not the parser's: whether the
    /// streams can carry a full-screen view is decided inside
    /// <see cref="TuiHost"/>, which refuses a redirected run with one line of
    /// reason and the usage status.
    /// </para>
    /// </remarks>
    private static int Tui(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0 || args[0] != "replay")
            {
                WriteTuiUsage(stderr);
                return UsageError.ExitCode;
            }

            // --ascii is a boolean switch: strip it before the tokens are read as
            // paths, so it can never be mistaken for one.
            var ascii = args.Skip(1).Contains("--ascii", StringComparer.Ordinal);
            var tokens = args.Skip(1).Where(argument => argument != "--ascii").ToArray();

            foreach (var token in tokens)
            {
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageError($"unknown flag '{token}'.");
                }
            }

            if (tokens.Length == 0)
            {
                WriteTuiUsage(stderr);
                return UsageError.ExitCode;
            }

            if (tokens.Length > 1)
            {
                throw new UsageError($"unexpected argument '{tokens[1]}'.");
            }

            // Read-only, and through the same reader every other command uses, so a
            // malformed file is rejected here rather than by the viewer.
            var document = ReplaySource.ReadFile(tokens[0]);

            using var keys = new KeyQueue(Console.In);
            return TuiHost.Run(new TuiHostRequest(
                document,
                stdout,
                stderr,
                CapabilityDetector.Detect(),
                ascii,
                new TerminalGuardSessionFactory(),
                keys,
                new MonotonicClock())).ExitCode;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// The viewer's own usage line, on stderr only. Deliberately one line: the
    /// help text belongs with the rest of the CLI's help, which this stage does not
    /// touch.
    /// </summary>
    private static void WriteTuiUsage(TextWriter sink) =>
        sink.WriteLine("usage: lattice tui replay <trajectory.jsonl> [--ascii]");

    private static bool IsKnownCommand(string command) =>
        command is "generate" or "simulate" or "render" or "analyze"
            or "replay" or "benchmark" or "evaluate" or "validate-scenario" or "tui";

    /// <summary>
    /// Whether <paramref name="command"/> accepts <c>--quiet</c>. Only
    /// <c>simulate</c> does today: it is the one command with incidental output
    /// beyond its artifact, and widening the set would widen what a stray
    /// <c>--quiet</c> in another command's argument list could silence.
    /// </summary>
    private static bool AcceptsQuiet(string command) => command == "simulate";

    private static int Generate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (flags, positionals) = ParseFlags(args, "--seed", "--min-fairness", "--out");
            GuardNoPositionals(positionals);
            var seed = ParseULong(Require(flags, "--seed"), "--seed");

            MapGraph map;
            if (flags.TryGetValue("--min-fairness", out var fairnessThresholdText))
            {
                var threshold = ParseFairnessThreshold(fairnessThresholdText);
                var evaluator = new Lattice.Analytics.MapFairnessEvaluator(FairnessSimulationConfig);
                double? measuredBias = null;
                map = MapGenerator.Generate(
                    seed,
                    DefaultGeneratorConfig,
                    candidate =>
                    {
                        var bias = evaluator.Evaluate(candidate, seed).SpawnBiasIndex;
                        measuredBias = bias;
                        return bias <= threshold;
                    });
                var invariant = CultureInfo.InvariantCulture;
                stderr.WriteLine(
                    $"spawn bias index {measuredBias!.Value.ToString("0.###", invariant)}" +
                    $" <= fair-threshold {threshold.ToString("0.###", invariant)}; map accepted");
            }
            else
            {
                map = MapGenerator.Generate(seed, DefaultGeneratorConfig);
            }

            return WriteOutput(flags, "--out", JsonSerializer.Serialize(map), stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    private static int Simulate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            // --quiet is a boolean switch: strip it before the key/value flag
            // parser so it never demands a trailing value.
            var quiet = args.Contains("--quiet", StringComparer.Ordinal);
            if (quiet)
            {
                args = args.Where(arg => arg != "--quiet").ToArray();
            }

            var (flags, positionals) = ParseFlags(args, "--seed", "--steps", "--agent", "--scenario", "--out", "--rules");
            GuardNoPositionals(positionals);
            var seed = ParseULong(Require(flags, "--seed"), "--seed");
            var steps = flags.TryGetValue("--steps", out var stepsText)
                ? ParsePositiveInt(stepsText, "--steps")
                : DefaultSimulationSteps;

            // A --scenario value is EITHER a path to a descriptor file or a
            // built-in name — never both, and never guessed between. The test
            // is a path separator, not a case-insensitive match against a name,
            // so a file that happens to be called "infiltration" stays a file
            // and a typo'd name is reported as a name rather than as a missing
            // file.
            if (flags.TryGetValue("--scenario", out var scenarioText) && LooksLikePath(scenarioText))
            {
                return SimulateScenarioFile(flags, scenarioText, seed, steps, quiet, stdout, stderr);
            }

            var scenario = flags.TryGetValue("--scenario", out var namedText)
                ? namedText.ToLowerInvariant()
                : "";

            if (scenario == InfiltrationScenario.ScenarioName)
            {
                return SimulateInfiltration(flags, seed, steps, quiet, stdout, stderr);
            }

            if (scenario.Length > 0)
            {
                throw new UsageError(
                    $"invalid --scenario '{scenarioText}' (expected 'infiltration' or a path to a scenario file).");
            }

            var agent = flags.TryGetValue("--agent", out var agentText)
                ? agentText.ToLowerInvariant()
                : "greedy";

            var rules = flags.TryGetValue("--rules", out var rulesPath)
                ? LoadRules(rulesPath)
                : DynamicMapRuleSet.None;

            var config = new SimulationConfig(AgentCount: 2, MaxTicks: steps);
            var map = MapGenerator.Generate(seed, DefaultGeneratorConfig);
            var agent0 = agent switch
            {
                "greedy" => (IAgent)new GreedyCollectorAgent(0),
                "random" => new RandomAgent(0, new Rng(seed)),
                "mcts" => new MctsAgent(0, config, seed, new MctsSearchConfig(), rules),
                _ => throw new UsageError(
                    $"invalid --agent '{agentText}' (expected 'greedy', 'random', or 'mcts')."),
            };
            var contenders = new IAgent[] { agent0, new RandomAgent(1, new Rng(seed)) };
            var stopwatch = Stopwatch.StartNew();
            var scenarioResult = ScenarioRunner.Run(map, config, contenders, maxSteps: steps, rules: rules);
            stopwatch.Stop();

            var jsonl = new StringBuilder();
            using (var sink = new StringWriter(jsonl))
            {
                TrajectoryWriter.Record(
                    map, config, seed, scenarioResult.Turns, sink, rules: rules,
                    scenarioSha256: BuiltInScenarioDigest(BuiltInScenarioCatalog.Standard));
            }

            var lastInfo = scenarioResult.Results[^1].Info;
            stderr.WriteLine(
                $"recorded {scenarioResult.Metrics.TotalSteps} steps" +
                $" ({(lastInfo.IsTerminal ? lastInfo.Reason : "budget-reached")}," +
                $" winner: {(lastInfo.WinnerAgentId.HasValue ? $"agent {lastInfo.WinnerAgentId}" : "none")})");

            var trajectory = jsonl.ToString().TrimEnd();
            var exit = WriteOutput(flags, "--out", trajectory, stdout, stderr);
            if (!quiet)
            {
                var rows = scenarioResult.Metrics.Agents
                    .Select(metrics => new AgentScoreboardRow(
                        metrics.AgentId,
                        "Collector",
                        contenders[metrics.AgentId].GetType().Name,
                        metrics.Score,
                        GenericStatus(lastInfo, metrics.AgentId),
                        metrics.Moves))
                    .ToArray();
                RenderDashboard(
                    stderr,
                    map,
                    config,
                    "collection skirmish",
                    seed,
                    scenarioResult,
                    stopwatch.Elapsed.TotalMilliseconds,
                    rows,
                    flags.TryGetValue("--out", out var path) ? path : null,
                    trajectory,
                    rules);
            }

            return exit;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// True when a <c>--scenario</c> value is a file path rather than a
    /// built-in name. The test is a directory separator, on either platform's
    /// convention, because that is what distinguishes them unambiguously: a
    /// built-in name is a bare token, and a descriptor is a file. Deliberately
    /// not a case-insensitive name comparison — that would make the two forms
    /// ambiguous and could silently reinterpret a file as a name.
    /// </summary>
    private static bool LooksLikePath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// The SHA-256 of a built-in descriptor's exact committed bytes, which is
    /// the provenance a built-in recording carries. Computed from the embedded
    /// copy of the file; a test asserts the embedded bytes equal the committed
    /// file's, so this digest identifies a file that is really in the tree.
    /// </summary>
    private static string BuiltInScenarioDigest(string id) =>
        ScenarioLoader.ComputeDigest(BuiltInScenarioCatalog.Read(id));

    /// <summary>
    /// Records an episode from a declarative scenario descriptor: the file is
    /// the authority for the map, the roster, the simulation config, and the
    /// victory/scoring choice, and the recording carries the file's SHA-256 so
    /// a reader can name the bytes behind it.
    /// <para>
    /// The descriptor's <c>StepLimit</c> and <c>--steps</c> are two statements
    /// about the same thing, so the combination is stated rather than resolved
    /// by precedence: passing both is refused, because either silently winning
    /// would make the recorded budget different from the one the caller asked
    /// for without saying so. <c>--agent</c> is likewise refused — the roster is
    /// the descriptor's, and overriding one seat of a declared roster would make
    /// the run something the file does not describe.
    /// </para>
    /// </summary>
    private static int SimulateScenarioFile(
        Dictionary<string, string> flags,
        string path,
        ulong seed,
        int steps,
        bool quiet,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (flags.ContainsKey("--agent"))
        {
            throw new UsageError(
                "--agent cannot be used with a scenario file: the roster is declared by the descriptor's 'Slots'.");
        }

        ScenarioDescriptor descriptor;
        string digest;
        try
        {
            (descriptor, digest) = ScenarioLoader.LoadFile(path);
        }
        catch (ScenarioValidationException ex)
        {
            foreach (var error in ex.Errors)
            {
                stderr.WriteLine($"scenario error: {error.FieldPath}: {error.Message}");
            }

            return Failure;
        }

        if (flags.ContainsKey("--steps"))
        {
            throw new UsageError(
                $"--steps cannot be combined with a scenario file: the descriptor declares 'Simulation.StepLimit' " +
                $"({descriptor.StepLimit}) and overriding it would make the recorded budget differ from the declared one.");
        }

        var config = new SimulationConfig(
            descriptor.AgentCount, descriptor.StepLimit, TransitSpeed: descriptor.TransitSpeed);
        var map = descriptor.BuildMap(seed);
        var roster = BuildRoster(descriptor, config, seed, recordPerceptions: out var recordPerceptions);

        var stopwatch = Stopwatch.StartNew();
        var result = ScenarioRunner.Run(
            map, config, roster, maxSteps: descriptor.StepLimit, recordPerceptions: recordPerceptions);
        stopwatch.Stop();

        var jsonl = new StringBuilder();
        using (var sink = new StringWriter(jsonl))
        {
            TrajectoryWriter.Record(
                map, config, seed, result.Turns, sink,
                scenario: descriptor.Id,
                agentRoles: descriptor.Slots.Select(slot => slot.Role ?? slot.Policy).ToArray(),
                perceptions: result.Perceptions,
                scenarioSha256: digest);
        }

        var lastInfo = result.Results[^1].Info;
        stderr.WriteLine(
            $"scenario {descriptor.Id} (sha256 {digest})");
        stderr.WriteLine(
            $"recorded {result.Metrics.TotalSteps} steps" +
            $" ({(lastInfo.IsTerminal ? lastInfo.Reason : "budget-reached")}," +
            $" winner: {(lastInfo.WinnerAgentId.HasValue ? $"agent {lastInfo.WinnerAgentId}" : "none")})");

        var trajectory = jsonl.ToString().TrimEnd();
        var exit = WriteOutput(flags, "--out", trajectory, stdout, stderr);
        if (!quiet)
        {
            var rows = result.Metrics.Agents
                .Select(metrics => new AgentScoreboardRow(
                    metrics.AgentId,
                    descriptor.Slots[metrics.AgentId].Role ?? descriptor.Slots[metrics.AgentId].Policy,
                    roster[metrics.AgentId].GetType().Name,
                    metrics.Score,
                    GenericStatus(lastInfo, metrics.AgentId),
                    metrics.Moves))
                .ToArray();
            RenderDashboard(
                stderr, map, config, descriptor.Id, seed, result,
                stopwatch.Elapsed.TotalMilliseconds, rows,
                flags.TryGetValue("--out", out var outPath) ? outPath : null,
                trajectory,
                DynamicMapRuleSet.None);
        }

        return exit;
    }

    /// <summary>
    /// Builds one agent per declared slot, in slot order, through each policy's
    /// real constructor — so a descriptor cannot name a configuration the agent
    /// does not have. Also reports whether the whole roster can carry
    /// decision-time perceptions, which is all-or-nothing by the step contract
    /// and is therefore a property of the roster rather than a per-slot choice.
    /// </summary>
    private static IAgent[] BuildRoster(
        ScenarioDescriptor descriptor,
        SimulationConfig config,
        ulong seed,
        out bool recordPerceptions)
    {
        var agents = new IAgent[descriptor.Slots.Count];
        for (var i = 0; i < agents.Length; i++)
        {
            var slot = descriptor.Slots[i];
            agents[i] = slot.Policy switch
            {
                "greedy" => new GreedyCollectorAgent(slot.Slot),
                "random" => new RandomAgent(slot.Slot, new Rng(seed)),
                "mcts" => new MctsAgent(slot.Slot, config, seed, new MctsSearchConfig()),
                "scout" => new ScoutCollectorAgent(slot.Slot, slot.Vision),
                "sentry" => new SentryPatrolAgent(slot.Slot, slot.RivalSlot!.Value, vision: slot.Vision),
                "infiltrator" => new InfiltratorAgent(slot.Slot, slot.RivalSlot!.Value, vision: slot.Vision),
                // Not a UsageError on purpose: the command line named a file
                // that parsed cleanly, and what is wrong is a fact discovered
                // inside that file. It is a runtime failure of this build
                // against this descriptor, not an unrunnable invocation.
                _ => throw new ArgumentException(
                    $"scenario slot {slot.Slot} names policy '{slot.Policy}', which this build cannot construct."),
            };
        }

        recordPerceptions = descriptor.Slots.All(
            slot => ScenarioMechanics.PerceivingPolicies.Contains(slot.Policy, StringComparer.Ordinal));

        return agents;
    }

    /// <summary>
    /// Runs the "Dungeon Infiltration &amp; Sentry Patrol" demonstration
    /// scenario (<see cref="Lattice.Agents.InfiltrationScenario"/>) and records
    /// it as trajectory JSONL whose header carries the scenario name and the
    /// tactical roster, so the renderers and web viewer can label the guard and
    /// the rogue. Same seed, same serialized trajectory under the runtime contract.
    /// </summary>
    private static int SimulateInfiltration(
        Dictionary<string, string> flags,
        ulong seed,
        int steps,
        bool quiet,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (flags.ContainsKey("--agent"))
        {
            throw new UsageError("--agent cannot be used with --scenario infiltration (the roster is fixed: Sentry vs Infiltrator).");
        }

        if (flags.ContainsKey("--rules"))
        {
            throw new UsageError("--rules cannot be used with --scenario infiltration (the scenario owns its topology).");
        }

        var stopwatch = Stopwatch.StartNew();
        var run = InfiltrationScenario.Run(seed, steps);
        stopwatch.Stop();

        var jsonl = new StringBuilder();
        using (var sink = new StringWriter(jsonl))
        {
            // The decision-time perceptions come from the agents' own filters
            // as they decided, carried on the run; the writer records them and
            // declares the cone each one was projected through.
            TrajectoryWriter.Record(
                run.Map,
                run.Config,
                seed,
                run.Base.Turns,
                sink,
                scenario: InfiltrationScenario.ScenarioName,
                agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
                perceptions: run.Base.Perceptions,
                scenarioSha256: BuiltInScenarioDigest(BuiltInScenarioCatalog.Infiltration));
        }

        var lastInfo = run.Base.Results[^1].Info;
        stderr.WriteLine(
            $"recorded {run.Base.Metrics.TotalSteps} steps" +
            $" ({(lastInfo.IsTerminal ? lastInfo.Reason : "budget-reached")}," +
            $" outcome: {run.Outcome.Status})");

        var trajectory = jsonl.ToString().TrimEnd();
        var exit = WriteOutput(flags, "--out", trajectory, stdout, stderr);
        if (!quiet)
        {
            var rows = new[]
            {
                new AgentScoreboardRow(
                    InfiltrationScenario.SentryAgentId,
                    InfiltrationScenario.SentryRole,
                    nameof(SentryPatrolAgent),
                    run.Sentry.Score,
                    run.Outcome.Intercepted
                        ? "Interception"
                        : run.Outcome.Exfiltrated ? "Evaded" : "On Patrol",
                    run.Sentry.Moves),
                new AgentScoreboardRow(
                    InfiltrationScenario.InfiltratorAgentId,
                    InfiltrationScenario.InfiltratorRole,
                    nameof(InfiltratorAgent),
                    run.Infiltrator.Score,
                    run.Outcome.Intercepted
                        ? "Intercepted"
                        : run.Outcome.Exfiltrated ? "Extracted" : "Active",
                    run.Infiltrator.Moves),
            };
            RenderDashboard(
                stderr,
                run.Map,
                run.Config,
                "dungeon infiltration & sentry patrol",
                seed,
                run.Base,
                stopwatch.Elapsed.TotalMilliseconds,
                rows,
                flags.TryGetValue("--out", out var path) ? path : null,
                trajectory,
                DynamicMapRuleSet.None);
        }

        return exit;
    }

    /// <summary>
    /// Builds the scoreboard status label for a generic (non-scenario) run:
    /// Winner/Defeated on a decided terminal tick, Active otherwise.
    /// </summary>
    private static string GenericStatus(Info lastInfo, int agentId) =>
        lastInfo.IsTerminal
            ? !lastInfo.WinnerAgentId.HasValue
                ? "Draw"
                : lastInfo.WinnerAgentId.Value == agentId ? "Winner" : "Defeated"
            : "Active";

    /// <summary>
    /// Emits the end-of-run ANSI dashboard to <paramref name="sink"/>: run
    /// header, scoreboard, choke contention, and the output footer with file
    /// size and replay-verified determinism status.
    /// </summary>
    private static void RenderDashboard(
        TextWriter sink,
        MapGraph map,
        SimulationConfig config,
        string scenarioLabel,
        ulong seed,
        ScenarioResult result,
        double elapsedMilliseconds,
        IReadOnlyList<AgentScoreboardRow> rows,
        string? trajectoryPath,
        string trajectory,
        DynamicMapRuleSet rules)
    {
        var verified = VerifyDeterministicReplay(map, config, result, rules);
        SimulationConsoleRenderer.RenderDashboard(
            sink,
            new RunHeaderInfo(
                scenarioLabel,
                seed,
                map.Zones.Length,
                result.Metrics.TotalSteps,
                elapsedMilliseconds),
            rows,
            ComputeContention(map, config, result, rules),
            new OutputFooterInfo(
                trajectoryPath,
                trajectoryPath is null ? null : SimulationConsoleRenderer.ByteCount(trajectory) + 1,
                verified,
                verified ? result.Results.Length : 0));
    }

    /// <summary>
    /// Replays the recorded turns through the pure step function — preserving
    /// any dynamic topology rules the episode ran under — and compares the
    /// complete stream of serialized step results against the recorded ones.
    /// Every tick's serialized result must match, not just the final tick, so a
    /// divergence anywhere in the episode is caught. When this returns true
    /// the footer reports "serialized StepResult replay equivalence verified
    /// across all N steps" — equivalence of the per-step serialized result
    /// stream, not raw file-byte identity of the artifact across hosts.
    /// </summary>
    private static bool VerifyDeterministicReplay(
        MapGraph map,
        SimulationConfig config,
        ScenarioResult result,
        DynamicMapRuleSet rules)
    {
        var replay = SimulationDriver.Play(map, config, result.Turns, rules);
        if (replay.Count != result.Results.Length)
        {
            return false;
        }

        for (var tick = 0; tick < replay.Count; tick++)
        {
            if (JsonSerializer.Serialize(replay[tick]) != JsonSerializer.Serialize(result.Results[tick]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reconstructs per-choke contention from the recorded turns and results:
    /// an attempt is a Move action across a choke edge by an agent free to
    /// act; the attempt is denied when capacity gating left the agent in its
    /// origin zone with no transit started. The replay simulation steps the
    /// episode's dynamic topology rules in lockstep, so each attempt's
    /// reported capacity is the override active on that tick (portcullises and
    /// event locks included), falling back to the base map value when static.
    /// </summary>
    private static IReadOnlyList<ChokeContentionRow> ComputeContention(
        MapGraph map,
        SimulationConfig config,
        ScenarioResult result,
        DynamicMapRuleSet rules)
    {
        var stats = new Dictionary<(int Lo, int Hi), (int Capacity, int Attempts, int Denials)>();
        var state = Simulation.CreateInitial(map, config, rules);
        var previous = state.Agents;

        for (var tick = 0; tick < result.Results.Length; tick++)
        {
            var turn = result.Turns[tick];
            var next = result.Results[tick].Observations[0].AgentStates;
            for (var agentId = 0; agentId < turn.Length; agentId++)
            {
                var action = turn[agentId];
                var before = previous[agentId];
                if (action.Kind != ActionKind.Move || before.Transit is not null)
                {
                    continue;
                }

                var chokeIndex = FindChoke(map, before.ZoneId, action.ZoneId);
                if (chokeIndex < 0)
                {
                    continue;
                }

                var key = (Math.Min(before.ZoneId, action.ZoneId), Math.Max(before.ZoneId, action.ZoneId));
                stats.TryGetValue(key, out var entry);
                var after = next[agentId];
                var denied = after.ZoneId == before.ZoneId && after.Transit is null;
                var capacity = state.Dynamics.EffectiveChokeCapacity(map, chokeIndex);
                stats[key] = (capacity, entry.Attempts + 1, entry.Denials + (denied ? 1 : 0));
            }

            previous = next;
            state = Simulation.Step(state, turn, config).NextState;
        }

        return stats
            .OrderBy(pair => pair.Key)
            .Select(pair => new ChokeContentionRow(
                pair.Key.Item1,
                pair.Key.Item2,
                pair.Value.Capacity,
                pair.Value.Attempts,
                pair.Value.Denials))
            .ToArray();
    }

    /// <summary>The index of the choke connecting the two zones, or -1 when they are not adjacent.</summary>
    private static int FindChoke(MapGraph map, int fromZone, int toZone)
    {
        for (var index = 0; index < map.ChokePoints.Length; index++)
        {
            var choke = map.ChokePoints[index];
            if ((choke.FromZoneId == fromZone && choke.ToZoneId == toZone)
                || (choke.ToZoneId == fromZone && choke.FromZoneId == toZone))
            {
                return index;
            }
        }

        return -1;
    }

    private static int Render(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (flags, positionals) = ParseFlags(args, "--trajectory", "--format", "--out");
            GuardNoPositionals(positionals);
            var trajectoryPath = Require(flags, "--trajectory");
            var format = flags.TryGetValue("--format", out var formatText)
                ? formatText.ToLowerInvariant()
                : "ascii";
            if (format is not ("ascii" or "svg"))
            {
                throw new UsageError(
                    $"invalid --format '{formatText}' (expected 'ascii' or 'svg').");
            }

            string content;
            using (var reader = new StreamReader(trajectoryPath))
            {
                content = format == "svg"
                    ? SvgTrajectoryExporter.Export(reader)
                    : RenderAscii(reader);
            }

            return WriteOutput(flags, "--out", content, stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    private static int Analyze(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (flags, positionals) = ParseFlags(args, "--trajectory", "--out");
            GuardNoPositionals(positionals);
            var trajectoryPath = Require(flags, "--trajectory");

            TrajectoryRecording recording;
            using (var reader = new StreamReader(trajectoryPath))
            {
                recording = TrajectoryReader.Read(reader);
            }

            if (flags.TryGetValue("--out", out var outPath))
            {
                File.WriteAllText(outPath, Lattice.Analytics.ReportGenerator.Report(recording) + "\n");
                stderr.WriteLine($"wrote {outPath}");
            }
            else
            {
                stdout.WriteLine(Lattice.Analytics.ReportGenerator.TerminalReport(recording));
            }

            return Success;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// Replays a recorded trajectory and (with <c>--verify</c>) asserts
    /// per-step serialized <see cref="StepResult"/> equivalence against the
    /// recording. With <c>--verify</c> the exit code is 0 only when every
    /// recorded tick's serialized result matches; a divergence or structural
    /// defect returns a non-zero exit. Without <c>--verify</c> it re-serializes
    /// the recording to stdout so a caller can inspect or re-host it.
    /// </summary>
    private static int Replay(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            // --verify is a boolean switch: strip it before the key/value flag
            // parser so it never demands a trailing value.
            var verify = args.Contains("--verify", StringComparer.Ordinal);
            if (verify)
            {
                args = args.Where(arg => arg != "--verify").ToArray();
            }

            var (flags, positionals) = ParseFlags(args, "--trajectory", "--out");
            var trajectoryPath = flags.TryGetValue("--trajectory", out var flaggedPath)
                ? flaggedPath
                : positionals.Count == 1 ? positionals[0] : null;
            if (trajectoryPath is null)
            {
                throw new UsageError(
                    "missing trajectory path (pass a positional path or --trajectory <file>).");
            }

            if (positionals.Count > (flags.ContainsKey("--trajectory") ? 0 : 1))
            {
                throw new UsageError($"unexpected argument '{positionals[^1]}'.");
            }

            TrajectoryRecording recording;
            using (var reader = new StreamReader(trajectoryPath))
            {
                recording = TrajectoryReader.Read(reader);
            }

            var report = TrajectoryReplay.VerifyDetailed(recording);
            if (report.Problems.Count > 0)
            {
                foreach (var problem in report.Problems)
                {
                    stderr.WriteLine($"replay error: {problem}");
                }

                return Failure;
            }

            foreach (var notice in report.Notices)
            {
                stderr.WriteLine($"replay notice: {notice}");
            }

            if (!verify)
            {
                TrajectoryWriter.Write(recording, stdout);
                return Success;
            }

            var hashed = recording.Steps.Count(step => step.StateHash is not null);
            var stateCoverage = hashed == recording.Steps.Length && hashed > 0
                ? $", {hashed} state hash(es) matched"
                : string.Empty;
            var perceived = recording.Steps.Count(step => step.Perceptions is not null);
            var perceptionCoverage = perceived == recording.Steps.Length && perceived > 0
                ? $", {perceived} decision-time perception(s) matched"
                : string.Empty;
            stderr.WriteLine(
                $"replay verified: {recording.Steps.Length} step(s) serialized-equivalent{stateCoverage}{perceptionCoverage} " +
                $"(seed {recording.Header.Seed}, schema v{recording.Header.SchemaVersion}).");
            return Success;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// Checks a declarative scenario descriptor and reports what it declares,
    /// without running an episode and without writing an artifact. The command
    /// exists so an author can check a descriptor in isolation, and it is
    /// deliberately side-effect free: it reads the file once (the same read the
    /// SHA-256 is taken over), validates it, and prints. Nothing is simulated,
    /// nothing is written, and no file is created even on the success path.
    /// </summary>
    private static int ValidateScenario(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (flags, positionals) = ParseFlags(args, "--out");

            // Arity is settled before the path is read, so "you passed two
            // paths" and "you passed none" are two different messages rather
            // than both collapsing into "missing path" — a caller who passed an
            // extra argument needs to be told which argument was extra.
            var allowedPositionals = flags.ContainsKey("--out") ? 0 : 1;
            if (positionals.Count > allowedPositionals)
            {
                throw new UsageError(
                    $"unexpected argument '{positionals[allowedPositionals]}'.");
            }

            var path = flags.TryGetValue("--out", out var flagged)
                ? flagged
                : positionals.Count == 1 ? positionals[0] : null;
            if (path is null)
            {
                throw new UsageError(
                    "missing scenario path (pass a positional path or --out <file>).");
            }

            (ScenarioDescriptor descriptor, string digest) scenario;
            try
            {
                scenario = ScenarioLoader.LoadFile(path);
            }
            catch (ScenarioValidationException ex)
            {
                foreach (var error in ex.Errors)
                {
                    stderr.WriteLine($"scenario error: {error.FieldPath}: {error.Message}");
                }

                return Failure;
            }

            var invariant = CultureInfo.InvariantCulture;
            stderr.WriteLine($"scenario {scenario.descriptor.Id} is valid (schema v{scenario.descriptor.SchemaVersion.ToString(invariant)})");
            stderr.WriteLine($"  sha256         {scenario.digest}");
            stderr.WriteLine($"  map            {DescribeMap(scenario.descriptor.Map)}");
            stderr.WriteLine(
                $"  simulation     {scenario.descriptor.AgentCount.ToString(invariant)} agent(s), " +
                $"step limit {scenario.descriptor.StepLimit.ToString(invariant)}, " +
                $"transit speed {scenario.descriptor.TransitSpeed.ToString(invariant)}");
            foreach (var slot in scenario.descriptor.Slots)
            {
                stderr.WriteLine(
                    $"  slot {slot.Slot.ToString(invariant)}         {slot.Policy}" +
                    (slot.Role is null ? string.Empty : $" ({slot.Role})"));
            }

            stderr.WriteLine(
                $"  victory        {scenario.descriptor.VictoryCondition}   " +
                $"scoring {scenario.descriptor.ScoringScheme}");
            return Success;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// One line naming where the scenario's map comes from, so the reader can
    /// tell a seeded family from a hand-authored graph without opening the
    /// file. A generated map's concrete topology is not resolved here: that
    /// needs a seed, and this command deliberately runs no simulation.
    /// </summary>
    private static string DescribeMap(ScenarioMapSpec map) => map switch
    {
        ScenarioMapSpec.Static => "hand-authored (static)",
        ScenarioMapSpec.Generated generated => $"generated family '{generated.Family}'",
        _ => "unknown",
    };

    private static int RunBenchmark(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var (flags, positionals) = ParseFlags(args, "--runs", "--warmup", "--steps", "--out", "--commit", "--cpu");
            GuardNoPositionals(positionals);
            var runs = flags.TryGetValue("--runs", out var runsText)
                ? ParsePositiveInt(runsText, "--runs")
                : WorkloadCatalog.DefaultRuns;
            var warmupSteps = flags.TryGetValue("--warmup", out var warmupText)
                ? ParsePositiveInt(warmupText, "--warmup")
                : WorkloadCatalog.DefaultWarmupSteps;
            var steps = flags.TryGetValue("--steps", out var stepsText)
                ? ParsePositiveInt(stepsText, "--steps")
                : (int?)null;
            var commit = flags.TryGetValue("--commit", out var commitText) ? commitText : null;
            var cpu = flags.TryGetValue("--cpu", out var cpuText) ? cpuText : null;

            var workloads = WorkloadCatalog.BuildAll(steps);
            var metadata = EnvironmentSample.Capture(commit, cpu);
            var result = BenchmarkHarness.RunAll(workloads, metadata, runs, warmupSteps);

            var json = JsonArtifact.SerializeIndented(result);
            return WriteOutput(flags, "--out", json, stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// Runs the mirrored-seat paired evaluation of the MCTS policy against the
    /// Scout heuristic baseline over a canonical seed suite, writes the
    /// machine-readable report artifact, and prints a per-suite summary to
    /// stderr. The baseline seats are mirrored per seed so spawn bias cancels
    /// out of the paired delta; the decision rule (mean paired delta &gt; 0 with
    /// a 95% CI lower bound &gt; 0) is graded on the held-out suite.
    /// </summary>
    private static int Evaluate(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CommandLifecycleScope? lifecycle = null)
    {
        try
        {
            // §9.5: --agent-cmd *is* the candidate side, so it cannot be combined
            // with an in-process candidate selector. Checked against the raw
            // arguments, before the flag whitelist runs, because a conflict has to
            // be reported as a conflict rather than as an unknown flag.
            AgentCandidateSelection.GuardAgainstInProcessSelector(args);

            var (flags, positionals) = ParseFlags(
                args,
                "--seed-set", "--rollouts", "--seeds", "--out", "--commit", "--scenario",
                "--agent-cmd", "--agent-step-timeout-ms");
            GuardNoPositionals(positionals);
            var seedSetText = flags.TryGetValue("--seed-set", out var setText)
                ? setText.ToLowerInvariant()
                : HeldOutSuite;
            var suites = seedSetText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (suites.Length == 0)
            {
                throw new UsageError("--seed-set requires at least one suite.");
            }

            if (suites.Any(suite => suite is not (DevelopmentSuite or HeldOutSuite)))
            {
                throw new UsageError(
                    $"invalid --seed-set '{seedSetText}' (expected '{DevelopmentSuite}' and/or '{HeldOutSuite}').");
            }

            var rollouts = flags.TryGetValue("--rollouts", out var rolloutsText)
                ? ParsePositiveInt(rolloutsText, "--rollouts")
                : DefaultEvaluationRollouts;
            var seedCap = flags.TryGetValue("--seeds", out var capText)
                ? ParsePositiveInt(capText, "--seeds")
                : 50;
            var commit = flags.TryGetValue("--commit", out var commitText) ? commitText : null;

            var scenarioText2 = flags.GetValueOrDefault("--scenario") ?? "standard";

            // A --scenario value is EITHER a path to a descriptor or a built-in
            // name, decided by the same separator test `simulate` uses, so the
            // two forms are never confused. The case fold applies ONLY to the
            // built-in token: folding a path would rewrite it, and a checkout
            // containing an uppercase directory name would then resolve to
            // nothing on a case-sensitive filesystem. A file-loaded descriptor
            // supplies the MAP for each seed; the study's roster and simulation
            // protocol stay the study's, because a paired MCTS-vs-Scout number
            // is only commensurable with other such numbers under that
            // protocol.
            if (LooksLikePath(scenarioText2))
            {
                return EvaluateScenarioFile(flags, scenarioText2, stdout, stderr, lifecycle);
            }

            var scenario = scenarioText2.ToLowerInvariant();
            Func<ulong, MapGraph> mapFactory = scenario switch
            {
                "standard" => seed => MapGenerator.Generate(seed, DefaultGeneratorConfig),
                "bottleneck" => BottleneckScenario.ForSeed,
                _ => throw new UsageError(
                    $"invalid --scenario '{scenarioText2}' (expected 'standard' and/or 'bottleneck', or a path to a scenario file)."),
            };

            var search = new MctsSearchConfig(rolloutsPerAction: rollouts, maxDepth: 12);
            var teams = new IAgentFactory[]
            {
                new MctsAgentFactory(EvaluationSimulationConfig, search, name: EvaluationTargetPolicy),
                new ScoutCollectorAgentFactory(name: EvaluationBaselinePolicy),
            };

            var request = new EvaluateRequest(
                suites,
                rollouts,
                seedCap,
                commit,
                scenario,
                mapFactory,
                teams,
                AgentCommand: flags.TryGetValue("--agent-cmd", out var commandText) ? commandText : null,
                AgentStepTimeoutMs: flags.TryGetValue("--agent-step-timeout-ms", out var stepText)
                    ? ParseAgentStepTimeoutMs(stepText)
                    : ExternalTimeLimits.DefaultStepTimeoutMs);

            return request.AgentCommand is null
                ? EvaluateInProcess(request, flags, stdout, stderr, lifecycle)
                : EvaluateExternal(request, flags, stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
    }

    /// <summary>
    /// Runs the paired study on the map a declarative scenario descriptor
    /// supplies, with the study's own protocol otherwise unchanged.
    /// <para>
    /// What the descriptor controls and what it does not is stated rather than
    /// blurred. It supplies the <b>map</b> for every seed, and its SHA-256 is
    /// printed so the run names the file it came from. It does <b>not</b>
    /// supply the roster (the study is MCTS vs Scout, or an external candidate
    /// in the MCTS seat) and does <b>not</b> supply the simulation config: a
    /// paired delta is only commensurable with other paired deltas under the
    /// same protocol, so silently adopting a descriptor's agent count or tick
    /// budget would produce a number that looks like a published study and is
    /// not one. A descriptor asking for something the protocol cannot honour —
    /// anything other than two seats — is refused with the reason.
    /// </para>
    /// <para>
    /// The artifact's field set is unchanged, because it is pinned to the
    /// in-process shape by a golden fixture; the scenario digest goes to stderr
    /// rather than into the JSON, so the published artifact format does not
    /// move under a study that is otherwise identical to one already run.
    /// </para>
    /// </summary>
    private static int EvaluateScenarioFile(
        Dictionary<string, string> flags,
        string path,
        TextWriter stdout,
        TextWriter stderr,
        CommandLifecycleScope? lifecycle = null)
    {
        ScenarioDescriptor descriptor;
        string digest;
        try
        {
            (descriptor, digest) = ScenarioLoader.LoadFile(path);
        }
        catch (ScenarioValidationException ex)
        {
            foreach (var error in ex.Errors)
            {
                stderr.WriteLine($"scenario error: {error.FieldPath}: {error.Message}");
            }

            return Failure;
        }

        if (descriptor.AgentCount != EvaluationSimulationConfig.AgentCount)
        {
            // Runtime failure, not a UsageError, for the same reason as the
            // unconstructable policy above: the descriptor parsed, and its
            // contents are incompatible with what this study runs. Nothing about
            // the command line itself was wrong.
            throw new ArgumentException(
                $"scenario '{descriptor.Id}' declares 'Simulation.AgentCount' = {descriptor.AgentCount}, but the paired " +
                $"study is head-to-head and runs {EvaluationSimulationConfig.AgentCount} seats. A descriptor may supply the " +
                "map for the study; the roster and the simulation protocol stay the study's, so that a paired delta stays " +
                "commensurable with published studies.");
        }

        stderr.WriteLine($"scenario {descriptor.Id} (sha256 {digest}) supplies the study's map");

        var suites = (flags.TryGetValue("--seed-set", out var setText)
            ? setText.ToLowerInvariant()
            : HeldOutSuite)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (suites.Length == 0 || suites.Any(suite => suite is not (DevelopmentSuite or HeldOutSuite)))
        {
            throw new UsageError(
                $"invalid --seed-set '{flags.GetValueOrDefault("--seed-set", HeldOutSuite)}' (expected '{DevelopmentSuite}' and/or '{HeldOutSuite}').");
        }

        var rollouts = flags.TryGetValue("--rollouts", out var rolloutsText)
            ? ParsePositiveInt(rolloutsText, "--rollouts")
            : DefaultEvaluationRollouts;
        var seedCap = flags.TryGetValue("--seeds", out var capText)
            ? ParsePositiveInt(capText, "--seeds")
            : 50;

        var search = new MctsSearchConfig(rolloutsPerAction: rollouts, maxDepth: 12);
        var teams = new IAgentFactory[]
        {
            new MctsAgentFactory(EvaluationSimulationConfig, search, name: EvaluationTargetPolicy),
            new ScoutCollectorAgentFactory(name: EvaluationBaselinePolicy),
        };

        var request = new EvaluateRequest(
            suites,
            rollouts,
            seedCap,
            flags.TryGetValue("--commit", out var commitText) ? commitText : null,
            descriptor.Id,
            descriptor.BuildMap,
            teams,
            flags.TryGetValue("--agent-cmd", out var commandText) ? commandText : null,
            flags.TryGetValue("--agent-step-timeout-ms", out var stepText)
                ? ParseAgentStepTimeoutMs(stepText)
                : ExternalTimeLimits.DefaultStepTimeoutMs);

        return request.AgentCommand is null
            ? EvaluateInProcess(request, flags, stdout, stderr, lifecycle)
            : EvaluateExternal(request, flags, stdout, stderr);
    }

    /// <summary>
    /// The in-process evaluation: the fixed MCTS candidate against the Scout
    /// baseline, through the batch harness.
    /// </summary>
    /// <remarks>
    /// This path is untouched by the external-agent work, and that is the point of
    /// keeping it in its own method: the harness has no failure path and needs
    /// none, because an in-process agent cannot fail the way a process can. Every
    /// number an in-process run produces still comes from the same harness, the
    /// same analyzer and the same artifact as before.
    /// </remarks>
    private static int EvaluateInProcess(
        EvaluateRequest request,
        Dictionary<string, string> flags,
        TextWriter stdout,
        TextWriter stderr,
        CommandLifecycleScope? lifecycle = null)
    {
        var pairings = new[] { (0, 1), (1, 0) };

        var studies = new List<PairedStudyReport>();
        foreach (var suite in request.Suites)
        {
            var seeds = EvaluationSeeds(suite).Take(request.SeedCap).ToArray();

            // The harness builds a map exactly once per match, from the factory
            // the CLI supplies (EvaluationHarness.Evaluate), so counting the
            // factory's own calls counts matches actually started. That is a real
            // counter read from the real work, not an estimate of it, and it
            // needs no change to the harness.
            //
            // Null when no lifecycle line is showing, in which case the factory
            // is handed over unwrapped and the study is byte-for-byte the study
            // that ran before this existed.
            var matches = lifecycle?.Matches(seeds.Length * pairings.Length);
            var spec = new EvaluationSpec(
                seeds,
                matches?.Wrap(request.MapFactory) ?? request.MapFactory,
                EvaluationSimulationConfig,
                request.Teams,
                pairings,
                maxSteps: EvaluationSimulationConfig.MaxTicks);
            var study = PairedStudy.Analyze(
                suite,
                EvaluationTargetPolicy,
                EvaluationBaselinePolicy,
                request.Rollouts,
                EvaluationSimulationConfig.MaxTicks,
                EvaluationHarness.Evaluate(spec));
            studies.Add(study);
            WriteEvaluationSummary(stderr, study);
        }

        var artifact = new EvaluationArtifact(
            request.Commit,
            DateTime.UtcNow,
            RuntimeDescription(),
            OsDescription(),
            System.Environment.ProcessorCount,
            ArchitectureDescription(),
            studies.ToArray());
        var json = JsonArtifact.SerializeIndented(artifact);
        return WriteOutput(flags, "--out", json, stdout, stderr);
    }

    /// <summary>
    /// The external evaluation: one agent process per match, driven by
    /// <see cref="ExternalMatchRunner"/>, scored by the same
    /// <see cref="PairedStudy"/> analyzer the in-process path uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirrors of the in-process study are all here and unchanged: the same
    /// seed suites, the same <c>(0, 1)</c> and <c>(1, 0)</c> seatings per seed,
    /// the same per-match tick budget, the same baseline agent built by the same
    /// factory, and the same <c>rollouts</c> provenance. The external agent takes
    /// the seat MCTS would have taken, which is what makes the two runs
    /// commensurable (§9.4).
    /// </para>
    /// <para>
    /// The argv and the program are resolved before the first match (§3.3), and a
    /// program that cannot be run is a usage error rather than a result: no agent
    /// would have spoken, so there is nothing to score and nothing to write.
    /// </para>
    /// </remarks>
    private static int EvaluateExternal(
        EvaluateRequest request,
        Dictionary<string, string> flags,
        TextWriter stdout,
        TextWriter stderr)
    {
        var argv = AgentCommandLine.Require(request.AgentCommand);
        if (!AgentProgramResolver.TryResolve(argv[0], out var program, out var resolutionFailure))
        {
            throw new UsageError($"--agent-cmd could not be run: {resolutionFailure}");
        }

        var maxSteps = EvaluationSimulationConfig.MaxTicks;

        // §7: the match budget is computed from the step budget, so
        // match_timeout_ms >= step_timeout_ms x max_ticks holds by construction
        // and a caller cannot mis-set the pair into scoring every match a loss.
        var limits = new ExternalTimeLimits(
            request.AgentStepTimeoutMs,
            ExternalTimeLimits.ComputeMatchTimeoutMs(request.AgentStepTimeoutMs, maxSteps),
            maxSteps);
        var launch = new ExternalAgentLaunch(program!, argv[1..]);

        // The baseline is taken from the roster rather than constructed afresh, so
        // the opponent is the same agent the in-process study is published
        // against. The external agent takes the candidate seat.
        var baseline = request.Teams[EvaluationBaselineIndex];
        var externalName = $"external:{Path.GetFileNameWithoutExtension(program)}";

        var studies = new List<PairedStudyReport>();

        // Sorted, so two runs of the same failing agent produce the same artifact
        // bytes: a report whose field order depended on a dictionary's insertion
        // order would differ between runs for no reason a reader could see.
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var forfeits = new List<EvaluationForfeit>();
        var voidRuns = 0;

        foreach (var suite in request.Suites)
        {
            var results = new List<ExternalMatchResult>();
            foreach (var seed in EvaluationSeeds(suite).Take(request.SeedCap))
            {
                var map = request.MapFactory(seed);
                foreach (var externalSlot in new[] { 0, 1 })
                {
                    results.Add(PlayExternalMatch(
                        request,
                        map,
                        launch,
                        program!,
                        externalSlot,
                        seed,
                        maxSteps,
                        limits,
                        baseline));
                }
            }

            var report = ExternalMatchRunner.Report(results);
            var study = ExternalPairedStudy.Analyze(
                suite,
                externalName,
                baseline.Name,
                request.Rollouts,
                maxSteps,
                results);

            studies.Add(study);
            WriteEvaluationSummary(stderr, study);
            WriteExternalSummary(stderr, report, study);

            foreach (var (reason, count) in report.AgentFailuresByCode)
            {
                var code = reason.ToWireString();
                failures[code] = failures.TryGetValue(code, out var seen) ? seen + count : count;
            }

            // The §9.3 partials, recorded after the analyzer has already run. The
            // study above was computed from the forfeited rows, so nothing here can
            // reach a statistic; this exists so a reader can still see how far each
            // failed match actually got.
            foreach (var result in results.Where(r => r.IsAgentFailure))
            {
                var match = result.Match!;
                forfeits.Add(new EvaluationForfeit(
                    result.Seed,
                    result.ExternalSlot,
                    result.Fault!.TerminationReason,
                    result.PartialScoreA ?? match.ScoreA,
                    result.PartialScoreB ?? match.ScoreB,
                    result.ExternalSlot == 0 ? match.ScoreA : match.ScoreB,
                    result.ExternalSlot == 0 ? match.ScoreB : match.ScoreA));
            }

            voidRuns += report.VoidRuns;
        }

        var artifact = new EvaluationArtifact(
            request.Commit,
            DateTime.UtcNow,
            RuntimeDescription(),
            OsDescription(),
            System.Environment.ProcessorCount,
            ArchitectureDescription(),
            studies.ToArray(),
            AgentFailures: failures,
            VoidRuns: voidRuns,
            AgentCommand: argv,
            AgentLimits: new EvaluationAgentLimits(limits.StepTimeoutMs, limits.MatchTimeoutMs),
            AgentForfeits: forfeits.Count == 0 ? null : [.. forfeits]);
        var json = JsonArtifact.SerializeIndented(artifact);
        return WriteOutput(flags, "--out", json, stdout, stderr);
    }

    /// <summary>
    /// Plays one match of an external study: the external process in the candidate
    /// seat, a fresh baseline in the other, both on the same map and seed.
    /// </summary>
    /// <exception cref="UsageError">
    /// The program resolved but the OS would not start it. That is not a match
    /// result and not a §8 reason code — no agent ever spoke, so there is no agent
    /// behaviour to attribute anything to — and it is reported the same way as a
    /// program that does not resolve, with the run abandoned and no artifact
    /// written. Resolution catches the common case up front; this catches the one
    /// that can only be discovered by asking the OS.
    /// </exception>
    private static ExternalMatchResult PlayExternalMatch(
        EvaluateRequest request,
        MapGraph map,
        ExternalAgentLaunch launch,
        string program,
        int externalSlot,
        ulong seed,
        int maxSteps,
        ExternalTimeLimits limits,
        IAgentFactory baseline)
    {
        try
        {
            return ExternalMatchRunner.Run(
                map,
                EvaluationSimulationConfig,
                launch,
                externalSlot,
                baseline.Create(1 - externalSlot, seed),
                seed,
                maxSteps,
                request.Scenario,
                limits);
        }
        catch (ExternalAgentLaunchException launchFailure)
        {
            throw new UsageError(
                $"--agent-cmd could not be run: could not start '{program}': {launchFailure.Message}",
                launchFailure);
        }
    }

    /// <summary>
    /// The external run's own counts, under the ordinary summary: how many seeds
    /// were valid, how many runs were void, and how the agent's failures broke
    /// down by §8 reason code.
    /// </summary>
    /// <remarks>
    /// <b>Valid seeds</b> is the number the grading floor actually reads, and it is
    /// the number a void run reduces — a void contributes no row, so its seed is
    /// absent from the study rather than counted as a defeat. That is the whole
    /// point of the carve-out, and it is also the one way a void can change a
    /// verdict, so the caveat line below spells it out rather than leaving a
    /// reader to infer a shrunken denominator from a total that came out short.
    /// </remarks>
    private static void WriteExternalSummary(
        TextWriter stderr,
        ExternalMatchReport report,
        PairedStudyReport study)
    {
        var invariant = CultureInfo.InvariantCulture;
        var byCode = report.AgentFailuresByCode
            .OrderBy(pair => pair.Key.ToWireString(), StringComparer.Ordinal)
            .Select(pair => $"{pair.Key.ToWireString()}={pair.Value.ToString(invariant)}");

        stderr.WriteLine(
            $"  external        valid_seeds={study.Statistics.Seeds.ToString(invariant)}" +
            $"  void_runs={report.VoidRuns.ToString(invariant)}" +
            $"  agent_failures={(report.AgentFailureCount == 0 ? "none" : string.Join(",", byCode))}");

        // The decision itself, in the same analyzer's own words -- the verdict
        // string the in-process study records under `Decision`, not a second
        // phrasing of it. The statistics are identical, so the verdict is too.
        stderr.WriteLine($"  decision        {study.Decision}");

        if (report.VoidRuns > 0 && study.Decision.StartsWith("Not graded", StringComparison.Ordinal))
        {
            stderr.WriteLine(
                $"  void caveat     {report.VoidRuns.ToString(invariant)} void run(s) were excluded from the" +
                " statistics and from the grading floor, which is why this run is not graded.");
        }
    }

    /// <summary>
    /// The canonical seeds of one suite (spec §9.4: dev 1001..1050, held-out
    /// 2001..2050), before any <c>--seeds</c> cap.
    /// </summary>
    private static ulong[] EvaluationSeeds(string suite) =>
        suite == DevelopmentSuite ? DevelopmentEvaluationSeeds : HeldOutEvaluationSeeds;

    /// <summary>
    /// <c>--agent-step-timeout-ms</c>, which is a usage error rather than a plain
    /// argument error: a budget below 1 would make <c>match_timeout_ms</c>
    /// unsatisfiable, and a budget so large that the computed
    /// <c>match_timeout_ms</c> no longer fits the integer the wire carries cannot
    /// be honoured at all. §7 requires a mis-set pair to be refused rather than
    /// played, and refusing it here means naming the flag instead of surfacing an
    /// arithmetic failure from the middle of the run.
    /// </summary>
    private static int ParseAgentStepTimeoutMs(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new UsageError($"flag '--agent-step-timeout-ms' expects a positive integer, got '{text}'.");
        }

        // match_timeout_ms = step_timeout_ms x max_ticks + slack, so this is the
        // largest step budget whose match budget still fits the wire's integer.
        var largest = (int.MaxValue - ExternalTimeLimits.MatchTimeoutSlackMs) / EvaluationSimulationConfig.MaxTicks;
        if (value > largest)
        {
            throw new UsageError(
                $"flag '--agent-step-timeout-ms' got '{text}', which cannot be turned into a " +
                $"match_timeout_ms: the match budget is step_timeout_ms x {EvaluationSimulationConfig.MaxTicks} + " +
                $"{ExternalTimeLimits.MatchTimeoutSlackMs} and must stay within {int.MaxValue}, so the largest " +
                $"usable value is {largest}.");
        }

        return value;
    }

    private static void WriteEvaluationSummary(TextWriter stderr, PairedStudyReport study)
    {
        var stats = study.Statistics;
        var invariant = CultureInfo.InvariantCulture;
        stderr.WriteLine(
            $"evaluation suite={study.Suite} seeds={stats.Seeds} matches={stats.Matches}" +
            $" policy={study.TargetPolicy} baseline={study.BaselinePolicy} rollouts={study.RolloutsPerAction}" +
            $" max_steps={study.MaxStepsPerMatch}");
        stderr.WriteLine(
            $"  paired delta   mean={stats.MeanDelta.ToString("0.###", invariant)}" +
            $"  median={stats.MedianDelta.ToString("0.###", invariant)}" +
            $"  sd={stats.StdDevDelta.ToString("0.###", invariant)}" +
            $"  iqr={stats.IqrDelta.ToString("0.###", invariant)}");
        stderr.WriteLine(
            $"  95% CI         [{stats.CiLower95.ToString("0.###", invariant)}, {stats.CiUpper95.ToString("0.###", invariant)}]" +
            $"  -> {(study.Passed ? "PASS" : "FAIL")}");
        stderr.WriteLine(
            $"  outcomes       win={Percent(stats.WinRate, invariant)}" +
            $"  draw={Percent(stats.DrawRate, invariant)}" +
            $"  loss={Percent(stats.LossRate, invariant)}" +
            $"  timeout={Percent(stats.TimeoutRate, invariant)}" +
            $"  contention={Percent(stats.MeanContentionSaturation, invariant)}");
    }

    private static string Percent(double fraction, CultureInfo invariant) =>
        (fraction * 100.0).ToString("0.0", invariant) + "%";

    /// <summary>
    /// Loads a <see cref="DynamicMapRuleSet"/> from a JSON file and re-hydrates
    /// every rule through its validating constructor, so a file cannot smuggle
    /// a degenerate schedule (zero-length cycle, negative capacity) past the
    /// same checks the programmatic API enforces.
    /// </summary>
    private static DynamicMapRuleSet LoadRules(string path)
    {
        DynamicMapRuleSet loaded;
        using (var reader = new StreamReader(path))
        {
            loaded = JsonSerializer.Deserialize<DynamicMapRuleSet>(reader.ReadToEnd())
                ?? throw new InvalidDataException($"No dynamic rules could be parsed from '{path}'.");
        }

        var rules = new List<IDynamicMapRule>();
        foreach (var rule in loaded.Rules)
        {
            rules.Add(rule switch
            {
                TimedPortcullisRule portcullis =>
                    new TimedPortcullisRule(
                        portcullis.ChokeId,
                        portcullis.OpenTicks,
                        portcullis.ClosedTicks,
                        portcullis.OpenCapacity,
                        portcullis.ClosedCapacity),
                EventLockedChokeRule eventLock =>
                    new EventLockedChokeRule(
                        eventLock.ChokeId,
                        eventLock.TriggerResourceId,
                        eventLock.LockedCapacity),
                _ => rule,
            });
        }

        return new DynamicMapRuleSet(rules);
    }

    private static string RuntimeDescription() => RuntimeInformation.FrameworkDescription;

    private static string OsDescription() => RuntimeInformation.OSDescription.Trim();

    private static string ArchitectureDescription() => RuntimeInformation.ProcessArchitecture.ToString();

    private static ulong[] SeedRange(ulong start, int count)
    {
        var seeds = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            seeds[i] = start + (ulong)i;
        }

        return seeds;
    }

    private static string RenderAscii(TextReader source)
    {
        var builder = new StringBuilder();
        using (var sink = new StringWriter(builder))
        {
            TrajectoryPlayback.Playback(source, sink);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The single funnel for <c>--out</c> artifact writes. Routing every one
    /// through <see cref="JsonArtifact"/> is what makes the LF guarantee
    /// unconditional rather than a property of each call site remembering to
    /// normalize.
    /// </summary>
    private static int WriteOutput(
        Dictionary<string, string> flags,
        string outFlag,
        string content,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (flags.TryGetValue(outFlag, out var path))
        {
            JsonArtifact.Write(path, content);
            stderr.WriteLine($"wrote {path}");
            return Success;
        }

        stdout.WriteLine(content);
        return Success;
    }

    /// <summary>
    /// Refuses an unrecognised verb. A verb the CLI does not have is a property
    /// of the command line alone, so it reports the usage status rather than the
    /// runtime-failure status: nothing was ever run.
    /// </summary>
    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown command '{command}'.");
        WriteUsage(stderr);
        return UsageError.ExitCode;
    }

    /// <summary>
    /// Maps a caught exception to an exit status. A <see cref="UsageError"/> was
    /// raised on the way in — before any work, any seed and any artifact — so it
    /// reports the usage status; §3.3's <c>--agent-cmd</c> status is the same
    /// channel. Everything else was discovered while doing the work and keeps
    /// the runtime-failure status.
    /// </summary>
    private static int Report(Exception ex, TextWriter stderr)
    {
        stderr.WriteLine($"error: {ex.Message}");
        return ex is UsageError ? UsageError.ExitCode : Failure;
    }

    /// <summary>
    /// Parses `--key value` pairs into a flag map, rejecting duplicates and
    /// bare flags, and collecting any non-flag tokens as positionals.
    /// </summary>
    /// <remarks>
    /// Every rejection here is decidable from <c>args</c> before the command
    /// touches the filesystem, so each is a <see cref="UsageError"/> and reports
    /// the usage status — the same status an unknown verb gets.
    /// </remarks>
    private static (Dictionary<string, string> Flags, List<string> Positionals) ParseFlags(
        string[] args,
        params string[] allowedFlags)
    {
        var allowed = new HashSet<string>(
            allowedFlags.Select(flag => flag.StartsWith("--", StringComparison.Ordinal) ? flag[2..] : flag),
            StringComparer.Ordinal);
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var name = token[2..];
                if (!allowed.Contains(name))
                {
                    throw new UsageError($"unknown flag '--{name}'.");
                }

                if (i + 1 >= args.Length)
                {
                    throw new UsageError($"flag '--{name}' requires a value.");
                }

                if (!flags.TryAdd(token, args[++i]))
                {
                    throw new UsageError($"duplicate flag '--{name}'.");
                }
            }
            else
            {
                positionals.Add(token);
            }
        }

        return (flags, positionals);
    }

    private static string Require(Dictionary<string, string> flags, string name)
    {
        if (!flags.TryGetValue(name, out var value))
        {
            throw new UsageError($"missing required flag '{name}'.");
        }

        return value;
    }

    private static void GuardNoPositionals(List<string> positionals)
    {
        if (positionals.Count > 0)
        {
            throw new UsageError($"unexpected argument '{positionals[0]}'.");
        }
    }

    private static ulong ParseULong(string text, string flag)
    {
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new UsageError($"flag '{flag}' expects an unsigned integer, got '{text}'.");
        }

        return value;
    }

    private static int ParsePositiveInt(string text, string flag)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new UsageError($"flag '{flag}' expects a positive integer, got '{text}'.");
        }

        return value;
    }

    private static double ParseFairnessThreshold(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0.0 || value > 1.0)
        {
            throw new UsageError(
                $"flag '--min-fairness' expects a SpawnBiasIndex threshold in [0, 1], got '{text}'.");
        }

        return value;
    }

    private static void WriteUsage(TextWriter sink)
    {
        sink.WriteLine("Lattice: An auditable multi-agent research and benchmarking environment for");
        sink.WriteLine("deterministic Dec-POMDP experiments under partial observability, dynamic topology,");
        sink.WriteLine("and resource contention.");
        sink.WriteLine();
        sink.WriteLine("usage: lattice <command> [options]");
        sink.WriteLine();
        sink.WriteLine("  generate  --seed <ulong> [--min-fairness <0..1>] [--out <file>]");
        sink.WriteLine("            Generate a valid map (JSON) to stdout or file; with --min-fairness,");
        sink.WriteLine("            retry generation until the mirrored spawn-bias index meets the threshold");
        sink.WriteLine("  simulate  --seed <ulong> [--steps <n>] [--agent greedy|random|mcts] [--out <file>] [--quiet] [--rules <file>]");
        sink.WriteLine("            Record a greedy (default)-vs-random episode as trajectory JSONL;");
        sink.WriteLine("            '--agent mcts' selects the MCTS evaluation subject for agent 0;");
        sink.WriteLine("            '--rules' loads a JSON DynamicMapRuleSet (timed portcullises / event locks)");
        sink.WriteLine("            into the episode, the recorded header, contention, and replay verification;");
        sink.WriteLine("            an ANSI run dashboard renders on stderr unless '--quiet' suppresses it");
        sink.WriteLine("  simulate  --seed <ulong> --scenario infiltration [--steps <n>] [--out <file>] [--quiet]");
        sink.WriteLine("            Record a Dungeon Infiltration & Sentry Patrol episode: fix the roster to");
        sink.WriteLine("            a SentryPatrolAgent (guard) vs an InfiltratorAgent (rogue) on the gated");
        sink.WriteLine("            dungeon, with the trajectory header carrying the scenario + roster");
        sink.WriteLine("  render    --trajectory <file> [--format ascii|svg] [--out <file>]");
        sink.WriteLine("            Replay a recorded trajectory to the terminal or a standalone SVG");
        sink.WriteLine("  analyze   --trajectory <file> [--out <file>]");
        sink.WriteLine("            Report contention, turning points, pathing efficiency, and heatmaps");
        sink.WriteLine("            (compact terminal view without --out, full Markdown report with it)");
        sink.WriteLine("  replay    <file> [--verify] [--out <file>]");
        sink.WriteLine("            Re-run a recorded trajectory from its header. With --verify:");
        sink.WriteLine("            Reconstructs the episode from the header and asserts tick-by-tick");
        sink.WriteLine("            serialized StepResult equivalence. Without --verify it re-serializes");
        sink.WriteLine("            the recording to stdout");
        sink.WriteLine("  benchmark [--runs <n>] [--warmup <n>] [--steps <n>] [--out <file>] [--commit <sha>] [--cpu <model>]");
        sink.WriteLine("            Measure the five-case workload matrix (raw stepping, facility, dynamic");
        sink.WriteLine("            topology, stress, and MCTS policy) after a warm-up pass; reports per-case");
        sink.WriteLine("            median/mean/stddev throughput, p50/p95 step latency, per-step allocation,");
        sink.WriteLine("            and GC counts; '--out' writes a JSON artifact scoped to the host (commit,");
        sink.WriteLine("            timestamp, runtime, OS, CPU, cores, RAM, GC mode). '--steps' overrides the");
        sink.WriteLine("            per-iteration budget of the raw stepping cases, so a pass with tiny");
        sink.WriteLine("            '--steps' and '--runs' makes a quick smoke run; the MCTS policy case keeps");
        sink.WriteLine("            its own small catalog budget (100 ticks) in every mode");
        sink.WriteLine("  evaluate  [--seed-set dev|heldout[,dev|heldout]] [--rollouts <n>] [--seeds <n>] [--scenario standard|bottleneck] [--out <file>] [--commit <sha>]");
        sink.WriteLine("            Run the mirrored-seat paired evaluation of MCTS vs the Scout baseline");
        sink.WriteLine("            over a canonical seed suite (dev: 1001..1050, held-out: 2001..2050);");
        sink.WriteLine("            '--scenario bottleneck' selects the seeded procedural contention topology");
        sink.WriteLine("            family with capacity-1 choke bottlenecks, so transit denials and claim");
        sink.WriteLine("            races surface non-zero contention; '--out' writes the machine-readable");
        sink.WriteLine("            per-seed + statistics artifact");
        sink.WriteLine("  evaluate  --agent-cmd \"<command line>\" [--agent-step-timeout-ms <n>] [--seed-set dev|heldout] [--seeds <n>] [--scenario standard|bottleneck] [--out <file>] [--commit <sha>]");
        sink.WriteLine("            Score an external agent process as the candidate, in the seat MCTS would");
        sink.WriteLine("            take and against the same Scout baseline, with identical statistics;");
        sink.WriteLine("            the command line is split without a shell (double quotes group, so a path");
        sink.WriteLine("            with spaces must be quoted) and a program that cannot be run exits 2 with");
        sink.WriteLine("            nothing written. '--agent-step-timeout-ms' (default 5000) is the per-step");
        sink.WriteLine("            budget; the match budget is computed from it");
        sink.WriteLine();
        sink.WriteLine("  validate-scenario <file>");
        sink.WriteLine("            Check a declarative scenario descriptor and report its id, SHA-256,");
        sink.WriteLine("            map source, roster, victory and scoring. Reads the file once and");
        sink.WriteLine("            validates it; runs no episode and writes no artifact. A rejected");
        sink.WriteLine("            descriptor prints one 'scenario error: <field-path>: <reason>' line per");
        sink.WriteLine("            fault and exits non-zero");
        sink.WriteLine();
        sink.WriteLine("  -h, --help                                    Show this help and exit");
        sink.WriteLine("  -v, --version                                 Print the version and exit");
        sink.WriteLine();
        sink.WriteLine("  Exit status: 0 on success; 2 when the command line is not runnable (unknown");
        sink.WriteLine("  command, unknown/duplicated/missing flag, malformed value, unexpected");
        sink.WriteLine("  argument) and nothing is run; 1 when the command was runnable and the work");
        sink.WriteLine("  it named failed (a file that is missing or unreadable, a rejected");
        sink.WriteLine("  descriptor, a replay divergence).");
    }
}