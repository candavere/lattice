using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The cockpit's arrangement: which pane sits where, what each pane says, and
/// what a terminal too small for the whole thing gets instead.
/// </summary>
public class CockpitLayoutTests
{
    private static readonly PaneSize Cockpit = new(100, 30);

    [Fact]
    public void EveryPaneIsFramedWhereTheLayoutSaysItIs()
    {
        var cells = Render(Cockpit, 3);

        // The screen's own frame, and the four panes inside it, each opening on a
        // rounded corner at its documented top-left.
        Assert.Equal(BorderGlyphs.Rounded.TopLeft, cells[0, 0].Glyph);
        foreach (var (x, y) in new[] { (1, 1), (67, 1), (67, 13), (1, 26) })
        {
            Assert.Equal(BorderGlyphs.Rounded.TopLeft, cells[x, y].Glyph);
        }

        // The key hints ride on the screen's own bottom border, which is why the frame is
        // still there and the panes above it are not.
        Assert.Equal(BorderGlyphs.Rounded.BottomLeft, cells[0, CockpitLayout.KeyHintRow(Cockpit)].Glyph);
        Assert.Equal(BorderGlyphs.Rounded.BottomRight, cells[99, CockpitLayout.KeyHintRow(Cockpit)].Glyph);
        Assert.Equal(1, CockpitLayout.KeyHintRowFromBottom);
    }

