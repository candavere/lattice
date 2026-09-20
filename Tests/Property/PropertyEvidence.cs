using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Tests.Property;

/// <summary>
/// Shared, seeded-free evidence helpers for the property suite: canonical JSON
/// for reproducibility, per-zone neighbor lists, and BFS hop distances over
/// the map's undirected choke edges. The BFS expansion matches the perception
/// filter's cone semantics (graph hops over chokes, ascending neighbor order),
/// giving the reachability property an independent from-scratch oracle.
/// </summary>
internal static class PropertyEvidence
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Canonical JSON serialization used for equality evidence (byte-level determinism checks).</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Sorted list of neighbor zone ids for every zone (undirected choke adjacency).</summary>
    public static int[][] NeighborLists(MapGraph map)
    {
        var zoneCount = map.Zones.Length;
        var adjacency = new List<int>[zoneCount];
        for (var i = 0; i < zoneCount; i++)
        {
            adjacency[i] = new List<int>();
        }

        foreach (var choke in map.ChokePoints)
        {
            adjacency[choke.FromZoneId].Add(choke.ToZoneId);
            adjacency[choke.ToZoneId].Add(choke.FromZoneId);
        }

        return adjacency.Select(list => list.OrderBy(id => id).ToArray()).ToArray();
    }

    /// <summary>
    /// Graph-hop distance from <paramref name="fromZoneId"/> to every zone
    /// (-1 for unreachable), BFS expanding neighbors in ascending id order to
    /// mirror the filter's deterministic expansion.
    /// </summary>
    public static int[] HopDistances(MapGraph map, int fromZoneId)
    {
        var neighbors = NeighborLists(map);
        var distances = new int[map.Zones.Length];
        Array.Fill(distances, -1);
        distances[fromZoneId] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(fromZoneId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in neighbors[current])
            {
                if (distances[next] != -1)
                {
                    continue;
                }

                distances[next] = distances[current] + 1;
                queue.Enqueue(next);
            }
        }

        return distances;
    }

    /// <summary>True when choke <paramref name="choke"/> connects <paramref name="a"/> and <paramref name="b"/> in either direction.</summary>
    public static bool IsSameEdge(ChokePoint choke, int a, int b) =>
        (choke.FromZoneId == a && choke.ToZoneId == b) || (choke.FromZoneId == b && choke.ToZoneId == a);
}