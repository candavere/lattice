using System.Globalization;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Two whole-cockpit frames for the two row-overflow cases: an action list longer
/// than the log pane, and a role longer than the scoreboard's role column.
/// </summary>
/// <remarks>
/// <para>
/// These goldens are <b>recomputed, not blessed</b>. Every row of the screen is
/// accounted for by a source other than <see cref="CockpitLayout"/>: the
/// scoreboard and event-log rows are composed here from the document's own values
/// with the column widths written out longhand; the world rows are checked against
/// <see cref="WorldRenderer"/>, which the layout copies rather than redraws; and
/// the frame, pane titles and key hints are ASCII literals asserted at their known
/// places. Only then is the whole frame compared with the frozen file, so a golden
/// cannot drift into agreeing with a regression.
/// </para>
/// </remarks>
public class CockpitRowGoldens
{
    /// <summary>The minimum terminal the whole cockpit is drawn for.</summary>
    private static readonly PaneSize Cockpit = new(100, 30);

    /// <summary>The first column of the right-hand column of panes.</summary>
    private const int PaneLeft = 67;

    /// <summary>The first column of a pane's contents, one inside its border.</summary>
    private const int InnerLeft = PaneLeft + 1;

    /// <summary>The width inside any pane on the right-hand column.</summary>
    private const int InnerWidth = 30;

    /// <summary>The world pane's inner area at the cockpit's minimum size.</summary>
    private const int WorldWidth = 63;

    /// <summary>The world pane's inner height at the cockpit's minimum size.</summary>
    private const int WorldHeight = 23;

    /// <summary>An action list of 69 characters — forty-four columns too many.</summary>
    private const string LongActions =
        "agent0: Move(1); agent1: Collect(0); agent0: Wait; agent1: Move(1); agent0: Move(0)";

    /// <summary>A role name of 30 characters — eighteen columns too many.</summary>
    private const string LongRole = "InfiltratorWithAVeryLongNameXY";

    /// <summary>An action list of 33 characters — eight columns too many.</summary>
    private const string ShortActions = "agent0: Move(1); agent1: Collect(0)";

    /// <summary>A role name of eleven characters, which fits its column exactly.</summary>
    private const string ShortRole = "Infiltrator";

    [Fact]
    public void ALongActionListIsACockpitFramePinnedByItsOwnRows()
    {
        var document = GoldenDocument(LongActions, "Sentry", ShortRole);
        var lines = Render(document);

        // The scoreboard's own rows, composed here column by column.
        Assert.Equal(Heading(), Pane(lines[2]));
        Assert.Equal(ScoreRow("Sentry", slot: 0, zone: 0, score: 12), Pane(lines[3]));
        Assert.Equal(ScoreRow(ShortRole, slot: 1, zone: 1, score: 6), Pane(lines[4]));
        Assert.Equal("tick    3/6                   ", Pane(lines[5]));
        Assert.Equal("claims  1/1                   ", Pane(lines[6]));
        Assert.Equal("digest  not recorded          ", Pane(lines[7]));

        // The log rows: three columns of tick, two of padding, then the action
        // list cut to twenty-five columns — twenty-four of text and the marker.
        for (var tick = 1; tick <= 3; tick++)
        {
            Assert.Equal(
                Invariant(tick).PadLeft(3) + "  " + LongActions[..24] + "\u2026",
                Pane(lines[EventLogTop + tick]));
        }

        Assert.Equal("seed 42  schema v5  sha256 no\u2026", Pane(lines[EventLogTop + 4]));

        AssertFrameAndTitles(lines);
        AssertWorldRows(lines, document, frameIndex: 3);
        Assert.Equal(Golden("cockpit-long-actions"), string.Join("\n", lines));
    }

    [Fact]
    public void ALongRoleIsACockpitFramePinnedByItsOwnRows()
    {
        var document = GoldenDocument(ShortActions, LongRole, ShortRole);
        var lines = Render(document);

        Assert.Equal(Heading(), Pane(lines[2]));

        // Eleven columns of the role and one marker, so the zone and score columns
        // stand exactly where they stand on the row below.
        Assert.Equal(
            "\u25C6 " + "0    " + "Infiltrator\u2026" + "0     " + "   12",
            Pane(lines[3]));
        Assert.Equal(ScoreRow(ShortRole, slot: 1, zone: 1, score: 6), Pane(lines[4]));

        // The log rows here are the same shape: even the short action list is
        // wider than the pane's twenty-five columns.
        for (var tick = 1; tick <= 3; tick++)
        {
            Assert.Equal(
                Invariant(tick).PadLeft(3) + "  " + ShortActions[..24] + "\u2026",
                Pane(lines[EventLogTop + tick]));
        }

        AssertFrameAndTitles(lines);
        AssertWorldRows(lines, document, frameIndex: 3);
        Assert.Equal(Golden("cockpit-long-role"), string.Join("\n", lines));
    }