    [Fact]
    public void TheWorldPaneHoldsTheRendererOutputAtTheSizeTheLayoutGivesIt()
    {
        var document = Document();
        var cockpit = Render(Cockpit, 3, document);
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[3],
            document.TrailBefore(3, 3),
            new PaneSize(63, 23),
            GlyphMode.Unicode,
            Palette.PanelBackground));

        // The pane is the renderer's grid, moved into the cockpit rather than drawn
        // again: a change to one is a change to both.
        for (var y = 0; y < 23; y++)
        {
            for (var x = 0; x < 63; x++)
            {
                Assert.Equal(world.Cells[x, y], cockpit[x + 2, y + 2]);
            }
        }
    }

    [Fact]
    public void TheScoreboardShowsTheRecordedScoresRolesAndClaims()
    {
        var rows = Rows(Render(Cockpit, 3));

        Assert.Contains(rows, row => row.Contains("Sentry", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("Infiltrator", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("tick", StringComparison.Ordinal) && row.Contains("3/6", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("claims", StringComparison.Ordinal) && row.Contains("1/2", StringComparison.Ordinal));
    }

    [Fact]
    public void APaneThatWantsADigestTheRecordingDoesNotCarrySaysNotRecorded()
    {
        var rows = Rows(Render(Cockpit, 3));
        var digest = Assert.Single(rows, row => row.Contains("digest", StringComparison.Ordinal));

        Assert.Contains("not recorded", digest, StringComparison.Ordinal);

        var recorded = Rows(Render(Cockpit, 4));
        Assert.Contains(
            "digest",
            Assert.Single(recorded, row => row.Contains("digest", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        Assert.Contains(
            "0000000000ff",
            Assert.Single(recorded, row => row.Contains("digest", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEventLogShowsTheRecordedActionsOfTheTickOnShow()
    {
        var rows = Rows(Render(Cockpit, 3));

        Assert.Contains(rows, row => row.Contains("agent0: Move(1)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTimelineShowsTheRecordedTickOverTheRecordedStepCount()
    {
        var rows = Rows(Render(Cockpit, 3));
        var timeline = Assert.Single(rows, row => row.Contains("steps/s", StringComparison.Ordinal));

        Assert.Contains("tick 3/6", timeline, StringComparison.Ordinal);
        Assert.Contains("█", timeline, StringComparison.Ordinal);
        Assert.Contains("░", timeline, StringComparison.Ordinal);
        Assert.Contains("paused", timeline, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimelineFillsInProportionAsTheRecordingIsScrubbed()
    {
        var start = Rows(Render(Cockpit, 0)).Single(row => row.Contains("steps/s", StringComparison.Ordinal));
        var middle = Rows(Render(Cockpit, 3)).Single(row => row.Contains("steps/s", StringComparison.Ordinal));
        var end = Rows(Render(Cockpit, 6)).Single(row => row.Contains("steps/s", StringComparison.Ordinal));

        Assert.Equal(0, start.Count(c => c == '█'));
        Assert.True(middle.Count(c => c == '█') > start.Count(c => c == '█'));
        Assert.True(middle.Count(c => c == '░') > end.Count(c => c == '░'));

        // One cell per recorded tick, so the bar is a ruler the reader can count.
        Assert.Equal(3, middle.Count(c => c == '█'));
        Assert.Equal(6, end.Count(c => c == '█'));
    }

    [Fact]
    public void TheKeyHintRowIsAsciiAndNamesEveryControl()
    {
        var row = Hints(Render(Cockpit, 0));

        Assert.All(row, glyph => Assert.True(glyph < 128, $"'{glyph}' is not ASCII."));
        foreach (var key in new[] { "space", "step", "speed", "scrub", "quit" })
        {
            Assert.Contains(key, row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKeyHintRowIsTheSameInAsciiAndUnicodeBecauseItIsAscii()
    {
        Assert.Equal(Hints(Render(Cockpit, 0)), Hints(Render(Cockpit, 0, glyphs: GlyphMode.Ascii)));
    }

    /// <summary>
    /// The hint row's own text, without the frame's own border glyph at either end:
    /// the border follows the glyph vocabulary and the hints do not.
    /// </summary>
    private static string Hints(CellBuffer cells) =>
        string.Concat(
            cells.ToLines()[CockpitLayout.KeyHintRow(Cockpit)]
                .Skip(2)
                .Take(CockpitLayout.KeyHints.Length));

    [Theory]
    [InlineData(99, 30)]
    [InlineData(100, 29)]
    [InlineData(80, 25)]
    [InlineData(40, 12)]
    public void ATerminalTooSmallForTheCockpitGetsOnePaneAndTheResizeNotice(int width, int height)
    {
        var cells = Render(new PaneSize(width, height), 3);
        var rows = Rows(cells);

        // The notice leads with the size it measured, so it survives being clipped
        // to a narrow terminal, and it is the only line that says what is missing.
        Assert.Contains($"terminal is {width}x{height}", rows[1], StringComparison.Ordinal);

        // One pane, not four: the other three panes are simply not drawn.
        var drawn = string.Join("\n", rows);
        Assert.DoesNotContain("SCOREBOARD", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT LOG", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("TIMELINE", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResizeNoticeNamesTheSizeTheCockpitNeeds()
    {
        var row = Rows(Render(new PaneSize(80, 25), 0))[1];

        Assert.Contains(
            $"the cockpit needs {CockpitLayout.MinimumWidth}x{CockpitLayout.MinimumHeight}",
            row,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(39, 11)]
    public void ATerminalTooSmallEvenForTheFallbackDrawsTheFrameAndTheNoticeAndNoPane(int width, int height)
    {
        var size = new PaneSize(width, height);
        var cells = Render(size, 0);

        Assert.Equal(width, cells.Width);
        Assert.Equal(height, cells.Height);
        Assert.False(CockpitLayout.IsCockpit(size));

        if (height >= 4 && width >= 24)
        {
            Assert.Contains($"terminal is {width}x{height}", Rows(cells)[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFallbackPaneIsTheWorldAtItsOwnSize()
    {
        var document = Document();
        var cells = Render(new PaneSize(80, 25), 3, document);

        // The single pane the fallback shows is the same world, at the size the
        // terminal can give it.
        Assert.Contains("WORLD", Rows(cells)[3], StringComparison.Ordinal);
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[3],
            document.TrailBefore(3, 3),
            new PaneSize(76, 17),
            GlyphMode.Unicode,
            Palette.PanelBackground));

        for (var y = 0; y < 17; y++)
        {
            for (var x = 0; x < 76; x++)
            {
                Assert.Equal(world.Cells[x, y], cells[x + 2, y + 4]);
            }
        }
    }

    [Fact]
    public void EveryCockpitIsExactlyAsBigAsTheTerminalAndNothingIsDrawnOutsideIt()
    {
        foreach (var size in new[] { Cockpit, new PaneSize(100, 30), new PaneSize(120, 40), new PaneSize(80, 25) })
        {
            var cells = Render(size, 3);

            Assert.Equal(size.Width, cells.Width);
            Assert.Equal(size.Height, cells.Height);
        }
    }

    private static string[] Rows(CellBuffer cells) => cells.ToLines();

    private static CellBuffer Render(
        PaneSize size,
        int index,
        ReplayDocument? document = null,
        GlyphMode glyphs = GlyphMode.Unicode) =>
        CockpitLayout.Render(new CockpitRequest(
            document ?? Document(),
            index,
            size,
            glyphs,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0)));

    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[]
                {
                    new WorldAgent(0, 0, tick),
                    new WorldAgent(1, 1, tick / 2, tick == 2 ? new WorldTransit(1, 2, 1, 2) : null),
                },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : "agent0: Move(1); agent1: Collect(0)",
                stateDigest: tick == 4 ? "0000000000ff" + new string('0', 52) : null,
                isTerminal: tick == 6,
                terminalReason: tick == 6 ? "tick-limit" : null,
                winnerSlot: tick == 6 ? 0 : null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[]
                {
                    new WorldZone(0, "0", 0, 0, Role: "EntryHall"),
                    new WorldZone(1, "1", 10, 0, Role: "Corridor"),
                    new WorldZone(2, "2", 20, 10, Role: "Vault"),
                },
                new[] { new WorldResource(0, 0, 2, 2), new WorldResource(1, 1, 12, 2) },
                new[] { new WorldEdge(0, 0, 1, Capacity: 1), new WorldEdge(1, 1, 2, Capacity: 2) }),
            new ReplayHeader(42, 5, "dungeon", new[] { "Sentry", "Infiltrator" }, 6, null, true),
            frames);
    }
}