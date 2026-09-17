using System.Globalization;
using System.Text;
using Lattice.Environment;

namespace Lattice.Visualization;

/// <summary>
/// Renders a lattice state as a standalone, self-contained SVG. Like
/// the ASCII renderer, it is strictly a reader: every element it emits (zone
/// circles, choke edges, claim-colored resource nodes, color-coded agent
/// tokens) is already present in the state it is handed, and it never
/// re-derives game logic. Output is a deterministic pure function of its
/// inputs — identical states produce byte-identical documents, both pinned by
/// tests and produced with zero NuGet dependencies (pure StringBuilder).
/// Coordinates are projected by a fixed scale so the same map always lands on
/// the same pixels; zone and resource ids are embedded in each element's
/// &lt;title&gt; so hoverable tooltips carry coordinates and identity.
/// </summary>
public static class SvgRenderer
{
    /// <summary>Screen pixels per map unit; fixed so rendering is reproducible.</summary>
    public const double UnitPixels = 20;

    /// <summary>Screen pixels of empty margin on every side of the content.</summary>
    public const double PaddingPixels = 40;

    /// <summary>SVG fill of the document background rect.</summary>
    public const string BackgroundFill = "#0F111A";

    /// <summary>Stroke of choke-point edges.</summary>
    public const string EdgeStroke = "#8A8678";

    /// <summary>Fill of a zone circle.</summary>
    public const string ZoneFill = "#10131F";

    /// <summary>Stroke (rim) of a zone circle.</summary>
    public const string ZoneStroke = "#7AA2F7";

    /// <summary>Fill of a zone's id label text.</summary>
    public const string ZoneTextFill = "#C0CAF5";

    /// <summary>Fill of a still-unclaimed resource node.</summary>
    public const string UnclaimedResourceFill = "#E0AF68";

    /// <summary>Fill of an already-claimed resource node.</summary>
    public const string ClaimedResourceFill = "#3DA66B";

    /// <summary>Stroke (rim) of an agent token.</summary>
    public const string AgentStroke = "#FFFFFF";

    /// <summary>
    /// Per-agent fill colors, indexed by AgentId so each agent keeps a stable,
    /// distinct color across every frame of a replay.
    /// </summary>
    public static readonly string[] AgentPalette = { "#F7768E", "#BB9AF7", "#73DACA", "#FF9E64" };

    /// <summary>
    /// Computes the canvas dimensions for <paramref name="map"/> under the
    /// fixed unit scale and padding, so a caller can size its own &lt;svg&gt;
    /// frame identically to <see cref="RenderFrame"/>. Throws on an empty
    /// map (no content to project), mirroring the ASCII renderer's guard.
    /// </summary>
    public static SvgViewport CreateViewport(MapGraph map)
    {
        var (minX, maxX, minY, maxY) = Bounds(map);
        var width = (maxX - minX) * UnitPixels + 2 * PaddingPixels;
        var height = (maxY - minY) * UnitPixels + 2 * PaddingPixels;
        return new SvgViewport(width, height);
    }

    /// <summary>
    /// Renders one complete, standalone SVG document describing the given
    /// (agents, claims) state on <paramref name="map"/>. Chartable in any
    /// browser or image viewer with no external files, stylesheets, or fonts.
    /// </summary>
    public static string RenderFrame(MapGraph map, AgentState[] agents, int[] claims) =>
        RenderFrame(map, agents, claims, null);

