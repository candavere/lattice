using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// What a pane does with a row that came from a recording and is longer than the
/// pane: the row must be marked as cut, and the columns after the cut must still
/// be there.
/// </summary>
/// <remarks>
/// <para>
/// The expected strings here are written out column by column from the values
/// the document carries, rather than by calling the clipper the panes call, so a
/// test cannot pass because the two agree with each other.
/// </para>
/// </remarks>
public class PaneRowClippingTests
{
    /// <summary>The minimum terminal the whole cockpit is drawn for.</summary>
    private static readonly PaneSize Cockpit = new(100, 30);

    /// <summary>
    /// The first column of the right-hand column of panes: the world pane's right
    /// border, from the layout's own geometry at the minimum terminal.
    /// </summary>
    private const int PaneLeft = 67;

    /// <summary>The first column of a pane's contents, one inside its border.</summary>
    private const int InnerLeft = PaneLeft + 1;

    /// <summary>The event log pane's top row.</summary>
    private const int EventLogTop = 13;

    /// <summary>The width inside any pane on the right-hand column.</summary>
    private const int PaneInnerWidth = 30;

    /// <summary>
    /// Where each scoreboard column starts, counted from the pane's own left: a
    /// glyph and a space, then the slot, role, zone and score columns.
    /// </summary>
    private const int SlotColumnStart = 2;

    /// <summary>Where the role column starts, after the glyph and the slot column.</summary>
    private const int RoleColumnStart = SlotColumnStart + 5;

    /// <summary>Where the zone column starts, after the role column.</summary>
    private const int ZoneColumnStart = RoleColumnStart + 12;

    /// <summary>Where the score column starts, after the zone column.</summary>
    private const int ScoreColumnStart = ZoneColumnStart + 6;

    /// <summary>Columns the scoreboard's role column occupies.</summary>
    private const int RoleColumnWidth = 12;

    [Fact]
    public void ALongActionRowIsMarkedAndStaysInsideItsPane()
    {
        const string Actions = "agent0: Move(1); agent1: Collect(0); agent0: Move(2); agent1: Wait";
        var cells = Render(Document(actions: Actions), frameIndex: 3);

        // The log pane's inner area is thirty columns. Five of them are the tick
        // and its padding, so the action list gets twenty-five: twenty-four of
        // text and one marker column.
        var expected = "  3  " + Actions[..24] + "\u2026";
        var row = cells.ToLines()[EventLogTop + 3];

        Assert.Equal(30, expected.Length);
        Assert.Equal(expected, row.Substring(InnerLeft, PaneInnerWidth));

        // The mark is at the cut, and both of the pane's own borders are still
        // borders: a row that overwrote them would have been a row nothing cut.
        Assert.Equal('\u2026', row[InnerLeft + PaneInnerWidth - 1]);
        Assert.Equal(BorderGlyphs.Rounded.Vertical, cells[PaneLeft, EventLogTop + 3].Glyph);
        Assert.Equal(BorderGlyphs.Rounded.Vertical, cells[99, EventLogTop + 3].Glyph);
    }

