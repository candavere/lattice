using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The world renderer's own decisions, asserted on hand-built maps so each claim
/// is about a shape the renderer cannot hide behind: where a zone lands, what a
/// shut edge looks like, where an agent mid-crossing is drawn, and that nothing
/// it draws can leave its pane.
/// </summary>
public class WorldRendererTests
{
    /// <summary>The pane the cockpit gives the world pane at the 100x30 minimum.</summary>
    private static readonly PaneSize CockpitPane = new(63, 23);

    [Fact]
    public void ZonesLandOnCellsTheirOwnCoordinatesScaleTo()
    {
        var (placements, _, unplaced) = WorldRenderer.Place(ThreeZoneMap(), CockpitPane);

        Assert.Empty(unplaced);
        Assert.Equal(
            new[]
            {
                new ZonePlacement(0, "0", 0, 0, false, false),
                new ZonePlacement(1, "1", 31, 22, false, false),
                new ZonePlacement(2, "2", 62, 11, false, false),
            },
            placements);
    }

    [Fact]
    public void TwoZonesThatScaleOntoOneCellAreSeparatedAndReported()
    {
        // Zones 1 and 2 share a map coordinate, so both scale onto the same cell.
        var map = new WorldMap(
            new[]
            {
                new WorldZone(0, "0", 0, 0),
                new WorldZone(1, "1", 10, 10),
                new WorldZone(2, "2", 10, 10),
            },
            Array.Empty<WorldResource>(),
            Array.Empty<WorldEdge>());

        var render = WorldRenderer.Render(Request(map, Frame(), CockpitPane));

        Assert.Equal(3, render.Zones.Count);
        Assert.Equal(new[] { 2 }, render.NudgedZoneIds);
        Assert.Equal(3, render.Zones.Select(zone => (zone.X, zone.Y)).Distinct().Count());
        Assert.False(render.Zones[0].Nudged);
        Assert.False(render.Zones[1].Nudged);
        Assert.True(render.Zones[2].Nudged);
    }

    [Fact]
    public void AZoneWithNoFreeCellIsNamedRatherThanDrawnOverAnother()
    {
        // Two cells, three zones: one zone cannot be placed at all.
        var map = new WorldMap(
            new[]
            {
                new WorldZone(0, "0", 0, 0),
                new WorldZone(1, "1", 10, 0),
                new WorldZone(2, "2", 20, 0),
            },
            Array.Empty<WorldResource>(),
            Array.Empty<WorldEdge>());

        var (placements, _, unplaced) = WorldRenderer.Place(map, new PaneSize(2, 1));

        Assert.Equal(new[] { 2 }, unplaced);
        Assert.Equal(2, placements.Count);
    }

    [Fact]
    public void AShutEdgeIsDrawnDimWithTheClosedMarkHalfWayAlong()
    {
        var cells = RenderStraightEdge(0);

        var mark = Assert.Single(Marks(cells, Glyphs.ClosedMark));
        Assert.Equal(31, mark.X);
        Assert.Equal(0, mark.Y);
        Assert.Equal(Theme.ClosedEdgeMark, cells[mark.X, mark.Y].Foreground);

        // The rest of the run is the line itself, dimmed.
        Assert.Equal(Glyphs.Horizontal, cells[10, 0].Glyph);
        Assert.Equal(Theme.ClosedEdge, cells[10, 0].Foreground);
    }

    [Fact]
    public void AFiniteCapacityChokeIsDrawnWithThatCapacityHalfWayAlong()
    {
        var cells = RenderStraightEdge(3);

        var capacity = Assert.Single(Marks(cells, '3'));
        Assert.Equal(31, capacity.X);
        Assert.Equal(Theme.ContestedChoke, cells[capacity.X, capacity.Y].Foreground);
    }

    [Fact]
    public void AnUncappedEdgeCarriesNoCapacityMark()
    {
        var cells = RenderStraightEdge(WorldMap.UnlimitedCapacity);

        Assert.Equal(Glyphs.Horizontal, cells[31, 0].Glyph);
        Assert.Equal(Theme.Sage, cells[31, 0].Foreground);
    }

    [Fact]
    public void AShutZoneIsDrawnDimJustAsAShutEdgeIs()
    {
        var map = new WorldMap(
            new[]
            {
                new WorldZone(0, "0", 0, 0),
                new WorldZone(1, "1", 20, 0, Capacity: 0),
            },
            Array.Empty<WorldResource>(),
            new[] { new WorldEdge(0, 0, 1, Capacity: 1) });

        var cells = WorldRenderer.Render(Request(map, Frame(), CockpitPane)).Cells;

        Assert.Equal(Theme.ClosedEdge, cells[62, 0].Foreground);
        Assert.Equal(Palette.TextPrimary, cells[0, 0].Foreground);
    }

