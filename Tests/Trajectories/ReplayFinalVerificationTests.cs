using Lattice.Environment;
using Lattice.Tests.Environment;
using Lattice.Tests.Fuzz;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// Final-line authentication for <see cref="TrajectoryReplay.Verify"/>: the
/// recorded final summary line's aggregate fields (Reason, WinnerAgentId,
/// TotalSteps, FinalScores, ResourcesClaimed, TotalResources) are recomputed
/// from the re-simulated run and compared field by field, so a same-length
/// tampered final line fails instead of slipping past the reader's
/// array-length check.
/// </summary>
public class ReplayFinalVerificationTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static AgentAction[][] CollectorEpisode() => new[]
    {
        new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Wait) },
    };

    private static TrajectoryRecording RecordedEpisode()
    {
        return TrajectoryWriter.Record(
            Map, new SimulationConfig(2, 20), 0xF17EUL, CollectorEpisode(), new StringWriter());
    }

    private static string Jsonl(TrajectoryRecording recording)
    {
        var buffer = new StringWriter();
        TrajectoryWriter.Write(recording, buffer);
        // Normalize line endings so tampering by string surgery is stable on
        // CRLF (Windows) runners as well as LF (Linux/macOS) runners.
        return buffer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void UntouchedGoldenFixture_PassesVerification()
    {
        using var reader = new StreamReader(FixtureResolver.Fixture("golden_trajectory.jsonl"));
        var recording = TrajectoryReader.Read(reader);

        Assert.Empty(TrajectoryReplay.Verify(recording));
    }

    /// <summary>
    /// Rewrites the first digit of the first FinalScores entry in the final
    /// line: identical string length and identical array length, so the
    /// reader's length checks pass and only replay-level authentication can
    /// catch the tamper.
    /// </summary>
    private static string TamperFirstFinalScore(string jsonl)
    {
        const string marker = "\"FinalScores\":[";
        var start = jsonl.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var firstDigit = jsonl[start];
        var replacement = firstDigit == '0' ? '9' : '0';

        return jsonl[..start] + replacement + jsonl[(start + 1)..];
    }

    [Fact]
    public void TamperedFinalScores_SameLength_FailsNamingTheField()
    {
        var tampered = TrajectoryReader.Read(new StringReader(TamperFirstFinalScore(Jsonl(RecordedEpisode()))));

        var problems = TrajectoryReplay.Verify(tampered);

        Assert.Contains(problems, p => p.Contains("'FinalScores'", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedReason_FailsNamingTheField()
    {
        var recording = RecordedEpisode();
        var forgedReason = recording.Final.Reason == "TimeLimit" ? "AllResourcesClaimed" : "TimeLimit";
        var forged = recording with { Final = recording.Final with { Reason = forgedReason } };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("'Reason'", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedWinnerAgentId_FailsNamingTheField()
    {
        var recording = RecordedEpisode();
        var forged = recording with { Final = recording.Final with { WinnerAgentId = recording.Final.WinnerAgentId is 0 ? 1 : 0 } };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("'WinnerAgentId'", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedTotalSteps_FailsNamingTheField()
    {
        var recording = RecordedEpisode();
        var forged = recording with { Final = recording.Final with { TotalSteps = recording.Final.TotalSteps + 1 } };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("'TotalSteps'", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedResourcesClaimed_FailsNamingTheField()
    {
        var recording = RecordedEpisode();
        var forged = recording with { Final = recording.Final with { ResourcesClaimed = recording.Final.ResourcesClaimed + 1 } };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("'ResourcesClaimed'", StringComparison.Ordinal));
    }

    [Fact]
    public void TamperedTotalResources_FailsNamingTheField()
    {
        var recording = RecordedEpisode();
        var forged = recording with { Final = recording.Final with { TotalResources = recording.Final.TotalResources + 1 } };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("'TotalResources'", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFinalLine_IsRejected()
    {
        var lines = Jsonl(RecordedEpisode()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var withoutFinal = string.Join('\n', lines.Take(lines.Length - 1));

        Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(withoutFinal)));
    }
}
