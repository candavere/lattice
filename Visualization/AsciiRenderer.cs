using System.Text;
using Lattice.Environment;

namespace Lattice.Visualization;

/// <summary>
/// Renders a lattice map as a plain-ASCII grid. It replaces the
/// "thin web viewer" idea with something catchier: a zero-dependency terminal
/// projection that works anywhere. It is strictly a reader — every glyph it
/// emits (zone tokens, choke edges, resource markers, agent positions, claim
/// state) is already present in the <see cref="Observation"/>/<see cref="StepResult"/>
/// it is handed, and it never re-derives game logic. Output is a deterministic pure function of its
/// inputs: identical inputs, byte-identical frames, which the tests pin down.
/// </summary>
public static class AsciiRenderer
{
    /// <summary>Unclaimed resource marker placed at the resource's grid cell.</summary>
    public const char UnclaimedMarker = '$';

    /// <summary>Claimed resource marker placed at the resource's grid cell.</summary>
    public const char ClaimedMarker = '*';

    /// <summary>Cell with no zone, resource, or edge drawn in it.</summary>
    public const char EmptyCell = '.';

    /// <summary>
    /// Renders one frame from an explicit (agents, claims) state — the common
    /// case for a freshly stepped or recorded state. Claims is the set of
    /// resource ids already collected (as in <see cref="Observation.Claims"/>).
    /// </summary>
    public static string RenderFrame(MapGraph map, AgentState[] agents, int[] claims)
    {
        if (agents is null)
        {
            throw new ArgumentNullException(nameof(agents));
        }

        if (claims is null)
        {
            throw new ArgumentNullException(nameof(claims));
        }

        var cells = DrawMap(map, claims);
        var canvas = BuildCanvas(cells, map);
        return string.Join("\n", new[] { canvas }.Concat(Footer(map, agents, claims)));
    }

    /// <summary>
    /// Convenience overload: renders the exact state an agent observed this
    /// tick, so a replay loop can render each frame directly from the step's
    /// recorded <see cref="Observation"/>.
    /// </summary>
    public static string RenderFrame(MapGraph map, Observation observation) =>
        RenderFrame(map, observation.AgentStates, observation.Claims);

    /// <summary>
    /// All grid cells are dedicated to a single character with a fixed,
    /// documented precedence so a cell is never ambiguous: choke edges are
    /// drawn first, resource markers then overwrite edges, and zone tokens
    /// overwrite both — a resource sharing a cell with its zone center is
    /// reported through the per-zone footer line instead. Character choice per
    /// choke is set by the segment's dominant axis so maps read as cheap
    /// topology maps in a terminal.
    /// </summary>
    private static Dictionary<(int X, int Y), char> DrawMap(MapGraph map, int[] claims)
    {
        if (map.Zones.Length == 0 && map.Resources.Length == 0)
        {
            throw new ArgumentException("Cannot render a map with no zones and no resources.", nameof(map));
        }

        var cells = new Dictionary<(int X, int Y), char>();
        var positionById = map.Zones.ToDictionary(z => z.Id, z => z.Position);
        foreach (var choke in map.ChokePoints)
        {
            var from = positionById[choke.FromZoneId];
            var to = positionById[choke.ToZoneId];
            foreach (var cell in Line(from.X, from.Y, to.X, to.Y))
            {
                cells.TryAdd(cell, EdgeChar(from, to));
            }
        }

        foreach (var resource in map.Resources)
        {
            cells[(resource.Position.X, resource.Position.Y)] = claims.Contains(resource.Id)
                ? ClaimedMarker
                : UnclaimedMarker;
        }

        foreach (var zone in map.Zones)
        {
            cells[(zone.Position.X, zone.Position.Y)] = ZoneToken(zone.Id);
        }

        return cells;
    }

    private static char EdgeChar(GridPoint from, GridPoint to)
    {
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);
        if (dx > dy)
        {
            return '-';
        }

        return dy > dx ? '|' : '+';
    }

    private static string BuildCanvas(Dictionary<(int X, int Y), char> cells, MapGraph map)
    {
        var points = map.Zones.Select(z => z.Position)
            .Concat(map.Resources.Select(r => r.Position))
            .ToList();
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);

        var builder = new StringBuilder();
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                builder.Append(cells.TryGetValue((x, y), out var glyph) ? glyph : EmptyCell);
            }

            builder.Append('\n');
        }

        builder.Length--; // no trailing newline: footer lines carry the terminator
        return builder.ToString();
    }

    private static IEnumerable<string> Footer(MapGraph map, AgentState[] agents, int[] claims)
    {
        var agentLine = "Agents: " + string.Join(" ",
            agents.OrderBy(a => a.AgentId).Select(a => $"A{a.AgentId}@Z{a.ZoneId}({a.Score})"));
        yield return agentLine;

        var claimedResources = map.Resources.Where(r => claims.Contains(r.Id)).ToList();
        yield return $"Resources: claimed {claimedResources.Count} of {map.Resources.Length}";

        foreach (var zone in map.Zones.OrderBy(z => z.Id))
        {
            var inZone = map.Resources.Where(r => r.ZoneId == zone.Id).ToList();
            var claimed = inZone.Where(r => claims.Contains(r.Id)).Select(r => r.Id).OrderBy(id => id);
            var unclaimed = inZone.Where(r => !claims.Contains(r.Id)).Select(r => r.Id).OrderBy(id => id);
            yield return $"Z{zone.Id}: claimed [{string.Join(" ", claimed)}] unclaimed [{string.Join(" ", unclaimed)}]";
        }
    }

    /// <summary>
    /// Maps a zone id onto a single ASCII glyph: 0-9, then A-Z, then a-z.
    /// Maps with 62+ zones cannot be projected one-glyph-per-zone and fail
    /// loudly rather than silently aliasing two ids to the same character.
    /// </summary>
    private static char ZoneToken(int zoneId)
    {
        if (zoneId < 10)
        {
            return (char)('0' + zoneId);
        }

        if (zoneId < 36)
        {
            return (char)('A' + zoneId - 10);
        }

        if (zoneId < 62)
        {
            return (char)('a' + zoneId - 36);
        }

        throw new ArgumentOutOfRangeException(
            nameof(zoneId), zoneId, "Zone ids of 62 or more cannot be rendered as a single ASCII character.");
    }

    /// <summary>
    /// Integer (Bresenham) line cells between two grid points, inclusive. The
    /// deterministic raster is what makes choke edges reproducible on every
    /// platform — no floating-point rounding is involved anywhere.
    /// </summary>
    private static IEnumerable<(int X, int Y)> Line(int x0, int y0, int x1, int y1)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            yield return (x0, y0);
            if (x0 == x1 && y0 == y1)
            {
                yield break;
            }

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }
}