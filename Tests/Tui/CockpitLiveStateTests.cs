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
    public void AnEndNobodyRecordedIsTheSameWordInTheScoreboardAndTheTimeline()
    {
        // A frame the engine ended without a reason, and a live episode reporting the
        // absence rather than a sentence of its own. The two panes must not disagree:
        // one reason, one set of words, and nothing that reads like a verdict the
        // simulation never reached.
        var rows = Render(
            new LiveState(6, 6, FinishedReason: CockpitEpisodes.NotRecorded),
            frameIndex: 6,
            terminalReason: null);
        var text = string.Join("\n", rows);

        Assert.Contains("end     " + CockpitEpisodes.NotRecorded, text, StringComparison.Ordinal);
        Assert.Contains("finished: " + CockpitEpisodes.NotRecorded, rows[TimelineRow], StringComparison.Ordinal);
        foreach (var invented in new[] { "step limit", "--steps", "budget", "exhausted" })
        {
            Assert.DoesNotContain(invented, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ARecordedEndIsTheEnginesOwnWordInBothPanes()
    {
        // The other half of the same claim: what the engine did record reaches both
        // panes unchanged, and neither of them paraphrases it. The frame and the live
        // state carry the same recorded reason, as they do in a real run.
        var rows = Render(
            new LiveState(6, 6, FinishedReason: "resources-exhausted"),
            frameIndex: 6,
            terminalReason: "resources-exhausted");
        var text = string.Join("\n", rows);

        Assert.Contains("end     resources-exhausted", text, StringComparison.Ordinal);
        Assert.Contains("finished: resources-exhausted", rows[TimelineRow], StringComparison.Ordinal);
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
        var live = Render(new LiveState(6, 30, Seed: 42));

        // The world, the scoreboard and the log's action rows are drawn from the same
        // frames and are identical. The provenance row is not, and must not be: a
        // live episode has not been recorded, so it has no schema version and no
        // digest to name.
        for (var row = 0; row < Cockpit.Height - 4; row++)
        {
            if (row == 17)
            {
                Assert.NotEqual(recording[row], live[row]);
                continue;
            }

            Assert.Equal(recording[row], live[row]);
        }

        // What the panes can honestly say about where the episode came from. A
        // recording names its seed, its schema version and its digest; a live
        // episode names its seed and says "not recorded" for the two things that only
        // exist once an episode has been written down.
        Assert.Contains("seed 42  schema v5  sha256", string.Join("\n", recording), StringComparison.Ordinal);

        var liveText = string.Join("\n", live);
        Assert.Contains("seed 42  schema not recorded", liveText, StringComparison.Ordinal);
        Assert.DoesNotContain("start of recording", liveText, StringComparison.Ordinal);
        Assert.DoesNotContain("schema v5", liveText, StringComparison.Ordinal);

        Assert.NotEqual(recording[TimelineRow], live[TimelineRow]);
        Assert.NotEqual(recording[^1], live[^1]);
    }

    [Fact]
    public void AnEpisodeThatHasProducedNothingSaysItIsAnEpisodeAndNotARecording()
    {
        // On the start frame the log says what the frames are. A live episode has not
        // been recorded, and calling it one would be a claim about its provenance
        // that nothing supports.
        Assert.Contains("start of episode", string.Join("\n", Render(new LiveState(0, 30, Seed: 42), frameIndex: 0)), StringComparison.Ordinal);
        Assert.Contains("start of recording", string.Join("\n", Render(null, frameIndex: 0)), StringComparison.Ordinal);
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

    private static string[] Render(
        LiveState? Live,
        GlyphMode glyphs = GlyphMode.Unicode,
        int frameIndex = 3,
        string? terminalReason = "tick-limit") =>
        CockpitLayout.Render(Request(Live, glyphs, frameIndex, terminalReason)).ToLines();

    private static CockpitRequest Request(
        LiveState? live,
        GlyphMode glyphs,
        int frameIndex = 3,
        string? terminalReason = "tick-limit") =>
        new(
            Document(terminalReason),
            frameIndex,
            Cockpit,
            glyphs,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0),
            Live: live);

    private static ReplayDocument Document(string? terminalReason = "tick-limit")
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
                terminalReason: tick == 6 ? terminalReason : null,
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