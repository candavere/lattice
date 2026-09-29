using System.Text.Json;
using System.Text.Json.Serialization;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Records full episodes to inspectable JSONL: a header line (seed + map +
/// sim config), one line per tick (actions + complete StepResult), and a
/// final metrics line. From schema 4 a step line may also carry the
/// decision-time <see cref="PartialObservation"/>s the agents acted on, with
/// the per-agent cones they were projected through declared in the header.
/// Each line is independently parseable JSON; simulation
/// output is byte-stable so re-recording or replaying yields identical lines.
/// Newlines are emitted as a bare <c>\n</c> on every platform, so two
/// recordings of the same episode are byte-identical once line endings are
/// normalized and the comparison is made on identical host environments; a
/// raw cross-host file-byte claim is not asserted here.
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
    /// <para>
    /// <paramref name="perceptions"/> is the decision-time fog, one array per
    /// turn in agent-slot order, supplied by the caller from the agents' own
    /// filters (<c>ScenarioResult.Perceptions</c>); this writer never projects
    /// anything itself, because a perception it rebuilt would be a second
    /// reading of the world rather than a record of the one the agents acted on.
    /// It does derive the header's per-agent <see cref="TrajectoryHeader.AgentVision"/>
    /// from those perceptions, so the declared cone cannot disagree with the
    /// recorded fog. Null records no perceptions at all, and then the file is
    /// byte-for-byte what a build without the field produced.
    /// </para>
    /// </summary>
    /// <param name="scenarioSha256">
    /// The SHA-256 digest, in lowercase hex, of the declarative scenario
    /// descriptor this episode was produced from, taken over that file's exact
    /// source bytes. It is provenance for the recording: it names the
    /// declaration, not the simulation state (which <see cref="SimulationStateHash"/>
    /// covers per tick, and which this digest is never compared to). Null writes
    /// no digest field at all, so a caller that has no descriptor produces the
    /// exact bytes it produced before the field existed.
    /// </param>
    public static TrajectoryRecording Record(
        MapGraph map,
        SimulationConfig simulationConfig,
        ulong seed,
        AgentAction[][] actions,
        TextWriter sink,
        string? scenario = null,
        string[]? agentRoles = null,
        DynamicMapRuleSet? rules = null,
        PartialObservation[][]? perceptions = null,
        string? scenarioSha256 = null)
    {
        var effectiveRules = rules ?? DynamicMapRuleSet.None;
        // A static-map episode writes no rules at all: the header field is
        // only present when a non-empty policy governs the recording.
        var serializedRules = effectiveRules == DynamicMapRuleSet.None ? null : effectiveRules;
        int[]? agentVision = PerceptionProjector.DeclaredVision(perceptions);
        if (perceptions is not null && perceptions.Length < actions.Length)
        {
            throw new ArgumentException(
                $"Supplied {perceptions.Length} perception step(s) for {actions.Length} turn(s); " +
                "every turn must carry the perceptions its agents decided from.",
                nameof(perceptions));
        }

        var state = Simulation.CreateInitial(map, simulationConfig, effectiveRules);

        sink.Write(Serialize(new HeaderLine(
            "header", seed, map, simulationConfig, serializedRules, TrajectorySchema.CurrentVersion,
            Scenario: scenario, AgentRoles: agentRoles, AgentVision: agentVision,
            ScenarioSha256: scenarioSha256)) + "\n");

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
            var stepPerceptions = perceptions?[steps.Count];
            var step = new TrajectoryStep(
                outcome.Result.Info.StepNumber, turn, outcome.Result, stateHash, stepPerceptions);
            steps.Add(step);
            sink.Write(Serialize(new StepLine(
                "step", step.StepNumber, turn, outcome.Result, stateHash, stepPerceptions)) + "\n");
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
                scenario, agentRoles, agentVision, scenarioSha256),
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
    /// <see cref="TrajectorySchema.CurrentVersion"/> there.
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
            AgentVision: recording.Header.AgentVision,
            // Carried through verbatim from the recording, exactly as the schema
            // version is: a rewrite must not add a digest a legacy file never
            // had, and must not drop one a schema-5 file carries. A read/write
            // round trip is byte-identical in both directions.
            ScenarioSha256: recording.Header.ScenarioSha256)) + "\n");

        foreach (var step in recording.Steps)
        {
            sink.Write(Serialize(new StepLine(
                "step", step.StepNumber, step.Actions, step.Result, step.StateHash, step.Perceptions)) + "\n");
        }

        sink.Write(Serialize(new FinalLine("final", recording.Final)) + "\n");
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
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? AgentVision = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScenarioSha256 = null)
    {
        public TrajectoryHeader ToModel() =>
            new(Seed, Map, SimulationConfig, DynamicRules, SchemaVersion, Scenario, AgentRoles, AgentVision, ScenarioSha256);
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