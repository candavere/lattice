using System.Text.Json.Serialization;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// The header line of a trajectory: everything needed to reconstruct the
/// episode. The seed and map are captured together so replay never needs the
/// generator again; the simulation config is required to rebuild the
/// environment exactly. <see cref="Scenario"/> and <see cref="AgentRoles"/>
/// are optional demonstration-layer metadata (e.g. the "infiltration" scenario
/// and its "Sentry"/"Infiltrator" roster) that viewer tooling reads to render
/// tactical roles; the replay core ignores them.
/// </summary>
public sealed record TrajectoryHeader(
    ulong Seed,
    MapGraph Map,
    SimulationConfig SimulationConfig,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? AgentRoles = null);

/// <summary>
/// One recorded tick: the exact <see cref="AgentAction"/>s submitted, the
/// tick number, and the full <see cref="StepResult"/> (observations, rewards,
/// terminal info). The result embeds its own observations, so nothing else is
/// needed to replay the tick.
/// </summary>
public sealed record TrajectoryStep(
    int StepNumber,
    AgentAction[] Actions,
    StepResult Result);

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