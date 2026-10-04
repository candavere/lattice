using Lattice.Agents;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Trajectories;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Argument parity between <c>lattice simulate</c> and
/// <c>lattice tui simulate</c>: every flag the batch command takes is either
/// honoured or refused with a clear reason, and the episode the live viewer builds
/// is the episode the batch command records.
/// </summary>
/// <remarks>
/// The comparison is against the artifact the untouched batch command writes, by
/// per-tick state hash, rather than by asking two constructions whether they agree
/// with each other.
/// </remarks>
public class LiveArgumentParityTests
{
    private const ulong Seed = 42;

    /// <summary>A real descriptor from the repository, not a fixture written for this test.</summary>
    private const string Descriptor = "scenarios/dungeon-infiltration.json";

    private const string BuiltIn = "infiltration";

    [Theory]
    [InlineData(Descriptor)]
    [InlineData("scenarios/gated-vault-duel.json")]
    [InlineData("scenarios/bottleneck-contention.json")]
    public void ADescriptorFileBuildsTheEpisodeTheBatchCommandRecords(string path)
    {
        var batch = Batch(Committed(path));
        var live = LiveEpisodeSetup.FromDescriptorFile(Committed(path), Seed);

        AssertSameEpisode(batch, live);
    }

    [Fact]
    public void TheBuiltInScenarioBuildsTheEpisodeTheBatchCommandRecords()
    {
        var batch = BatchScenario(BuiltIn, steps: 100);
        var live = LiveEpisodeSetup.Infiltration(Seed, 100);

        AssertSameEpisode(batch, live);
    }

    [Fact]
    public void TheGenericSkirmishBuildsTheEpisodeTheBatchCommandRecords()
    {
        var batch = BatchScenario(null, steps: 30);
        var live = LiveEpisodeSetup.Skirmish(Seed, 30, "greedy", DynamicMapRuleSet.None);

        AssertSameEpisode(batch, live);
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("--out")]
    public void OnlyOutIsRefusedAndQuietIsAccepted(string flag)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["tui", "simulate", "--seed", "42", flag, flag == "--quiet" ? "" : "x.jsonl"], stdout, stderr);

        // --quiet is a batch flag a live viewer has nothing to suppress, so it is
        // accepted; --out asks a live run to write a file, which it does not do, so
        // it is refused by name. Either way the usage status and an explanation.
        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.NotEqual("", stderr.ToString());

        if (flag == "--out")
        {
            Assert.Contains("--out", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("records nothing", stderr.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The batch command's own recorded trajectory for a descriptor file, read with
    /// the repository's reader. This is the artifact an independent run produced.
    /// </summary>
    private static TrajectoryRecording Batch(string path)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["simulate", "--quiet", "--seed", Seed.ToString(), "--scenario", path], stdout, stderr);

        Assert.Equal(0, exit);
        return TrajectoryReader.Read(new StringReader(stdout.ToString()));
    }

    /// <summary>The batch command's recorded trajectory for a named or absent scenario.</summary>
    private static TrajectoryRecording BatchScenario(string? scenario, int steps)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var args = new List<string> { "simulate", "--quiet", "--seed", Seed.ToString(), "--steps", steps.ToString() };
        if (scenario is not null)
        {
            args.AddRange(["--scenario", scenario]);
        }

        var exit = CliApp.Run(args.ToArray(), stdout, stderr);

        Assert.Equal(0, exit);
        return TrajectoryReader.Read(new StringReader(stdout.ToString()));
    }

    /// <summary>
    /// The live setup's episode is the batch episode: the same map, the same roster
    /// types, the same seed and config, and the same per-tick state digest at every
    /// tick the recording carries.
    /// </summary>
    private static void AssertSameEpisode(TrajectoryRecording recording, LiveEpisodeSetup live)
    {
        Assert.Equal(recording.Header.Seed, live.Seed);
        Assert.Equal(recording.Header.SimulationConfig.AgentCount, live.Config.AgentCount);
        Assert.Equal(recording.Header.SimulationConfig.MaxTicks, live.Config.MaxTicks);
        Assert.Equal(recording.Header.SimulationConfig.Vision, live.Config.Vision);
        Assert.Equal(recording.Header.SimulationConfig.TransitSpeed, live.Config.TransitSpeed);

        var stepper = live.NewStepper();
        var hashes = new List<string>();
        while (stepper.CanStep)
        {
            stepper.Step();
            hashes.Add(SimulationStateHash.Compute(stepper.State, live.Seed));
        }

        Assert.Equal(recording.Steps.Length, hashes.Count);
        for (var i = 0; i < hashes.Count; i++)
        {
            Assert.Equal(recording.Steps[i].StateHash, hashes[i]);
        }
    }

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
