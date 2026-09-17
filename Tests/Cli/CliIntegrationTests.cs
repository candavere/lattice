using System.Text.Json;
using Lattice.Cli;
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
    public void Benchmark_PrintsThroughputAndMemory()
    {
        var (exit, stdout, _) = Run("benchmark", "--ticks", "200");

        Assert.Equal(0, exit);
        Assert.Contains("ticks=200", stdout);
        Assert.Contains("steps_per_second=", stdout);
        Assert.Contains("allocated_bytes=", stdout);
        Assert.Contains("bytes_per_tick=", stdout);
    }

    [Fact]
    public void Benchmark_InvalidTicks_NonZeroExit()
    {
        var (exit, _, stderr) = Run("benchmark", "--ticks", "-5");
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
}