using System.Text.RegularExpressions;
using Lattice.Environment;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// The point of the per-step state hash: verification recomputes the digest of
/// the replayed world at every tick and compares it to the recorded one, so a
/// recording whose step results look right but whose world state diverged is
/// caught, and the first bad tick is named. A recording with no hashes at all
/// still verifies — on step results alone — and says so.
/// </summary>
public class StateHashVerificationTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static AgentAction[][] Episode() => new[]
    {
        new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) },
    };

    private static TrajectoryRecording Recorded() =>
        TrajectoryWriter.Record(Map, new SimulationConfig(2, 20), 0xF17EUL, Episode(), new StringWriter());

    private static string Jsonl(TrajectoryRecording recording)
    {
        var buffer = new StringWriter();
        TrajectoryWriter.Write(recording, buffer);
        return buffer.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// Turns a current recording into what a pre-hash build wrote: the hash
    /// field removed from every step line and the header stamped schema 2.
    /// </summary>
    private static string ToLegacyJsonl(TrajectoryRecording recording)
    {
        var stripped = Regex.Replace(
            Jsonl(recording),
            ",\"StateHash\":\"[0-9a-f]{64}\"",
            string.Empty);
        return stripped.Replace(
            $"\"SchemaVersion\":{TrajectorySchema.CurrentVersion}",
            "\"SchemaVersion\":2",
            StringComparison.Ordinal);
    }

    private static TrajectoryRecording Legacy() =>
        TrajectoryReader.Read(new StringReader(ToLegacyJsonl(Recorded())));

    /// <summary>
    /// A well-formed digest of a state that is not the recorded one — the
    /// realistic tamper, as opposed to a non-hex string the reader would reject
    /// before replay ever runs.
    /// </summary>
    private static string DigestOfADifferentState(TrajectoryRecording recording)
    {
        var state = new SimulationState(
            recording.Header.Map,
            recording.Steps[0].Result.Observations[0].AgentStates,
            recording.Steps[0].Result.Observations[0].Claims,
            recording.Steps[0].Result.Observations[0].StepNumber);

        return SimulationStateHash.Compute(state, recording.Header.Seed + 1UL);
    }

    [Fact]
    public void UntamperedRecording_VerifiesWithNoProblemsAndNoNotice()
    {
        var report = TrajectoryReplay.VerifyDetailed(Recorded());

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
    }

    [Fact]
    public void TamperedStateHash_FailsEvenThoughTheStepResultsAreIntact()
    {
        var recording = Recorded();
        var wrongHash = DigestOfADifferentState(recording);
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 1 ? step with { StateHash = wrongHash } : step)
                .ToArray(),
        };
        var stripped = forged with
        {
            Steps = forged.Steps.Select(step => step with { StateHash = null }).ToArray(),
        };

        // The step results are untouched, so the step-level comparison finds
        // nothing to complain about in either form — the digest is the only
        // thing that catches the forgery.
        Assert.DoesNotContain(
            TrajectoryReplay.Verify(forged),
            p => p.Contains("result diverges", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TrajectoryReplay.Verify(stripped),
            p => p.Contains("result diverges", StringComparison.Ordinal));

        // And removing the digests to dodge the check does not get a pass:
        // stripping the hashes is reported in its own right.
        Assert.NotEmpty(TrajectoryReplay.Verify(forged));
        Assert.NotEmpty(TrajectoryReplay.Verify(stripped));
    }

    [Fact]
    public void TamperedStateHash_NamesTheMismatchedTick()
    {
        var recording = Recorded();
        var wrongHash = DigestOfADifferentState(recording);
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 1 ? step with { StateHash = wrongHash } : step)
                .ToArray(),
        };

        var problems = TrajectoryReplay.Verify(forged);

        Assert.Contains(problems, p => p.Contains("Step 2", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("state hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TamperedStateHash_NamesTheFirstMismatchedTickAndBothDigests()
    {
        var recording = Recorded();
        var wrongHash = DigestOfADifferentState(recording);
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i >= 1 ? step with { StateHash = wrongHash } : step)
                .ToArray(),
        };

        var problems = TrajectoryReplay.Verify(forged);

        // Steps 2, 3 and 4 all carry the wrong digest. Every one is reported —
        // as the step-result comparison already does — and they are reported in
        // tick order, so the first entry names the first mismatched tick and
        // carries both digests for diagnosis.
        var hashProblems = problems
            .Where(p => p.Contains("state hash", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(3, hashProblems.Length);
        Assert.Contains("Step 2", hashProblems[0], StringComparison.Ordinal);
        Assert.Contains(wrongHash, hashProblems[0], StringComparison.Ordinal);
        Assert.Contains("expected ", hashProblems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRecordingWithoutHashes_VerifiesOnStepResultsWithANotice()
    {
        var recording = Legacy();
        Assert.All(recording.Steps, step => Assert.Null(step.StateHash));

        var report = TrajectoryReplay.VerifyDetailed(recording);

        Assert.Empty(report.Problems);
        Assert.Contains(report.Notices, n => n.Contains("no state hash: step-level verification only", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyRecording_StillReportsARealTamper()
    {
        var recording = Legacy();
        var forged = recording with
        {
            Final = recording.Final with { ResourcesClaimed = recording.Final.ResourcesClaimed + 1 },
        };

        var report = TrajectoryReplay.VerifyDetailed(forged);

        Assert.Contains(report.Problems, p => p.Contains("'ResourcesClaimed'", StringComparison.Ordinal));
    }

    [Fact]
    public void PartiallyHashedRecording_IsAProblem()
    {
        var recording = Recorded();
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 0 ? step with { StateHash = null } : step)
                .ToArray(),
        };

        var report = TrajectoryReplay.VerifyDetailed(forged);

        Assert.Contains(report.Problems, p => p.Contains("StateHash", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stripping every digest off a schema-3 file must not be a silent exit-0
    /// downgrade: v3 is defined as carrying a digest at every step, so a v3
    /// file with none is a corrupt recording, not a legacy one.
    /// </summary>
    [Fact]
    public void CurrentSchemaRecordingWithEveryHashStripped_IsAProblemNotANotice()
    {
        var recording = Recorded();
        Assert.Equal(TrajectorySchema.CurrentVersion, recording.Header.SchemaVersion);
        var stripped = recording with
        {
            Steps = recording.Steps.Select(step => step with { StateHash = null }).ToArray(),
        };

        var report = TrajectoryReplay.VerifyDetailed(stripped);

        Assert.Contains(report.Problems, p => p.Contains("StateHash", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Notices, n => n.Contains("step-level verification only", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacySchemaRecordingWithNoHashes_StillOnlyNotices()
    {
        var legacy = Legacy();
        Assert.True(legacy.Header.SchemaVersion < TrajectorySchema.CurrentVersion);

        var report = TrajectoryReplay.VerifyDetailed(legacy);

        Assert.Empty(report.Problems);
        Assert.Contains(report.Notices, n => n.Contains("step-level verification only", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_IsTheProblemsHalfOfTheDetailedReport()
    {
        var recording = Legacy();

        Assert.Equal(
            TrajectoryReplay.VerifyDetailed(recording).Problems,
            TrajectoryReplay.Verify(recording));
    }

    [Fact]
    public void ReadBackFromDisk_HashesStillVerify()
    {
        var recording = TrajectoryReader.Read(new StringReader(Jsonl(Recorded())));

        var report = TrajectoryReplay.VerifyDetailed(recording);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
    }

    private static DynamicMapRuleSet Portcullis() => new(new IDynamicMapRule[]
    {
        new TimedPortcullisRule(0, OpenTicks: 1, ClosedTicks: 1),
    });

    /// <summary>
    /// End-to-end over a portcullis episode. Every other test here runs on a
    /// static map, where the capacity snapshot the digest adds over the
    /// pre-existing step-result gate is empty at every tick; this one drives the
    /// snapshot through record -> write -> read -> verify with the gate
    /// alternating open and closed.
    /// </summary>
    [Fact]
    public void DynamicChokeEpisode_VerifiesEndToEndThroughTheSnapshot()
    {
        var rules = Portcullis();
        var recorded = TrajectoryWriter.Record(
            Map, new SimulationConfig(2, 8), 0xABCDUL, Episode(), new StringWriter(), rules: rules);
        var readBack = TrajectoryReader.Read(new StringReader(Jsonl(recorded)));

        var report = TrajectoryReplay.VerifyDetailed(readBack);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
        Assert.All(readBack.Steps, step => Assert.Matches("^[0-9a-f]{64}$", step.StateHash ?? string.Empty));
    }

    /// <summary>
    /// The digest is load-bearing: a well-formed digest of the wrong world is
    /// caught even though every recorded step result is untouched.
    /// </summary>
    [Fact]
    public void DynamicChokeEpisode_TamperedDigestIsCaught()
    {
        var recorded = TrajectoryWriter.Record(
            Map, new SimulationConfig(2, 8), 0xABCDUL, Episode(), new StringWriter(), rules: Portcullis());
        var wrong = SimulationStateHash.Compute(
            Simulation.CreateInitial(Map, new SimulationConfig(2, 8), Portcullis()),
            0xABCDUL);
        var forged = recorded with
        {
            Steps = recorded.Steps.Select(step => step with { StateHash = wrong }).ToArray(),
        };

        Assert.Contains(
            TrajectoryReplay.Verify(forged),
            p => p.Contains("state hash", StringComparison.OrdinalIgnoreCase));
    }
}
