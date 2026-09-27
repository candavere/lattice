using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Deterministic helpers for the optional decision-time fog side-channel:
/// the <see cref="PartialObservation"/> each agent projected <em>before</em>
/// a step's actions were applied. Tick alignment matches the agents'
/// <c>PerceptionFilter.Project(++tick, observation)</c> convention
/// (<see cref="TrajectoryStep.StepNumber"/> is the 1-based decision tick).
/// </summary>
public static class DecisionPerceptionRecording
{
    /// <summary>
    /// Creates one <see cref="PerceptionFilter"/> per agent slot using
    /// <paramref name="agentVision"/>[agentId]. Filters start with empty
    /// memory and must be fed turns in ascending step order.
    /// </summary>
    public static PerceptionFilter[] CreateFilters(MapGraph map, int[] agentVision)
    {
        if (agentVision is null || agentVision.Length == 0)
        {
            throw new ArgumentException("AgentVision must contain one horizon per agent.", nameof(agentVision));
        }

        var filters = new PerceptionFilter[agentVision.Length];
        for (var agentId = 0; agentId < agentVision.Length; agentId++)
        {
            filters[agentId] = new PerceptionFilter(map, agentId, agentVision[agentId]);
        }

        return filters;
    }

    /// <summary>
    /// Projects the pre-step omniscient observations for decision tick
    /// <paramref name="decisionTick"/> (1-based, equal to the forthcoming
    /// step number). Agents are projected in ascending id order.
    /// </summary>
    public static PartialObservation[] ProjectTurn(
        PerceptionFilter[] filters,
        IReadOnlyDictionary<int, Observation> observations,
        int decisionTick)
    {
        if (decisionTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionTick), decisionTick, "Decision ticks are 1-based.");
        }

        var perceptions = new PartialObservation[filters.Length];
        for (var agentId = 0; agentId < filters.Length; agentId++)
        {
            if (!observations.TryGetValue(agentId, out var observation))
            {
                throw new ArgumentException($"Missing observation for agent {agentId}.", nameof(observations));
            }

            perceptions[agentId] = filters[agentId].Project(decisionTick, observation);
        }

        return perceptions;
    }

    /// <summary>
    /// Replays decision-time perceptions from a recording's header map,
    /// <see cref="TrajectoryHeader.AgentVision"/>, and recorded actions —
    /// projecting on the pre-step state before each action turn, never on the
    /// post-step <see cref="StepResult"/>.
    /// </summary>
    public static PartialObservation[][] ReplayFromActions(TrajectoryRecording recording)
    {
        var vision = recording.Header.AgentVision
            ?? throw new ArgumentException("Recording has no AgentVision; cannot replay decision perceptions.");

        if (vision.Length != recording.Header.SimulationConfig.AgentCount)
        {
            throw new ArgumentException(
                $"AgentVision length ({vision.Length}) must equal AgentCount ({recording.Header.SimulationConfig.AgentCount}).");
        }

        var filters = CreateFilters(recording.Header.Map, vision);
        var state = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);

        var projected = new List<PartialObservation[]>(recording.Steps.Length);
        foreach (var step in recording.Steps)
        {
            var observations = BuildObservations(state);
            projected.Add(ProjectTurn(filters, observations, step.StepNumber));
            var outcome = Simulation.Step(state, step.Actions, recording.Header.SimulationConfig);
            state = outcome.NextState;
            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return projected.ToArray();
    }

    private static Dictionary<int, Observation> BuildObservations(SimulationState state) =>
        state.Agents.ToDictionary(
            a => a.AgentId,
            a => new Observation(a.AgentId, state.Map, state.Agents, state.Claims, state.StepCount));
}
