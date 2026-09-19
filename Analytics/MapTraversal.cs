using Lattice.Environment;

namespace Lattice.Analytics;

/// <summary>
/// Deterministic graph queries over a <see cref="MapGraph"/>'s choke-point
/// edges: adjacency lookup, the choke point used for a hop, and unweighted
/// (edge-count) shortest-path distances via BFS. Pure functions over the map —
/// no state, no caching, no mutation — so analytics stay identical for
/// identical maps. Zones that are not connected by any choke path are at
/// <see cref="Distance"/> <see cref="int.MaxValue"/>.
/// </summary>
internal static class MapTraversal
{
    /// <summary>
    /// The choke point id an agent crossing from <paramref name="fromZone"/> to
    /// <paramref name="toZone"/> would use: the lowest id among all choke
    /// points connecting that unordered pair, or null when no choke connects
    /// them (i.e. the hop is illegal).
    /// </summary>
    public static int? ChokeForHop(MapGraph map, int fromZone, int toZone)
    {
        int? best = null;
        foreach (var choke in map.ChokePoints)
        {
            var connects = (choke.FromZoneId == fromZone && choke.ToZoneId == toZone)
                           || (choke.FromZoneId == toZone && choke.ToZoneId == fromZone);
            if (connects && (!best.HasValue || choke.Id < best.Value))
            {
                best = choke.Id;
            }
        }

        return best;
    }

    /// <summary>
    /// Minimum number of choke edges needed to travel between two zones
    /// (unweighted BFS), or <see cref="int.MaxValue"/> when unreachable.
    /// </summary>
    public static int Distance(MapGraph map, int fromZone, int toZone)
    {
        if (fromZone == toZone)
        {
            return 0;
        }

        var adjacency = BuildAdjacency(map);
        if (!adjacency.TryGetValue(fromZone, out var neighbors))
        {
            return int.MaxValue;
        }

        var queue = new Queue<int>();
        var distance = new Dictionary<int, int> { [fromZone] = 0 };
        queue.Enqueue(fromZone);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in neighbors)
            {
                if (distance.ContainsKey(next))
                {
                    continue;
                }

                var nextDistance = distance[current] + 1;
                if (next == toZone)
                {
                    return nextDistance;
                }

                distance[next] = nextDistance;
                queue.Enqueue(next);
            }
        }

        return int.MaxValue;
    }

    private static Dictionary<int, int[]> BuildAdjacency(MapGraph map)
    {
        var adjacency = new Dictionary<int, int[]>();
        var lists = new Dictionary<int, HashSet<int>>();
        foreach (var zone in map.Zones)
        {
            lists[zone.Id] = new HashSet<int>();
        }

        foreach (var choke in map.ChokePoints)
        {
            lists[choke.FromZoneId].Add(choke.ToZoneId);
            lists[choke.ToZoneId].Add(choke.FromZoneId);
        }

        foreach (var pair in lists)
        {
            adjacency[pair.Key] = pair.Value.OrderBy(id => id).ToArray();
        }

        return adjacency;
    }
}