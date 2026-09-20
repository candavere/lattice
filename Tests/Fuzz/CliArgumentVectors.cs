using Lattice.Environment;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Seeded generator of adversarial argument vectors for
/// <see cref="Lattice.Cli.CliApp.Run"/>. Every vector is a pure function of its
/// seed plus a per-run temp workspace, so a failing case re-materializes
/// exactly. The corpus targets the parser surfaces the audit inventories:
/// unknown flags, flags at end-of-args with no value, malformed/overflowing
/// seed and step integers, invalid scenario/agent/format identifiers, and
/// non-existent <c>--rules</c>/<c>replay</c> paths. Execution-heavy branches
/// (<c>--agent mcts</c>, <c>benchmark</c>, <c>evaluate</c>, and strict
/// <c>--min-fairness</c> retry loops) are deliberately excluded so the harness
/// stays runtime-bounded; they have dedicated integration coverage in the CLI
/// suite.
/// </summary>
internal static class CliArgumentVectors
{
    private static readonly string[] Commands = { "generate", "simulate", "render", "analyze", "replay" };

    private static readonly string[] TopLevelTokens =
    {
        "-h", "--help", "--version", "-v", "frobnicate", "", " ", "--", "-",
    };

    private static readonly string[] Seeds =
    {
        "1", "2", "7", "42", "2024", "0", "-1", "0xFFFF", "", " ", "abc", "３",
    };

    private static readonly string[] BoundarySeeds =
    {
        "18446744073709551615", "18446744073709551616", "99999999999999999999999999999",
    };

    private static readonly string[] ExecutionSafeSteps = { "1", "5", "10", "20", "50", "100", "200" };

    private static readonly string[] InfiltrationSafeSteps = { "1", "10", "20", "40" };

    private static readonly string[] RejectionSteps =
    {
        "0", "-1", "-0", "abc", "", "9999999999999", "2147483648", "0x10", "1_000", "+5", " 5 ",
    };

    private static readonly string[] Agents =
    {
        "greedy", "random", "Greedy", "RANDOM", "cfr", "", " ", "c4.5",
    };

    private static readonly string[] Scenarios = { "infiltration", "Infiltration", "", " ", "dungeon" };

    private static readonly string[] Formats = { "ascii", "svg", "png", "", " " };

    private static readonly string[] MinFairness =
    {
        "1.0", "0.999999", "1.5", "-0.1", "banana", "", "NaN", "1e-1",
    };

    private static readonly string[] AdversarialTokens =
    {
        "null", "\u0000", "--", "--=", "--seed=", "-", "", " ", "\r", "\n",
        new string('A', 512), new string('9', 64), "\\", "\"", "--quiet", "--verify",
    };

    private static readonly string[] FlagHeavyTokens =
    {
        "--seed", "--steps", "--agent", "--scenario", "--out", "--rules", "--trajectory",
        "--format", "--verify", "--quiet", "--min-fairness", "--bogus-flag", "--version",
    };

    /// <summary>
    /// Builds one argument vector, materializing any fixture-backed files
    /// (<c>--rules</c>/<c>--trajectory</c>) through <paramref name="writeFile"/>
    /// before returning.
    /// </summary>
    public static string[] Generate(Rng rng, int caseSeed, string tempDir, Action<string, string> writeFile)
    {
        var counter = 0;
        string TrajectoryFor(byte[]? mutated)
        {
            var path = Path.Combine(tempDir, $"trajectory-{caseSeed & 0xFFFF:X4}-{counter++}.jsonl");
            writeFile(path, mutated is null ? GoldenTrajectory : Decode(mutated));
            return path;
        }

        string RulesFor(byte[]? mutated)
        {
            var path = Path.Combine(tempDir, $"rules-{caseSeed & 0xFFFF:X4}-{counter++}.json");
            writeFile(path, mutated is null ? GoldenRules : Decode(mutated));
            return path;
        }

        string OutPath() => Path.Combine(tempDir, $"out-{caseSeed & 0xFFFF:X4}-{counter++}.jsonl");
        string MissingTrajectory() => Path.Combine(Path.GetTempPath(), $"lattice-fuzz-missing-{rng.Next()}.jsonl");
        string MissingRules() => Path.Combine(Path.GetTempPath(), $"lattice-fuzz-missing-rules-{rng.Next()}.json");

        var mutateTrajectory = rng.Next(0, 4) == 0;

        return rng.Next(0, 12) switch
        {
            0 => GenerateCommand(rng, OutPath),
            1 => Vector("generate", "--seed", Seed(rng), "--min-fairness", MinFairness[rng.Next(0, MinFairness.Length)]),
            2 => Simulate(rng, RulesFor, OutPath, MissingRules),
            3 => Simulate(rng, RulesFor, OutPath, MissingRules),
            4 => Render(rng, TrajectoryFor(mutateTrajectory ? Mutate(GoldenTrajectoryBytes, rng) : null), OutPath, MissingTrajectory),
            5 => Analyze(rng, TrajectoryFor(mutateTrajectory ? Mutate(GoldenTrajectoryBytes, rng) : null), OutPath),
            6 => Replay(rng, TrajectoryFor(mutateTrajectory ? Mutate(GoldenTrajectoryBytes, rng) : null), MissingTrajectory),
            7 => RawVector(rng),
            8 => MissingValueVector(rng),
            9 => TopLevel(rng),
            10 => Vector("simulate", "--seed", BoundarySeeds[rng.Next(0, BoundarySeeds.Length)], "--steps", ExecutionSafeSteps[rng.Next(0, ExecutionSafeSteps.Length)]),
            _ => FlagHeavyFlush(rng),
        };
    }

