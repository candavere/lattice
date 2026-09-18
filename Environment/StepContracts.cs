namespace Lattice.Environment;

/// <summary>
/// The closed set of things an agent may ask to do in one tick. Kept as an
/// enum plus one positional record (rather than a class hierarchy) so the
/// contract stays pure data — serializable, comparable, and with no behavior
/// hooks, per the "step contracts are data" principle.
/// </summary>
public enum ActionKind
{
    /// <summary>Idle; the safe default for any invalid or missing action.</summary>
    Wait,

    /// <summary>Attempt to move to the zone given by <see cref="AgentAction.ZoneId"/>.</summary>
    Move,

    /// <summary>Attempt to collect the resource given by <see cref="AgentAction.ResourceId"/>.</summary>
    Collect,
}

/// <summary>
/// One agent's request for one tick. A single closed record covers every
/// <see cref="ActionKind"/>, with unused fields left at -1 so the JSON shape
/// is uniform across kinds. Named <c>AgentAction</c> (not <c>Action</c>) so
/// the contract type never collides with <see cref="System.Action"/> under
/// implicit usings.
/// </summary>
/// <param name="Kind">Which action is being requested.</param>
/// <param name="ZoneId">Move target zone; ignored for other kinds.</param>
/// <param name="ResourceId">Collect target resource; ignored for other kinds.</param>
public sealed record AgentAction(ActionKind Kind, int ZoneId = -1, int ResourceId = -1);

/// <summary>
/// Kinematic transit state: the agent is mid-edge between two zones and not
/// occupying any node. <see cref="RemainingTicks"/> is the number of further
/// ticks until arrival, counting the tick in which the agent will arrive —
/// so a value of 1 means "arrives at the end of this tick" and a value of 0
/// never appears on a <see cref="AgentState"/>, because an agent with zero
/// remaining ticks has already arrived (and thus has no transit).
/// </summary>
public sealed record InTransit(int FromZoneId, int ToZoneId, int RemainingTicks);

/// <summary>
/// The observable state of one agent: where it is and how many resources it
/// has collected. Score is used for winner determination at termination.
/// While the agent is mid-edge, <see cref="ZoneId"/> stays at the departure
/// node and <see cref="Transit"/> describes the crossing; an agent
/// with <c>Transit == null</c> is at a node and may act.
/// </summary>
public sealed record AgentState(int AgentId, int ZoneId, int Score, InTransit? Transit = null);

/// <summary>
/// Everything an agent can see this tick. Full observability (the whole map,
/// every agent, and every claimed resource id) keeps rule-based agents simple
/// and makes their decisions deterministic given the state, per the
/// "environment over agents" principle. Unclaimed resources are Map.Resources
/// minus Claims. <see cref="StepNumber"/> is the tick being decided (the same
/// value <see cref="StepResult.Info"/> carries): time-varying resolution and
/// dynamic topology both depend on it, so an agent that plans ahead must be
/// able to read it to stay aligned with the live simulation.
/// </summary>
public sealed record Observation(
    int AgentId,
    MapGraph Map,
    AgentState[] AgentStates,
    int[] Claims,
    int StepNumber = 0);

/// <summary>
/// Per-agent reward for one tick. +1 for each resource collected this tick,
/// otherwise 0 — kept a double for forward-compatibility with shaping later.
/// </summary>
public sealed record Reward(int AgentId, double Value);

/// <summary>
/// Step-level metadata about the simulation: tick number, whether the
/// simulation ended this tick, why, and (when terminal) the winner. This is
/// the only place win/loss conditions surface.
/// </summary>
public sealed record Info(int StepNumber, bool IsTerminal, string? Reason, int? WinnerAgentId);

/// <summary>
/// The full outcome of one tick: one <see cref="Observation"/> and one
/// <see cref="Reward"/> per agent, plus step <see cref="Info"/>. This is the
/// unit that trajectory logging writes one per line.
/// </summary>
public sealed record StepResult(Observation[] Observations, Reward[] Rewards, Info Info);