using System.Text.Json.Serialization;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// The current on-disk trajectory schema version. Version 3 records a
/// SHA-256 digest of the simulation state at every step
/// (<see cref="TrajectoryStep.StateHash"/>), so verification can attest to the
/// state each tick produced and not only the step results. Version 2 recorded the
/// episode's dynamic topology policy (<see cref="TrajectoryHeader.DynamicRules"/>,
/// null for static maps) so a replay can recreate the exact choke-capacity
/// schedule the recording was made under. Files written before either field
/// existed read back as schema version 0 — the static-map contract — and are
/// still accepted by <see cref="TrajectoryReader"/>; a recording with no state
/// hash anywhere verifies on step results alone and says so.
/// </summary>
public static class TrajectorySchema
{
    /// <summary>The version this library writes and can verify.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// The first schema version in which a per-step
    /// <see cref="TrajectoryStep.StateHash"/> is mandatory. A recording that
    /// declares this version or later but carries no state hash is internally
    /// inconsistent and fails verification; a recording from an earlier version
    /// legitimately has none and verifies with a notice instead.
    /// </summary>
    public const int StateHashRequiredVersion = 3;
}

/// <summary>
/// The header line of a trajectory: everything needed to reconstruct the
/// episode. The seed and map are captured together so replay never needs the
/// generator again; the simulation config is required to rebuild the
/// environment exactly. <see cref="DynamicRules"/> preserves the episode's
/// dynamic topology policy (timed portcullises, event locks) — null or empty
/// for static maps — so recorded steps replay against the same choke-capacity
/// schedule. <see cref="SchemaVersion"/> stamps the wire format for the
/// reader. <see cref="Scenario"/> and <see cref="AgentRoles"/> are optional
/// demonstration-layer metadata (e.g. the "infiltration" scenario and its
/// "Sentry"/"Infiltrator" roster) that viewer tooling reads to render tactical
/// roles; the replay core ignores them.
/// </summary>
public sealed record TrajectoryHeader(
    ulong Seed,
    MapGraph Map,
    SimulationConfig SimulationConfig,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DynamicMapRuleSet? DynamicRules = null,
    int SchemaVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? AgentRoles = null);

/// <summary>
/// One recorded tick: the exact <see cref="AgentAction"/>s submitted, the
/// tick number, the full <see cref="StepResult"/> (observations, rewards,
/// terminal info), and the <see cref="SimulationStateHash"/> digest of the world
/// as it stands at the <i>end</i> of that tick. The result embeds its own
/// observations, so nothing else is needed to replay the tick; the hash adds the
/// one piece of persistent state an observation does not carry — the
/// per-tick dynamic choke-capacity snapshot — and is what lets verification
/// attest to the state each tick produced. Null for a recording made before
/// schema 3, which still verifies on step results alone and says so; a
/// schema-3-or-later recording with no digest anywhere is a corrupt file and is
/// reported as a discrepancy.
/// </summary>
public sealed record TrajectoryStep(
    int StepNumber,
    AgentAction[] Actions,
    StepResult Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StateHash = null);

/// <summary>
/// Terminal bookkeeping: why the episode ended, who won, and final aggregate
/// metrics. A non-terminal episode (action list exhausted) has null reason and
/// winner; the metrics describe the last recorded state.
/// </summary>
public sealed record TrajectoryFinal(
    string? Reason,
    int? WinnerAgentId,
    int TotalSteps,
    int[] FinalScores,
    int ResourcesClaimed,
    int TotalResources);

/// <summary>
/// An in-memory trajectory: header plus ordered steps plus final metrics.
/// This is the read/write neutral format — the JSONL on disk and the
/// in-memory model are the same data.
/// </summary>
public sealed record TrajectoryRecording(
    TrajectoryHeader Header,
    TrajectoryStep[] Steps,
    TrajectoryFinal Final);