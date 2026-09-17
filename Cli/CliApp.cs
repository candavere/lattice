using System.Globalization;
using System.Text;
using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Lattice.Visualization;

namespace Lattice.Cli;

/// <summary>
/// The Lattice command-line driver: simulate, analyze, compare, render, and
    /// benchmark subcommands over the seedable environment.
/// Turns plain `args` into one of five subcommands — `generate`, `simulate`,
/// `render`, `analyze`, `benchmark` — and routes all
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
    private const int DefaultBenchmarkTicks = 1000;
    private const ulong BenchmarkSeed = 42;

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
            var (flags, positionals) = ParseFlags(args, "--seed", "--steps", "--agent", "--out");
            GuardNoPositionals(positionals);
            var seed = ParseULong(Require(flags, "--seed"), "--seed");
            var steps = flags.TryGetValue("--steps", out var stepsText)
                ? ParsePositiveInt(stepsText, "--steps")
                : DefaultSimulationSteps;
            var agent = flags.TryGetValue("--agent", out var agentText)
                ? agentText.ToLowerInvariant()
                : "greedy";

            var config = new SimulationConfig(AgentCount: 2, MaxTicks: steps);
            var map = MapGenerator.Generate(seed, DefaultGeneratorConfig);
            var agent0 = agent switch
            {
                "greedy" => (IAgent)new GreedyCollectorAgent(0),
                "random" => new RandomAgent(0, new Rng(seed)),
                "mcts" => new MctsAgent(0, config, seed, new MctsSearchConfig()),
                _ => throw new ArgumentException(
                    $"invalid --agent '{agentText}' (expected 'greedy', 'random', or 'mcts')."),
            };
            var scenario = ScenarioRunner.Run(
                map,
                config,
                new IAgent[] { agent0, new RandomAgent(1, new Rng(seed)) },
                maxSteps: steps);

            var jsonl = new StringBuilder();
            using (var sink = new StringWriter(jsonl))
            {
                TrajectoryWriter.Record(map, config, seed, scenario.Turns, sink);
            }

            var lastInfo = scenario.Results[^1].Info;
            stderr.WriteLine(
                $"recorded {scenario.Metrics.TotalSteps} steps" +
                $" ({(lastInfo.IsTerminal ? lastInfo.Reason : "budget-reached")}," +
                $" winner: {(lastInfo.WinnerAgentId.HasValue ? $"agent {lastInfo.WinnerAgentId}" : "none")})");
            return WriteOutput(flags, "--out", jsonl.ToString().TrimEnd(), stdout, stderr);
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
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
            var (flags, positionals) = ParseFlags(args, "--ticks");
            GuardNoPositionals(positionals);
            var ticks = flags.TryGetValue("--ticks", out var ticksText)
                ? ParsePositiveInt(ticksText, "--ticks")
                : DefaultBenchmarkTicks;

            var map = MapGenerator.Generate(BenchmarkSeed, DefaultGeneratorConfig);
            var config = new SimulationConfig(AgentCount: 2, MaxTicks: ticks);
            var turn = new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) };
            var result = Lattice.Cli.Benchmark.Run(map, config, turn, ticks);

            var invariant = CultureInfo.InvariantCulture;
            stdout.WriteLine($"ticks={result.Ticks}");
            stdout.WriteLine($"elapsed_ms={result.ElapsedMs.ToString("0.###", invariant)}");
            stdout.WriteLine($"steps_per_second={result.StepsPerSecond.ToString("0.#", invariant)}");
            stdout.WriteLine($"allocated_bytes={result.AllocatedBytes}");
            stdout.WriteLine($"bytes_per_tick={result.BytesPerTick.ToString("0.#", invariant)}");
            return Success;
        }
        catch (Exception ex)
        {
            return Report(ex, stderr);
        }
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
        sink.WriteLine("  simulate  --seed <ulong> [--steps <n>] [--agent greedy|random|mcts] [--out <file>]");
        sink.WriteLine("            Record a greedy (default)-vs-random episode as trajectory JSONL;");
        sink.WriteLine("            '--agent mcts' substitutes a rollout-based tactical agent for agent 0");
        sink.WriteLine("  render    --trajectory <file> [--format ascii|svg] [--out <file>]");
        sink.WriteLine("            Replay a recorded trajectory to the terminal or a standalone SVG");
        sink.WriteLine("  analyze   --trajectory <file> [--out <file>]");
        sink.WriteLine("            Report contention, turning points, pathing efficiency, and heatmaps");
        sink.WriteLine("            (compact terminal view without --out, full Markdown report with it)");
        sink.WriteLine("  benchmark [--ticks <n>]                       Measure core throughput and allocations");
        sink.WriteLine();
        sink.WriteLine("  -h, --help                                    Show this help and exit");
    }
}