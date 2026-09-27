using System.Text.Json;
using System.Text.Json.Serialization;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Records full episodes to inspectable JSONL: a header line (seed + map +
/// sim config), one line per tick (actions + complete StepResult), and a
/// final metrics line. Each line is independently parseable JSON; simulation
/// output is byte-stable so re-recording or replaying yields identical lines.
/// Newlines are emitted as a bare <c>\n</c> on every platform, so two
/// recordings of the same episode are byte-identical once line endings are
/// normalized and the comparison is made on identical host environments; a
/// raw cross-host file-byte claim is not asserted here.
/// Optional decision-time <see cref="PartialObservation"/> fog
/// (<see cref="TrajectoryHeader.AgentVision"/> + per-step
/// <see cref="TrajectoryStep.Perceptions"/>) is preserved on write and
/// round-trip; legacy recordings omit those fields. Schema 3 also records a
/// per-step <see cref="TrajectoryStep.StateHash"/>.
/// </summary>
public static class TrajectoryWriter
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Plays <paramref name="actions"/> as one episode on
    /// <paramref name="map"/> and writes the trajectory to
    /// <paramref name="sink"/>, returning the same recording in memory.
    /// Every submitted action is checked against
    /// <see cref="ActionSpace"/> first so a malformed episode never lands on
    /// disk; stepping stops at the first terminal tick (extra turns are
    /// ignored, mirroring <see cref="SimulationDriver"/>).
    /// When <paramref name="decisionPerceptions"/> is supplied it must align
    /// 1:1 with the recorded turns (decision-time projections, not post-step
    /// views); <paramref name="agentVision"/> is written on the header so
    /// replay can re-project and verify that side-channel.
    /// </summary>
    public static TrajectoryRecording Record(
        MapGraph map,
        SimulationConfig simulationConfig,
        ulong seed,
        AgentAction[][] actions,
        TextWriter sink,
        string? scenario = null,
        string[]? agentRoles = null,
        DynamicMapRuleSet? rules = null,
        PartialObservation[][]? decisionPerceptions = null,
        int[]? agentVision = null)
    {
        ValidatePerceptionSideChannel(simulationConfig, actions, decisionPerceptions, agentVision);

        var effectiveRules = rules ?? DynamicMapRuleSet.None;
        // A static-map episode writes no rules at all: the header field is
        // only present when a non-empty policy governs the recording.
        var serializedRules = effectiveRules == DynamicMapRuleSet.None ? null : effectiveRules;
        var state = Simulation.CreateInitial(map, simulationConfig, effectiveRules);

        sink.Write(Serialize(new HeaderLine(
            "header", seed, map, simulationConfig, serializedRules, TrajectorySchema.CurrentVersion,
            Scenario: scenario, AgentRoles: agentRoles, AgentVision: agentVision)) + "\n");

        var steps = new List<TrajectoryStep>();
        Info? lastInfo = null;
        foreach (var turn in actions)
        {
            ValidateTurn(map, steps.Count + 1, turn);

            var outcome = Simulation.Step(state, turn, simulationConfig);
            // The digest covers the POST-step state: it is the world the step's
            // own Observation describes, so the last step's hash pins the final
            // state the summary line aggregates come from, and hash[N] is the
            // pre-state of step N+1 — one field chain-pins the whole episode.
            var stateHash = SimulationStateHash.Compute(outcome.NextState, seed);
            var perceptions = decisionPerceptions?[steps.Count];
            var step = new TrajectoryStep(
                outcome.Result.Info.StepNumber, turn, outcome.Result, stateHash, perceptions);
            steps.Add(step);
            sink.Write(Serialize(new StepLine(
                "step", step.StepNumber, turn, outcome.Result, stateHash, perceptions)) + "\n");
            state = outcome.NextState;
            lastInfo = outcome.Result.Info;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        var final = BuildFinal(state, lastInfo);
        sink.Write(Serialize(new FinalLine("final", final)) + "\n");

        return new TrajectoryRecording(
            new TrajectoryHeader(
                seed, map, simulationConfig, serializedRules, TrajectorySchema.CurrentVersion,
                scenario, agentRoles, agentVision),
            steps.ToArray(),
            final);
    }

    /// <summary>
    /// Writes an already-built <paramref name="recording"/> to
    /// <paramref name="sink"/> using the same line format as
    /// <see cref="Record"/>. Byte-identical to the original recording's own
    /// output, which is how a read-back is verified. The header is stamped
    /// with the recording's OWN <see cref="TrajectoryHeader.SchemaVersion"/>,
    /// never <see cref="TrajectorySchema.CurrentVersion"/>: rewriting a
    /// hash-less schema-2 recording must not silently relabel it as schema 3
    /// with zero state hashes present. <see cref="Record"/> is the only place
    /// that mints a new-schema recording, and it stamps
    /// <see cref="TrajectorySchema.CurrentVersion"/> there. Preserves optional
    /// <see cref="TrajectoryHeader.AgentVision"/> and per-step
    /// <see cref="TrajectoryStep.Perceptions"/> when present.
    /// </summary>
    public static void Write(
        TrajectoryRecording recording,
        TextWriter sink,
        string? scenario = null,
        string[]? agentRoles = null)
    {
        sink.Write(Serialize(new HeaderLine(
            "header",
            recording.Header.Seed,
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules,
            recording.Header.SchemaVersion,
            Scenario: scenario ?? recording.Header.Scenario,
            AgentRoles: agentRoles ?? recording.Header.AgentRoles,
            AgentVision: recording.Header.AgentVision)) + "\n");

        foreach (var step in recording.Steps)
        {
            sink.Write(Serialize(new StepLine(
                "step", step.StepNumber, step.Actions, step.Result, step.StateHash, step.Perceptions)) + "\n");
        }

        sink.Write(Serialize(new FinalLine("final", recording.Final)) + "\n");
    }

    private static void ValidatePerceptionSideChannel(
        SimulationConfig simulationConfig,
        AgentAction[][] actions,
        PartialObservation[][]? decisionPerceptions,
        int[]? agentVision)
    {
        if (decisionPerceptions is null && agentVision is null)
        {
            return;
        }

        if (decisionPerceptions is null || agentVision is null)
        {
            throw new ArgumentException(
                "DecisionPerceptions and AgentVision must be supplied together (or both omitted).");
        }

        DecisionPerceptionRecording.ValidateAgentVision(simulationConfig.AgentCount, agentVision);

        if (decisionPerceptions.Length != actions.Length)
        {
            throw new ArgumentException(
                $"DecisionPerceptions length ({decisionPerceptions.Length}) must equal actions length ({actions.Length}).",
                nameof(decisionPerceptions));
        }

        for (var i = 0; i < decisionPerceptions.Length; i++)
        {
            var turnPerceptions = decisionPerceptions[i]
                ?? throw new ArgumentException($"DecisionPerceptions[{i}] is null.", nameof(decisionPerceptions));
            if (turnPerceptions.Length != simulationConfig.AgentCount)
            {
                throw new ArgumentException(
                    $"DecisionPerceptions[{i}] length ({turnPerceptions.Length}) must equal AgentCount ({simulationConfig.AgentCount}).",
                    nameof(decisionPerceptions));
            }
        }
    }

    private static void ValidateTurn(MapGraph map, int stepNumber, AgentAction[] turn)
    {
        for (var i = 0; i < turn.Length; i++)
        {
            var problems = ActionSpace.Validate(turn[i], map);
            if (problems.Count > 0)
            {
                var detail = string.Join(" ", problems);
                throw new InvalidOperationException(
                    $"Agent {i}'s action at step {stepNumber} is outside the action space: {detail}");
            }
        }
    }

    /// <summary>
    /// Builds the final summary line from the terminal (or last recorded)
    /// state: why the episode ended, who won, the tick count, per-agent
    /// scores, claimed resources, and total resources. Shared with
    /// <see cref="TrajectoryReplay"/> so verification recomputes the final
    /// line through the exact builder the writer used.
    /// </summary>
    internal static TrajectoryFinal BuildFinal(SimulationState state, Info? info)
    {
        return new TrajectoryFinal(
            Reason: info?.Reason,
            WinnerAgentId: info?.WinnerAgentId,
            TotalSteps: state.StepCount,
            FinalScores: state.Agents.Select(a => a.Score).ToArray(),
            ResourcesClaimed: state.Claims.Length,
            TotalResources: state.Map.Resources.Length);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal sealed record HeaderLine(
        string Kind,
        ulong Seed,
        MapGraph Map,
        SimulationConfig SimulationConfig,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DynamicMapRuleSet? DynamicRules = null,
        int SchemaVersion = 0,
        string? Scenario = null,
        string[]? AgentRoles = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? AgentVision = null)
    {
        public TrajectoryHeader ToModel() =>
            new(Seed, Map, SimulationConfig, DynamicRules, SchemaVersion, Scenario, AgentRoles, AgentVision);
    }

    internal sealed record StepLine(
        string Kind,
        int StepNumber,
        AgentAction[] Actions,
        StepResult Result,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StateHash = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartialObservation[]? Perceptions = null)
    {
        public TrajectoryStep ToModel() => new(StepNumber, Actions, Result, StateHash, Perceptions);
    }

    internal sealed record FinalLine(string Kind, TrajectoryFinal Metrics);
}
