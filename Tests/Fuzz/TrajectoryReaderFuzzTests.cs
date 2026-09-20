using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of <see cref="TrajectoryReader"/>: the golden trajectory is
/// mutated by bit flips, byte deletions/insertions, line truncation and
/// re-splitting, critical-token value corruption, duplicate/reordered lines,
/// and adversarial payload injections. Every mutation must either be rejected
/// gracefully (typed <see cref="InvalidDataException"/>/<see cref="System.Text.Json.JsonException"/>
/// or a domain exception) or parse into a recording that the replay and
/// re-write surfaces consume without crashing. The invariant mirrors the
/// engine contract: an adversarial file rejects with clear typed exceptions and
/// never escapes with an unhandled runtime fault.
/// </summary>
public sealed class TrajectoryReaderFuzzTests
{
    private static readonly string GoldenTrajectory =
        File.ReadAllText(FixtureResolver.Fixture("golden_trajectory.jsonl"));

    private static readonly string[] TrajectoryTokens =
    {
        "SchemaVersion", "StepNumber", "TotalSteps", "Kind", "FinalScores", "Claims",
        "Actions", "ResourceId", "ZoneId", "WinnerAgentId", "ResourcesClaimed",
        "TotalResources", "Seed", "Reason", "MaxOccupancy", "TransitSpeed",
    };

    [Theory]
    [InlineData(1337)]
    [InlineData(42424242)]
    public void TrajectoryReader_RejectsOrAcceptsEveryMutation_Gracefully(int baseSeed)
    {
        FuzzHarness.Run("trajectory-reader", baseSeed, FuzzHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng(caseSeed);
            var mutated = TextMutator.Mutate(GoldenTrajectory, rng, TrajectoryTokens);

            var recording = ReadSafely(mutated);
            if (recording is not null)
            {
                FuzzHarness.ExpectGraceful("TrajectoryReplay.Verify on accepted mutation", () =>
                {
                    _ = TrajectoryReplay.Verify(recording);
                });
                FuzzHarness.ExpectGraceful("TrajectoryWriter.Write on accepted mutation", () =>
                {
                    TrajectoryWriter.Write(recording, new StringWriter());
                });
            }

            FuzzHarness.ExpectGraceful("TrajectoryReader.ReadHeader + StreamSteps", () =>
            {
                using var lazy = new StringReader(mutated);
                _ = TrajectoryReader.ReadHeader(lazy);
                _ = TrajectoryReader.StreamSteps(lazy).ToArray();
            });
        });
    }

    [Theory]
    [InlineData(777)]
    [InlineData(5150)]
    public void TrajectoryReader_RejectsTruncatedAndHeaderlessStreams_Gracefully(int baseSeed)
    {
        FuzzHarness.Run("trajectory-reader-truncation", baseSeed, 600, caseSeed =>
        {
            var rng = new Rng(caseSeed);
            var text = GoldenTrajectory;

            var drop = rng.Next(1, 4);
            var lines = text.Split('\n').ToList();
            lines = lines.Take(Math.Max(0, lines.Count - drop)).ToList();

            if (rng.Next(0, 2) == 0)
            {
                lines[0] = lines[0][..Math.Max(1, lines[0].Length / 2)];
            }

            _ = ReadSafely(string.Join('\n', lines));
        });
    }

    public static IEnumerable<object[]> KnownDefects()
    {
        yield return new object[] { "trajectory_null_actions.jsonl" };
        yield return new object[] { "trajectory_null_final_scores.jsonl" };
        yield return new object[] { "trajectory_short_final_scores.jsonl" };
        yield return new object[] { "trajectory_missing_map_resources.jsonl" };
        yield return new object[] { "trajectory_null_simulation_config.jsonl" };
    }

    [Theory]
    [MemberData(nameof(KnownDefects))]
    public void TrajectoryReader_RejectsKnownAdversarialFixtures_Gracefully(string fixture)
    {
        var path = FixtureResolver.Fixture(Path.Combine("fuzz", fixture));
        using var reader = new StreamReader(path);
        var exception = Assert.ThrowsAny<System.IO.InvalidDataException>(() => TrajectoryReader.Read(reader));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    private static TrajectoryRecording? ReadSafely(string text)
    {
        TrajectoryRecording? recording = null;
        FuzzHarness.ExpectGraceful("TrajectoryReader.Read", () =>
        {
            using var reader = new StringReader(text);
            recording = TrajectoryReader.Read(reader);
        });

        return recording;
    }
}