using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A deterministic rule-based agent that always reaches for the closest
/// unclaimed resource. "Closest" is topological hop count (BFS over
/// <see cref="ChokePoint"/> edges, expanding in ascending zone-id order so
/// ties resolve predictably); when standing on a zone containing at least
/// one unclaimed resource, it immediately claims the lowest-id one via
/// Collect. Every emitted action is in the action space by construction,
/// and no observation data is mutated during the decision.
///
/// Stall recovery: a Move the agent issued that is denied three ticks in a
/// row (the environment keeps the agent at its zone — a saturated choke or
/// zone, or a contested edge) counts as a blocked tick. On the third
/// consecutive blocked tick the agent clears the contested path target and
/// routes around the blocked first hop instead of hammering it; when every
/// alternative route is equally blocked it drops the latch and re-probes the
/// contested edge, so the choke is retried as soon as congestion clears
/// rather than abandoned forever.
///
/// Decisions are a deterministic function of the observation *stream*: the
/// blocked-tick counter is derived only from the previous observations and
/// this one, so identical streams still yield identical decisions. An agent
/// instance is therefore bound to one episode at a time. Pass
/// <paramref name="stallRecovery"/> = false to keep the strictly stateless
/// nearest-resource behavior (used by MCTS rollouts, where every rollout must
/// be an independent continuation).
/// </summary>
public sealed class GreedyCollectorAgent : IAgent
{
    /// <summary>How many consecutive denied Move attempts trigger recovery.</summary>
    public const int StallRecoveryThreshold = 3;

    private readonly bool _stallRecovery;
    private bool _hasHistory;
    private int _lastZone;
    private int? _lastMoveTarget;
    private int _blockedStreak;
    private int? _avoidedFirstHop;

    /// <summary>
    /// Creates the agent pinned to the given agent slot, with stall recovery
    /// enabled (pass <paramref name="stallRecovery"/> = false for callers that
    /// need a strictly stateless policy).
    /// </summary>
    public GreedyCollectorAgent(int agentId, bool stallRecovery = true)
    {
        AgentId = agentId;
        _stallRecovery = stallRecovery;
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

        if (_stallRecovery)
        {
            TrackStall(myZone);
        }

        var choice = ChooseNearest(observation.Map, unclaimed, myZone, _avoidedFirstHop);
        if (choice.Resource is null && _stallRecovery && _avoidedFirstHop is not null)
        {
            // Every resource is behind the contested first hop: drop the
            // latch and retry the shortest route so the choke is re-probed
            // once congestion clears, starting a fresh stall window.
            _avoidedFirstHop = null;
            _blockedStreak = 0;
            choice = ChooseNearest(observation.Map, unclaimed, myZone, null);
        }

        if (choice.Resource is null)
        {
            return new AgentAction(ActionKind.Wait);
        }

        if (choice.Resource.ZoneId == myZone)
        {
            return new AgentAction(ActionKind.Collect, ResourceId: choice.Resource.Id);
        }

        if (_stallRecovery && _avoidedFirstHop is not null)
        {
            // A detour exists: a fresh route replaces the cleared target.
            _avoidedFirstHop = null;
            _blockedStreak = 0;
        }

        RecordDecision(myZone, ActionKind.Move, choice.NextHop);
        return new AgentAction(ActionKind.Move, choice.NextHop);
    }

    /// <summary>
    /// Advances the blocked-tick bookkeeping from this observation: a Move
    /// emitted last tick that did not advance the agent (still in the same
    /// zone, not at the target) is one blocked tick; any advance or arrival
    /// resets the window. On the third consecutive blocked tick the contested
    /// first hop is latched as the path target to route around.
    /// </summary>
    private void TrackStall(int myZone)
    {
        if (_hasHistory && _lastMoveTarget is { } target)
        {
            if (myZone == target || myZone != _lastZone)
            {
                _blockedStreak = 0;
                _avoidedFirstHop = null;
            }
            else
            {
                _blockedStreak++;
            }
        }

        if (_avoidedFirstHop is null && _blockedStreak >= StallRecoveryThreshold)
        {
            _avoidedFirstHop = _lastMoveTarget;
        }
    }

    private void RecordDecision(int myZone, ActionKind kind, int? moveTarget)
    {
        _lastZone = myZone;
        _lastMoveTarget = kind == ActionKind.Move ? moveTarget : null;
        _hasHistory = true;
    }

    /// <summary>
    /// Returns the nearest unclaimed resource by hop count and the first hop
    /// toward it. Resources whose shortest path begins at
    /// <paramref name="excludeFirstHop"/> are skipped, so a latched blocked
    /// edge is routed around. An unclaimed resource in the agent's own zone is
    /// always the nearest candidate and wins immediately.
    /// </summary>
    private static (ResourceNode? Resource, int NextHop) ChooseNearest(
        MapGraph map,
        List<ResourceNode> unclaimed,
        int myZone,
        int? excludeFirstHop)
    {
        ResourceNode? bestResource = null;
        var bestDistance = int.MaxValue;
        var bestHop = -1;

        foreach (var resource in unclaimed)
        {
            if (resource.ZoneId == myZone)
            {
                return (resource, -1);
            }

            var path = ShortestPath(map, myZone, resource.ZoneId);
            if (path is null || path.Length < 2)
            {
                continue;
            }

            if (excludeFirstHop is { } blocked && path[1] == blocked)
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

        return (bestResource, bestHop);
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