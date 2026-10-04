namespace Lattice.Tui;

/// <summary>
/// The size of one pane's drawable area, in cells. A zero-sized pane is rejected
/// rather than clamped: a screen that measures zero has not asked for a frame,
/// and handing back an empty grid would hide that.
/// </summary>
public readonly record struct PaneSize
{
    /// <summary>Creates a pane size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Either dimension is below 1.</exception>
    public PaneSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
    }

    /// <summary>The pane's column count.</summary>
    public int Width { get; }

    /// <summary>The pane's row count.</summary>
    public int Height { get; }

    /// <summary>Whether the cell is inside this pane.</summary>
    public bool Contains(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
}

/// <summary>
/// Where one zone landed, and whether it had to be moved to get there.
/// </summary>
/// <param name="ZoneId">The zone this placement is for.</param>
/// <param name="Label">The one character the zone draws as.</param>
/// <param name="X">The column the zone's cell is in.</param>
/// <param name="Y">The row the zone's cell is in.</param>
/// <param name="Shut">
/// Whether the recording says nothing may enter the zone, which is the same
/// recorded fact a shut edge draws as, and travels with the placement so the panes
/// do not have to look the zone up again.
/// </param>
/// <param name="Nudged">
/// Whether scaling put this zone on a cell another zone already held, and the
/// renderer had to move it to the nearest free cell to keep the two apart.
/// </param>
public sealed record ZonePlacement(int ZoneId, string Label, int X, int Y, bool Shut, bool Nudged);

/// <summary>
/// Everything the world pane needs for one frame, and nothing else.
/// </summary>
/// <param name="Map">The map to draw.</param>
/// <param name="Frame">The frame's world: agents, claims, tick.</param>
/// <param name="TrailFrames">
/// The up-to-three earlier frames the trail fades behind, oldest first. Passed
/// explicitly rather than looked up so the renderer stays a function of its
/// inputs and cannot read a frame the caller did not offer.
/// </param>
/// <param name="Size">The drawable area, which the renderer's output matches exactly.</param>
/// <param name="Glyphs">Which glyph vocabulary to draw from.</param>
/// <param name="PanelFill">
/// The panel background to paint under the map, or <c>null</c> to leave the
/// terminal's own background alone.
/// </param>
/// <param name="Phase">
/// How far through the current frame's dwell time this redraw is, in [0, 1).
/// Zero draws every agent exactly where the recording puts it; anything above
/// zero slides an agent in transit a fraction of a cell further along its edge.
/// Drawing only: no tick is created, moved, or rounded by it.
/// </param>
public sealed record WorldRenderRequest(
    WorldMap Map,
    ReplayFrame Frame,
    IReadOnlyList<ReplayFrame> TrailFrames,
    PaneSize Size,
    GlyphMode Glyphs,
    Rgb? PanelFill,
    double Phase = 0.0);

/// <summary>
/// One rendered world pane: the cells, where the zones landed, and what the
/// renderer had to do to keep them apart.
/// </summary>
/// <param name="Cells">
/// The grid, always exactly the requested size — the renderer's output cannot
/// reach outside its own pane because there is nowhere outside it to write.
/// </param>
/// <param name="Zones">One placement per zone that fitted, in the map's own zone order.</param>
/// <param name="UnplacedZoneIds">
/// Zones with no free cell to take, which happens only when the pane holds fewer
/// cells than the map has zones. They are named rather than drawn on top of each
/// other; their agents are still in the scoreboard, which is not size-bound.
/// </param>
/// <param name="NudgedZoneIds">
/// The zones scaling collided and the renderer had to move, in zone order.
/// </param>
public sealed record WorldRender(
    CellBuffer Cells,
    IReadOnlyList<ZonePlacement> Zones,
    IReadOnlyList<int> UnplacedZoneIds,
    IReadOnlyList<int> NudgedZoneIds);

/// <summary>
/// Draws a replayed map into a pane's cell grid. Pure: a request in, a grid out,
/// no terminal touched and no clock read, so the same request always produces the
/// same cells.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement.</b> Zones and resources are scaled by the map's own bounds into
/// the pane, each axis independently, in integer arithmetic. Independent axes is
/// what keeps a 7x5 dungeon and a 992x893 one both fully visible in the same
/// pane; the cost is that a zone graph is drawn anisotropically, which is the
/// price of fitting either of those into the same number of columns.
/// </para>
/// <para>
/// <b>Collisions.</b> Two zones can scale onto the same cell. They are never
/// drawn on top of each other: the later zone in map order moves to the nearest
/// free cell, searched outward in a fixed order so the outcome is the same on
/// every host, and the fact is reported through
/// <see cref="WorldRender.NudgedZoneIds"/> rather than being invisible.
/// </para>
/// <para>
/// <b>Precedence.</b> Each cell carries the priority of whatever last held it, and
/// a pass may only take a cell from something less important: agents over zones,
/// zones over resources, resources over trail marks, trail marks over edges, and
/// nothing over a space. So a pane never shows two things in one cell, and a mark
/// never paints over the panel's background.
/// </para>
/// </remarks>
public static class WorldRenderer
{
    /// <summary>The precedence of an edge line.</summary>
    private const int PriorityEdge = 1;

