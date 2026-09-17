using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Deterministic graph utilities shared by the tactical agents and the
/// infiltration scenario: BFS hop counts and first hops over choke edges
/// (expanding neighbors in ascending id so ties resolve predictably) plus the
/// Chebyshev grid metric used for spatial perception bounds. All math is pure
/// integer arithmetic over the <see cref="MapGraph"/> topology, identical on
/// every platform.
/// </summary>
internal static class Pathfinder
{
    /// <summary>
    /// Hop distance between two zones (shortest path over choke edges), or
    /// <c>null</c> when no path exists. A zone is zero hops from itself.
    /// </summary>
    public static int? Distance(MapGraph map, int from, int to)
    {
        if (from == to)
        {
            return 0;
        }

        var visited = new HashSet<int> { from };
        var frontier = new Queue<(int Zone, int Depth)>();
        frontier.Enqueue((from, 0));

        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            foreach (var neighbor in Neighbors(map, current))
            {
                if (!visited.Add(neighbor))
                {
                    continue;
                }

                if (neighbor == to)
                {
                    return depth + 1;
                }

                frontier.Enqueue((neighbor, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// The first zone to enter on a shortest path from <paramref name="from"/>
    /// to <paramref name="to"/> (the first hop), or <c>null</c> when the zones
    /// are equal or disconnected.
    /// </summary>
    public static int? FirstHop(MapGraph map, int from, int to)
    {
        if (from == to)
        {
            return null;
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
                    var hop = neighbor;
                    var cursor = neighbor;
                    while (parent.TryGetValue(cursor, out var next) && next != from)
                    {
                        hop = next;
                        cursor = next;
                    }

                    return hop;
                }

                frontier.Enqueue(neighbor);
            }
        }

        return null;
    }

    /// <summary>
    /// Chebyshev distance between two grid points (max of axis deltas), the
    /// "king-move" metric used to bound spatial perception horizons.
    /// </summary>
    public static int Chebyshev(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Adjacent zone ids in ascending order (deterministic expansion).</summary>
    public static IEnumerable<int> Neighbors(MapGraph map, int zone)
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

        return result.OrderBy(id => id);
    }
}