    [Fact]
    public void AnAgentMidCrossingIsDrawnOnItsEdgeWhereItsCountdownSays()
    {
        // Three ticks of crossing with one still to run is two thirds of the way
        // along a 63-cell edge: cell 41 of 0..62.
        var cells = RenderTransit(remaining: 1, total: 3, phase: 0.0);

        Assert.Equal(Glyphs.HollowDiamond, cells[41, 0].Glyph);
        Assert.Equal(Theme.AgentSlot(0), cells[41, 0].Foreground);
        Assert.Equal(Glyphs.Horizontal, cells[10, 0].Glyph);
    }

    [Fact]
    public void TheWithinTickPhaseSlidesAnAgentForwardButNeverOffItsEdge()
    {
        var resting = RenderTransit(remaining: 1, total: 3, phase: 0.0);
        var almostThere = RenderTransit(remaining: 1, total: 3, phase: 0.99);

        Assert.Equal(Glyphs.HollowDiamond, resting[41, 0].Glyph);
        Assert.Equal(Glyphs.Horizontal, resting[7, 0].Glyph);
        Assert.Equal(Glyphs.HollowDiamond, almostThere[61, 0].Glyph);

        // The destination zone keeps its label: the agent reaches the far cell of
        // the edge and no further.
        Assert.Equal('1', almostThere[62, 0].Glyph);
    }

    [Fact]
    public void TheTrailFadesWithAgeAndNeverTakesAZonesLabel()
    {
        var map = ThreeZoneMap();
        var request = Request(map, Frame(new WorldAgent(0, 2, 0)), CockpitPane) with
        {
            TrailFrames = new[] { Frame(new WorldAgent(0, 0, 0)), Frame(new WorldAgent(0, 1, 0)) },
        };

        var cells = WorldRenderer.Render(request).Cells;

        // The zone the agent left a frame ago keeps its label and takes the trail
        // wash as its background instead.
        Assert.Equal('1', cells[31, 22].Glyph);
        Assert.Equal('0', cells[0, 0].Glyph);

        var oneStepOld = cells[31, 22].Background;
        var twoStepsOld = cells[0, 0].Background;
        Assert.NotNull(oneStepOld);
        Assert.NotNull(twoStepsOld);

        // Older is closer to the panel fill, which is the direction the fade goes.
        Assert.True(Distance(twoStepsOld!.Value, Palette.PanelBackground)
            < Distance(oneStepOld!.Value, Palette.PanelBackground));
        Assert.True(Distance(oneStepOld.Value, Theme.AgentSlot(0))
            < Distance(twoStepsOld.Value, Theme.AgentSlot(0)));
    }