    /// <summary>The precedence of a trail mark.</summary>
    private const int PriorityTrail = 2;

    /// <summary>The precedence of a resource marker.</summary>
    private const int PriorityResource = 3;

    /// <summary>The precedence of a choke's capacity mark, which is a number and not a line.</summary>
    private const int PriorityChoke = 4;

    /// <summary>The precedence of a zone's own label.</summary>
    private const int PriorityZone = 5;

    /// <summary>The precedence of an agent.</summary>
    private const int PriorityAgent = 6;

    /// <summary>
    /// One step's worth of trail fade, over a denominator wide enough that the
    /// newest mark is still visibly the brightest. Drawing only: it multiplies a
    /// colour, never a recorded value.
    /// </summary>
    private const int TrailFadeNumerator = 3;

    /// <summary>The colour a trail mark fades towards: the panel's own fill.</summary>
    private static readonly Rgb TrailFadeTarget = Palette.PanelBackground;

    /// <summary>Renders one frame of the world pane.</summary>
    public static WorldRender Render(WorldRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var size = request.Size;
        var cells = new CellBuffer(size.Width, size.Height);

        if (request.PanelFill is { } fill)
        {
            cells.Fill(0, 0, size.Width, size.Height, new Cell(' ', null, fill));
        }

        var (placements, byZone, unplaced) = Place(request.Map, size);
        var painter = new Painter(cells, size);
        foreach (var placement in placements)
        {
            painter.Claim(placement.X, placement.Y, PriorityZone);
        }

        DrawEdges(request, byZone, painter);
        var washes = DrawTrail(request, byZone, painter);
        DrawResources(request, new MapScale(request.Map, size), painter);
        DrawZones(request, placements, washes, painter);
        DrawAgents(request, byZone, painter);

        var nudged = placements.Where(placement => placement.Nudged).Select(placement => placement.ZoneId).ToArray();
        return new WorldRender(cells, placements, unplaced, nudged);
    }

    /// <summary>
    /// Scales every zone into the pane and separates the ones that collide. Split
    /// out because placement is the decision the frames depend on most, and it is
    /// worth being able to assert it without drawing anything.
    /// </summary>
    public static (
        IReadOnlyList<ZonePlacement> Placements,
        Dictionary<int, ZonePlacement> ByZone,
        IReadOnlyList<int> Unplaced)
        Place(WorldMap map, PaneSize size)
    {
        ArgumentNullException.ThrowIfNull(map);

        var scale = new MapScale(map, size);
        var taken = new HashSet<long>();
        var placements = new List<ZonePlacement>(map.Zones.Length);
        var byZone = new Dictionary<int, ZonePlacement>(map.Zones.Length);
        var unplaced = new List<int>();

        foreach (var zone in map.Zones)
        {
            var column = scale.Column(zone.X);
            var row = scale.Row(zone.Y);

            if (taken.Add(scale.Key(column, row)))
            {
                var placement = new ZonePlacement(zone.Id, zone.Label, column, row, zone.Capacity == 0, Nudged: false);
                placements.Add(placement);
                byZone[zone.Id] = placement;
                continue;
            }

            var moved = Move(column, row, taken, scale, zone, out var found);
            if (!found)
            {
                unplaced.Add(zone.Id);
                continue;
            }

            placements.Add(moved!);
            byZone[zone.Id] = moved!;
        }

        return (placements, byZone, unplaced);
    }

