using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// Decision-time fog side-channel: Perceptions are what agents projected
/// before each step, Verify checks them, Write preserves them, and legacy
/// recordings without the side-channel still verify.
/// </summary>
public class DecisionPerceptionTests
{
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void InfiltrationRecording_CarriesDecisionTimePerceptions_AndVerifies()
    {
        var run = InfiltrationScenario.Run(seed: 42, maxSteps: 100);
        Assert.NotNull(run.Base.DecisionPerceptions);
        Assert.Equal(run.Base.Turns.Length, run.Base.DecisionPerceptions!.Length);

        var buffer = new StringWriter();
        var recording = TrajectoryWriter.Record(
            run.Map,
            run.Config,
            run.Seed,
            run.Base.Turns,
            buffer,
            scenario: InfiltrationScenario.ScenarioName,
            agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
            decisionPerceptions: run.Base.DecisionPerceptions,
            agentVision: run.AgentVision);

        Assert.Equal(run.AgentVision, recording.Header.AgentVision);
        Assert.All(recording.Steps, step => Assert.NotNull(step.Perceptions));
        Assert.Empty(TrajectoryReplay.Verify(recording));

        var jsonl = buffer.ToString();
        Assert.Contains("\"AgentVision\"", jsonl, StringComparison.Ordinal);
        Assert.Contains("\"Perceptions\"", jsonl, StringComparison.Ordinal);

        var readBack = TrajectoryReader.Read(new StringReader(jsonl));
        Assert.Empty(TrajectoryReplay.Verify(readBack));
        Assert.Equal(JsonSerializer.Serialize(recording.Header.AgentVision, Json),
            JsonSerializer.Serialize(readBack.Header.AgentVision, Json));
    }

    [Fact]
    public void Write_PreservesPerceptions_OnRoundTrip()
    {
        var run = InfiltrationScenario.Run(seed: 42, maxSteps: 100);
        var original = TrajectoryWriter.Record(
            run.Map,
            run.Config,
            run.Seed,
            run.Base.Turns,
            new StringWriter(),
            scenario: InfiltrationScenario.ScenarioName,
            agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
            decisionPerceptions: run.Base.DecisionPerceptions,
            agentVision: run.AgentVision);

        var rewritten = new StringWriter();
        TrajectoryWriter.Write(original, rewritten);
        var roundTripped = TrajectoryReader.Read(new StringReader(rewritten.ToString()));

        Assert.Equal(
            JsonSerializer.Serialize(original.Header.AgentVision, Json),
            JsonSerializer.Serialize(roundTripped.Header.AgentVision, Json));
        Assert.Equal(original.Steps.Length, roundTripped.Steps.Length);
        for (var i = 0; i < original.Steps.Length; i++)
        {
            Assert.Equal(
                JsonSerializer.Serialize(original.Steps[i].Perceptions, Json),
                JsonSerializer.Serialize(roundTripped.Steps[i].Perceptions, Json));
        }

        Assert.Empty(TrajectoryReplay.Verify(roundTripped));
    }

    [Fact]
    public void Verify_Fails_WhenRecordedPerceptionIsTampered()
    {
        var run = InfiltrationScenario.Run(seed: 42, maxSteps: 100);
        var recording = TrajectoryWriter.Record(
            run.Map,
            run.Config,
            run.Seed,
            run.Base.Turns,
            new StringWriter(),
            scenario: InfiltrationScenario.ScenarioName,
            agentRoles: new[] { InfiltrationScenario.SentryRole, InfiltrationScenario.InfiltratorRole },
            decisionPerceptions: run.Base.DecisionPerceptions,
            agentVision: run.AgentVision);

        Assert.Empty(TrajectoryReplay.Verify(recording));

        var first = recording.Steps[0];
        Assert.NotNull(first.Perceptions);
        var originalZones = first.Perceptions![0].Zones;
        var tamperedZones = originalZones.ToArray();
        // Flip the first non-Observed zone status (or force Unknown→Observed) so
        // serialized Perceptions diverge while StepResult stays intact.
        var flipIndex = Array.FindIndex(tamperedZones, z => z.Status != KnowledgeStatus.Observed);
        if (flipIndex < 0)
        {
            flipIndex = 0;
        }

        var victim = tamperedZones[flipIndex];
        tamperedZones[flipIndex] = victim with
        {
            Status = victim.Status == KnowledgeStatus.Unknown
                ? KnowledgeStatus.Observed
                : KnowledgeStatus.Unknown,
            LastKnownPosition = victim.Status == KnowledgeStatus.Unknown ? new GridPoint(0, 0) : null,
            LastSeenTick = victim.Status == KnowledgeStatus.Unknown ? 1 : -1,
            ObservedNeighbors = victim.Status == KnowledgeStatus.Unknown ? new[] { 0 } : Array.Empty<int>(),
        };

        var tamperedPerceptions = first.Perceptions.ToArray();
        tamperedPerceptions[0] = first.Perceptions[0] with { Zones = tamperedZones };
        var tamperedSteps = recording.Steps.ToArray();
        tamperedSteps[0] = first with { Perceptions = tamperedPerceptions };
        var tampered = recording with { Steps = tamperedSteps };

        var problems = TrajectoryReplay.Verify(tampered);
        Assert.Contains(problems, p => p.Contains("perceptions diverge", StringComparison.OrdinalIgnoreCase));
        // StepResult gate must still be clean — only fog was tampered.
        Assert.DoesNotContain(problems, p => p.Contains("result diverges", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verify_LegacyRecordingWithoutPerceptions_StillPasses()
    {
        var golden = File.ReadAllText(Lattice.Tests.Fuzz.FixtureResolver.Fixture("golden_trajectory.jsonl"));
        var recording = TrajectoryReader.Read(new StringReader(golden));
        Assert.Null(recording.Header.AgentVision);
        Assert.All(recording.Steps, step => Assert.Null(step.Perceptions));
        Assert.Empty(TrajectoryReplay.Verify(recording));
    }

    [Fact]
    public void DecisionPerceptions_MatchAgentFilterProjection_OnPreStepObservation()
    {
        var run = InfiltrationScenario.Run(seed: 42, maxSteps: 30);
        Assert.NotNull(run.Base.DecisionPerceptions);

        // Independent filters fed the same pre-step observation stream must
        // reproduce the recorded decision-time perceptions exactly.
        var filters = DecisionPerceptionRecording.CreateFilters(run.Map, run.AgentVision);
        var state = Simulation.CreateInitial(run.Map, run.Config);
        for (var i = 0; i < run.Base.Turns.Length; i++)
        {
            var observations = state.Agents.ToDictionary(
                a => a.AgentId,
                a => new Observation(a.AgentId, state.Map, state.Agents, state.Claims, state.StepCount));
            var projected = DecisionPerceptionRecording.ProjectTurn(filters, observations, i + 1);
            Assert.Equal(
                JsonSerializer.Serialize(run.Base.DecisionPerceptions![i], Json),
                JsonSerializer.Serialize(projected, Json));
            state = Simulation.Step(state, run.Base.Turns[i], run.Config).NextState;
        }
    }
}