    [Fact]
    public void AClaimedResourceIsDrawnFilledAndAnUnclaimedOneHollow()
    {
        var map = new WorldMap(
            new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 20, 20) },
            new[] { new WorldResource(7, 1, 10, 0), new WorldResource(8, 1, 20, 10) },
            Array.Empty<WorldEdge>());

        var cells = WorldRenderer.Render(Request(map, Frame(claims: new[] { 7 }), CockpitPane)).Cells;

        Assert.Equal(Glyphs.FilledCircle, cells[31, 0].Glyph);
        Assert.Equal(Palette.AccentBright, cells[31, 0].Foreground);
        Assert.Equal(Glyphs.HollowCircle, cells[62, 11].Glyph);
        Assert.Equal(Palette.TextDim, cells[62, 11].Foreground);
    }

    [Fact]
    public void TheAsciiGlyphSetNeverEmitsANonAsciiCell()
    {
        var frame = Frame(
            new WorldAgent(0, 0, 1),
            new WorldAgent(1, 2, 0, new WorldTransit(1, 0, RemainingTicks: 1, TotalTicks: 2)));

        var cells = WorldRenderer.Render(
            Request(ThreeZoneMap(), frame, CockpitPane) with { Glyphs = GlyphMode.Ascii }).Cells;

        foreach (var line in cells.ToLines())
        {
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }
    }

    [Theory]
    [InlineData(ColorDepth.None)]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.TrueColor)]
    public void EveryColourDepthRendersIntoThePaneAndNoColourWritesNoEscape(ColorDepth depth)
    {
        var request = Request(ThreeZoneMap(), Frame(new WorldAgent(1, 1, 2)), CockpitPane) with
        {
            PanelFill = Theme.PanelFill(depth),
        };

        var rendered = FrameDiff.Render(previous: null, WorldRenderer.Render(request).Cells, depth);

        // Every frame carries cursor positioning, at every depth: that is how a run
        // is placed. What must not appear without colour is an SGR, so that is what
        // is asserted.
        if (depth == ColorDepth.None)
        {
            Assert.DoesNotMatch(SelectGraphicRendition, rendered);
        }
        else
        {
            Assert.Matches(SelectGraphicRendition, rendered);
        }
    }

    /// <summary>Any SGR sequence: escape, digits and semicolons, then <c>m</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex SelectGraphicRendition = new(
        "\\[[0-9;]*m",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    [InlineData(63, 23)]
    public void EveryMarkStaysInsideThePaneTheRendererWasGiven(int width, int height)
    {
        var frame = Frame(
            new WorldAgent(0, 0, 0, new WorldTransit(0, 2, RemainingTicks: 1, TotalTicks: 4)),
            new WorldAgent(1, 2, 1));

        var render = WorldRenderer.Render(Request(ThreeZoneMap(), frame, new PaneSize(width, height)));

        Assert.Equal(width, render.Cells.Width);
        Assert.Equal(height, render.Cells.Height);
        Assert.All(
            render.Zones,
            zone =>
            {
                Assert.InRange(zone.X, 0, width - 1);
                Assert.InRange(zone.Y, 0, height - 1);
            });
    }

    [Fact]
    public void AMapWithNoZonesIsRejectedBeforeAnythingIsDrawn()
    {
        Assert.Throws<ArgumentException>(() => new WorldMap(
            Array.Empty<WorldZone>(),
            Array.Empty<WorldResource>(),
            Array.Empty<WorldEdge>()));
    }

    [Fact]
    public void APaneWithNoCellsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaneSize(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaneSize(10, 0));
    }

    /// <summary>
    /// A two-zone map with a single straight edge across the top row, carrying the
    /// given capacity. The edge is 63 cells long, so its midpoint is column 31.
    /// </summary>
    private static CellBuffer RenderStraightEdge(int capacity)
    {
        var map = new WorldMap(
            new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 20, 0) },
            Array.Empty<WorldResource>(),
            new[] { new WorldEdge(0, 0, 1, capacity) });

        return WorldRenderer.Render(Request(map, Frame(), CockpitPane)).Cells;
    }

    private static CellBuffer RenderTransit(int remaining, int total, double phase)
    {
        var map = new WorldMap(
            new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 20, 0) },
            Array.Empty<WorldResource>(),
            new[] { new WorldEdge(0, 0, 1) });

        var frame = Frame(new WorldAgent(0, 0, 0, new WorldTransit(0, 1, remaining, total)));

        return WorldRenderer.Render(Request(map, frame, CockpitPane) with { Phase = phase }).Cells;
    }

    private static WorldMap ThreeZoneMap() => new(
        new[]
        {
            new WorldZone(0, "0", 0, 0),
            new WorldZone(1, "1", 10, 10),
            new WorldZone(2, "2", 20, 5),
        },
        Array.Empty<WorldResource>(),
        new[] { new WorldEdge(0, 0, 1), new WorldEdge(1, 1, 2) });

    private static ReplayFrame Frame(params WorldAgent[] agents) =>
        Frame(agents, Array.Empty<int>());

    private static ReplayFrame Frame(WorldAgent[] agents, int[] claims) =>
        new(1, isStart: false, agents, claims, "agent0: Wait", stateDigest: null, false, null, null);

    private static ReplayFrame Frame(int[] claims, params WorldAgent[] agents) =>
        Frame(agents, claims);

    private static WorldRenderRequest Request(WorldMap map, ReplayFrame frame, PaneSize size) =>
        new(map, frame, Array.Empty<ReplayFrame>(), size, GlyphMode.Unicode, Palette.PanelBackground);

    /// <summary>Every cell holding one particular glyph, with its coordinates.</summary>
    private static List<(int X, int Y)> Marks(CellBuffer cells, char glyph)
    {
        var marks = new List<(int X, int Y)>();
        for (var y = 0; y < cells.Height; y++)
        {
            for (var x = 0; x < cells.Width; x++)
            {
                if (cells[x, y].Glyph == glyph)
                {
                    marks.Add((x, y));
                }
            }
        }

        return marks;
    }

    private static int Distance(Rgb left, Rgb right) => left.DistanceSquaredTo(right);
}