    /// <summary>
    /// Searches outward from a taken cell for the first free one, in a fixed
    /// order of rings and a fixed order within each ring, so two colliding zones
    /// always separate the same way on every host. Returns <c>false</c> when the
    /// pane has no cell left, which is the only way a zone goes unplaced.
    /// </summary>
    private static ZonePlacement? Move(
        int column,
        int row,
        HashSet<long> taken,
        MapScale scale,
        WorldZone zone,
        out bool found)
    {
        found = false;

        for (var distance = 1; distance < scale.Size.Width + scale.Size.Height && !found; distance++)
        {
            foreach (var (dx, dy) in Ring(distance))
            {
                var candidateX = column + dx;
                var candidateY = row + dy;
                if (!scale.Size.Contains(candidateX, candidateY))
                {
                    continue;
                }

                if (!taken.Add(scale.Key(candidateX, candidateY)))
                {
                    continue;
                }

                found = true;
                return new ZonePlacement(zone.Id, zone.Label, candidateX, candidateY, zone.Capacity == 0, Nudged: true);
            }
        }

        return null;
    }

    /// <summary>The eight offsets of one ring around a cell, in the order they are tried.</summary>
    private static IEnumerable<(int Dx, int Dy)> Ring(int distance)
    {
        yield return (distance, 0);
        yield return (-distance, 0);
        yield return (0, distance);
        yield return (0, -distance);
        yield return (distance, distance);
        yield return (distance, -distance);
        yield return (-distance, distance);
        yield return (-distance, -distance);
    }

    private static void DrawEdges(
        WorldRenderRequest request,
        Dictionary<int, ZonePlacement> byZone,
        Painter painter)
    {
        foreach (var edge in request.Map.Edges)
        {
            if (!byZone.TryGetValue(edge.FromZoneId, out var from)
                || !byZone.TryGetValue(edge.ToZoneId, out var to))
            {
                continue;
            }

            var path = Line(from.X, from.Y, to.X, to.Y);
            var color = edge.Capacity == 0 ? Theme.ClosedEdge : Theme.Sage;
            var glyph = GlyphModes.Glyph(EdgeGlyph(from, to), request.Glyphs);

            for (var i = 1; i < path.Count - 1; i++)
            {
                painter.TryPaint(path[i].X, path[i].Y, new Cell(glyph, color, request.PanelFill), PriorityEdge);
            }

            DrawChoke(request, edge, path, painter);
        }
    }

    /// <summary>
    /// The capacity mark at an edge's midpoint. Three shapes, one per recorded
    /// fact: nothing at all where the edge is uncapped, the capacity itself where
    /// it is finite, and the closed mark in the error colour where it is shut.
    /// </summary>
    private static void DrawChoke(
        WorldRenderRequest request,
        WorldEdge edge,
        List<(int X, int Y)> path,
        Painter painter)
    {
        if (path.Count < 3)
        {
            // A one-cell edge is two zones on each other's doorstep: there is no
            // between them to put a mark in, and overwriting a zone's label with a
            // digit would lose the zone.
            return;
        }

        var middle = path[path.Count / 2];
        if (edge.Capacity == 0)
        {
            painter.TryPaint(
                middle.X,
                middle.Y,
                new Cell(
                    GlyphModes.Glyph(Glyphs.ClosedMark, request.Glyphs),
                    Theme.ClosedEdgeMark,
                    request.PanelFill),
                PriorityChoke);
            return;
        }

        if (edge.Capacity == WorldMap.UnlimitedCapacity)
        {
            return;
        }

        var capacity = edge.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        painter.TryPaintText(
            middle.X,
            middle.Y,
            capacity,
            new Cell(' ', Theme.ContestedChoke, request.PanelFill),
            PriorityChoke);
    }

    /// <summary>
    /// The glyph an edge is drawn with: the axis it mostly runs along, so a chain
    /// of zones reads as a chain rather than as a wash of crossings. An edge
    /// running exactly as far on both axes gets the crossing glyph, which is what
    /// it is.
    /// </summary>
    private static char EdgeGlyph(ZonePlacement from, ZonePlacement to)
    {
        var dx = Math.Abs(to.X - from.X);
        var dy = Math.Abs(to.Y - from.Y);

        if (dx > dy)
        {
            return Glyphs.Horizontal;
        }

        return dy > dx ? Glyphs.Vertical : Glyphs.Cross;
    }

