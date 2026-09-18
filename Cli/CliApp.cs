using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Lattice.Agents;
using Lattice.Analytics.Benchmarking;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Lattice.Visualization;

namespace Lattice.Cli;

/// <summary>
/// The Lattice command-line driver: simulate, analyze, compare, render, and
    /// benchmark subcommands over the seedable environment.
/// Turns plain `args` into one of six subcommands — `generate`, `simulate`,
/// `render`, `analyze`, `benchmark`, `evaluate` — and routes all
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

    private static readonly GeneratorConfig DefaultGeneratorConfig = new(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap);
    private const int DefaultSimulationSteps = 100;
    private const int DefaultEvaluationRollouts = 32;

    private const string DevelopmentSuite = "dev";
    private const string HeldOutSuite = "heldout";
    private const string EvaluationTargetPolicy = "MCTS";
    private const string EvaluationBaselinePolicy = "Scout";

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

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The machine-readable evaluation artifact written by
    /// <c>evaluate --out</c>: the per-suite paired-study reports plus the
    /// host and provenance facts the numbers were produced on.
    /// </summary>
    internal sealed record EvaluationArtifact(
        string? CommitSha,
        DateTime CreatedAtUtc,
        string Runtime,
        string Os,
        int Cores,
        string Architecture,
        PairedStudyReport[] Studies);

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

        return args[0] switch
        {
            "generate" => Generate(args[1..], stdout, stderr),
            "simulate" => Simulate(args[1..], stdout, stderr),
            "render" => Render(args[1..], stdout, stderr),
            "analyze" => Analyze(args[1..], stdout, stderr),
            "benchmark" => RunBenchmark(args[1..], stdout, stderr),
            "evaluate" => Evaluate(args[1..], stdout, stderr),
            _ => UnknownCommand(args[0], stderr),
        };
    }

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
    /// the rogue. Same seed, same bytes.
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
            TrajectoryWriter.Record(
                run.Map,
                run.Config,
                seed,
                run.Base.Turns,
                sink,
                scenario: InfiltrationScenario.ScenarioName,
                agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole });
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
    /// Every tick must reproduce byte-for-byte, not just the final tick, so a
    /// divergence anywhere in the episode is caught. When this returns true
    /// the footer reports "byte-identical replay verified across all N steps".
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

            var json = JsonSerializer.Serialize(result, ArtifactJsonOptions);
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
            var (flags, positionals) = ParseFlags(args, "--seed-set", "--rollouts", "--seeds", "--out", "--commit");
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

            var search = new MctsSearchConfig(rolloutsPerAction: rollouts, maxDepth: 12);
            var teams = new IAgentFactory[]
            {
                new MctsAgentFactory(EvaluationSimulationConfig, search, name: EvaluationTargetPolicy),
                new ScoutCollectorAgentFactory(name: EvaluationBaselinePolicy),
            };
            var pairings = new[] { (0, 1), (1, 0) };

            var studies = new List<PairedStudyReport>();
            foreach (var suite in suites)
            {
                var seeds = (suite == DevelopmentSuite ? DevelopmentEvaluationSeeds : HeldOutEvaluationSeeds)
                    .Take(seedCap)
                    .ToArray();
                var spec = new EvaluationSpec(
                    seeds,
                    seed => MapGenerator.Generate(seed, DefaultGeneratorConfig),
                    EvaluationSimulationConfig,
                    teams,
                    pairings,
                    maxSteps: EvaluationSimulationConfig.MaxTicks);
                var study = PairedStudy.Analyze(
                    suite,
                    EvaluationTargetPolicy,
                    EvaluationBaselinePolicy,
                    rollouts,
                    EvaluationSimulationConfig.MaxTicks,
                    EvaluationHarness.Evaluate(spec));
                studies.Add(study);
                WriteEvaluationSummary(stderr, study);
            }

            var artifact = new EvaluationArtifact(
                commit,
                DateTime.UtcNow,
                RuntimeDescription(),
                OsDescription(),
                System.Environment.ProcessorCount,
                ArchitectureDescription(),
                studies.ToArray());
            var json = JsonSerializer.Serialize(artifact, ArtifactJsonOptions);
            return WriteOutput(flags, "--out", json, stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
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

    private static int WriteOutput(
        Dictionary<string, string> flags,
        string outFlag,
        string content,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (flags.TryGetValue(outFlag, out var path))
        {
            File.WriteAllText(path, content + "\n");
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
        return Failure;
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
        sink.WriteLine("lattice — seeded deterministic procedural strategy/tactical simulation");
        sink.WriteLine();
        sink.WriteLine("usage: lattice <command> [options]");
        sink.WriteLine();
        sink.WriteLine("  generate  --seed <ulong> [--min-fairness <0..1>] [--out <file>]");
        sink.WriteLine("            Generate a valid map (JSON) to stdout or file; with --min-fairness,");
        sink.WriteLine("            retry generation until the mirrored spawn-bias index meets the threshold");
        sink.WriteLine("  simulate  --seed <ulong> [--steps <n>] [--agent greedy|random|mcts] [--out <file>] [--quiet] [--rules <file>]");
        sink.WriteLine("            Record a greedy (default)-vs-random episode as trajectory JSONL;");
        sink.WriteLine("            '--agent mcts' substitutes a rollout-based tactical agent for agent 0;");
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
        sink.WriteLine("  benchmark [--runs <n>] [--warmup <n>] [--steps <n>] [--out <file>] [--commit <sha>] [--cpu <model>]");
        sink.WriteLine("            Measure the five-case workload matrix (raw stepping, facility, dynamic");
        sink.WriteLine("            topology, stress, and MCTS policy) after a warm-up pass; reports per-case");
        sink.WriteLine("            median/mean/stddev throughput, p50/p95 step latency, per-step allocation,");
        sink.WriteLine("            and GC counts; '--out' writes a JSON artifact scoped to the host (commit,");
        sink.WriteLine("            timestamp, runtime, OS, CPU, cores, RAM, GC mode). '--steps' overrides the");
        sink.WriteLine("            per-iteration budget of the raw stepping cases, so a pass with tiny");
        sink.WriteLine("            '--steps' and '--runs' makes a quick smoke run; the MCTS policy case keeps");
        sink.WriteLine("            its own small catalog budget (100 ticks) in every mode");
        sink.WriteLine("  evaluate  [--seed-set dev|heldout[,dev|heldout]] [--rollouts <n>] [--seeds <n>] [--out <file>] [--commit <sha>]");
        sink.WriteLine("            Run the mirrored-seat paired evaluation of MCTS vs the Scout baseline");
        sink.WriteLine("            over a canonical seed suite (dev: 1001..1050, held-out: 2001..2050);");
        sink.WriteLine("            '--out' writes the machine-readable per-seed + statistics artifact");
        sink.WriteLine();
        sink.WriteLine("  -h, --help                                    Show this help and exit");
    }
}