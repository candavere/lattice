using System.Globalization;
using System.Text;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// One whole live-mode cockpit, pinned. The LIVE label, the produced ticks over
/// the budget, the live controls on the hint row, and the panes drawn from a
/// running episode rather than a recording.
/// </summary>
/// <remarks>
/// The same discipline as the replay goldens: every row is accounted for by a
/// source other than <see cref="CockpitLayout"/> — the scoreboard and event-log rows
/// composed here from the document's own values, the world against
/// <see cref="WorldRenderer"/>, the frame and the titles as structural rules —
/// and only then compared with the frozen file.
/// </remarks>
public class LiveCockpitGoldenTests
{
    /// <summary>The minimum terminal the whole cockpit is drawn for.</summary>
    private static readonly PaneSize Cockpit = new(100, 30);

    /// <summary>The first column of the right-hand column of panes.</summary>
    private const int PaneLeft = 67;

    /// <summary>The first column of a pane's contents, one inside its border.</summary>
    private const int InnerLeft = PaneLeft + 1;

    /// <summary>The width inside any pane on the right-hand column.</summary>
    private const int InnerWidth = 30;

    /// <summary>The event log pane's top row; its first content row is one below.</summary>
    private const int EventLogTop = 13;

    /// <summary>The timeline pane's content row.</summary>
    private const int TimelineRow = 27;

    /// <summary>The world pane's inner area at the cockpit's minimum size.</summary>
    private const int WorldWidth = 63;

    /// <summary>The world pane's inner height at the cockpit's minimum size.</summary>
    private const int WorldHeight = 23;

    /// <summary>
    /// The episode this golden draws: seven ticks produced of a budget of thirty,
    /// still running, so the screen says LIVE, counts what has been produced against
    /// what can be, and shows the cursor's own state rather than an ending.
    /// </summary>
    private static readonly LiveState Running = new(
        ProducedTicks: 7,
        MaximumTicks: 30,
        Seed: 7,
        AgentRoles: new[] { "Sentry", "Infiltrator" });

    [Fact]
    public void ALiveCockpitIsTheCommittedFrame()
    {
        var document = Document();
        var cells = CockpitLayout.Render(new CockpitRequest(
            document,
            FrameIndex: 5,
            Cockpit,
            GlyphMode.Unicode,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: false, StepsPerSecond: 4.0),
            Live: Running));
        var lines = cells.ToLines();

        // The timeline: LIVE, the tick on show, the ticks produced, the budget.
        Assert.Equal("LIVE tick 5/7 of 30", lines[TimelineRow].Substring(2, 19));
        var timeline = lines[TimelineRow].Substring(2, 96).TrimEnd();
        Assert.Equal("playing 4 steps/s", timeline.Substring(timeline.Length - 17, 17));

        // The bar is the budget, so a reader sees what is left as well as what ran.
        Assert.Equal(7, lines[TimelineRow].Count(glyph => glyph == '\u2588'));

        // The scoreboard's own rows, composed here column by column.
        Assert.Equal(
            "  " + "slot".PadRight(5) + "role".PadRight(12) + "zone".PadRight(6) + "score".PadRight(5),
            Pane(lines[2]));
        Assert.Equal(ScoreRow("Sentry", 0, zone: 1, score: 5, transitTo: 1), Pane(lines[3]));
        Assert.Equal(ScoreRow("Infiltrator", 1, zone: 2, score: 2, transitTo: 3), Pane(lines[4]));
        Assert.Equal("tick    5/30                  ", Pane(lines[5]));
        Assert.Equal("claims  1/3                   ", Pane(lines[6]));

        // The event log's rows: the tick on show, its actions, and the mark where
        // the list was cut. Five columns go to the tick and its padding, so
        // twenty-five are left: twenty-four of text and the marker.
        Assert.Equal("  5  agent0: Move(2); agent1:\u2026", Pane(lines[EventLogTop + 5]));

        // The provenance row says what a live episode can be said to have: its
        // seed, and "not recorded" for the two things a recording would carry.
        Assert.Equal("seed 7  schema not recorded  \u2026", Pane(lines[EventLogTop + 6]));

