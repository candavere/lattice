using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// The decision-time perception recording, asserted on behaviour rather than
/// on the shape of the file. Four claims, each of which the previous recording
/// could not make at all:
/// <list type="bullet">
/// <item>the recorded perception is the very object the agent's own filter
/// produced inside <c>Decide</c> — reference equality, read from inside the
/// decision by a test hook, for every step and every agent;</item>
/// <item>verify recomputes each perception and catches an edit to a zone
/// status, a last-seen tick, or a rival sighting, while the older
/// <see cref="StepResult"/> and <see cref="SimulationStateHash"/> gates still
/// fire on their own tampering;</item>
/// <item>stripping the new fields reproduces the pre-change recording byte for
/// byte, so the episode itself is untouched by the schema bump;</item>
/// <item>a recording from before the field — the golden fixture and the
/// committed demo — still verifies, and a schema-4 recording with no
/// perceptions says so instead of passing in silence.</item>
/// </list>
/// </summary>
public class DecisionTimePerceptionTests
{
    /// <summary>
    /// The seed and budget the committed <c>site/infiltration.jsonl</c> is
    /// recorded with, so these tests exercise the episode the site ships rather
    /// than a convenient one.
    /// </summary>
    private const ulong Seed = 42;

    private const int MaxSteps = 100;

    /// <summary>
    /// SHA-256 of <c>site/infiltration.jsonl</c> as it stood before decision-time
    /// perceptions existed: 66,465 bytes, schema v3, 20 steps. Pinned so the
    /// invariance claim is a claim about those exact bytes rather than about a
    /// re-run of today's writer, which would agree with itself by construction.
    /// An engine change that moves the episode must move this constant on
    /// purpose.
    /// </summary>
    private const string PreChangeEpisodeSha256 =
        "fe213bd5ed2180942cba5334e350a974d3a36e17b319281d9ad8efe99d57f3f5";

    private const int PreChangeSchemaVersion = 3;

    /// <summary>
    /// A perception probe that reads the wrapped agent's own
    /// <see cref="IDecidesFromPerception.LastPerception"/> from inside
    /// <see cref="Decide"/>, after the wrapped agent has decided and before the
    /// runner has moved on. It is the only place the test sees the value, so a
    /// recording that captured anything other than this object fails on
    /// reference equality rather than on a coincidence of contents.
    /// </summary>
    private sealed class DecisionProbe : IAgent, IDecidesFromPerception
    {
        private readonly IAgent _inner;
        private readonly IDecidesFromPerception _perceiving;

        internal DecisionProbe(IAgent inner)
        {
            _inner = inner;
            _perceiving = (IDecidesFromPerception)inner;
            Snapshots = new List<PartialObservation>();
        }

        public int AgentId => _inner.AgentId;

        public PartialObservation? LastPerception => _perceiving.LastPerception;

        internal List<PartialObservation> Snapshots { get; }

        public AgentAction Decide(Observation observation)
        {
            var action = _inner.Decide(observation);
            Snapshots.Add(_perceiving.LastPerception!);
            return action;
        }
    }

    private static InfiltrationScenarioResult Run()
    {
        return InfiltrationScenario.Run(Seed, MaxSteps);
    }

    private static TrajectoryRecording Recorded(InfiltrationScenarioResult run)
    {
        return TrajectoryWriter.Record(
            run.Map,
            run.Config,
            Seed,
            run.Base.Turns,
            new StringWriter(),
            scenario: InfiltrationScenario.ScenarioName,
            agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
            perceptions: run.Base.Perceptions);
    }

