using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Reads an <see cref="Observation"/> without mutating it: resolves the
/// observer's own zone and the unclaimed resources. Shared by the rule-based
/// agents so there is one place that turns the full-observability
/// Observation into "where am I, what is available".
/// </summary>
internal static class ObservationView
{
    /// <summary>
    /// The zone the observing agent currently occupies, found by matching on
    /// <see cref="AgentState.AgentId"/>. Throws if the observation omitted
    /// the agent — a corrupt or inconsistent Observation should fail loudly.
    /// </summary>
    public static int MyZone(Observation observation)
    {
        foreach (var agent in observation.AgentStates)
        {
            if (agent.AgentId == observation.AgentId)
            {
                return agent.ZoneId;
            }
        }

        throw new InvalidOperationException($"Observation omits agent {observation.AgentId}.");
    }

    /// <summary>
    /// All resource nodes no agent has claimed yet, ascending by id so
    /// iteration order is deterministic regardless of map layout.
    /// </summary>
    public static IEnumerable<ResourceNode> Unclaimed(Observation observation) =>
        observation.Map.Resources
            .Where(resource => !observation.Claims.Contains(resource.Id))
            .OrderBy(resource => resource.Id);
}