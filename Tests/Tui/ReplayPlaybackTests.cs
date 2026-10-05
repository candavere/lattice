using System.Collections.Immutable;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The playback cursor: what a key does to it, and what elapsed time does to it.
/// Every assertion here is about index arithmetic and a clock the test supplies,
/// so none of them depends on how fast the machine runs.
/// </summary>
public class ReplayPlaybackTests
{
    [Fact]
    public void ANewCursorIsPausedOnTheStartFrame()
    {
        var playback = new ReplayPlayback(Document());

        Assert.True(playback.IsPaused);
        Assert.Equal(0, playback.Index);
        Assert.True(playback.Frame.IsStart);
    }

    [Fact]
    public void APausedCursorDoesNotMoveHoweverMuchTimePasses()
    {
        var playback = new ReplayPlayback(Document());

        Assert.False(playback.Advance(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, playback.Index);
        Assert.Equal(0.0, playback.Phase);
    }

    [Fact]
    public void ARunningCursorAdvancesOneFramePerTickOfWallClock()
    {
        var playback = new ReplayPlayback(Document());
        playback.TogglePause();

        // Four recorded steps per second, so half a step is an eighth of a second.
        Assert.True(playback.Advance(TimeSpan.FromMilliseconds(125)));
        Assert.Equal(0, playback.Index);
        Assert.Equal(0.5, playback.Phase);

        Assert.True(playback.Advance(TimeSpan.FromMilliseconds(125)));
        Assert.Equal(1, playback.Index);
        Assert.Equal(0.0, playback.Phase);
    }

    [Fact]
    public void TheWithinFramePhaseFreezesWhilePaused()
    {
        var playback = new ReplayPlayback(Document());
        playback.TogglePause();
        playback.Advance(TimeSpan.FromMilliseconds(125));

        Assert.Equal(0.5, playback.Phase);

        playback.TogglePause();

        // Paused: the phase a frame is drawn at returns to zero, so a paused frame
        // shows the recorded positions and not a point between two ticks.
        Assert.Equal(0.0, playback.Phase);
        Assert.Equal(0, playback.Index);
    }

    [Fact]
    public void ARunningCursorStopsAndPausesAtTheLastFrame()
    {
        var playback = new ReplayPlayback(Document());
        playback.TogglePause();

        // Six ticks at four steps a second lands on the last frame without having
        // noticed yet that there is nowhere further to go.
        Assert.True(playback.Advance(TimeSpan.FromSeconds(1.5)));
        Assert.Equal(playback.Document.LastIndex, playback.Index);
        Assert.False(playback.IsPaused);

        // The next tick finds the end, stops, and reports that it did: the pane that
        // says whether the replay is running has to hear about the stop, or it goes
        // on saying "playing" over the last frame for ever.
        Assert.True(playback.Advance(TimeSpan.FromMilliseconds(250)));
        Assert.True(playback.IsPaused);

        // And then nothing changes any more.
        Assert.False(playback.Advance(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ResumingAtTheEndStartsOverFromTheBeginning()
    {
        var playback = new ReplayPlayback(Document());
        playback.JumpToEnd();

        Assert.False(playback.TogglePause());
        Assert.Equal(0, playback.Index);
    }

    [Fact]
    public void SteppingAndScrubbingClampAtBothEndsAndNeverWrap()
    {
        var playback = new ReplayPlayback(Document());

        Assert.Equal(0, playback.StepBack());
        Assert.Equal(0, playback.ScrubTo(-5));
        Assert.Equal(0, playback.JumpToStart());

        Assert.Equal(playback.Document.LastIndex, playback.ScrubTo(9999));
        Assert.Equal(playback.Document.LastIndex, playback.JumpToEnd());

        playback.ScrubTo(3);
        Assert.Equal(4, playback.StepForward());
        Assert.Equal(3, playback.StepBack());
        Assert.Equal(3, playback.Index);
    }

    [Fact]
    public void SpeedMovesAlongTheLadderAndStopsAtBothEnds()
    {
        var playback = new ReplayPlayback(Document());
        var top = ReplayPlayback.SpeedsPerSecond[^1];
        var bottom = ReplayPlayback.SpeedsPerSecond[0];

        // The documented default: four recorded steps a second.
        Assert.Equal(4.0, playback.StepsPerSecond);
        Assert.Contains(playback.StepsPerSecond, ReplayPlayback.SpeedsPerSecond);

        for (var i = 0; i < ReplayPlayback.SpeedsPerSecond.Length; i++)
        {
            playback.SpeedUp();
        }

        Assert.Equal(top, playback.StepsPerSecond);
        Assert.False(playback.CanSpeedUp);
        Assert.Equal(top, playback.SpeedUp());

        for (var i = 0; i < ReplayPlayback.SpeedsPerSecond.Length; i++)
        {
            playback.SlowDown();
        }

        Assert.Equal(bottom, playback.StepsPerSecond);
        Assert.False(playback.CanSlowDown);
        Assert.Equal(bottom, playback.SlowDown());
    }

    [Fact]
    public void EverySpeedIsAWholeOrHalfNumberOfRecordedStepsPerSecond()
    {
        // The speed is a UI setting, and it is shown as it is: one decimal place at
        // most, so a pane printing it never has to round.
        Assert.All(
            ReplayPlayback.SpeedsPerSecond,
            speed => Assert.Equal(speed, Math.Round(speed * 2) / 2));
    }

    [Fact]
    public void TheLadderIsStrictlyIncreasingSoASpeedChangeIsAlwaysVisible()
    {
        var speeds = ReplayPlayback.SpeedsPerSecond;

        for (var i = 1; i < speeds.Length; i++)
        {
            Assert.True(speeds[i] > speeds[i - 1], $"{speeds[i]} does not follow {speeds[i - 1]}.");
        }
    }

    /// <summary>
    /// Naming Enter, Tab, Backspace, Delete and Escape as their own kinds is a
    /// change to the input vocabulary, and the cockpit is the one consumer that
    /// must not notice: it has no field to commit and no text to edit, so each of
    /// them has to leave the cursor exactly where it was.
    /// </summary>
    [Fact]
    public void TheTextEntryKeysMoveTheCockpitCursorNothing()
    {
        foreach (var kind in new[]
                 {
                     TuiKeyKind.Enter, TuiKeyKind.Tab, TuiKeyKind.Backspace,
                     TuiKeyKind.Delete, TuiKeyKind.Escape,
                 })
        {
            var playback = new ReplayPlayback(Document());
            playback.TogglePause();
            playback.StepForward();
            var before = (playback.Index, playback.IsPaused, playback.StepsPerSecond);

            Assert.False(playback.Apply(new TuiKey(kind)));
            Assert.Equal(before, (playback.Index, playback.IsPaused, playback.StepsPerSecond));
        }
    }

    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick) },
                Array.Empty<int>(),
                tick == 0 ? null : "agent0: Wait",
                stateDigest: null,
                isTerminal: false,
                terminalReason: null,
                winnerSlot: null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[] { new WorldZone(0, "0", 0, 0) },
                Array.Empty<WorldResource>(),
                Array.Empty<WorldEdge>()),
            new ReplayHeader(42, 5, null, ImmutableArray<string>.Empty, 6, null, 0),
            frames);
    }
}