    private static string Jsonl(TrajectoryRecording recording)
    {
        var buffer = new StringWriter();
        TrajectoryWriter.Write(recording, buffer);
        return buffer.ToString().Replace("\r\n", "\n");
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The recording with one step's perception for one agent replaced.</summary>
    private static TrajectoryRecording WithPerception(
        TrajectoryRecording recording,
        int stepIndex,
        int agentId,
        Func<PartialObservation, PartialObservation> edit) =>
        recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i != stepIndex
                    ? step
                    : step with
                    {
                        Perceptions = step.Perceptions!
                            .Select((p, a) => a == agentId ? edit(p) : p)
                            .ToArray(),
                    })
                .ToArray(),
        };

    /// <summary>Walks up from the test output directory to the committed site recording.</summary>
    private static string SiteRecording(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 12; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "site", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"site/{name} not found while searching upward from {AppContext.BaseDirectory}.");
    }

    // -- the recording IS the agent's own decision-time object ----------------

    [Fact]
    public void RecordedPerception_IsTheVeryObjectTheAgentsDecidedFrom()
    {
        var map = DungeonMapBuilder.Build(Seed);
        var config = InfiltrationScenario.DefaultConfig(MaxSteps);
        var sentry = new DecisionProbe(new SentryPatrolAgent(
            InfiltrationScenario.SentryAgentId, InfiltrationScenario.InfiltratorAgentId));
        var infiltrator = new DecisionProbe(new InfiltratorAgent(
            InfiltrationScenario.InfiltratorAgentId, InfiltrationScenario.SentryAgentId));

        var result = ScenarioRunner.Run(
            map, config, new IAgent[] { sentry, infiltrator }, MaxSteps, recordPerceptions: true);

        var probes = new[] { sentry, infiltrator };
        Assert.NotNull(result.Perceptions);
        Assert.NotEmpty(result.Perceptions);
        Assert.Equal(result.Turns.Length, result.Perceptions!.Length);

        for (var step = 0; step < result.Perceptions.Length; step++)
        {
            for (var agentId = 0; agentId < probes.Length; agentId++)
            {
                Assert.Same(
                    probes[agentId].Snapshots[step],
                    result.Perceptions[step][agentId]);
            }
        }
    }

    [Fact]
    public void RecordedPerception_IsStampedWithTheDecisionTickAndTheAgentsOwnCone()
    {
        var run = Run();
        var recording = Recorded(run);

        Assert.NotNull(recording.Header.AgentVision);
        Assert.Equal(
            new[] { SentryPatrolAgent.DefaultVision, InfiltratorAgent.DefaultVision },
            recording.Header.AgentVision);

        for (var step = 0; step < recording.Steps.Length; step++)
        {
            var perceptions = recording.Steps[step].Perceptions!;
            Assert.Equal(2, perceptions.Length);
            for (var agentId = 0; agentId < perceptions.Length; agentId++)
            {
                // The tick the decision was made on, not the tick the step
                // produced: an agent decides from the world as it was.
                Assert.Equal(recording.Steps[step].StepNumber, perceptions[agentId].Tick);
                Assert.Equal(agentId, perceptions[agentId].AgentId);
                Assert.Equal(recording.Header.AgentVision![agentId], perceptions[agentId].Vision);
            }
        }
    }

    [Fact]
    public void RecordedPerception_ReachesTheFileOnEveryStepLine()
    {
        var recording = Recorded(Run());
        var lines = Jsonl(recording).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var stepLines = lines.Where(line => line.Contains("\"Kind\":\"step\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(recording.Steps.Length, stepLines.Length);
        Assert.All(stepLines, line => Assert.Contains("\"Perceptions\":", line, StringComparison.Ordinal));
        Assert.Contains("\"AgentVision\":", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatRecordsNoPerceptions_LeavesTheFieldsOffTheWireEntirely()
    {
        // The default path: agents that carry no filter record none, and the
        // line is byte-for-byte what a build without the field produced.
        var recording = TrajectoryWriter.Record(
            TestMaps.TriangleWithResources(),
            new SimulationConfig(2, 20),
            7UL,
            new[] { new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) } },
            new StringWriter());

        var jsonl = Jsonl(recording);
        Assert.DoesNotContain("Perceptions", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentVision", jsonl, StringComparison.Ordinal);
        Assert.Null(recording.Header.AgentVision);
        Assert.All(recording.Steps, step => Assert.Null(step.Perceptions));
    }

    // -- verify recomputes it, and an edit to it is caught --------------------

    [Fact]
    public void UntamperedPerceptionRecording_VerifiesWithNoProblemsAndNoPerceptionNotice()
    {
        var report = TrajectoryReplay.VerifyDetailed(Recorded(Run()));

        Assert.Empty(report.Problems);
        // What this test is about is the perception half of the pass, so it
        // asserts the absence of the perception notice specifically rather than
        // of every notice. A recording built straight through the library has no
        // scenario descriptor, so the schema-5 digest notice is expected and is
        // asserted as the only notice present.
        Assert.DoesNotContain(TrajectoryReplay.NoPerceptionNotice, report.Notices);
        Assert.Equal([TrajectoryReplay.NoScenarioDigestNotice], report.Notices);
    }

    [Fact]
    public void ATamperedZoneStatus_IsCaughtByVerify()
    {
        var recording = Recorded(Run());
        // A room an agent could not see, claimed as one it could. Found in the
        // recording rather than at a fixed index, so the test edits fog that is
        // actually there: the episode is 20 ticks of two agents on a six-room
        // map, and a hand-picked tick can be a tick where everything is lit.
        var (stepIndex, agentId, zoneId) = FirstZone(recording, zone => zone.Status != KnowledgeStatus.Observed);
        var forged = WithPerception(
            recording,
            stepIndex,
            agentId,
            perception => perception with
            {
                Zones = perception.Zones
                    .Select((zone, i) => i == zoneId
                        ? zone with { Status = KnowledgeStatus.Observed }
                        : zone)
                    .ToArray(),
            });

        var problems = TrajectoryReplay.Verify(forged);
        Assert.Contains(
            problems,
            p => p.Contains($"perception diverges from replay for agent {agentId}", StringComparison.Ordinal));
    }

    [Fact]
    public void ATamperedLastSeenTick_IsCaughtByVerify()
    {
        var recording = Recorded(Run());
        // A stale room's last-seen tick is the tick the memory was written, so
        // moving it is a lie about when the agent last looked — and it is the
        // field the whole "last known" tier of the fog rests on.
        var (stepIndex, agentId, zoneId) = FirstZone(recording, zone => zone.Status == KnowledgeStatus.Stale);
        var forged = WithPerception(
            recording,
            stepIndex,
            agentId,
            perception => perception with
            {
                Zones = perception.Zones
                    .Select((zone, i) => i == zoneId
                        ? zone with { LastSeenTick = zone.LastSeenTick + 7 }
                        : zone)
                    .ToArray(),
            });

        var problems = TrajectoryReplay.Verify(forged);
        Assert.Contains(
            problems,
            p => p.Contains($"perception diverges from replay for agent {agentId}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first (step, agent, room) in the recording whose room matches
    /// <paramref name="predicate"/>, so a tamper test edits a field that is
    /// present in this episode instead of assuming a tick the engine may have
    /// moved.
    /// </summary>
    private static (int StepIndex, int AgentId, int ZoneId) FirstZone(
        TrajectoryRecording recording,
        Func<ZoneSight, bool> predicate)
    {
        for (var stepIndex = 0; stepIndex < recording.Steps.Length; stepIndex++)
        {
            var perceptions = recording.Steps[stepIndex].Perceptions!;
            for (var agentId = 0; agentId < perceptions.Length; agentId++)
            {
                for (var zoneId = 0; zoneId < perceptions[agentId].Zones.Length; zoneId++)
                {
                    if (predicate(perceptions[agentId].Zones[zoneId]))
                    {
                        return (stepIndex, agentId, zoneId);
                    }
                }
            }
        }

        throw new InvalidOperationException("The recorded episode has no room matching the tamper under test.");
    }

    [Fact]
    public void ATamperedRivalSighting_IsCaughtByVerify()
    {
        var recording = Recorded(Run());
        // The guard, as the infiltrator last remembered it: a sighting the
        // infiltrator never had, moved into a room it never saw the guard in.
        var (stepIndex, _, _) = FirstZone(recording, _ => true);
        var forged = WithPerception(
            recording,
            stepIndex,
            InfiltrationScenario.InfiltratorAgentId,
            perception => perception with
            {
                Agents = perception.Agents
                    .Select(agent => agent.AgentId != InfiltrationScenario.SentryAgentId || agent.LastKnownState is null
                        ? agent
                        : agent with
                        {
                            LastKnownState = agent.LastKnownState with
                            {
                                ZoneId = (agent.LastKnownState.ZoneId + 1) % perception.Zones.Length,
                            },
                        })
                    .ToArray(),
            });

        var problems = TrajectoryReplay.Verify(forged);
        Assert.Contains(problems, p => p.Contains("perception diverges from replay for agent 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ATamperedStepResult_StillFiresTheResultGateAlongsideThePerceptionGate()
    {
        var recording = Recorded(Run());
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 1
                    ? step with
                    {
                        Result = step.Result with
                        {
                            Info = step.Result.Info with { Reason = "hand-edited" },
                        },
                    }
                    : step)
                .ToArray(),
        };

        var problems = TrajectoryReplay.Verify(forged);
        Assert.Contains(problems, p => p.Contains("result diverges from replay", StringComparison.Ordinal));
    }

    [Fact]
    public void ATamperedStateHash_StillFiresTheStateHashGateOnAPerceptionRecording()
    {
        var recording = Recorded(Run());
        var forged = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 1
                    ? step with { StateHash = new string('0', 64) }
                    : step)
                .ToArray(),
        };

        var problems = TrajectoryReplay.Verify(forged);
        Assert.Contains(problems, p => p.Contains("state hash diverges from replay", StringComparison.Ordinal));
    }

    [Fact]
    public void StrippingThePerceptionsFromATamperedStepDoesNotMakeItPass()
    {
        // The downgrade the state-hash gate exists to close, closed for the fog
        // as well: removing the perceptions of one step is reported in its own
        // right rather than quietly reducing the check.
        var recording = Recorded(Run());
        var forged = WithPerception(
            recording,
            0,
            0,
            perception => perception with { Vision = perception.Vision + 1 });
        var stripped = forged with
        {
            Steps = forged.Steps
                .Select((step, i) => i == 0 ? step with { Perceptions = null } : step)
                .ToArray(),
        };

        var problems = TrajectoryReplay.Verify(stripped);
        Assert.Contains(problems, p => p.Contains("carry 'Perceptions'", StringComparison.Ordinal));
    }

    // -- the gate, in the shape the state hash gate has -----------------------

    [Fact]
    public void ASchema4RecordingWithNeitherPerceptionsNorVision_ReportsANotice()
    {
        var stripped = StripPerceptions(Recorded(Run()));

        var report = TrajectoryReplay.VerifyDetailed(stripped);

        Assert.Empty(report.Problems);
        Assert.Contains(TrajectoryReplay.NoPerceptionNotice, report.Notices);
    }

    [Fact]
    public void ASchema3RecordingWithNoPerceptions_IsNeitherNaggedNorFailing()
    {
        // What every pre-existing recording is: the field never existed, so
        // there is nothing to report and nothing to have failed.
        var recording = Recorded(Run());
        var asSchema3 = recording with
        {
            Header = recording.Header with
            {
                SchemaVersion = PreChangeSchemaVersion,
                AgentVision = null,
            },
            Steps = recording.Steps.Select(step => step with { Perceptions = null }).ToArray(),
        };

        var report = TrajectoryReplay.VerifyDetailed(asSchema3);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
    }

    [Fact]
    public void AHeaderThatDeclaresAVisionNoStepBacks_IsADiscrepancy()
    {
        var stripped = StripPerceptions(Recorded(Run()));
        var declared = stripped with
        {
            Header = stripped.Header with { AgentVision = new[] { 2, 2 } },
        };

        Assert.NotEmpty(TrajectoryReplay.Verify(declared));
    }

    [Fact]
    public void PerceptionsOnSomeStepsButNotOthers_IsADiscrepancy()
    {
        var recording = Recorded(Run());
        var partial = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 0 ? step with { Perceptions = null } : step)
                .ToArray(),
        };

        Assert.NotEmpty(TrajectoryReplay.Verify(partial));
    }

    [Fact]
    public void PerceptionsWithoutADeclaredVision_IsADiscrepancy()
    {
        var recording = Recorded(Run());
        var undeclared = recording with { Header = recording.Header with { AgentVision = null } };

        var problems = TrajectoryReplay.Verify(undeclared);
        Assert.Contains(problems, p => p.Contains("declares no 'AgentVision'", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredVisionThatDoesNotMatchTheRoster_IsADiscrepancy()
    {
        var recording = Recorded(Run());
        var wrong = recording with
        {
            Header = recording.Header with { AgentVision = new[] { 2, 2, 2 } },
        };

        var problems = TrajectoryReplay.Verify(wrong);
        Assert.Contains(problems, p => p.Contains("'AgentVision'", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredVisionOfZero_IsRejected()
    {
        var recording = Recorded(Run());
        var jsonl = Jsonl(recording).Replace("\"AgentVision\":[2,2]", "\"AgentVision\":[0,2]", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));
        Assert.Contains("vision", ex.Message, StringComparison.Ordinal);
    }

    // -- the reader refuses a fog that does not match its roster --------------

    [Fact]
    public void ReaderRejectsAPerceptionArrayThatDoesNotCoverTheRoster()
    {
        var jsonl = Jsonl(Recorded(Run()));
        // Drop the second agent's perception from the first step line, leaving
        // an array that is one entry short of the roster.
        var forged = RemoveSecondPerceptionOfFirstStep(jsonl);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(forged)));
        Assert.Contains("perception", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsAPerceptionInTheWrongAgentSlot()
    {
        var recording = Recorded(Run());
        var swapped = WithPerception(
            recording,
            0,
            0,
            perception => perception with { AgentId = 1 });

        var ex = Assert.Throws<InvalidDataException>(
            () => TrajectoryReader.Read(new StringReader(Jsonl(swapped))));
        Assert.Contains("slot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderRejectsAPerceptionArrayWithAHoleInIt()
    {
        var recording = Recorded(Run());
        var holed = recording with
        {
            Steps = recording.Steps
                .Select((step, i) => i == 0
                    ? step with
                    {
                        // A hole where a perception should be: the array is the
                        // right length and the second slot is null.
                        Perceptions = new[] { step.Perceptions![0], null! },
                    }
                    : step)
                .ToArray(),
        };

        var ex = Assert.Throws<InvalidDataException>(
            () => TrajectoryReader.Read(new StringReader(Jsonl(holed))));
        Assert.Contains("agent slot 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_PreservesEveryPerception()
    {
        var original = Recorded(Run());

        var readBack = TrajectoryReader.Read(new StringReader(Jsonl(original)));

        Assert.Equal(original.Header.AgentVision, readBack.Header.AgentVision);
        Assert.Equal(
            Jsonl(original),
            Jsonl(readBack).Replace("﻿", string.Empty, StringComparison.Ordinal));
    }

    // -- the episode itself is untouched -------------------------------------

    [Fact]
    public void StrippingTheNewFields_ReproducesThePreChangeRecordingByteForByte()
    {
        var run = Run();
        var recorded = Recorded(run);
        var stripped = recorded with
        {
            // The one field the schema bump is allowed to move: the wire stamp
            // that says which version wrote the file.
            Header = recorded.Header with
            {
                SchemaVersion = PreChangeSchemaVersion,
                AgentVision = null,
            },
            Steps = recorded.Steps.Select(step => step with { Perceptions = null }).ToArray(),
        };

        var jsonl = Jsonl(stripped);

        Assert.Equal(PreChangeEpisodeSha256, Sha256(jsonl));
    }

    [Fact]
    public void ThePerceptionsAreTheOnlyDifferenceFromThePreChangeRecording()
    {
        // The same claim stated structurally, so a failure says which part of
        // the episode moved: with the new fields removed and the version stamp
        // put back, the actions, results, state hashes and summary are
        // untouched.
        var recorded = Recorded(Run());
        var stripped = StripPerceptions(recorded);

        Assert.Equal(
            recorded.Steps.Select(step => step.Actions).ToArray(),
            stripped.Steps.Select(step => step.Actions).ToArray());
        Assert.Equal(
            recorded.Steps.Select(step => step.StateHash).ToArray(),
            stripped.Steps.Select(step => step.StateHash).ToArray());
        Assert.Equal(
            recorded.Steps.Select(step => JsonSerializer.Serialize(step.Result)).ToArray(),
            stripped.Steps.Select(step => JsonSerializer.Serialize(step.Result)).ToArray());
        Assert.Equal(recorded.Final, stripped.Final);
    }

    // -- the recordings that predate the field still verify -------------------

    [Fact]
    public void TheCommittedDemoRecording_StillVerifiesAndIsNotNagged()
    {
        using var reader = new StreamReader(SiteRecording("demo.jsonl"));
        var recording = TrajectoryReader.Read(reader);

        var report = TrajectoryReplay.VerifyDetailed(recording);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
        Assert.All(recording.Steps, step => Assert.Null(step.Perceptions));
        Assert.Null(recording.Header.AgentVision);
    }

    [Fact]
    public void TheGoldenFixture_StillVerifiesAndIsNotNagged()
    {
        using var reader = new StreamReader(
            Lattice.Tests.Fuzz.FixtureResolver.Fixture("golden_trajectory.jsonl"));
        var recording = TrajectoryReader.Read(reader);

        var report = TrajectoryReplay.VerifyDetailed(recording);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
        Assert.All(recording.Steps, step => Assert.Null(step.Perceptions));
    }

    [Fact]
    public void TheCommittedInfiltrationRecording_VerifiesItsRecordedPerceptions()
    {
        using var reader = new StreamReader(SiteRecording("infiltration.jsonl"));
        var recording = TrajectoryReader.Read(reader);

        var report = TrajectoryReplay.VerifyDetailed(recording);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
        Assert.NotNull(recording.Header.AgentVision);
        Assert.All(recording.Steps, step => Assert.NotNull(step.Perceptions));
    }

    private static TrajectoryRecording StripPerceptions(TrajectoryRecording recording) =>
        recording with
        {
            Header = recording.Header with { AgentVision = null },
            Steps = recording.Steps.Select(step => step with { Perceptions = null }).ToArray(),
        };

    private static string RemoveSecondPerceptionOfFirstStep(string jsonl)
    {
        var lines = jsonl.Split('\n');
        var first = JsonDocument.Parse(lines[1]).RootElement;
        var array = first.GetProperty("Perceptions").GetRawText();
        lines[1] = lines[1].Replace(array, $"[{first.GetProperty("Perceptions")[0].GetRawText()}]", StringComparison.Ordinal);
        return string.Join('\n', lines);
    }
}