    [Fact]
    public void TheTwoGoldensAreTheSameScreenApartFromTheRowTheyExistFor()
    {
        var withLongActions = Golden("cockpit-long-actions").Split('\n');
        var withLongRole = Golden("cockpit-long-role").Split('\n');

        Assert.Equal(withLongActions.Length, withLongRole.Length);

        // Only the first scoreboard row differs: the short action list still overflows
        // the log pane, so both goldens mark those rows the same way. Every other
        // row on the screen is identical, which is what makes each golden a
        // single-variable picture of one defect.
        var expectedToDiffer = new HashSet<int> { 3 };

        for (var row = 0; row < withLongActions.Length; row++)
        {
            if (expectedToDiffer.Contains(row))
            {
                Assert.NotEqual(withLongActions[row], withLongRole[row]);
                continue;
            }

            Assert.Equal(withLongActions[row], withLongRole[row]);
        }
    }

    /// <summary>The event log pane's top row; its first content row is one below.</summary>
    private const int EventLogTop = 13;

    /// <summary>A pane's own thirty columns, without its borders.</summary>
    private static string Pane(string row) => row.Substring(InnerLeft, InnerWidth);

    /// <summary>
    /// The scoreboard heading row, written out: two columns of indent, then the
    /// four named columns at the widths the rows use.
    /// </summary>
    private static string Heading() =>
        "  " + "slot".PadRight(5) + "role".PadRight(12) + "zone".PadRight(6) + "score".PadRight(5);

    /// <summary>
    /// One agent row, written out: the filled diamond, a space, then the four
    /// columns. A role longer than its column is eleven characters and the marker.
    /// </summary>
    private static string ScoreRow(string role, int slot, int zone, int score) =>
        "\u25C6 "
        + Invariant(slot).PadRight(5)
        + (role.Length > 12 ? role[..11] + "\u2026" : role.PadRight(12))
        + Invariant(zone).PadRight(6)
        + Invariant(score).PadLeft(5);

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The screen's own frame and the four pane frames and titles: box-drawing
    /// characters and ASCII words at known places, asserted rather than copied.
    /// </summary>
    private static void AssertFrameAndTitles(string[] lines)
    {
        Assert.Equal("\u256d", lines[0][..1]);
        Assert.Equal("LATTICE TUI  replay  long-rows", lines[0].Substring(2, 30));
        Assert.Equal("\u256e", lines[0][^1..]);

        Assert.Equal(" WORLD ", lines[1].Substring(3, 7));
        Assert.Equal(" SCOREBOARD ", lines[1].Substring(69, 12));
        Assert.Equal(" EVENT LOG ", lines[EventLogTop].Substring(69, 11));
        Assert.Equal(" TIMELINE ", lines[26].Substring(3, 10));

        // The one row that explains the keys is ASCII: it rides on the screen's own
        // border, and a terminal that cannot encode a box-drawing glyph can still
        // be told which keys to press.
        Assert.Equal(CockpitLayout.KeyHints, lines[^1].Substring(2, CockpitLayout.KeyHints.Length));
        Assert.All(CockpitLayout.KeyHints, glyph => Assert.True(glyph < 128));
        Assert.Equal("\u2570", lines[^1][..1]);
        Assert.Equal("\u256f", lines[^1][^1..]);

        // The timeline's own line: the recorded tick, a bar, and the cursor's state.
        Assert.Equal("tick 3/6  ", lines[27].Substring(2, 10));
        var timeline = lines[27].Substring(2, 96).TrimEnd();
        Assert.Equal("paused 4 steps/s", timeline.Substring(timeline.Length - 16, 16));
        Assert.Equal(3, timeline.Count(glyph => glyph == '\u2588'));
    }

    /// <summary>
    /// The world pane's rows, checked against the renderer the layout copies into
    /// the pane rather than drawing a second time. An independent source for those
    /// twenty-three rows.
    /// </summary>
    private static void AssertWorldRows(string[] lines, ReplayDocument document, int frameIndex)
    {
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[frameIndex],
            document.TrailBefore(frameIndex, CockpitLayout.TrailFrames),
            new PaneSize(WorldWidth, WorldHeight),
            GlyphMode.Unicode,
            Palette.PanelBackground));

        for (var y = 0; y < WorldHeight; y++)
        {
            Assert.Equal(
                string.Join(string.Empty, world.Cells.ToLines()[y]),
                lines[y + 2].Substring(2, WorldWidth));
        }
    }

    private static string[] Render(ReplayDocument document) =>
        CockpitLayout.Render(new CockpitRequest(
            document,
            3,
            Cockpit,
            GlyphMode.Unicode,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0)))
            .ToLines();

    /// <summary>
    /// The document both goldens draw: a six-tick, two-agent episode whose only
    /// named values are the action text and the two roles, so every expected row
    /// is a value this test chose.
    /// </summary>
    private static ReplayDocument GoldenDocument(string actions, string firstRole, string secondRole)
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick * 4), new WorldAgent(1, 1, tick * 2) },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : actions,
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
            new ReplayHeader(42, 5, "long-rows", new[] { firstRole, secondRole }, 6, null, 0),
            frames);
    }

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "Tests",
            "Tui",
            "Goldens",
            name + ".txt")).TrimEnd('\n');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}