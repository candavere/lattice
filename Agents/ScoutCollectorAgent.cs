using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A deterministic exploration-first agent. It holds its own
/// <see cref="PerceptionFilter"/> (a bounded-vision sensor) and an
/// <see cref="AgentBeliefMap"/>, and decides strictly from what it sees:
/// each tick it projects the incoming <see cref="Observation"/> to its
/// partial view, folds that view into its beliefs, collects any resource
/// it believes unclaimed in its own zone, and otherwise moves toward the
/// most promising unaware territory — unexplored or stale zones, preferring
/// zones that may hold an unclaimed resource, while refusing to route
/// through the last zones where it saw an enemy agent. When the sensor
/// refutes a held belief — a resource it believed unclaimed observed as
/// claimed, or a choke it believed traversable observed occupied by a rival
/// mid-crossing — the scout re-plans within the same tick: the refuted
/// target drops out of contention and routing avoids the occupied choke for
/// that tick instead of honoring the stale plan. All reasoning runs over
/// believed edges only (choke exits it has actually observed), so it never
/// reasons about geometry it has not seen. No ambient randomness; the same
/// observation stream always yields the same decision stream.
/// </summary>
public sealed class ScoutCollectorAgent : IAgent
{
    private readonly AgentBeliefMap _belief;
    private PerceptionFilter? _filter;
    private int _tick = 1;
    private int _zoneCount;

    /// <summary>
    /// Creates the scout for agent slot <paramref name="agentId"/> with a
    /// vision cone of <paramref name="vision"/> graph hops (or
    /// <see cref="SimulationConfig.UnboundedVision"/>. The perception filter
    /// and belief map are per-instance state and reset only by construction;
    /// reuse the agent across episodes only if that is intended.
    /// </summary>
    public ScoutCollectorAgent(int agentId, int vision)
    {
        if (vision != SimulationConfig.UnboundedVision && vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vision), vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        AgentId = agentId;
        _vision = vision;
        _belief = new AgentBeliefMap(agentId);
    }

    private readonly int _vision;

    /// <inheritdoc />
    public int AgentId { get; }

    /// <summary>The belief map, exposed read-only for tests.</summary>
    public AgentBeliefMap Belief => _belief;

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        // The full observation is used only to build the observer's own sensor
        // (the filter needs the map topology) — every decision below reads
        // exclusively the projected partial view and this agent's beliefs.
        _filter ??= new PerceptionFilter(observation.Map, AgentId, _vision);
        var partial = _filter.Project(_tick++, observation);
        _belief.Update(partial);
        _zoneCount = partial.Zones.Length;

        if (_belief.MyZone is not int myZone)
        {
            return new AgentAction(ActionKind.Wait);
        }

        var localResource = _belief.Resources.Values
            .Where(resource => resource.ZoneId == myZone && !resource.Claimed)
            .OrderBy(resource => resource.ResourceId)
            .FirstOrDefault();
        if (localResource is not null)
        {
            return new AgentAction(ActionKind.Collect, ResourceId: localResource.ResourceId);
        }

        var hop = BestExplorationHop(myZone, partial.Tick, out var found);
        return found
            ? new AgentAction(ActionKind.Move, hop!.Value)
            : new AgentAction(ActionKind.Wait);
    }

    /// <summary>
    /// Chooses the first hop of the shortest believed path to the best
    /// exploration target: zones with a suspected unclaimed resource are
    /// preferred; among equals, unexplored before stale; ties break by hop
    /// distance then zone id. Zones the agent last saw an enemy in are never
    /// traversed, so the scout won't charge into a remembered enemy position.
    /// Beliefs refuted by this tick's sensor — a suspected resource observed
    /// claimed, an intended choke observed occupied — have already been folded
    /// in by <see cref="AgentBeliefMap.Update"/>, so the plan is rebuilt from
    /// the corrected belief state rather than the stale one. <paramref name="tick"/>
    /// is the projection tick, so occupancy sightings carry their real-time tick
    /// into routing decisions.
    /// </summary>
    private int? BestExplorationHop(int myZone, int tick, out bool found)
    {
        found = false;
        var enemyZones = _belief.Enemies.Values
            .Select(sighting => sighting.ZoneId)
            .ToHashSet();

        (int Priority, int Distance, int Id, int Hop)? best = null;

        for (var zoneId = 0; zoneId < _zoneCount; zoneId++)
        {
            if (zoneId == myZone || enemyZones.Contains(zoneId))
            {
                continue;
            }

            var status = _belief.Zones.TryGetValue(zoneId, out var zoneBelief)
                ? zoneBelief.Status
                : TerritoryStatus.Unexplored;

            var suspected = HasUnclaimedResourceBelief(zoneId);
            var unexplored = status == TerritoryStatus.Unexplored;
            if (status == TerritoryStatus.Known && !suspected)
            {
                continue;
            }

            var priority = (suspected ? 2 : 0) + (unexplored ? 1 : 0);
            if (!TryPlan(myZone, zoneId, enemyZones, tick, out var hop, out var distance))
            {
                continue;
            }

            var candidate = (priority, distance, zoneId, hop);
            if (best is null ||
                candidate.Item1 > best.Value.Priority ||
                (candidate.Item1 == best.Value.Priority && candidate.Item2 < best.Value.Distance) ||
                (candidate.Item1 == best.Value.Priority && candidate.Item2 == best.Value.Distance && candidate.Item3 < best.Value.Id))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return null;
        }

        found = true;
        return best.Value.Hop;
    }

    private bool HasUnclaimedResourceBelief(int zoneId) =>
        _belief.Resources.Values.Any(resource => resource.ZoneId == zoneId && !resource.Claimed);

    /// <summary>
    /// BFS over believed edges from <paramref name="from"/> to
    /// <paramref name="to"/>, expanding neighbors in ascending id, refusing to
    /// enter <paramref name="enemyZones"/>, and refusing choke edges observed
    /// occupied at <paramref name="tick"/> (a saturated choke re-routes the
    /// plan this tick instead of sending the scout into the blockage).
    /// Returns the first hop and the hop count, or false when no believed
    /// path exists.
    /// </summary>
    private bool TryPlan(int from, int to, HashSet<int> enemyZones, int tick, out int hop, out int distance)
    {
        hop = -1;
        distance = -1;

        var visited = new HashSet<int> { from };
        var parent = new Dictionary<int, int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(from);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current == to)
            {
                var path = new List<int>();
                var cursor = to;
                while (cursor != from)
                {
                    path.Add(cursor);
                    cursor = parent[cursor];
                }

                path.Reverse();
                distance = path.Count;
                hop = path.Count > 0 ? path[0] : -1;
                return true;
            }

            foreach (var neighbor in _belief.KnownEdges(current).OrderBy(edge => edge))
            {
                if (enemyZones.Contains(neighbor) || _belief.IsEdgeBusy(current, neighbor, tick) || !visited.Add(neighbor))
                {
                    continue;
                }

                parent[neighbor] = current;
                frontier.Enqueue(neighbor);
            }
        }

        return false;
    }
}