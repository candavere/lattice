using Lattice.Environment;

namespace Lattice.Generator;

/// <summary>
/// Checks that every zone is reachable from every other zone through
/// choke-point edges — the map is one connected component. Connectivity is the
/// primary "playable map" guarantee: a disconnected map would strand agents
/// with no way to reach resources, and AGENTS.md's lineage demands hard
/// constraints over "probably fine" placement.
/// </summary>
public static class ConnectivityChecker
{
    /// <summary>
    /// Returns true when every zone in <paramref name="map"/> belongs to a
    /// single connected component. Pure: reads the map, returns a verdict.
    /// A map with zero zones is trivially connected.
    /// </summary>
    public static bool IsSatisfied(MapGraph map)
    {
        if (map.Zones.Length == 0)
        {
            return true;
        }

        var adjacency = MapTopology.AdjacencyByZoneId(map);

        var reachable = new HashSet<int>();
        var frontier = new Stack<int>();
        frontier.Push(map.Zones[0].Id);
        reachable.Add(map.Zones[0].Id);

        while (frontier.Count > 0)
        {
            var current = frontier.Pop();
            foreach (var neighbor in adjacency[current])
            {
                if (reachable.Add(neighbor))
                {
                    frontier.Push(neighbor);
                }
            }
        }

        return reachable.Count == map.Zones.Length;
    }
}

/// <summary>
/// Checks that no zone is stranded with zero choke-point connections. This is
/// deliberately weaker and cheaper than full connectivity (which it also
/// implies): it exists so the generator can fail fast on the most common and
/// most obvious defect rather than computing reachability first each retry.
/// </summary>
public static class NoIsolatedZoneChecker
{
    /// <summary>
    /// Returns true when every zone in <paramref name="map"/> has at least one
    /// incident choke point (a self-connecting choke counts once). Pure:
    /// reads the map, returns a verdict. A map with zero zones passes.
    /// </summary>
    public static bool IsSatisfied(MapGraph map)
    {
        var degrees = MapTopology.DegreesByZoneId(map);
        foreach (var zone in map.Zones)
        {
            if (degrees[zone.Id] < 1)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Checks that every zone has at least <paramref name="minimumPerZone"/>
/// incident choke points. Gatekeeping the tactical texture of the map: a zone
/// with a single point of entry is a dead-end sentry funnel, and the generator
/// config should be able to forbid that explicitly.
/// </summary>
public static class MinimumChokePointChecker
{
    /// <summary>
    /// Returns true when every zone in <paramref name="map"/> has at least
    /// <paramref name="minimumPerZone"/> incident choke points (a
    /// self-connecting choke counts once). Pure: reads the map, returns a
    /// verdict.
    /// </summary>
    public static bool IsSatisfied(MapGraph map, int minimumPerZone)
    {
        if (minimumPerZone < 1)
        {
            return true;
        }

        var degrees = MapTopology.DegreesByZoneId(map);
        foreach (var zone in map.Zones)
        {
            if (degrees[zone.Id] < minimumPerZone)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Shared pure topology for the checkers: choke-point degrees and adjacency
/// derived from a map without mutating it.
/// </summary>
internal static class MapTopology
{
    /// <summary>
    /// Maps every zone id (including zero-degree zones) to its number of
    /// incident choke points. Choke points are undirected; a choke that
    /// connects a zone to itself counts as one incident edge.
    /// </summary>
    public static Dictionary<int, int> DegreesByZoneId(MapGraph map)
    {
        var degrees = map.Zones.ToDictionary(z => z.Id, _ => 0);

        foreach (var choke in map.ChokePoints)
        {
            if (choke.FromZoneId == choke.ToZoneId)
            {
                if (degrees.TryGetValue(choke.FromZoneId, out var selfDegree))
                {
                    degrees[choke.FromZoneId] = selfDegree + 1;
                }

                continue;
            }

            if (degrees.TryGetValue(choke.FromZoneId, out var fromDegree))
            {
                degrees[choke.FromZoneId] = fromDegree + 1;
            }

            if (degrees.TryGetValue(choke.ToZoneId, out var toDegree))
            {
                degrees[choke.ToZoneId] = toDegree + 1;
            }
        }

        return degrees;
    }

    /// <summary>
    /// Maps every zone id to the set of zone ids it shares a choke point
    /// with (adjacency lists). Every zone id appears, even with an empty set.
    /// </summary>
    public static Dictionary<int, HashSet<int>> AdjacencyByZoneId(MapGraph map)
    {
        var adjacency = map.Zones.ToDictionary(z => z.Id, _ => new HashSet<int>());

        foreach (var choke in map.ChokePoints)
        {
            if (choke.FromZoneId == choke.ToZoneId)
            {
                continue;
            }

            if (adjacency.TryGetValue(choke.FromZoneId, out var fromNeighbors))
            {
                fromNeighbors.Add(choke.ToZoneId);
            }

            if (adjacency.TryGetValue(choke.ToZoneId, out var toNeighbors))
            {
                toNeighbors.Add(choke.FromZoneId);
            }
        }

        return adjacency;
    }
}