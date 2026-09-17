using Lattice.Cli;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Integration coverage for the `analyze` subcommand added in T6.3: it drives
/// the real <see cref="CliApp.Run"/> entry point — the same code
/// <c>Program.Main</c> forwards to — against a written trajectory JSONL file,
/// asserting exit codes, stdout/stderr side effects, and byte-identical output
/// across runs.
/// </summary>
public class CliAnalyzeTests : IDisposable
{
    private sealed record TempTrajectory(string DirectoryPath, string TrajectoryPath, string ReportPath) : IDisposable
    {
        public static TempTrajectory Create(int seed = 123, int steps = 12)
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"lattice-analyze-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var trajectoryPath = System.IO.Path.Combine(directory, "trajectory.jsonl");
            using (var sink = new StreamWriter(trajectoryPath))
            {
                TrajectoryWriter.Record(
                    MapGeneratorLoader.For(seed),
                    new Lattice.Environment.SimulationConfig(AgentCount: 2, MaxTicks: steps),
                    (ulong)seed,
                    TurnsFor(steps),
                    sink);
            }

            return new TempTrajectory(directory, trajectoryPath, System.IO.Path.Combine(directory, "report.md"));
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private readonly TempTrajectory temp;

    public CliAnalyzeTests()
    {
        temp = TempTrajectory.Create();
    }

    void IDisposable.Dispose()
    {
        temp?.Dispose();
    }

    private static Lattice.Environment.AgentAction[][] TurnsFor(int count)
    {
        var turns = new Lattice.Environment.AgentAction[count][];
        for (var tick = 0; tick < count; tick++)
        {
            turns[tick] = new[]
            {
                new Lattice.Environment.AgentAction(Lattice.Environment.ActionKind.Wait),
                new Lattice.Environment.AgentAction(Lattice.Environment.ActionKind.Wait),
            };
        }

        return turns;
    }

    [Fact]
    public void Analyze_WithoutOut_PrintsCompactTerminalReportToStdout()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(new[] { "analyze", "--trajectory", temp.TrajectoryPath }, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Empty(stderr.ToString());
        Assert.Contains("Lattice Tactical Report", stdout.ToString());
        Assert.Contains("Zone Occupancy", stdout.ToString());
    }

    [Fact]
    public void Analyze_WithOut_WritesMarkdownReportAndReportsToStderr()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(
            new[] { "analyze", "--trajectory", temp.TrajectoryPath, "--out", temp.ReportPath },
            stdout,
            stderr);

        Assert.Equal(0, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("wrote", stderr.ToString());
        var report = File.ReadAllText(temp.ReportPath);
        Assert.Contains("## Summary", report);
        Assert.Contains("## Contention Events", report);
        Assert.Contains("## Turning Points", report);
        Assert.Contains("## Resource Timeline", report);
        Assert.Contains("## Zone Occupancy", report);
        Assert.Contains("## Edge Traversals", report);
        Assert.Contains("## Steps Timeline", report);
    }

    [Fact]
    public void Analyze_WithOut_IsDeterministicAcrossRuns()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(
            0,
            CliApp.Run(
                new[] { "analyze", "--trajectory", temp.TrajectoryPath, "--out", temp.ReportPath },
                stdout,
                stderr));
        var first = File.ReadAllBytes(temp.ReportPath);

        var secondPath = temp.ReportPath + ".2";
        try
        {
            Assert.Equal(
                0,
                CliApp.Run(new[] { "analyze", "--trajectory", temp.TrajectoryPath, "--out", secondPath }, stdout, stderr));
            Assert.Equal(first, File.ReadAllBytes(secondPath));
        }
        finally
        {
            File.Delete(secondPath);
        }
    }

    [Fact]
    public void Analyze_WithoutOut_IsDeterministicAcrossRuns()
    {
        using var firstOut = new StringWriter();
        using var firstErr = new StringWriter();
        CliApp.Run(new[] { "analyze", "--trajectory", temp.TrajectoryPath }, firstOut, firstErr);

        using var secondOut = new StringWriter();
        using var secondErr = new StringWriter();
        CliApp.Run(new[] { "analyze", "--trajectory", temp.TrajectoryPath }, secondOut, secondErr);
        Assert.Equal(firstOut.ToString(), secondOut.ToString());
    }

    [Fact]
    public void Analyze_MissingTrajectoryFlag_FailsWithMessage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(new[] { "analyze" }, stdout, stderr);
        Assert.Equal(1, exit);
        Assert.Contains("missing required flag '--trajectory'.", stderr.ToString());
    }

    [Fact]
    public void Analyze_UnknownFlag_FailsWithMessage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(
            new[] { "analyze", "--trajectory", temp.TrajectoryPath, "--bogus", "x" },
            stdout,
            stderr);
        Assert.Equal(1, exit);
        Assert.Contains("unknown flag '--bogus'.", stderr.ToString());
    }

    [Fact]
    public void Analyze_MissingFile_FailsWithMessage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(
            new[] { "analyze", "--trajectory", "/definitely/not/here.jsonl" },
            stdout,
            stderr);
        Assert.Equal(1, exit);
        Assert.Contains("error:", stderr.ToString());
    }

    [Fact]
    public void Analyze_RejectsSpuriousPositionals()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(new[] { "analyze", "--trajectory", temp.TrajectoryPath, "stray" }, stdout, stderr);
        Assert.Equal(1, exit);
        Assert.Contains("unexpected argument 'stray'.", stderr.ToString());
    }

    /// <summary>
    /// Loads a map deterministically; kept separate from the fixture logic so
    /// this test-class helper reads like the generator API it wraps.
    /// </summary>
    private static class MapGeneratorLoader
    {
        public static Lattice.Environment.MapGraph For(int seed) =>
            Lattice.Generator.MapGenerator.Generate(
                (ulong)seed,
                new Lattice.Generator.GeneratorConfig(3, 5, 1, 1, 3, 50));
    }
}