    /// <summary>
    /// The trail behind the agents: where each agent stood on each of the last few
    /// recorded frames, fading with age. Drawing only — the positions are earlier
    /// frames of the same recording, and the fade multiplies a colour towards the
    /// panel's own fill, because a terminal need not have a notion of dimming.
    /// </summary>
    /// <returns>
    /// The washes to paint behind zone cells: a mark that would have landed on a
    /// zone label is applied as that cell's background instead, so the trail stays
    /// visible without taking the zone's name off the map. The newest mark wins
    /// where two overlap.
    /// </returns>
    private static Dictionary<int, Rgb> DrawTrail(
        WorldRenderRequest request,
        Dictionary<int, ZonePlacement> byZone,
        Painter painter)
    {
        var washes = new Dictionary<int, Rgb>();
        if (request.TrailFrames.Count == 0)
        {
            return washes;
        }

        var width = (request.TrailFrames.Count * TrailFadeNumerator) + 1;
        var glyph = GlyphModes.Glyph(Glyphs.LightShade, request.Glyphs);

        for (var i = 0; i < request.TrailFrames.Count; i++)
        {
            var frame = request.TrailFrames[i];
            var age = request.TrailFrames.Count - i;

            foreach (var agent in frame.Agents)
            {
                if (!byZone.TryGetValue(agent.ZoneId, out var placement))
                {
                    continue;
                }

                var faded = Fade(Theme.AgentSlot(agent.Slot), age * TrailFadeNumerator, width);
                var key = painter.Key(placement.X, placement.Y);

                if (painter.Holds(key, PriorityZone))
                {
                    washes[key] = faded;
                    continue;
                }

                painter.TryPaint(placement.X, placement.Y, new Cell(glyph, faded, request.PanelFill), PriorityTrail);
            }
        }

        return washes;
    }

    private static void DrawResources(WorldRenderRequest request, MapScale scale, Painter painter)
    {
        foreach (var resource in request.Map.Resources)
        {
            var column = scale.Column(resource.X);
            var row = scale.Row(resource.Y);
            var claimed = request.Frame.Claims.Contains(resource.Id);

            painter.TryPaint(
                column,
                row,
                new Cell(
                    GlyphModes.Glyph(claimed ? Glyphs.FilledCircle : Glyphs.HollowCircle, request.Glyphs),
                    claimed ? Palette.AccentBright : Palette.TextDim,
                    request.PanelFill),
                PriorityResource);
        }
    }

    /// <summary>
    /// The zone labels. A zone nothing may enter is drawn dim, the same treatment a
    /// shut edge gets, because both are the same recorded fact about the map.
    /// </summary>
    private static void DrawZones(
        WorldRenderRequest request,
        IReadOnlyList<ZonePlacement> placements,
        Dictionary<int, Rgb> washes,
        Painter painter)
    {
        foreach (var placement in placements)
        {
            var shut = placement.Shut;
            washes.TryGetValue(painter.Key(placement.X, placement.Y), out var wash);
            var cell = new Cell(
                placement.Label[0],
                shut ? Theme.ClosedEdge : Palette.TextPrimary,
                wash is { } trail ? trail : request.PanelFill);

            if (painter.TryTake(placement.X, placement.Y, PriorityZone))
            {
                painter.Set(placement.X, placement.Y, cell);
            }
        }
    }

    /// <summary>
    /// The agents, each in its slot's accent. An agent at a zone is drawn on that
    /// zone's cell; one mid-crossing is drawn on its edge, as far along as its
    /// recorded countdown says, plus whatever fraction of a tick this redraw is.
    /// </summary>
    private static void DrawAgents(
        WorldRenderRequest request,
        Dictionary<int, ZonePlacement> byZone,
        Painter painter)
    {
        foreach (var agent in request.Frame.Agents)
        {
            var color = Theme.AgentSlot(agent.Slot);
            var glyph = GlyphModes.Glyph(
                agent.Transit is null ? Glyphs.FilledDiamond : Glyphs.HollowDiamond,
                request.Glyphs);

            if (agent.Transit is null)
            {
                if (byZone.TryGetValue(agent.ZoneId, out var placement)
                    && painter.TryTake(placement.X, placement.Y, PriorityAgent))
                {
                    painter.Set(
                        placement.X,
                        placement.Y,
                        new Cell(glyph, color, painter.Background(placement.X, placement.Y)));
                }

                continue;
            }

            if (!byZone.TryGetValue(agent.Transit.FromZoneId, out var from)
                || !byZone.TryGetValue(agent.Transit.ToZoneId, out var to))
            {
                continue;
            }

            var path = Line(from.X, from.Y, to.X, to.Y);
            var step = (int)(agent.Transit.CoveredFraction(request.Phase) * (path.Count - 1));
            step = step < 0 ? 0 : step > path.Count - 1 ? path.Count - 1 : step;
            var cell = path[step];

            if (painter.TryTake(cell.X, cell.Y, PriorityAgent))
            {
                painter.Set(cell.X, cell.Y, new Cell(glyph, color, painter.Background(cell.X, cell.Y)));
            }
        }
    }

