using System.Text.Json;
using Lattice.Cli;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Integration coverage for the <see cref="Lattice.Cli"/> driver: it
/// drives the real <see cref="CliApp.Run"/> entry point (which is what
/// <c>Program.Main</c> forwards to) through String writers, asserting exit
/// codes — 0 on success, non-zero on any bad-argument or runtime error — and
/// the stdout/stderr/file side effects each subcommand is documented to
/// produce. Every run is seeded, so repeated invocations are asserted
/// byte-identical.
/// </summary>
public class CliIntegrationTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string TempPath(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"lattice-cli-{Guid.NewGuid():N}{extension}");
    }

    [Fact]
    public void Generate_ValidSeed_WritesMapJsonToStdout()
    {
        var (exit, stdout, _) = Run("generate", "--seed", "123");

        Assert.Equal(0, exit);
        Assert.StartsWith("{", stdout);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.GetProperty("Zones").GetArrayLength() >= 2);
        Assert.True(document.RootElement.GetProperty("Resources").GetArrayLength() >= 1);
    }

    [Fact]
    public void Generate_IsDeterministicAcrossRuns()
    {
        Assert.Equal(Run("generate", "--seed", "123").Stdout, Run("generate", "--seed", "123").Stdout);
    }

    [Fact]
    public void Generate_WithOutFlag_WritesFile()
    {
        var path = TempPath(".json");
        try
        {
            var (exit, stdout, stderr) = Run("generate", "--seed", "7", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("wrote", stderr);
            Assert.StartsWith("{", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generate_MissingSeed_NonZeroExit()
    {
        var (exit, _, stderr) = Run("generate", "--out", "ignored.json");
        Assert.NotEqual(0, exit);
        Assert.Contains("missing required flag '--seed'", stderr);
    }

    [Fact]
    public void Generate_InvalidSeed_NonZeroExit()
    {
        var (exit, _, stderr) = Run("generate", "--seed", "oops");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects an unsigned integer", stderr);
    }

    [Fact]
    public void Generate_UnknownFlag_NonZeroExit()
    {
        var (exit, _, stderr) = Run("generate", "--seed", "1", "--bogus", "2");
        Assert.NotEqual(0, exit);
        Assert.Contains("unknown flag '--bogus'", stderr);
    }

    [Fact]
    public void Generate_MinFairnessPermissive_MatchesPlainGenerate()
    {
        var plain = Run("generate", "--seed", "9").Stdout;
        var (exit, gatedStdout, gatedStderr) = Run("generate", "--seed", "9", "--min-fairness", "1.0");

        Assert.Equal(0, exit);
        Assert.Equal(plain, gatedStdout);
        Assert.Contains("spawn bias index", gatedStderr);
    }

    [Fact]
    public void Generate_MinFairnessStrict_NonZeroExitOnBiasedMap()
    {
        // Seed 9 generates a map whose every structurally-valid candidate has a
        // measured spawn bias above 0.0, so a zero-tolerance gate exhausts the
        // generator retry budget instead of returning the biased map.
        var (exit, _, stderr) = Run("generate", "--seed", "9", "--min-fairness", "0.0");

        Assert.NotEqual(0, exit);
        Assert.Contains("acceptance-gate", stderr);
    }

    [Fact]
    public void Generate_MinFairnessOutOfRange_NonZeroExit()
    {
        var (exit, _, stderr) = Run("generate", "--seed", "9", "--min-fairness", "1.5");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects a SpawnBiasIndex threshold in [0, 1]", stderr);
    }

    [Fact]
    public void Generate_MinFairnessMalformed_NonZeroExit()
    {
        var (exit, _, stderr) = Run("generate", "--seed", "9", "--min-fairness", "banana");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects a SpawnBiasIndex threshold in [0, 1]", stderr);
    }

    [Fact]
    public void Simulate_RecordsTrajectoryJsonlToStdout()
    {
        var (exit, stdout, stderr) = Run("simulate", "--seed", "42");

        Assert.Equal(0, exit);
        var lines = stdout.Trim().Split('\n');
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"header\""));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"step\""));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"final\""));
        Assert.Contains("recorded", stderr);
    }

    [Fact]
    public void Simulate_IsDeterministicAcrossRuns()
    {
        Assert.Equal(Run("simulate", "--seed", "42").Stdout, Run("simulate", "--seed", "42").Stdout);
    }

    [Fact]
    public void Simulate_WithStepsAndOutFlag_WritesFile()
    {
        var path = TempPath(".jsonl");
        try
        {
            var (exit, stdout, stderr) = Run("simulate", "--seed", "9", "--steps", "20", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("wrote", stderr);
            var file = File.ReadAllText(path);
            Assert.Contains("\"Kind\":\"header\"", file);
            Assert.Contains("\"Kind\":\"step\"", file);
            Assert.Contains("\"Kind\":\"final\"", file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Simulate_MctsAgentFlag_RecordsTrajectory()
    {
        var (exit, stdout, stderr) = Run("simulate", "--seed", "42", "--agent", "mcts");

        Assert.Equal(0, exit);
        var lines = stdout.Trim().Split('\n');
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"header\""));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"step\""));
        Assert.Contains(lines, line => line.Contains("\"Kind\":\"final\""));
        Assert.Contains("recorded", stderr);
    }

    [Fact]
    public void Simulate_MctsAgent_IsDeterministicAcrossRuns()
    {
        Assert.Equal(
            Run("simulate", "--seed", "42", "--agent", "mcts").Stdout,
            Run("simulate", "--seed", "42", "--agent", "mcts").Stdout);
    }

    [Fact]
    public void Simulate_UnknownAgentFlag_NonZeroExit()
    {
        var (exit, _, stderr) = Run("simulate", "--seed", "1", "--agent", "cfr");
        Assert.NotEqual(0, exit);
        Assert.Contains("invalid --agent", stderr);
    }

    [Fact]
    public void Simulate_RendersAnsiDashboardOnStderr()
    {
        var path = TempPath(".jsonl");
        try
        {
            var (exit, stdout, stderr) = Run("simulate", "--seed", "9", "--steps", "20", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("LATTICE SIMULATION RUN", stderr);
            Assert.Contains("AGENT SCOREBOARD", stderr);
            Assert.Contains("CHOKE CONTENTION", stderr);
            Assert.Contains("serialized StepResult replay equivalence verified", stderr);
            Assert.Contains("\u001b[", stderr); // ANSI decoration present when not quiet
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Simulate_Quiet_SuppressesDashboard()
    {
        var path = TempPath(".jsonl");
        try
        {
            var (exit, stdout, stderr) = Run("simulate", "--seed", "9", "--steps", "20", "--out", path, "--quiet");

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("recorded", stderr);
            Assert.DoesNotContain("LATTICE SIMULATION RUN", stderr);
            Assert.DoesNotContain("\u001b[", stderr);
            Assert.Contains("\"Kind\":\"header\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Simulate_InvalidSteps_NonZeroExit()
    {
        var (exit, _, stderr) = Run("simulate", "--seed", "1", "--steps", "zero");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects a positive integer", stderr);
    }

    [Fact]
    public void Render_Ascii_ReplaysToStdout()
    {
        var trajectory = TempPath(".jsonl");
        try
        {
            Assert.Equal(0, Run("simulate", "--seed", "5", "--steps", "10", "--out", trajectory).ExitCode);

            var (exit, stdout, _) = Run("render", "--trajectory", trajectory);

            Assert.Equal(0, exit);
            Assert.Contains("== initial state ==", stdout);
            Assert.Contains("== step 1 ==", stdout);
            Assert.Contains("actions: ", stdout);
        }
        finally
        {
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Render_Svg_ExportsStandaloneSvg()
    {
        var trajectory = TempPath(".jsonl");
        try
        {
            Assert.Equal(0, Run("simulate", "--seed", "5", "--steps", "10", "--out", trajectory).ExitCode);

            var (exit, stdout, _) = Run("render", "--trajectory", trajectory, "--format", "svg");

            Assert.Equal(0, exit);
            Assert.StartsWith("<?xml", stdout);
            Assert.Contains("<svg", stdout);
            Assert.Contains("class=\"frame\"", stdout);
        }
        finally
        {
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Render_UnknownFormat_NonZeroExit()
    {
        var (exit, _, stderr) = Run("render", "--trajectory", "x.jsonl", "--format", "png");
        Assert.NotEqual(0, exit);
        Assert.Contains("invalid --format", stderr);
    }

    [Fact]
    public void Render_MissingFile_NonZeroExit()
    {
        var (exit, _, stderr) = Run("render", "--trajectory", "does-not-exist.jsonl");
        Assert.NotEqual(0, exit);
        Assert.Contains("error:", stderr);
    }

    [Fact]
    public void Benchmark_InvalidSteps_NonZeroExit()
    {
        var (exit, _, stderr) = Run("benchmark", "--steps", "-5");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects a positive integer", stderr);
    }

    [Fact]
    public void Benchmark_InvalidRuns_NonZeroExit()
    {
        var (exit, _, stderr) = Run("benchmark", "--runs", "0");
        Assert.NotEqual(0, exit);
        Assert.Contains("expects a positive integer", stderr);
    }

    [Fact]
    public void UnknownSubcommand_NonZeroExit()
    {
        var (exit, _, stderr) = Run("frobnicate");
        Assert.NotEqual(0, exit);
        Assert.Contains("unknown command 'frobnicate'", stderr);
    }

    [Fact]
    public void NoArgs_PrintsUsage_NonZeroExit()
    {
        var (exit, _, stderr) = Run();
        Assert.NotEqual(0, exit);
        Assert.Contains("usage: lattice <command> [options]", stderr);
    }

    [Fact]
    public void HelpFlag_PrintsUsage_ZeroExit()
    {
        var (exit, stdout, _) = Run("--help");
        Assert.Equal(0, exit);
        Assert.Contains("usage: lattice <command> [options]", stdout);
    }

    [Fact]
    public void Simulate_InfiltrationScenario_RecordsTrajectoryJsonl()
    {
        var path = TempPath(".jsonl");
        try
        {
            var (exit, stdout, stderr) = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--steps", "20", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("recorded", stderr);
            Assert.Contains("outcome:", stderr);
            var file = File.ReadAllText(path);
            Assert.Contains("\"Kind\":\"header\"", file);
            Assert.Contains("\"Kind\":\"step\"", file);
            Assert.Contains("\"Kind\":\"final\"", file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Simulate_InfiltrationScenario_IsDeterministicAcrossRuns()
    {
        var first = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--steps", "15").Stdout;
        var second = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--steps", "15").Stdout;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Simulate_InfiltrationScenario_RejectsAgentFlag()
    {
        var (exit, _, stderr) = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--agent", "greedy");

        Assert.NotEqual(0, exit);
        Assert.Contains("--agent cannot be used with --scenario infiltration", stderr);
    }

    [Fact]
    public void Simulate_InfiltrationScenario_HeaderCarriesScenarioAndRoles()
    {
        var (exit, stdout, _) = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--steps", "10");

        Assert.Equal(0, exit);
        var headerLine = stdout.Split('\n').First(line => line.Contains("\"Kind\":\"header\""));
        using var document = JsonDocument.Parse(headerLine);
        var header = document.RootElement;
        Assert.Equal("infiltration", header.GetProperty("Scenario").GetString());
        var roles = header.GetProperty("AgentRoles");
        Assert.Equal("Sentry", roles[0].GetString());
        Assert.Equal("Infiltrator", roles[1].GetString());
    }

    [Fact]
    public void Render_InfiltrationAscii_ReplaysScenarioRoster()
    {
        var trajectory = TempPath(".jsonl");
        try
        {
            Assert.Equal(0, Run("simulate", "--scenario", "infiltration", "--seed", "42", "--steps", "10", "--out", trajectory).ExitCode);

            var (exit, stdout, _) = Run("render", "--trajectory", trajectory);

            Assert.Equal(0, exit);
            Assert.Contains("Scenario: infiltration", stdout);
            Assert.Contains("Sentry@Z", stdout);
            Assert.Contains("Infiltrator@Z", stdout);
        }
        finally
        {
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Evaluate_WithOutFlag_WritesJsonArtifact()
    {
        var path = TempPath(".json");
        try
        {
            var (exit, stdout, stderr) = Run("evaluate", "--seed-set", "dev", "--rollouts", "2", "--seeds", "2", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("evaluation suite=dev", stderr);
            Assert.Contains("-> FAIL", stderr);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("CommitSha", out _));
            Assert.True(root.GetProperty("Studies").GetArrayLength() == 1);
            Assert.Equal("dev", root.GetProperty("Studies")[0].GetProperty("Suite").GetString());
            Assert.Equal("MCTS", root.GetProperty("Studies")[0].GetProperty("TargetPolicy").GetString());
            Assert.Equal(2, root.GetProperty("Studies")[0].GetProperty("PerSeed").GetArrayLength());
            Assert.Equal(4, root.GetProperty("Studies")[0].GetProperty("Statistics").GetProperty("Matches").GetInt32());
            Assert.True(root.GetProperty("Studies")[0].GetProperty("Statistics").TryGetProperty("CiLower95", out _));
            Assert.True(root.GetProperty("Studies")[0].GetProperty("Passed").GetBoolean() == false);
            Assert.True(root.GetProperty("Cores").GetInt32() > 0);
            Assert.False(string.IsNullOrEmpty(root.GetProperty("Runtime").GetString()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evaluate_StudyReport_IsDeterministicAcrossRuns()
    {
        var first = TempPath(".json");
        var second = TempPath(".json");
        try
        {
            Assert.Equal(0, Run("evaluate", "--seed-set", "dev", "--rollouts", "2", "--seeds", "2", "--out", first).ExitCode);
            Assert.Equal(0, Run("evaluate", "--seed-set", "dev", "--rollouts", "2", "--seeds", "2", "--out", second).ExitCode);

            using var docA = JsonDocument.Parse(File.ReadAllText(first));
            using var docB = JsonDocument.Parse(File.ReadAllText(second));
            Assert.Equal(
                docA.RootElement.GetProperty("Studies").GetRawText(),
                docB.RootElement.GetProperty("Studies").GetRawText());
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Evaluate_CommitFlag_RecordedInArtifact()
    {
        var path = TempPath(".json");
        try
        {
            Assert.Equal(0, Run("evaluate", "--seed-set", "dev", "--rollouts", "2", "--seeds", "2", "--commit", "abc1234", "--out", path).ExitCode);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("abc1234", document.RootElement.GetProperty("CommitSha").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evaluate_DefaultsToHeldOutSuite()
    {
        var (exit, _, stderr) = Run("evaluate", "--rollouts", "2", "--seeds", "2");

        Assert.Equal(0, exit);
        Assert.Contains("evaluation suite=heldout", stderr);
    }

    [Fact]
    public void Evaluate_MultipleSuites_WritesBothStudies()
    {
        var path = TempPath(".json");
        try
        {
            var (exit, stdout, stderr) = Run("evaluate", "--seed-set", "dev,heldout", "--rollouts", "2", "--seeds", "2", "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("evaluation suite=dev", stderr);
            Assert.Contains("evaluation suite=heldout", stderr);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, document.RootElement.GetProperty("Studies").GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Evaluate_InvalidSeedSet_NonZeroExit()
    {
        var (exit, _, stderr) = Run("evaluate", "--seed-set", "staging");

        Assert.NotEqual(0, exit);
        Assert.Contains("invalid --seed-set", stderr);
    }

    [Fact]
    public void Simulate_WithRulesFile_HeaderCarriesDynamicRulesAndReplayVerifies()
    {
        var rulesFile = TempPath(".json");
        var trajectory = TempPath(".jsonl");
        try
        {
            var map = MapGenerator.Generate(7, new GeneratorConfig(3, 5, 1, 1, 3, 50));
            var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
            {
                new TimedPortcullisRule(map.ChokePoints[0].Id, OpenTicks: 2, ClosedTicks: 2),
            });
            File.WriteAllText(rulesFile, JsonSerializer.Serialize(rules));

            var (exit, stdout, stderr) = Run("simulate", "--seed", "7", "--rules", rulesFile, "--steps", "15", "--out", trajectory);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("recorded", stderr);
            Assert.Contains("serialized StepResult replay equivalence verified", stderr);

            var file = File.ReadAllText(trajectory);
            Assert.Contains("\"DynamicRules\"", file);
            Assert.Contains("\"ruleKind\":\"timed-portcullis\"", file);
        }
        finally
        {
            File.Delete(rulesFile);
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Simulate_WithRulesFile_IsDeterministicAcrossRuns()
    {
        var rulesFile = TempPath(".json");
        try
        {
            var map = MapGenerator.Generate(7, new GeneratorConfig(3, 5, 1, 1, 3, 50));
            var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
            {
                new TimedPortcullisRule(map.ChokePoints[0].Id, OpenTicks: 2, ClosedTicks: 2),
            });
            File.WriteAllText(rulesFile, JsonSerializer.Serialize(rules));

            var first = Run("simulate", "--seed", "7", "--rules", rulesFile, "--steps", "20").Stdout;
            var second = Run("simulate", "--seed", "7", "--rules", rulesFile, "--steps", "20").Stdout;

            Assert.Equal(first, second);
        }
        finally
        {
            File.Delete(rulesFile);
        }
    }

    [Fact]
    public void Simulate_MissingRulesFile_NonZeroExit()
    {
        var (exit, _, stderr) = Run("simulate", "--seed", "7", "--rules", "does-not-exist.json");

        Assert.NotEqual(0, exit);
        Assert.Contains("error:", stderr);
    }

    [Fact]
    public void Simulate_RulesWithInfiltration_NonZeroExit()
    {
        var (exit, _, stderr) = Run("simulate", "--scenario", "infiltration", "--seed", "42", "--rules", "x.json");

        Assert.NotEqual(0, exit);
        Assert.Contains("--rules cannot be used with --scenario infiltration", stderr);
    }

    [Fact]
    public void Benchmark_WritesWorkloadMatrixJsonArtifact()
    {
        var path = TempPath(".json");
        try
        {
            var (exit, stdout, stderr) = Run(
                "benchmark",
                "--runs", "1",
                "--warmup", "40",
                "--steps", "40",
                "--commit", "deadbeef",
                "--cpu", "Unit Test CPU",
                "--out", path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Contains("wrote", stderr);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var metadata = document.RootElement.GetProperty("Metadata");
            Assert.Equal("deadbeef", metadata.GetProperty("Commit").GetString());
            Assert.Equal("Unit Test CPU", metadata.GetProperty("Cpu").GetString());
            Assert.Equal("Release", metadata.GetProperty("Configuration").GetString());
            Assert.True(metadata.GetProperty("Cores").GetInt32() > 0);
            Assert.True(metadata.GetProperty("RamBytes").GetInt64() > 0);

            var workloads = document.RootElement.GetProperty("Workloads");
            Assert.Equal(5, workloads.GetArrayLength());
            var policy = workloads[4];
            Assert.Equal("policy_lookahead_mcts_32", policy.GetProperty("Name").GetString());
            Assert.Equal("decisions", policy.GetProperty("ThroughputMetric").GetString());
            Assert.Equal(2, policy.GetProperty("Agents").GetInt32());
            // The --steps override applies to the raw cases; the MCTS policy
            // case keeps its own catalog budget so a full pass stays bounded.
            Assert.Equal(100, policy.GetProperty("StepsPerIteration").GetInt32());

            foreach (var workload in workloads.EnumerateArray())
            {
                Assert.True(workload.GetProperty("MedianThroughputPerSecond").GetDouble() > 0);
                Assert.True(workload.GetProperty("MeanThroughputPerSecond").GetDouble() > 0);
                Assert.True(
                    workload.GetProperty("P95StepLatencyMicros").GetDouble()
                    >= workload.GetProperty("MedianStepLatencyMicros").GetDouble());
                Assert.True(workload.GetProperty("AllocationsPerStepBytes").GetDouble() >= 0);
                Assert.True(workload.GetProperty("TotalGcGen0").GetInt32() >= 0);
                Assert.True(workload.GetProperty("TotalGcGen1").GetInt32() >= 0);
                Assert.True(workload.GetProperty("TotalGcGen2").GetInt32() >= 0);
            }

            var facility = workloads[1];
            Assert.Equal("facility_static_4agent", facility.GetProperty("Name").GetString());
            Assert.Equal("Medium", facility.GetProperty("MapScale").GetString());
            Assert.False(facility.GetProperty("DynamicTopology").GetBoolean());
            Assert.Equal(4, facility.GetProperty("Agents").GetInt32());

            var dynamic = workloads[2];
            Assert.Equal("dynamic_contention_4agent", dynamic.GetProperty("Name").GetString());
            Assert.True(dynamic.GetProperty("DynamicTopology").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Benchmark_JsonArtifactWithoutOut_PrintsToStdout()
    {
        var (exit, stdout, _) = Run("benchmark", "--runs", "1", "--warmup", "40", "--steps", "40");

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("Workloads", out var workloads));
        Assert.Equal(5, workloads.GetArrayLength());
    }

    [Fact]
    public void Replay_Verify_GeneratedTrajectory_ReturnsSuccess()
    {
        var trajectory = TempPath(".jsonl");
        try
        {
            Assert.Equal(0, Run("simulate", "--seed", "7", "--steps", "15", "--out", trajectory).ExitCode);

            var (exit, _, stderr) = Run("replay", trajectory, "--verify");

            Assert.Equal(0, exit);
            Assert.Contains("replay verified", stderr);
        }
        finally
        {
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Replay_Verify_CorruptedTrajectory_NonZeroExit()
    {
        var trajectory = TempPath(".jsonl");
        try
        {
            var (exit, _, _) = Run("simulate", "--seed", "7", "--steps", "15", "--out", trajectory);
            Assert.Equal(0, exit);

            var lines = File.ReadAllLines(trajectory).ToList();
            // Corrupt the first step's recorded result so replay diverges.
            lines[1] = lines[1].Replace("\"Kind\":\"step\"", "\"Kind\":\"step\"").Replace("\"Rewards\"", "\"ReWARDS\"");
            File.WriteAllLines(trajectory, lines);

            var (replayExit, _, stderr) = Run("replay", trajectory, "--verify");

            Assert.NotEqual(0, replayExit);
            Assert.False(string.IsNullOrEmpty(stderr));
        }
        finally
        {
            File.Delete(trajectory);
        }
    }

    [Fact]
    public void Replay_MissingPath_NonZeroExit()
    {
        var (exit, _, stderr) = Run("replay", "--verify");

        Assert.NotEqual(0, exit);
        Assert.Contains("missing trajectory path", stderr);
    }

    [Fact]
    public void Replay_NonexistentFile_NonZeroExit()
    {
        var (exit, _, stderr) = Run("replay", "/definitely/not/here.jsonl", "--verify");

        Assert.NotEqual(0, exit);
        Assert.False(string.IsNullOrEmpty(stderr));
    }
}