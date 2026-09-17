using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// How a zone appears in an agent's evolving belief model (T7.2). Zones
/// observed on the current tick are <see cref="Known"/> — real-time territory;
/// zones observed earlier but not now are <see cref="Stale"/>, remembered but
/// potentially outdated; zones never observed are <see cref="Unexplored"/>,
/// known only to exist (from <see cref="PartialObservation"/>'s id range) but
/// with no position or edge data until observed.
/// </summary>
public enum TerritoryStatus
{
    Unexplored,
    Stale,
    Known,
}

/// <summary>
/// One zone's belief status in the agent's map. Position is populated once the
/// zone is observed; edges accumulate across ticks (never pruned — exit
/// topology doesn't change). <see cref="LastSeenTick"/> is the most recent
/// tick on which the zone was observed.
/// </summary>
public sealed class ZoneBelief
{
    public TerritoryStatus Status { get; set; } = TerritoryStatus.Unexplored;
    public GridPoint? Position { get; set; }
    public int LastSeenTick { get; set; } = -1;
    public HashSet<int> KnownEdges { get; } = new();
}

/// <summary>
/// One resource's belief status: its static zone/position (learned once from
/// the first observation of it), whether the resource is known to be claimed,
/// and when the agent last saw it.
/// </summary>
public sealed class ResourceBelief
{
    public int ResourceId { get; }
    public int ZoneId { get; set; } = -1;
    public GridPoint? Position { get; set; }
    public bool Claimed { get; set; }
    public int LastSeenTick { get; set; } = -1;

    public ResourceBelief(int resourceId)
    {
        ResourceId = resourceId;
    }
}

/// <summary>
/// Immutable snapshot of an agent's knowledge of a rival's position (T7.2).
/// </summary>
public sealed record EnemySighting(int AgentId, int ZoneId, int LastSeenTick);

/// <summary>
/// The belief state a scouting agent builds up tick-by-tick from its
/// <see cref="PartialObservation"/> projections (T7.2). Every update is a
/// deterministic function of the latest projection alone, so repeated input
/// streams produce identical beliefs.
/// <see cref="Update"/> is the only mutation entry point; everything else is
/// read-only access for the decision logic and for tests.
/// </summary>
public sealed class AgentBeliefMap
{
    private readonly Dictionary<int, ZoneBelief> _zones = new();
    private readonly Dictionary<int, ResourceBelief> _resources = new();
    private readonly Dictionary<int, EnemySighting> _enemies = new();

    /// <summary>Creates an empty belief map for <paramref name="agentId"/>.</summary>
    public AgentBeliefMap(int agentId)
    {
        AgentId = agentId;
    }

    /// <summary>Which agent owns this belief map.</summary>
    public int AgentId { get; }

    /// <summary>The agent's most recently observed zone, or null when never observed.</summary>
    public int? MyZone { get; private set; }

    /// <summary>Latest sighting (tick, zone) of every rival the agent has seen.</summary>
    public IReadOnlyDictionary<int, EnemySighting> Enemies => _enemies;

    /// <summary>Belief for every zone id in the map (Unexplored until observed).</summary>
    public IReadOnlyDictionary<int, ZoneBelief> Zones => _zones;

    /// <summary>Belief for every resource id the agent has encountered.</summary>
    public IReadOnlyDictionary<int, ResourceBelief> Resources => _resources;

    /// <summary>
    /// Merges a fresh <see cref="PartialObservation"/> into the belief model.
    /// Observed zones become <see cref="TerritoryStatus.Known"/>; zones that
    /// drop out of the vision cone become <see cref="TerritoryStatus.Stale"/>
    /// (their last-known position and tick are retained); unexplored ids stay
    /// unexplored. Resource sightings populate or refresh resource beliefs,
    /// and visible claims mark resources as claimed.
    /// </summary>
    public void Update(PartialObservation observation)
    {
        for (var agentId = 0; agentId < observation.Agents.Length; agentId++)
        {
            var sight = observation.Agents[agentId];
            if (sight.AgentId == AgentId)
            {
                if (sight.Status == KnowledgeStatus.Observed && sight.LastKnownState is not null)
                {
                    MyZone = sight.LastKnownState.ZoneId;
                }

                continue;
            }

            if (sight.Status == KnowledgeStatus.Observed && sight.LastKnownState is not null)
            {
                _enemies[sight.AgentId] = new EnemySighting(sight.AgentId, sight.LastKnownState.ZoneId, sight.LastSeenTick);
            }
        }

        for (var zoneId = 0; zoneId < observation.Zones.Length; zoneId++)
        {
            var sight = observation.Zones[zoneId];
            if (!_zones.TryGetValue(zoneId, out var belief))
            {
                belief = new ZoneBelief();
                _zones[zoneId] = belief;
            }

            if (sight.Status == KnowledgeStatus.Observed)
            {
                belief.Status = TerritoryStatus.Known;
                belief.Position = sight.LastKnownPosition;
                belief.LastSeenTick = sight.LastSeenTick;
                foreach (var exit in sight.ObservedNeighbors)
                {
                    belief.KnownEdges.Add(exit);
                }
            }
            else if (sight.Status == KnowledgeStatus.Stale)
            {
                belief.Status = TerritoryStatus.Stale;
                belief.Position ??= sight.LastKnownPosition;
                belief.LastSeenTick = Math.Max(belief.LastSeenTick, sight.LastSeenTick);
            }
        }

        for (var resourceId = 0; resourceId < observation.Resources.Length; resourceId++)
        {
            var sight = observation.Resources[resourceId];
            if (sight.Status == KnowledgeStatus.Unknown)
            {
                continue;
            }

            if (!_resources.TryGetValue(resourceId, out var belief))
            {
                belief = new ResourceBelief(resourceId);
                _resources[resourceId] = belief;
            }

            if (sight.Status == KnowledgeStatus.Observed)
            {
                belief.ZoneId = sight.ZoneId ?? -1;
                belief.Position = sight.LastKnownPosition;
                belief.LastSeenTick = sight.LastSeenTick;
            }
        }

        foreach (var claimId in observation.VisibleClaims)
        {
            if (_resources.TryGetValue(claimId, out var belief))
            {
                belief.Claimed = true;
            }
        }
    }

    /// <summary>
    /// The edge set accumulated for <paramref name="zoneId"/> across all
    /// observations of that zone.
    /// </summary>
    public IReadOnlyCollection<int> KnownEdges(int zoneId) =>
        _zones.TryGetValue(zoneId, out var belief) ? belief.KnownEdges : Array.Empty<int>();
}