using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The cockpit asked to draw a live episode rather than a finished recording:
/// the LIVE label, the produced ticks over the budget, the recorded reason the
/// episode ended, and the one transient notice the host must be able to put in
/// front of the reader.
/// </summary>
public class CockpitLiveStateTests
{
    private static readonly PaneSize Cockpit = new(100, 30);

    /// <summary>The first column of a pane's contents on the right-hand column.</summary>
    private const int InnerLeft = 68;

    /// <summary>The timeline pane's content row.</summary>
    private const int TimelineRow = 27;

    [Fact]
    public void ARecordingDrawsNoLiveLabelAndNoLiveBudget()
    {
        var rows = Render(Live: null);

        Assert.DoesNotContain("LIVE", rows[TimelineRow], StringComparison.Ordinal);
        Assert.Equal(CockpitLayout.KeyHints, rows[^1].Substring(2, CockpitLayout.KeyHints.Length));
        Assert.Equal("tick 3/6", rows[TimelineRow].Substring(2, 8));

        // The bar is still over the recording's own recorded step count.
        Assert.Equal(3, rows[TimelineRow].Count(glyph => glyph == '\u2588'));
    }

    [Fact]
    public void ALiveFrameSaysLiveAndCountsProducedTicksOverTheBudget()
    {
        var rows = Render(new LiveState(ProducedTicks: 10, MaximumTicks: 30));

        Assert.StartsWith("LIVE", rows[TimelineRow].Substring(2, 4), StringComparison.Ordinal);
        Assert.Equal("LIVE tick 3/10 of 30", rows[TimelineRow].Substring(2, 20));
        Assert.Contains("paused 4 steps/s", rows[TimelineRow], StringComparison.Ordinal);

        // Ten of thirty: the bar is the budget, not the produced count, so the
        // reader sees how much episode is left as well as how much has run.
        Assert.Equal(10, rows[TimelineRow].Count(glyph => glyph == '\u2588'));
    }

    [Fact]
    public void AFinishedLiveEpisodeSaysFinishedAndTheRecordedReason()
    {
        var rows = Render(new LiveState(10, 30, FinishedReason: "resources-exhausted"));

        Assert.Contains("finished: resources-exhausted", rows[TimelineRow], StringComparison.Ordinal);
        Assert.DoesNotContain("steps/s", rows[TimelineRow], StringComparison.Ordinal);
    }

    [Fact]
    public void ANoticeTakesThePlaceOfTheStateLineAndSaysWhatIsHappening()
    {
        var rows = Render(new LiveState(3, 30, Notice: "waiting for the running agent turn to finish"));

        Assert.Contains(
            "waiting for the running agent turn to finish",
            rows[TimelineRow],
            StringComparison.Ordinal);
        Assert.DoesNotContain("steps/s", rows[TimelineRow], StringComparison.Ordinal);
        Assert.StartsWith("LIVE", rows[TimelineRow].Substring(2, 4), StringComparison.Ordinal);
    }

    [Fact]
    public void AStoppingEpisodeSaysStoppingAndNotStopped()
    {
        var rows = Render(new LiveState(3, 30, Notice: "stopping"));

        Assert.Contains("stopping", rows[TimelineRow], StringComparison.Ordinal);

        // "stopped" is a claim about a thread that has been joined, and nothing
        // but the host may make it. A notice of "stopping" must never print it.
        Assert.DoesNotContain("stopped", rows[TimelineRow], StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveKeyHintRowIsAsciiAndNamesEveryLiveControl()
    {
        var hints = Hints(Render(new LiveState(1, 30)), CockpitLayout.LiveKeyHints);

        Assert.All(hints, glyph => Assert.True(glyph < 128, $"'{glyph}' is not ASCII."));
        Assert.StartsWith("LIVE", hints, StringComparison.Ordinal);
        foreach (var control in new[] { "pause", "tick", "back", "speed", "restart", "quit" })
        {
            Assert.Contains(control, hints, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ALiveKeyHintRowIsTheSameInAsciiAndUnicodeBecauseItIsAscii()
    {
        Assert.Equal(
            Hints(Render(new LiveState(1, 30)), CockpitLayout.LiveKeyHints),
            Hints(Render(new LiveState(1, 30), GlyphMode.Ascii), CockpitLayout.LiveKeyHints));
    }

    [Fact]
    public void ALiveCockpitIsStillExactlyTheSizeOfTheTerminal()
    {
        var cells = CockpitLayout.Render(Request(new LiveState(10, 30), GlyphMode.Unicode));

        Assert.Equal(Cockpit.Width, cells.Width);
        Assert.Equal(Cockpit.Height, cells.Height);
        Assert.All(cells.ToLines(), row => Assert.Equal(Cockpit.Width, row.Length));
    }

    [Fact]
    public void ARecordingAndALiveEpisodeOfTheSameFramesDifferOnlyWhereTheyMust()
    {
        var recording = Render(Live: null);
        var live = Render(new LiveState(6, 30));

        // The world, the scoreboard and the log are drawn from the same frames and
        // must be identical; only the timeline and the hints know the difference.
        for (var row = 0; row < Cockpit.Height - 4; row++)
        {
            Assert.Equal(recording[row], live[row]);
        }

        Assert.NotEqual(recording[TimelineRow], live[TimelineRow]);
        Assert.NotEqual(recording[^1], live[^1]);
    }

    [Fact]
    public void AnEpisodeWithNoProducedTicksStillSaysLiveAndZero()
    {
        // On the start frame, before the stepper has produced anything: LIVE, a
        // produced count of zero, and an empty bar — not a blank pane that could be
        // mistaken for a recording with nothing to show.
        var rows = Render(new LiveState(0, 30), frameIndex: 0);

        Assert.Equal("LIVE tick 0/0 of 30", rows[TimelineRow].Substring(2, 19));
        Assert.Equal(0, rows[TimelineRow].Count(glyph => glyph == '\u2588'));
    }

    private static string Hints(string[] rows, string expected) =>
        string.Concat(rows[^1].Skip(2).Take(expected.Length));

    private static string[] Render(LiveState? Live, GlyphMode glyphs = GlyphMode.Unicode, int frameIndex = 3) =>
        CockpitLayout.Render(Request(Live, glyphs, frameIndex)).ToLines();

    private static CockpitRequest Request(LiveState? live, GlyphMode glyphs, int frameIndex = 3) =>
        new(
            Document(),
            frameIndex,
            Cockpit,
            glyphs,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0),
            Live: live);

    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick), new WorldAgent(1, 1, tick / 2) },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : "agent0: Move(1); agent1: Collect(0)",
                stateDigest: null,
                isTerminal: tick == 6,
                terminalReason: tick == 6 ? "tick-limit" : null,
                winnerSlot: tick == 6 ? 0 : null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 10, 10) },
                Array.Empty<WorldResource>(),
                new[] { new WorldEdge(0, 0, 1) }),
            new ReplayHeader(42, 5, "live-episode", new[] { "Sentry", "Infiltrator" }, 6, null, 0),
            frames);
    }
}