    private static string[] GenerateCommand(Rng rng, Func<string> outPath)
    {
        var tokens = new List<string> { "generate", "--seed", Seed(rng) };
        if (rng.Next(0, 2) == 0)
        {
            tokens.Add("--min-fairness");
            tokens.Add(MinFairness[rng.Next(0, MinFairness.Length)]);
        }

        if (rng.Next(0, 3) == 0)
        {
            tokens.Add("--out");
            tokens.Add(outPath());
        }

        return tokens.ToArray();
    }

    private static string[] Simulate(
        Rng rng,
        Func<byte[]?, string> rulesFor,
        Func<string> outPath,
        Func<string> missingRules)
    {
        var scenario = rng.Next(0, 5) == 0 ? Scenarios[rng.Next(0, Scenarios.Length)] : null;
        if (scenario is "infiltration")
        {
            var tokens = new List<string> { "simulate", "--seed", Seed(rng), "--scenario", scenario };
            tokens.Add("--steps");
            tokens.Add(rng.Next(0, 3) == 0
                ? RejectionSteps[rng.Next(0, RejectionSteps.Length)]
                : InfiltrationSafeSteps[rng.Next(0, InfiltrationSafeSteps.Length)]);
            if (rng.Next(0, 3) == 0)
            {
                tokens.Add("--agent");
                tokens.Add(Agents[rng.Next(0, Agents.Length)]);
            }

            return FinishSimulate(tokens, rng, rulesFor, missingRules, outPath);
        }

        var generic = new List<string> { "simulate", "--seed", Seed(rng), "--steps" };
        generic.Add(rng.Next(0, 3) == 0
            ? RejectionSteps[rng.Next(0, RejectionSteps.Length)]
            : ExecutionSafeSteps[rng.Next(0, ExecutionSafeSteps.Length)]);
        if (rng.Next(0, 2) == 0)
        {
            generic.Add("--agent");
            generic.Add(Agents[rng.Next(0, Agents.Length)]);
        }

        return FinishSimulate(generic, rng, rulesFor, missingRules, outPath);
    }

    private static string[] FinishSimulate(
        List<string> tokens,
        Rng rng,
        Func<byte[]?, string> rulesFor,
        Func<string> missingRules,
        Func<string> outPath)
    {
        if (rng.Next(0, 3) == 0)
        {
            tokens.Add("--rules");
            tokens.Add(rng.Next(0, 2) == 0 ? rulesFor(null) : missingRules());
        }

        if (rng.Next(0, 3) == 0)
        {
            tokens.Add("--out");
            tokens.Add(outPath());
        }

        AddJunk(tokens, rng, rng.Next(0, 2));
        return tokens.ToArray();
    }

    private static string[] Render(Rng rng, string trajectoryPath, Func<string> outPath, Func<string> missing)
    {
        var tokens = new List<string> { "render", "--trajectory" };
        tokens.Add(rng.Next(0, 3) == 0 ? missing() : trajectoryPath);
        if (rng.Next(0, 2) == 0)
        {
            tokens.Add("--format");
            tokens.Add(Formats[rng.Next(0, Formats.Length)]);
        }

        if (rng.Next(0, 3) == 0)
        {
            tokens.Add("--out");
            tokens.Add(outPath());
        }

        return tokens.ToArray();
    }

