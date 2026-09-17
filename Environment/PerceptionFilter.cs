namespace Lattice.Environment;

/// <summary>
/// How much real-time fidelity a sighted element carries in a
/// <see cref="PartialObservation"/>. <see cref="Observed"/> elements
/// were inside the observer's vision cone this tick and are real-time;
/// <see cref="Stale"/> elements are beyond the cone now but were seen before,
/// so their last-known data and the tick of that sighting survive;
/// <see cref="Unknown"/> elements have never been observed and expose no data.
/// </summary>
public enum KnowledgeStatus
{
    Observed,
    Stale,
    Unknown,
}

/// <summary>
/// One zone as the observing agent perceives it. Status tells whether the
/// zone is inside the vision cone this tick (<see cref="KnowledgeStatus.Observed"/>),
/// known only from a previous sighting (<see cref="KnowledgeStatus.Stale"/>),
/// or completely unobserved (<see cref="KnowledgeStatus.Unknown"/> — masked,
/// position null, visit tick -1). Zone geometry is static map data, so a
/// Stale sight keeps the real position but records <see cref="LastSeenTick"/>.
/// <see cref="ObservedNeighbors"/> lists the choke exits only when the zone is
/// observed this tick (empty otherwise) — the only adjacency knowledge an
/// agent is entitled to, so routing happens over exits it has actually seen.
/// </summary>
public sealed record ZoneSight(
    int ZoneId,
    KnowledgeStatus Status,
    GridPoint? LastKnownPosition,
    int LastSeenTick,
    int[] ObservedNeighbors);

/// <summary>
/// One resource as the observing agent perceives it. Observed resources carry
/// their zone and position; Stale resources keep the last-known (static)
/// geometry plus the tick of that sighting; Unknown resources are fully
/// masked (null zone/position, tick -1).
/// </summary>
public sealed record ResourceSight(
    int ResourceId,
    KnowledgeStatus Status,
    int? ZoneId,
    GridPoint? LastKnownPosition,
    int LastSeenTick);

/// <summary>
/// One agent as the observing agent perceives it. An Observed sighting is the
/// real-time <see cref="AgentState"/>; a Stale sighting holds the last-known
/// state and the tick it was true; an Unknown agent exposes nothing.
/// </summary>
public sealed record AgentSight(
    int AgentId,
    KnowledgeStatus Status,
    AgentState? LastKnownState,
    int LastSeenTick);

/// <summary>
/// The vision-bounded observation contract: everything an agent is
/// entitled to know this tick. It is produced by <see cref="PerceptionFilter"/>
/// as a pure projection of a full <see cref="Observation"/> and carries three
/// data tiers — real-time detail for elements within <see cref="Vision"/> hops,
/// last-known stale memory for previously observed elements, and fully masked
/// entries for everything else. Pure data, JSON-serializable, no behavior.
/// </summary>
public sealed record PartialObservation(
    int AgentId,
    int Tick,
    int Vision,
    ZoneSight[] Zones,
    ResourceSight[] Resources,
    AgentSight[] Agents,
    int[] VisibleClaims);

/// <summary>
/// The deterministic, vision-bounded observation filter. Given the
/// omniscient <see cref="Observation"/> the simulation core produces, it
/// projects what one observer can actually see: zones within
/// <see cref="SimulationConfig.Vision"/> graph hops (and everything in them),
/// and stale last-known data for anything beyond that it has seen before. It
/// holds only the observer's own memory — never any part of the simulation
/// core, which remains vision-agnostic and unchanged. Every input sequence
/// yields the same output sequence on every machine.
/// </summary>
public sealed class PerceptionFilter
{
    private readonly MapGraph _map;
    private readonly int _agentId;
    private readonly int _vision;

    private readonly Dictionary<int, int> _zoneLastSeenTicks = new();
    private readonly Dictionary<int, int> _resourceLastSeenTicks = new();
    private readonly Dictionary<int, AgentState> _agentLastKnown = new();
    private readonly Dictionary<int, int> _agentLastSeenTicks = new();

    /// <summary>
    /// Creates a filter for <paramref name="agentId"/>'s perception cone of
    /// <paramref name="vision"/> graph hops (or
    /// <see cref="SimulationConfig.UnboundedVision"/> for no masking) on the
    /// given map. Throws <see cref="ArgumentOutOfRangeException"/> for a
    /// vision smaller than a single hop.
    /// </summary>
    public PerceptionFilter(MapGraph map, int agentId, int vision)
    {
        if (vision != SimulationConfig.UnboundedVision && vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vision), vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        _map = map;
        _agentId = agentId;
        _vision = vision;
    }

    /// <summary>Which agent slot this filter perceives for.</summary>
    public int AgentId => _agentId;

    /// <summary>The vision cone this filter applies, in graph hops.</summary>
    public int Vision => _vision;

    /// <summary>The map this filter projects against.</summary>
    public MapGraph Map => _map;