    /// <summary>
    /// Blends <paramref name="color"/> towards the panel background by
    /// <paramref name="numerator"/> / <paramref name="denominator"/>, in integer
    /// channels. Integer because a trail has to fade identically on every runtime.
    /// </summary>
    private static Rgb Fade(Rgb color, int numerator, int denominator)
    {
        var keep = denominator - numerator;

        return new Rgb(
            (byte)(((color.R * keep) + (TrailFadeTarget.R * numerator)) / denominator),
            (byte)(((color.G * keep) + (TrailFadeTarget.G * numerator)) / denominator),
            (byte)(((color.B * keep) + (TrailFadeTarget.B * numerator)) / denominator));
    }

    /// <summary>
    /// The cells between two points, inclusive, by integer Bresenham. The raster
    /// the plain ASCII renderer already uses: no floating point is involved, so an
    /// edge crosses the same cells on every platform.
    /// </summary>
    internal static List<(int X, int Y)> Line(int x0, int y0, int x1, int y1)
    {
        var cells = new List<(int X, int Y)>();
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            cells.Add((x0, y0));
            if (x0 == x1 && y0 == y1)
            {
                return cells;
            }

            var doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// Map coordinates to pane cells: one integer division per axis, computed once
    /// per frame so every mark on the map lands through exactly the same
    /// transform. The spans are floored at one so a map with every point on the
    /// same row or column lands on the first cell rather than dividing by zero.
    /// </summary>
    private sealed class MapScale
    {
        private readonly long _minX;
        private readonly long _minY;
        private readonly long _spanX;
        private readonly long _spanY;

        internal MapScale(WorldMap map, PaneSize size)
        {
            Size = size;
            _minX = map.MinX;
            _minY = map.MinY;
            _spanX = Math.Max(1, (long)map.MaxX - map.MinX);
            _spanY = Math.Max(1, (long)map.MaxY - map.MinY);
        }

        internal PaneSize Size { get; }

        internal int Column(int x) => (int)(((long)x - _minX) * (Size.Width - 1) / _spanX);

        internal int Row(int y) => (int)(((long)y - _minY) * (Size.Height - 1) / _spanY);

        internal int Key(int column, int row) => (row * Size.Width) + column;
    }

    /// <summary>
    /// Writes cells under a fixed precedence: a pass may take a cell only from
    /// something less important, which is what makes "at most one mark per cell" a
    /// property of the renderer rather than of the order the passes happened to run
    /// in.
    /// </summary>
    private sealed class Painter
    {
        private readonly CellBuffer _cells;
        private readonly Dictionary<int, int> _held = new();
        private readonly PaneSize _size;

        internal Painter(CellBuffer cells, PaneSize size)
        {
            _cells = cells;
            _size = size;
        }

        internal int Key(int x, int y) => (y * _size.Width) + x;

        /// <summary>Reserves a cell so later passes have to outrank it.</summary>
        internal void Claim(int x, int y, int priority) => _held[Key(x, y)] = priority;

        /// <summary>Whether this cell currently holds something at that priority.</summary>
        internal bool Holds(int key, int priority) => _held.TryGetValue(key, out var held) && held == priority;

        /// <summary>
        /// Whether this pass may write the cell: it is inside the pane and holds
        /// nothing more important.
        /// </summary>
        internal bool TryTake(int x, int y, int priority)
        {
            if (!_size.Contains(x, y))
            {
                return false;
            }

            var key = Key(x, y);
            if (_held.TryGetValue(key, out var current) && current > priority)
            {
                return false;
            }

            _held[key] = priority;
            return true;
        }

        /// <summary>Paints one cell whose permission has already been taken.</summary>
        internal void Set(int x, int y, Cell cell) => _cells[x, y] = cell;

        /// <summary>The cell's own background, so a mark drawn over it keeps the fill beneath.</summary>
        internal Rgb? Background(int x, int y) => _cells[x, y].Background;

        /// <summary>Paints one cell, taking it only when this pass outranks what holds it.</summary>
        internal void TryPaint(int x, int y, Cell cell, int priority)
        {
            if (TryTake(x, y, priority))
            {
                _cells[x, y] = cell;
            }
        }

        /// <summary>
        /// Paints a string left to right from a cell, one column per character,
        /// stopping at the pane's right edge. Used for the one thing wider than a
        /// cell: a choke's capacity, which is a number rather than a mark.
        /// </summary>
        internal void TryPaintText(int x, int y, string text, Cell style, int priority)
        {
            for (var i = 0; i < text.Length; i++)
            {
                TryPaint(x + i, y, style with { Glyph = text[i] }, priority);
            }
        }
    }
}