    [Fact]
    public void AnActionListExactlyTheWidthIsNotMarked()
    {
        var exact = new string('x', PaneInnerWidth - 5);
        var cells = Render(Document(actions: exact), frameIndex: 3);
        var row = cells.ToLines()[EventLogTop + 3];

        Assert.Equal("  3  " + exact, row.Substring(InnerLeft, PaneInnerWidth));
        Assert.DoesNotContain("\u2026", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(30)]
    [InlineData(80)]
    public void ARoleTooLongForItsColumnStillLeavesZoneAndScoreVisible(int roleLength)
    {
        var role = new string('R', roleLength);
        var cells = Render(Document(roles: new[] { role, "Infiltrator" }), frameIndex: 3);
        var row = cells.ToLines()[3];

        // Twelve columns of role: eleven of it and one marker, so the zone and
        // score columns start exactly where they do for a short role.
        Assert.Equal(new string('R', 11) + "\u2026", row.Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("0     ", row.Substring(InnerLeft + ZoneColumnStart, 6));
        Assert.Equal("   12", row.Substring(InnerLeft + ScoreColumnStart, 5));

        // The same two columns on the second row, so a long first role did not
        // push the column boundaries for anything after it.
        var second = cells.ToLines()[4];
        Assert.Equal("Infiltrator ", second.Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("1     ", second.Substring(InnerLeft + ZoneColumnStart, 6));
        Assert.Equal("    6", second.Substring(InnerLeft + ScoreColumnStart, 5));
    }

    [Fact]
    public void ARoleWithASurrogatePairAtTheCutIsReplacedNotSplit()
    {
        // Ten columns of role, then an emoji that is two UTF-16 units and two
        // columns, then more text. Sanitised, the emoji is one column, so the cut
        // lands after it and the pair is never divided across two cells.
        var role = new string('R', 10) + "\uD83D\uDE00" + new string('T', 10);
        var cells = Render(Document(roles: new[] { role, "Infiltrator" }), frameIndex: 3);
        var row = cells.ToLines()[3];

        Assert.Equal(new string('R', 10) + "\uFFFD\u2026", row.Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("0     ", row.Substring(InnerLeft + ZoneColumnStart, 6));
        Assert.Equal("   12", row.Substring(InnerLeft + ScoreColumnStart, 5));
        Assert.All(cells.ToLines().SelectMany(line => line), glyph => Assert.False(char.IsSurrogate(glyph)));
    }

    [Fact]
    public void ARoleInCjkAndARoleInEmojiAreReplacedAndTheScoresStillShow()
    {
        var cells = Render(
            Document(roles: new[] { "\u6F22\u5B57\u6F22\u5B57", "\uD83D\uDE00\uD83D\uDE01" }),
            frameIndex: 3);

        Assert.Equal(
            new string('\uFFFD', 4) + new string(' ', 8),
            cells.ToLines()[3].Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("0     ", cells.ToLines()[3].Substring(InnerLeft + ZoneColumnStart, 6));
        Assert.Equal("   12", cells.ToLines()[3].Substring(InnerLeft + ScoreColumnStart, 5));

        Assert.Equal(
            new string('\uFFFD', 2) + new string(' ', 10),
            cells.ToLines()[4].Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("1     ", cells.ToLines()[4].Substring(InnerLeft + ZoneColumnStart, 6));
        Assert.Equal("    6", cells.ToLines()[4].Substring(InnerLeft + ScoreColumnStart, 5));
    }

    [Fact]
    public void AnAsciiCockpitIsAsciiAllTheWayThrough()
    {
        var cells = Render(
            Document(actions: "\u6F22\u5B57 \uD83D\uDE00 Collect(0)", roles: new[] { "\u6F22\u5B57\u6F22\u5B57", "\uD83D\uDE00\uD83D\uDE01" }),
            frameIndex: 3,
            glyphs: GlyphMode.Ascii);

        foreach (var line in cells.ToLines())
        {
            Assert.All(line, glyph => Assert.True(glyph < 128, $"'{glyph}' is not ASCII."));
        }

        // Four columns of role: every CJK ideograph is replaced, and the column
        // is padded rather than marked because the result fits.
        Assert.Equal("????        ", cells.ToLines()[3].Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("0     ", cells.ToLines()[3].Substring(InnerLeft + ZoneColumnStart, 6));

        // A role long enough to need the marker gets the three-character one.
        var marked = Render(
            Document(actions: "Move(0)", roles: new[] { new string('R', 30), "Infiltrator" }),
            frameIndex: 3,
            glyphs: GlyphMode.Ascii)
            .ToLines()[3];

        Assert.Equal(new string('R', 9) + "...", marked.Substring(InnerLeft + RoleColumnStart, RoleColumnWidth));
        Assert.Equal("0     ", marked.Substring(InnerLeft + ZoneColumnStart, 6));
    }

    [Fact]
    public void TheScoreboardHeadingsStandOverTheColumnsTheyName()
    {
        var heading = Render(Document(roles: new[] { "SentryPatrolAgentLong", "Infiltrator" }), frameIndex: 3)
            .ToLines()[2]
            .Substring(InnerLeft, PaneInnerWidth);
        var row = Render(Document(), frameIndex: 3).ToLines()[3].Substring(InnerLeft, PaneInnerWidth);

        // The heading's columns are the rows' columns: the slot column starts
        // after the agent glyph and its trailing space, at the same place.
        Assert.Equal(SlotColumnStart, heading.IndexOf("slot", StringComparison.Ordinal));
        Assert.Equal(SlotColumnStart, row.IndexOf('0'));
        Assert.Equal(ZoneColumnStart, heading.IndexOf("zone", StringComparison.Ordinal));
        Assert.Equal(ZoneColumnStart, row.IndexOf("0        ", StringComparison.Ordinal));
        Assert.Equal(ScoreColumnStart, heading.IndexOf("score", StringComparison.Ordinal));
        Assert.Equal(PaneInnerWidth, heading.TrimEnd().Length);
    }

    [Fact]
    public void EveryRowOfTheCockpitIsAsWideAsTheTerminalAndNothingLeaks()
    {
        var cells = Render(
            Document(actions: new string('m', 200), roles: new[] { new string('R', 80), new string('S', 80) }),
            frameIndex: 3);
        var lines = cells.ToLines();

        Assert.All(lines, row => Assert.Equal(Cockpit.Width, row.Length));

        // The screen's own frame is intact: the first row opens and closes on the
        // top corners and the last on the bottom ones, and every row between them
        // is one column in from each edge.
        Assert.Equal("\u256d", lines[0][..1]);
        Assert.Equal("\u256e", lines[0][^1..]);
        Assert.Equal("\u2570", lines[^1][..1]);
        Assert.Equal("\u256f", lines[^1][^1..]);
        Assert.All(lines[1..^1], row => Assert.Equal("\u2502", row[..1]));
        Assert.All(lines[1..^1], row => Assert.Equal("\u2502", row[^1..]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(20, 6)]
    [InlineData(60, 20)]
    [InlineData(80, 25)]
    public void ATerminalWithNoRoomForAColumnStillDrawsRatherThanThrowing(int width, int height)
    {
        var cells = Render(
            Document(actions: new string('m', 200), roles: new[] { new string('R', 80), "Infiltrator" }),
            frameIndex: 3,
            size: new PaneSize(width, height));

        Assert.Equal(width, cells.Width);
        Assert.Equal(height, cells.Height);
        Assert.All(cells.ToLines(), row => Assert.Equal(width, row.Length));
    }

    private static CellBuffer Render(
        ReplayDocument document,
        int frameIndex,
        GlyphMode glyphs = GlyphMode.Unicode,
        PaneSize? size = null) =>
        CockpitLayout.Render(new CockpitRequest(
            document,
            frameIndex,
            size ?? Cockpit,
            glyphs,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0)));

    /// <summary>
    /// A six-tick two-agent document with the roles and action text a test names,
    /// so every expected row below is a value the test chose.
    /// </summary>
    private static ReplayDocument Document(string? actions = null, string[]? roles = null)
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[]
                {
                    new WorldAgent(0, 0, tick * 4),
                    new WorldAgent(1, 1, tick * 2),
                },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : actions ?? "agent0: Move(1); agent1: Collect(0)",
                stateDigest: null,
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
                },
                new[] { new WorldResource(0, 0, 2, 2) },
                new[] { new WorldEdge(0, 0, 1, Capacity: 1) }),
            new ReplayHeader(42, 5, "long-rows", roles ?? new[] { "Sentry", "Infiltrator" }, 6, null, 0),
            frames);
    }
}