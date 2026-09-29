using System.Text.Json.Serialization;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// The current on-disk trajectory schema version. Version 5 records the
/// SHA-256 digest of the declarative scenario descriptor a recording was
/// produced from (<see cref="TrajectoryHeader.ScenarioSha256"/>), so a reader
/// holding a file can name the exact descriptor bytes behind it. Version 4
/// records what each agent actually perceived at the moment it decided
/// (<see cref="TrajectoryStep.Perceptions"/>, with the per-agent radii in
/// <see cref="TrajectoryHeader.AgentVision"/>), so the fog a viewer draws is
/// the agent's own decision-time view rather than something a reader
/// reconstructs from omniscient positions. Version 3 records a SHA-256 digest
/// of the simulation state at every step
/// (<see cref="TrajectoryStep.StateHash"/>), so verification can attest to the
/// state each tick produced and not only the step results. Version 2 recorded the
/// episode's dynamic topology policy (<see cref="TrajectoryHeader.DynamicRules"/>,
/// null for static maps) so a replay can recreate the exact choke-capacity
/// schedule the recording was made under. Files written before any of these
/// fields existed read back as schema version 0 — the static-map contract — and
/// are still accepted by <see cref="TrajectoryReader"/>; a recording with no
/// state hash anywhere verifies on step results alone and says so.
/// <para>
/// <b>Schema 0 through 4 remain readable, replayable, and verifiable.</b> The
/// scenario digest is a new optional header field, and like every other
/// optional field it is omitted when absent, so an older recording is not
/// rewritten or rejected by its absence — it simply has no digest, the same way
/// a pre-schema-3 recording has no state hash. Schema 5 is what
/// <see cref="TrajectoryWriter.Record"/> stamps on the recordings it mints
/// today; it does not retroactively relabel files already on disk, because
/// <see cref="TrajectoryWriter.Write"/> re-emits each recording's own header
/// version.
/// </para>
/// </summary>
public static class TrajectorySchema
{
    /// <summary>The version this library writes and can verify.</summary>
    public const int CurrentVersion = 5;

    /// <summary>
    /// The first schema version in which a per-step
    /// <see cref="TrajectoryStep.StateHash"/> is mandatory. A recording that
    /// declares this version or later but carries no state hash is internally
    /// inconsistent and fails verification; a recording from an earlier version
    /// legitimately has none and verifies with a notice instead.
    /// </summary>
    public const int StateHashRequiredVersion = 3;

    /// <summary>
    /// The first schema version in which a recording is expected to carry
    /// decision-time <see cref="TrajectoryStep.Perceptions"/>. A recording that
    /// declares this version or later but carries neither perceptions nor a
    /// <see cref="TrajectoryHeader.AgentVision"/> is checked with a notice
    /// rather than passing silently, because the viewer would otherwise draw a
    /// reconstruction and call it the record. This is a notice and not a
    /// discrepancy: the perception fields are optional on the wire (an agent
    /// that carries no filter has none to record), so a schema-4 episode
    /// without them is a legitimate recording of a fog-free roster.
    /// </summary>
    public const int DecisionTimePerceptionVersion = 4;
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
/// roles; the replay core ignores them. <see cref="AgentVision"/> declares the
/// perception cone, in graph hops, each agent's own filter was built with, so a
/// reader can rebuild those filters to check the recorded
/// <see cref="TrajectoryStep.Perceptions"/>; it is null for a recording with no
/// decision-time perceptions, which is every recording made before schema 4.
/// <para>
/// <see cref="ScenarioSha256"/> (schema 5) is the SHA-256 digest, in lowercase
/// hex, of the declarative scenario descriptor the episode was produced from,
/// taken over that file's exact source bytes. Every recording written today
/// carries one: a file-loaded run carries the digest of the file it was given,
/// and a built-in named invocation carries the digest of the committed
/// descriptor that built-in resolves through. It is null on any recording made
/// before schema 5, which is a legitimate recording that predates the field
/// rather than a defective one.
/// </para>
/// <para>
/// This digest is <b>not</b> a <see cref="SimulationStateHash"/>, and the two
/// are never compared. The scenario digest names the <em>declaration</em> — the
/// file bytes that said what the environment is — while the per-tick state hash
/// names the <em>world</em> at one tick. Conflating them would let a reader
/// take a matching descriptor digest as an attestation about a matching
/// simulation state, which it is not.
/// </para>
/// </summary>
public sealed record TrajectoryHeader(
    ulong Seed,
    MapGraph Map,
    SimulationConfig SimulationConfig,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DynamicMapRuleSet? DynamicRules = null,
    int SchemaVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? AgentRoles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? AgentVision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScenarioSha256 = null);

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
/// <para>
/// <see cref="Perceptions"/> (schema 4) is the decision-time counterpart of that
/// result: one <see cref="PartialObservation"/> per agent slot, in slot order,
/// carrying the masked view each agent's own filter produced for this tick's
/// decision. It is the object the agents acted on, recorded rather than
/// re-derived, so a viewer can draw the fog the agents actually faced and
/// verification can recompute it and compare. Null for a recording made before
/// schema 4, or for one whose agents carry no perception filter — the field is
/// omitted from the line entirely when absent.
/// </para>
/// </summary>
public sealed record TrajectoryStep(
    int StepNumber,
    AgentAction[] Actions,
    StepResult Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StateHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartialObservation[]? Perceptions = null);

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