        // The hint row is the live one, and names the live controls.
        Assert.Equal(CockpitLayout.LiveKeyHints, lines[^1].Substring(2, CockpitLayout.LiveKeyHints.Length));
        Assert.All(CockpitLayout.LiveKeyHints, glyph => Assert.True(glyph < 128));

        AssertFrameAndTitles(lines);
        AssertWorldRows(lines, document);
        Assert.Equal(Golden("live-running"), string.Join("\n", lines));
    }

    private static string Pane(string row) => row.Substring(InnerLeft, InnerWidth);

    private static string ScoreRow(
        string role,
        int slot,
        int zone,
        int score,
        int? transitTo = null)
    {
        var glyph = transitTo is null ? '\u25C6' : '\u25C7';
        var where = transitTo is null
            ? zone.ToString(CultureInfo.InvariantCulture)
            : "->" + transitTo.Value.ToString(CultureInfo.InvariantCulture);

        return glyph + " "
            + slot.ToString(CultureInfo.InvariantCulture).PadRight(5)
            + role.PadRight(12)
            + where.PadRight(6)
            + score.ToString(CultureInfo.InvariantCulture).PadLeft(5);
    }

    private static void AssertFrameAndTitles(string[] lines)
    {
        Assert.Equal("\u256d", lines[0][..1]);
        // "LATTICE TUI" is eleven columns, and the live mode word is four where
        // "replay" was six — the whole title is thirty-one columns at 100 wide, and
        // the border takes the two the shorter word leaves over.
        Assert.Equal("LATTICE TUI  live  live-episode", lines[0].Substring(2, 31));
        Assert.Equal("\u256e", lines[0][^1..]);
        Assert.Equal(" WORLD ", lines[1].Substring(3, 7));
        Assert.Equal(" SCOREBOARD ", lines[1].Substring(69, 12));
        Assert.Equal(" EVENT LOG ", lines[EventLogTop].Substring(69, 11));
        Assert.Equal(" TIMELINE ", lines[TimelineRow - 1].Substring(3, 10));
        Assert.Equal("\u2570", lines[^1][..1]);
        Assert.Equal("\u256f", lines[^1][^1..]);
    }

    private static void AssertWorldRows(string[] lines, ReplayDocument document)
    {
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[5],
            document.TrailBefore(5, CockpitLayout.TrailFrames),
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

    /// <summary>
    /// A seven-tick, two-agent episode in the same shape the live viewer produces,
    /// with the transit and the long action list a live run really produces.
    /// </summary>
    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 7; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[]
                {
                    new WorldAgent(0, tick == 5 ? 1 : 0, tick, tick == 5 ? new WorldTransit(0, 1, 1, 2) : null),
                    new WorldAgent(1, 2, tick / 2, tick == 5 ? new WorldTransit(2, 3, 1, 2) : null),
                },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : "agent0: Move(2); agent1: Wait; agent0: Collect(0)",
                stateDigest: null,
                isTerminal: false,
                terminalReason: null,
                winnerSlot: null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[]
                {
                    new WorldZone(0, "0", 4, 4, Role: "SentryPost"),
                    new WorldZone(1, "1", 30, 4, Role: "EntryHall"),
                    new WorldZone(2, "2", 30, 16, Role: "Corridor"),
                    new WorldZone(3, "3", 52, 16, Role: "ChokeDoorway"),
                },
                new[]
                {
                    new WorldResource(0, 1, 30, 8),
                    new WorldResource(1, 2, 34, 16),
                    new WorldResource(2, 3, 52, 20),
                },
                new[]
                {
                    new WorldEdge(0, 0, 1, Capacity: 1),
                    new WorldEdge(1, 1, 2, Capacity: 2),
                    new WorldEdge(2, 2, 3, Capacity: 1),
                }),
            new ReplayHeader(7, 5, "live-episode", new[] { "Sentry", "Infiltrator" }, 30, null, 0),
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