    /// <summary>
    /// Tactical overload: <paramref name="agentRoles"/> is the trajectory
    /// header roster (see <see cref="TrajectoryModel.TrajectoryHeader"/>),
    /// indexed by agent id, appended to each token's tooltip so the guard and
    /// the rogue are identifiable by role. Purely header metadata — with null
    /// roles the document is byte-identical to the plain overload.
    /// </summary>
    public static string RenderFrame(MapGraph map, AgentState[] agents, int[] claims, string[]? agentRoles)
    {
        if (agents is null)
        {
            throw new ArgumentNullException(nameof(agents));
        }

        if (claims is null)
        {
            throw new ArgumentNullException(nameof(claims));
        }

        var viewport = CreateViewport(map);
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"")
            .Append(Num(viewport.Width))
            .Append("\" height=\"")
            .Append(Num(viewport.Height))
            .Append("\" viewBox=\"")
            .Append(viewport.ToViewBox())
            .Append("\">\n");
        builder.Append("<rect width=\"").Append(Num(viewport.Width)).Append("\" height=\"")
            .Append(Num(viewport.Height)).Append("\" fill=\"").Append(BackgroundFill).Append("\"/>\n");
        builder.Append(RenderScene(map, agents, claims, agentRoles));
        builder.Append("</svg>");
        return builder.ToString();
    }

    /// <summary>
    /// Convenience overload rendering the exact state an agent observed this
    /// tick, so a replay loop can render each frame from the step's recorded
    /// <see cref="Observation"/> directly.
    /// </summary>
    public static string RenderFrame(MapGraph map, Observation observation) =>
        RenderFrame(map, observation.AgentStates, observation.Claims);

    /// <summary>
    /// Convenience overload that threads a trajectory's header roster onto the
    /// frame the agent observed this tick.
    /// </summary>
    public static string RenderFrame(MapGraph map, Observation observation, string[]? agentRoles) =>
        RenderFrame(map, observation.AgentStates, observation.Claims, agentRoles);

    /// <summary>
    /// The layer markup shared by single-frame and trajectory exports: an
    /// &lt;edges&gt; group of choke lines, an &lt;resources&gt; group of
    /// claim-colored circles, a &lt;zones&gt; group of rimmed circles with
    /// id labels and coordinate/id tooltips, and an &lt;agents&gt; group of
    /// color-coded tokens on top. Every element is emitted in ascending id
    /// order so the document text never depends on map array order. Zone,
    /// choke, and resource roles (see <see cref="Environment.Zone.Role"/>,
    /// <see cref="Environment.ChokePoint.Role"/>, <see cref="Environment.ResourceNode.Role"/>)
    /// are demonstration-layer metadata: rooms and gates name themselves in
    /// their tooltips, and the agent roster threads through
    /// <paramref name="agentRoles"/>. The resulting markup is valid inside any
    /// &lt;svg&gt; element.
    /// </summary>
    public static string RenderScene(MapGraph map, AgentState[] agents, int[] claims) =>
        RenderScene(map, agents, claims, null);

    /// <summary>
    /// Tactical overlay of <see cref="RenderScene(MapGraph, AgentState[], int[])"/>:
    /// identical output when <paramref name="agentRoles"/> is null, role-tagged
    /// agent tooltips otherwise.
    /// </summary>
    public static string RenderScene(MapGraph map, AgentState[] agents, int[] claims, string[]? agentRoles)
    {
        var (minX, _maxX, minY, _maxY) = Bounds(map);
        double Px(int x) => (x - minX) * UnitPixels + PaddingPixels;
        double Py(int y) => (y - minY) * UnitPixels + PaddingPixels;
        var zoneById = map.Zones.ToDictionary(z => z.Id, z => z.Position);

        var builder = new StringBuilder();

        builder.Append("<g id=\"edges\">\n");
        foreach (var choke in map.ChokePoints.OrderBy(c => c.Id))
        {
            var from = zoneById[choke.FromZoneId];
            var to = zoneById[choke.ToZoneId];
            var gateRole = choke.Role is null ? "" : $" - {choke.Role} ({choke.FromZoneId}->{choke.ToZoneId})";
            builder.Append("<g id=\"choke-").Append(choke.Id).Append("\">")
                .Append("<title>choke ").Append(choke.Id).Append(gateRole).Append("</title>");
            builder.Append("<line x1=\"").Append(Num(Px(from.X)))
                .Append("\" y1=\"").Append(Num(Py(from.Y)))
                .Append("\" x2=\"").Append(Num(Px(to.X)))
                .Append("\" y2=\"").Append(Num(Py(to.Y)))
                .Append("\" stroke=\"").Append(EdgeStroke)
                .Append("\" stroke-width=\"5\" stroke-linecap=\"round\"/>\n");
            builder.Append("</g>\n");
        }

        builder.Append("</g>\n");

        builder.Append("<g id=\"resources\">\n");
        foreach (var resource in map.Resources.OrderBy(r => r.Id))
        {
            var fill = claims.Contains(resource.Id) ? ClaimedResourceFill : UnclaimedResourceFill;
            if (resource.Role is null)
            {
                builder.Append("<circle cx=\"").Append(Num(Px(resource.Position.X)))
                    .Append("\" cy=\"").Append(Num(Py(resource.Position.Y)))
                    .Append("\" r=\"5\" fill=\"").Append(fill).Append("\"/>\n");
                continue;
            }

            builder.Append("<g id=\"resource-").Append(resource.Id).Append("\">")
                .Append("<title>resource ").Append(resource.Id)
                .Append(" - ").Append(resource.Role)
                .Append(" in zone ").Append(resource.ZoneId).Append("</title>");
            builder.Append("<circle cx=\"").Append(Num(Px(resource.Position.X)))
                .Append("\" cy=\"").Append(Num(Py(resource.Position.Y)))
                .Append("\" r=\"5\" fill=\"").Append(fill).Append("\"/>\n");
            builder.Append("</g>\n");
        }

        builder.Append("</g>\n");

        builder.Append("<g id=\"zones\">\n");
        foreach (var zone in map.Zones.OrderBy(z => z.Id))
        {
            var cx = Num(Px(zone.Position.X));
            var cy = Num(Py(zone.Position.Y));
            builder.Append("<g id=\"zone-").Append(zone.Id).Append("\">")
                .Append("<title>zone ").Append(zone.Id)
                .Append(zone.Role is null ? "" : " - " + zone.Role)
                .Append(" (").Append(zone.Position.X).Append(", ").Append(zone.Position.Y).Append(")</title>")
                .Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy)
                .Append("\" r=\"12\" fill=\"").Append(ZoneFill).Append("\" stroke=\"").Append(ZoneStroke)
                .Append("\" stroke-width=\"2\"/>")
                .Append("<text x=\"").Append(cx).Append("\" y=\"").Append(cy)
                .Append("\" text-anchor=\"middle\" dominant-baseline=\"central\" font-family=\"monospace\" font-size=\"13\" fill=\"")
                .Append(ZoneTextFill).Append("\">").Append(zone.Id).Append("</text>")
                .Append("</g>\n");
        }

        builder.Append("</g>\n");

        builder.Append("<g id=\"agents\">\n");
        foreach (var agent in agents.OrderBy(a => a.AgentId))
        {
            var zone = zoneById[agent.ZoneId];
            var cx = Num(Px(zone.X));
            var cy = Num(Py(zone.Y));
            var fill = AgentPalette[agent.AgentId % AgentPalette.Length];
            var role = agentRoles is not null && agent.AgentId < agentRoles.Length && !string.IsNullOrEmpty(agentRoles[agent.AgentId])
                ? agentRoles[agent.AgentId]
                : null;
            builder.Append("<g id=\"agent-").Append(agent.AgentId).Append("\">")
                .Append("<title>agent ").Append(agent.AgentId)
                .Append(role is null ? "" : $", {role}")
                .Append(", zone ").Append(agent.ZoneId)
                .Append(", score ").Append(agent.Score).Append("</title>")
                .Append("<circle cx=\"").Append(cx).Append("\" cy=\"").Append(cy)
                .Append("\" r=\"7\" fill=\"").Append(fill).Append("\" stroke=\"").Append(AgentStroke)
                .Append("\" stroke-width=\"1.5\"/>")
                .Append("<text x=\"").Append(cx).Append("\" y=\"").Append(cy)
                .Append("\" text-anchor=\"middle\" dominant-baseline=\"central\" font-family=\"monospace\" font-size=\"9\" fill=\"")
                .Append(AgentStroke).Append("\">").Append(agent.AgentId).Append("</text>")
                .Append("</g>\n");
        }

        builder.Append("</g>\n");

        return builder.ToString();
    }

    internal static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (int MinX, int MaxX, int MinY, int MaxY) Bounds(MapGraph map)
    {
        if (map.Zones.Length == 0 && map.Resources.Length == 0)
        {
            throw new ArgumentException("Cannot render a map with no zones and no resources.", nameof(map));
        }

        var points = map.Zones.Select(z => z.Position).Concat(map.Resources.Select(r => r.Position)).ToList();
        return (points.Min(p => p.X), points.Max(p => p.X), points.Min(p => p.Y), points.Max(p => p.Y));
    }
}