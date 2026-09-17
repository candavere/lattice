using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A deterministic, stateless agent that always reaches for the closest
/// unclaimed resource. "Closest" is topological hop count (BFS over
/// <see cref="ChokePoint"/> edges, expanding in ascending zone-id order so
/// ties resolve predictably); when standing on a zone containing at least
/// one unclaimed resource, it immediately claims the lowest-id one via
/// Collect. Every emitted action is in the action space by construction,
/// and no observation data is mutated during the decision.
/// </summary>
public sealed class GreedyCollectorAgent : IAgent
{
    /// <summary>Creates the agent pinned to the given agent slot.</summary>
    public GreedyCollectorAgent(int agentId)
    {
        AgentId = agentId;
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        var myZone = ObservationView.MyZone(observation);
        var unclaimed = ObservationView.Unclaimed(observation).ToList();

        if (unclaimed.Count == 0)
        {
            return new AgentAction(ActionKind.Wait);
        }

        ResourceNode? bestResource = null;
        var bestDistance = int.MaxValue;
        var bestHop = -1;

        foreach (var resource in unclaimed)
        {
            if (resource.ZoneId == myZone)
            {
                return new AgentAction(ActionKind.Collect, ResourceId: resource.Id);
            }

            var path = ShortestPath(observation.Map, myZone, resource.ZoneId);
            if (path is null || path.Length < 2)
            {
                continue;
            }

            var distance = path.Length - 1;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestResource = resource;
                bestHop = path[1];
            }
        }

        if (bestResource is null)
        {
            return new AgentAction(ActionKind.Wait);
        }

        return new AgentAction(ActionKind.Move, bestHop);
    }

    private static int[]? ShortestPath(MapGraph map, int from, int to)
    {
        if (from == to)
        {
            return new[] { from };
        }

        var visited = new HashSet<int> { from };
        var parent = new Dictionary<int, int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(from);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var neighbor in Neighbors(map, current))
            {
                if (!visited.Add(neighbor))
                {
                    continue;
                }

                parent[neighbor] = current;
                if (neighbor == to)
                {
                    var path = new List<int>();
                    var cursor = to;
                    while (cursor != from)
                    {
                        path.Add(cursor);
                        cursor = parent[cursor];
                    }

                    path.Add(from);
                    path.Reverse();
                    return path.ToArray();
                }

                frontier.Enqueue(neighbor);
            }
        }

        return null;
    }

    private static IEnumerable<int> Neighbors(MapGraph map, int zone)
    {
        var result = new List<int>();
        foreach (var choke in map.ChokePoints)
        {
            if (choke.FromZoneId == zone)
            {
                result.Add(choke.ToZoneId);
            }
            else if (choke.ToZoneId == zone)
            {
                result.Add(choke.FromZoneId);
            }
        }

        return result.OrderBy(x => x);
    }
}