namespace Lattice.Tui;

/// <summary>
/// One zone of a replayed map, in the form a drawing needs: where it sits on the
/// map plane, the single character that names it in the world pane, and the
/// capacity that decides whether the zone can be entered at all.
/// </summary>
/// <remarks>
/// <para>
/// This is the renderer's own read-only projection of a zone rather than the
/// simulation's zone type. The split is deliberate: this library holds no
/// reference to the environment, so a recording is read and projected once, at
/// the edge, and everything downstream of that point — placement, drawing, the
/// cockpit — works against these values alone.
/// </para>
/// <para>
/// <see cref="Label"/> is supplied by the projection rather than derived here,
/// because deciding how many zones can each own a single character is a property
/// of a particular recording, and a map with more zones than there are characters
/// has to be refused where the recording is read rather than silently aliased at
/// draw time.
/// </para>
/// </remarks>
/// <param name="Id">The zone's identity, carried through from the recording.</param>
/// <param name="Label">The one character the world pane draws for the zone.</param>
/// <param name="X">The zone's map-plane X coordinate.</param>
/// <param name="Y">The zone's map-plane Y coordinate.</param>
/// <param name="Capacity">
/// How many agents the zone admits at once, or <see cref="WorldMap.UnlimitedCapacity"/>.
/// A zone nothing may enter is drawn as dim, the same as a shut edge.
/// </param>
/// <param name="Role">The recorded room tag, when the recording carries one.</param>
public sealed record WorldZone(
    int Id,
    string Label,
    int X,
    int Y,
    int Capacity = WorldMap.UnlimitedCapacity,
    string? Role = null);

/// <summary>
/// One collectable, in the form a drawing needs: which zone owns it and where it
/// sits on the map plane. <see cref="Role"/> is the recorded tag when there is
/// one; the world pane does not read it, so it is carried for the panes that do.
/// </summary>
/// <param name="Id">The resource's identity, which is also what a claim records.</param>
/// <param name="ZoneId">The zone the resource belongs to.</param>
/// <param name="X">The resource's map-plane X coordinate.</param>
/// <param name="Y">The resource's map-plane Y coordinate.</param>
/// <param name="Role">The recorded object tag, when the recording carries one.</param>
public sealed record WorldResource(int Id, int ZoneId, int X, int Y, string? Role = null);

/// <summary>
/// One traversable connection between two zones, in the form a drawing needs:
/// the capacity that gates it and the recorded tag, if any.
/// </summary>
/// <remarks>
/// Capacity is the whole of what the world pane says about an edge, and it says
/// it in three shapes: <see cref="WorldMap.UnlimitedCapacity"/> draws the edge
/// and nothing else, a finite capacity draws the capacity at the edge's midpoint,
/// and zero draws the edge dimmed with the closed mark over it. What the pane
/// shows is the capacity the recording's own map declares; a map recorded under a
/// dynamic-topology schedule has per-tick overrides the recording does not carry
/// as values, and those panes are told so rather than guessed at.
/// </remarks>
/// <param name="Id">The edge's identity within the recording's choke list.</param>
/// <param name="FromZoneId">One end of the edge.</param>
/// <param name="ToZoneId">The other end.</param>
/// <param name="Capacity">
/// How many agents may hold the edge at once, or <see cref="WorldMap.UnlimitedCapacity"/>.
/// </param>
/// <param name="Role">The recorded gate tag, when the recording carries one.</param>
public sealed record WorldEdge(
    int Id,
    int FromZoneId,
    int ToZoneId,
    int Capacity = WorldMap.UnlimitedCapacity,
    string? Role = null);

/// <summary>
/// The whole replayed map: the nodes, the collectables, and the connections
/// between them, as plain values, together with the bounds of every point the
/// world pane will draw.
/// </summary>
/// <remarks>
/// The bounds are computed from every drawn point — zones <em>and</em> resources
/// — so scaling the map into a pane can never place a recorded mark outside it.
/// Using the zones alone would leave a resource that sits away from its zone off
/// the pane, and clamping it back would put it somewhere the recording does not
/// say it is. The bounds are computed rather than supplied so a caller cannot
/// state them wrongly and have every later placement inherit the mistake.
/// </remarks>
public sealed record WorldMap
{
    /// <summary>The capacity value that means "no bound at all".</summary>
    public const int UnlimitedCapacity = int.MaxValue;

    /// <summary>
    /// Builds a map and measures it, rejecting the one shape a drawing cannot
    /// represent: an empty zone list, where there is no graph to place and every
    /// later decision about scaling would divide by an empty span.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="zones"/> is empty.</exception>
    public WorldMap(WorldZone[] zones, WorldResource[] resources, WorldEdge[] edges)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(edges);

        if (zones.Length == 0)
        {
            throw new ArgumentException("A replayed map needs at least one zone.", nameof(zones));
        }

        Zones = zones;
        Resources = resources;
        Edges = edges;

        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;

        foreach (var zone in zones)
        {
            minX = Math.Min(minX, zone.X);
            maxX = Math.Max(maxX, zone.X);
            minY = Math.Min(minY, zone.Y);
            maxY = Math.Max(maxY, zone.Y);
        }

        foreach (var resource in resources)
        {
            minX = Math.Min(minX, resource.X);
            maxX = Math.Max(maxX, resource.X);
            minY = Math.Min(minY, resource.Y);
            maxY = Math.Max(maxY, resource.Y);
        }

        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
    }

    /// <summary>The zones, in the order the recording lists them.</summary>
    public WorldZone[] Zones { get; }

    /// <summary>The collectables, in the order the recording lists them.</summary>
    public WorldResource[] Resources { get; }

    /// <summary>The connections between zones, in the order the recording lists them.</summary>
    public WorldEdge[] Edges { get; }

    /// <summary>The smallest X of any point the world pane draws.</summary>
    public int MinX { get; }

    /// <summary>The largest X of any point the world pane draws.</summary>
    public int MaxX { get; }

    /// <summary>The smallest Y of any point the world pane draws.</summary>
    public int MinY { get; }

    /// <summary>The largest Y of any point the world pane draws.</summary>
    public int MaxY { get; }

    /// <summary>The zone with this id, or <c>null</c> when the recording names no such zone.</summary>
    public WorldZone? Zone(int id)
    {
        foreach (var zone in Zones)
        {
            if (zone.Id == id)
            {
                return zone;
            }
        }

        return null;
    }
}