    /// <summary>
    /// Projects <paramref name="observation"/> (the recording's full per-agent
    /// observation) into a <see cref="PartialObservation"/> for this observer
    /// at <paramref name="tick"/>, updating this observer's stale memory. Each
    /// tick's output is a fresh, immutable value.
    /// </summary>
    public PartialObservation Project(int tick, Observation observation)
    {
        if (tick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "Projection ticks are 1-based (post-step observations).");
        }

        var myState = observation.AgentStates.FirstOrDefault(agent => agent.AgentId == _agentId);
        if (myState is null)
        {
            throw new ArgumentException($"Observation has no state for observer agent {_agentId}.", nameof(observation));
        }

        var observedZones = ObservedZones(myState.ZoneId);

        var zones = new ZoneSight[_map.Zones.Length];
        for (var zoneId = 0; zoneId < zones.Length; zoneId++)
        {
            var zone = _map.Zones[zoneId];
            if (observedZones.Contains(zoneId))
            {
                _zoneLastSeenTicks[zoneId] = tick;
                zones[zoneId] = new ZoneSight(
                    zoneId, KnowledgeStatus.Observed, zone.Position, tick, Neighbors(zoneId).ToArray());
            }
            else if (_zoneLastSeenTicks.TryGetValue(zoneId, out var lastTick))
            {
                zones[zoneId] = new ZoneSight(zoneId, KnowledgeStatus.Stale, zone.Position, lastTick, Array.Empty<int>());
            }
            else
            {
                zones[zoneId] = new ZoneSight(zoneId, KnowledgeStatus.Unknown, null, -1, Array.Empty<int>());
            }
        }

        var resources = new ResourceSight[_map.Resources.Length];
        for (var resourceId = 0; resourceId < resources.Length; resourceId++)
        {
            var resource = _map.Resources[resourceId];
            var inCone = observedZones.Contains(resource.ZoneId);
            if (inCone)
            {
                _resourceLastSeenTicks[resourceId] = tick;
                resources[resourceId] = new ResourceSight(
                    resourceId, KnowledgeStatus.Observed, resource.ZoneId, resource.Position, tick);
            }
            else if (_resourceLastSeenTicks.TryGetValue(resourceId, out var lastTick))
            {
                resources[resourceId] = new ResourceSight(
                    resourceId, KnowledgeStatus.Stale, resource.ZoneId, resource.Position, lastTick);
            }
            else
            {
                resources[resourceId] = new ResourceSight(resourceId, KnowledgeStatus.Unknown, null, null, -1);
            }
        }

        var agents = new AgentSight[observation.AgentStates.Length];
        for (var agentId = 0; agentId < agents.Length; agentId++)
        {
            var state = observation.AgentStates[agentId];
            if (observedZones.Contains(state.ZoneId))
            {
                _agentLastKnown[agentId] = state;
                _agentLastSeenTicks[agentId] = tick;
                agents[agentId] = new AgentSight(agentId, KnowledgeStatus.Observed, state, tick);
            }
            else if (_agentLastKnown.TryGetValue(agentId, out var lastKnown))
            {
                agents[agentId] = new AgentSight(agentId, KnowledgeStatus.Stale, lastKnown, _agentLastSeenTicks[agentId]);
            }
            else
            {
                agents[agentId] = new AgentSight(agentId, KnowledgeStatus.Unknown, null, -1);
            }
        }

        var visibleClaims = observation.Claims
            .Where(resourceId => observedZones.Contains(_map.Resources[resourceId].ZoneId))
            .ToArray();

        return new PartialObservation(_agentId, tick, _vision, zones, resources, agents, visibleClaims);
    }

    /// <summary>Clears this observer's stale memory (starting a fresh episode).</summary>
    public void Reset()
    {
        _zoneLastSeenTicks.Clear();
        _resourceLastSeenTicks.Clear();
        _agentLastKnown.Clear();
        _agentLastSeenTicks.Clear();
    }

    /// <summary>
    /// The set of zone ids within <see cref="_vision"/> graph hops of
    /// <paramref name="fromZone"/> (including it), or every zone when vision
    /// is unbounded. BFS over choke edges with expansion in ascending
    /// neighbor-id order so the result is deterministic.
    /// </summary>
    private HashSet<int> ObservedZones(int fromZone)
    {
        var seen = new HashSet<int> { fromZone };
        if (_vision == SimulationConfig.UnboundedVision)
        {
            foreach (var zone in _map.Zones)
            {
                seen.Add(zone.Id);
            }

            return seen;
        }

        var frontier = new Queue<int>();
        var distance = new Dictionary<int, int> { [fromZone] = 0 };
        frontier.Enqueue(fromZone);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var neighbor in Neighbors(current))
            {
                if (seen.Contains(neighbor))
                {
                    continue;
                }

                var nextDistance = distance[current] + 1;
                if (nextDistance > _vision)
                {
                    continue;
                }

                seen.Add(neighbor);
                distance[neighbor] = nextDistance;
                frontier.Enqueue(neighbor);
            }
        }

        return seen;
    }

    private IEnumerable<int> Neighbors(int zone)
    {
        var result = new List<int>();
        foreach (var choke in _map.ChokePoints)
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

        return result.OrderBy(id => id);
    }
}