    private static string[] Analyze(Rng rng, string trajectoryPath, Func<string> outPath)
    {
        var tokens = new List<string> { "analyze", "--trajectory", trajectoryPath };
        if (rng.Next(0, 3) == 0)
        {
            tokens.Add("--out");
            tokens.Add(outPath());
        }

        AddJunk(tokens, rng, rng.Next(0, 2));
        return tokens.ToArray();
    }

    private static string[] Replay(Rng rng, string trajectoryPath, Func<string> missing)
    {
        var tokens = new List<string> { "replay" };
        tokens.Add(rng.Next(0, 3) == 0 ? missing() : trajectoryPath);
        if (rng.Next(0, 2) == 0)
        {
            tokens.Add("--verify");
        }

        return tokens.ToArray();
    }

    private static string[] TopLevel(Rng rng)
    {
        var tokens = new List<string> { TopLevelTokens[rng.Next(0, TopLevelTokens.Length)] };
        AddJunk(tokens, rng, rng.Next(0, 3));
        return tokens.ToArray();
    }

    private static string[] RawVector(Rng rng)
    {
        var result = new List<string>();
        var count = rng.Next(1, 8);
        for (var i = 0; i < count; i++)
        {
            result.Add(rng.Next(0, 2) == 0
                ? AdversarialTokens[rng.Next(0, AdversarialTokens.Length)]
                : FlagHeavyTokens[rng.Next(0, FlagHeavyTokens.Length)]);
        }

        return result.ToArray();
    }

    private static string[] MissingValueVector(Rng rng)
    {
        var flags = new[] { "--seed", "--steps", "--agent", "--scenario", "--format", "--rules", "--out" };
        var tokens = new List<string> { Commands[rng.Next(0, Commands.Length)] };
        AddJunk(tokens, rng, rng.Next(0, 4));
        tokens.Add(flags[rng.Next(0, flags.Length)]);
        return tokens.ToArray();
    }

    private static string[] FlagHeavyFlush(Rng rng)
    {
        var result = new List<string>();
        var count = rng.Next(1, 7);
        for (var i = 0; i < count; i++)
        {
            result.Add(FlagHeavyTokens[rng.Next(0, FlagHeavyTokens.Length)]);
            if (rng.Next(0, 2) == 0)
            {
                result.Add(AdversarialTokens[rng.Next(0, AdversarialTokens.Length)]);
            }
        }

        return result.ToArray();
    }

    private static string[] Vector(params string?[] tokens) => tokens.Where(t => t is not null).ToArray()!;

    private static void AddJunk(List<string> tokens, Rng rng, int count)
    {
        for (var i = 0; i < count; i++)
        {
            tokens.Add(AdversarialTokens[rng.Next(0, AdversarialTokens.Length)]);
        }
    }

    private static string Seed(Rng rng) => rng.Next(0, 10) == 0
        ? BoundarySeeds[rng.Next(0, BoundarySeeds.Length)]
        : Seeds[rng.Next(0, Seeds.Length)];

    private static readonly string GoldenTrajectory =
        File.ReadAllText(FixtureResolver.Fixture("golden_trajectory.jsonl"));

    private static readonly string GoldenRules =
        File.ReadAllText(FixtureResolver.Fixture("golden_dynamic_rules.json"));

    private static readonly byte[] GoldenTrajectoryBytes = System.Text.Encoding.UTF8.GetBytes(GoldenTrajectory);

    private static readonly string[] TrajectoryTokens =
    {
        "SchemaVersion", "StepNumber", "TotalSteps", "Kind", "FinalScores", "Claims",
        "Actions", "ResourceId", "ZoneId", "WinnerAgentId", "ResourcesClaimed",
        "TotalResources", "Seed", "Reason", "MaxOccupancy", "TransitSpeed",
    };

    private static byte[] Mutate(byte[] source, Rng rng)
    {
        var mutated = TextMutator.Mutate(
            System.Text.Encoding.UTF8.GetString(source),
            rng,
            TrajectoryTokens);
        return System.Text.Encoding.UTF8.GetBytes(mutated);
    }

    private static string Decode(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}