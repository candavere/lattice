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
/// codes and stdout deterministically. Exit code is 0 on success, non-zero on
/// any bad-argument or runtime error.
/// </summary>
public static class CliApp
{
    private const int Success = 0;
    private const int Failure = 1;

    /// <summary>
    /// The CLI's reported version. Kept in lockstep with the project
    /// <c>&lt;Version&gt;</c> elements by the release workflow's tag-parity gate.
    /// </summary>
    public const string Version = "3.0.0";

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

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return Failure;
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

        return args[0] switch
        {
            "generate" => Generate(args[1..], stdout, stderr),
            "simulate" => Simulate(args[1..], stdout, stderr),
            "render" => Render(args[1..], stdout, stderr),
            "analyze" => Analyze(args[1..], stdout, stderr),
            "replay" => Replay(args[1..], stdout, stderr),
            "benchmark" => RunBenchmark(args[1..], stdout, stderr),
            "evaluate" => Evaluate(args[1..], stdout, stderr),
            "validate-scenario" => ValidateScenario(args[1..], stdout, stderr),
            _ => UnknownCommand(args[0], stderr),
        };
    }

    private static bool IsKnownCommand(string command) =>
        command is "generate" or "simulate" or "render" or "analyze"
            or "replay" or "benchmark" or "evaluate" or "validate-scenario";

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
            var scenario = flags.TryGetValue("--scenario", out var scenarioText)
                ? scenarioText.ToLowerInvariant()
                : "";

            if (scenario == InfiltrationScenario.ScenarioName)
            {
                return SimulateInfiltration(flags, seed, steps, quiet, stdout, stderr);
            }

            if (scenario.Length > 0)
            {
                throw new ArgumentException(
                    $"invalid --scenario '{scenarioText}' (expected 'infiltration').");
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
                _ => throw new ArgumentException(
                    $"invalid --agent '{agentText}' (expected 'greedy', 'random', or 'mcts')."),
            };
            var contenders = new IAgent[] { agent0, new RandomAgent(1, new Rng(seed)) };
            var stopwatch = Stopwatch.StartNew();
            var scenarioResult = ScenarioRunner.Run(map, config, contenders, maxSteps: steps, rules: rules);
            stopwatch.Stop();

            var jsonl = new StringBuilder();
            using (var sink = new StringWriter(jsonl))
            {
                TrajectoryWriter.Record(map, config, seed, scenarioResult.Turns, sink, rules: rules);
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
            throw new ArgumentException("--agent cannot be used with --scenario infiltration (the roster is fixed: Sentry vs Infiltrator).");
        }

        if (flags.ContainsKey("--rules"))
        {
            throw new ArgumentException("--rules cannot be used with --scenario infiltration (the scenario owns its topology).");
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
                perceptions: run.Base.Perceptions);
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
                throw new ArgumentException(
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
                throw new ArgumentException(
                    "missing trajectory path (pass a positional path or --trajectory <file>).");
            }

            if (positionals.Count > (flags.ContainsKey("--trajectory") ? 0 : 1))
            {
                throw new ArgumentException($"unexpected argument '{positionals[^1]}'.");
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
                throw new ArgumentException(
                    $"unexpected argument '{positionals[allowedPositionals]}'.");
            }

            var path = flags.TryGetValue("--out", out var flagged)
                ? flagged
                : positionals.Count == 1 ? positionals[0] : null;
            if (path is null)
            {
                throw new ArgumentException(
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
    private static int Evaluate(string[] args, TextWriter stdout, TextWriter stderr)
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
                throw new ArgumentException("--seed-set requires at least one suite.");
            }

            if (suites.Any(suite => suite is not (DevelopmentSuite or HeldOutSuite)))
            {
                throw new ArgumentException(
                    $"invalid --seed-set '{seedSetText}' (expected '{DevelopmentSuite}' and/or '{HeldOutSuite}').");
            }

            var rollouts = flags.TryGetValue("--rollouts", out var rolloutsText)
                ? ParsePositiveInt(rolloutsText, "--rollouts")
                : DefaultEvaluationRollouts;
            var seedCap = flags.TryGetValue("--seeds", out var capText)
                ? ParsePositiveInt(capText, "--seeds")
                : 50;
            var commit = flags.TryGetValue("--commit", out var commitText) ? commitText : null;

            var scenario = flags.TryGetValue("--scenario", out var scenarioText)
                ? scenarioText.ToLowerInvariant()
                : "standard";
            Func<ulong, MapGraph> mapFactory = scenario switch
            {
                "standard" => seed => MapGenerator.Generate(seed, DefaultGeneratorConfig),
                "bottleneck" => BottleneckScenario.ForSeed,
                _ => throw new ArgumentException(
                    $"invalid --scenario '{scenarioText}' (expected 'standard' and/or 'bottleneck')."),
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
                ? EvaluateInProcess(request, flags, stdout, stderr)
                : EvaluateExternal(request, flags, stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
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
        TextWriter stderr)
    {
        var pairings = new[] { (0, 1), (1, 0) };

        var studies = new List<PairedStudyReport>();
        foreach (var suite in request.Suites)
        {
            var seeds = EvaluationSeeds(suite).Take(request.SeedCap).ToArray();
            var spec = new EvaluationSpec(
                seeds,
                request.MapFactory,
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

    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown command '{command}'.");
        WriteUsage(stderr);
        return Failure;
    }

    private static int Report(Exception ex, TextWriter stderr)
    {
        stderr.WriteLine($"error: {ex.Message}");

        // A usage error is reported before any work is done, so it gets its own
        // exit status (§3.3) and says so on the way out. Everything else keeps the
        // runtime-failure status it has always returned.
        return ex is UsageError ? UsageError.ExitCode : Failure;
    }

    /// <summary>
    /// Parses `--key value` pairs into a flag map, rejecting duplicates and
    /// bare flags, and collecting any non-flag tokens as positionals.
    /// </summary>
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
                    throw new ArgumentException($"unknown flag '--{name}'.");
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"flag '--{name}' requires a value.");
                }

                if (!flags.TryAdd(token, args[++i]))
                {
                    throw new ArgumentException($"duplicate flag '--{name}'.");
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
            throw new ArgumentException($"missing required flag '{name}'.");
        }

        return value;
    }

    private static void GuardNoPositionals(List<string> positionals)
    {
        if (positionals.Count > 0)
        {
            throw new ArgumentException($"unexpected argument '{positionals[0]}'.");
        }
    }

    private static ulong ParseULong(string text, string flag)
    {
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"flag '{flag}' expects an unsigned integer, got '{text}'.");
        }

        return value;
    }

    private static int ParsePositiveInt(string text, string flag)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ArgumentException($"flag '{flag}' expects a positive integer, got '{text}'.");
        }

        return value;
    }

    private static double ParseFairnessThreshold(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0.0 || value > 1.0)
        {
            throw new ArgumentException(
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
    }
}