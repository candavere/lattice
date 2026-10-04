using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The live cursor: what each control does to a running episode, how far the
/// viewer is allowed to get ahead of the stepper, and what the screen says about
/// a stepper that has not stopped yet.
/// </summary>
public class LivePlaybackTests
{
    [Fact]
    public void ALiveCursorOpensOnTheStartFramePausedAndAtTheProducedFrontier()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);

        Assert.Equal(0, cursor.Index);
        Assert.True(cursor.IsPaused);
        Assert.Equal(ReplayPlayback.SpeedsPerSecond[3], cursor.StepsPerSecond);
        Assert.Equal(0, cursor.Live!.ProducedTicks);
        Assert.Equal(10, cursor.Live.MaximumTicks);
        Assert.Null(cursor.Live.FinishedReason);
        Assert.Null(cursor.Live.Notice);
        Assert.False(cursor.IsFinished);
    }

    [Fact]
    public void ACursorNeverStandsOnAFrameTheStepperHasNotProduced()
    {
        var episode = new FakeEpisode(Frames: 3, Maximum: 10);
        var cursor = new LivePlayback(episode);

        cursor.ScrubTo(int.MaxValue);

        Assert.Equal(3, cursor.Index);
        Assert.Equal(3, cursor.Live!.ProducedTicks);
    }

    [Fact]
    public void WhilePausedNothingIsComputedUntilTheReaderAsksForOneTick()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);

        // Time passing is not a request: a paused viewer computes nothing.
        Assert.False(cursor.Advance(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, episode.RequestedTicks);

        cursor.Apply(Character(' '));   // resume
        cursor.Apply(Character(' '));   // pause again, before the frontier
        Assert.False(cursor.Advance(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, episode.RequestedTicks);
    }

    [Fact]
    public void OneTickKeyAsksTheStepperForExactlyOneTickAndShowsIt()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);

        Assert.True(cursor.Apply(Character('n')));
        Assert.Equal(1, episode.RequestedTicks);
        Assert.Equal(1, cursor.Index);
        Assert.Equal(1, cursor.Live!.ProducedTicks);

        cursor.Apply(Character('n'));
        cursor.Apply(Character('n'));

        Assert.Equal(3, episode.RequestedTicks);
        Assert.Equal(3, cursor.Index);
    }

    [Fact]
    public void OneTickKeyOffTheFrontierWalksTheFramesAlreadyProducedInstead()
    {
        var episode = new FakeEpisode(Frames: 4, Maximum: 10);
        var cursor = new LivePlayback(episode);
        cursor.ScrubTo(1);

        // Already produced: showing the next one costs nothing and asks nothing.
        cursor.Apply(Character('n'));

        Assert.Equal(0, episode.RequestedTicks);
        Assert.Equal(2, cursor.Index);
    }

    [Fact]
    public void AScrubBackAndForthMovesOverProducedFramesAndNeverOffTheEnd()
    {
        var episode = new FakeEpisode(Frames: 3, Maximum: 10);
        var cursor = new LivePlayback(episode);

        cursor.Apply(Character('n'));
        cursor.Apply(Character('n'));
        Assert.Equal(2, cursor.Index);

        Assert.True(cursor.Apply(Character('p')));
        Assert.Equal(1, cursor.Index);
        cursor.Apply(Character('p'));
        cursor.Apply(Character('p'));

        Assert.Equal(0, cursor.Index);
    }

    [Fact]
    public void AScrubPastTheFrontierAsksForWhatItNeedsAndStopsWhereItArrives()
    {
        var episode = new FakeEpisode(Frames: 2, Maximum: 10) { TicksPerRequest = 1 };
        var cursor = new LivePlayback(episode);

        // A live episode has no end to jump to, so End asks for one more tick and
        // stands on it: never on a frame that does not exist, and never asking for
        // a whole episode's worth at once.
        cursor.Apply(new TuiKey(TuiKeyKind.End));

        Assert.Equal(1, episode.RequestedTicks);
        Assert.Equal(episode.Frames.Count - 1, cursor.Index);
        Assert.Equal(3, cursor.Index);
    }

    [Fact]
    public void AScrubNeverRunsAheadOfTheStepperByMoreThanTheFramesProduced()
    {
        var episode = new FakeEpisode(Frames: 1, Maximum: 10);
        var cursor = new LivePlayback(episode);

        for (var i = 0; i < 20; i++)
        {
            cursor.Apply(new TuiKey(TuiKeyKind.End));
            Assert.True(cursor.Index <= episode.Frames.Count - 1);
        }

        // Twenty end presses, and the stepper was asked for nine more ticks: one per
        // press until the episode's own budget of ten was spent, and not one after.
        // The viewer cannot outrun the frontier, and cannot outrun the budget.
        Assert.Equal(9, episode.RequestedTicks);
        Assert.Equal(10, cursor.Index);
        Assert.Equal(10, cursor.ProducedFrames);
    }

    [Fact]
    public void ARunningCursorAdvancesAndAsksAtTheFrontierOnly()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);
        cursor.Apply(Character(' '));   // resume

        // Four steps per second: a quarter of a second per tick.
        Assert.True(cursor.Advance(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(1, cursor.Index);
        Assert.Equal(1, episode.RequestedTicks);

        // At the frontier again: the stepper is asked for the next tick, and the
        // cursor never runs more than one tick ahead of what it has shown.
        cursor.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(2, cursor.Index);
        Assert.Equal(2, episode.RequestedTicks);
    }

    [Fact]
    public void AResumedCursorCatchesUpOverProducedFramesBeforeAskingForMore()
    {
        var episode = new FakeEpisode(Frames: 3, Maximum: 10);
        var cursor = new LivePlayback(episode);
        cursor.ScrubTo(0);
        cursor.Apply(Character(' '));   // resume

        cursor.Advance(TimeSpan.FromMilliseconds(250));

        // Frame 1 was already produced; the stepper was not asked for it.
        Assert.Equal(1, cursor.Index);
        Assert.Equal(0, episode.RequestedTicks);
    }

    [Fact]
    public void SpeedKeysMoveTheSameSpeedTableTheRecordingUses()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);
        var start = cursor.StepsPerSecond;

        cursor.Apply(Character('>'));
        Assert.True(cursor.StepsPerSecond > start);
        cursor.Apply(Character('-'));
        Assert.Equal(start, cursor.StepsPerSecond);
        cursor.Apply(Character('+'));
        cursor.Apply(Character('<'));
        Assert.Equal(start, cursor.StepsPerSecond);

        // Both bracket pairs are speed, as the live hint row states.
        cursor.Apply(Character(']'));
        Assert.True(cursor.StepsPerSecond > start);
        cursor.Apply(Character('['));
        Assert.Equal(start, cursor.StepsPerSecond);

        Assert.All(ReplayPlayback.SpeedsPerSecond, speed => Assert.True(speed > 0));
    }

    [Fact]
    public void ATopOrBottomSpeedKeyChangesNothingAndSaysSo()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);

        for (var i = 0; i < 8; i++)
        {
            cursor.Apply(Character('>'));
        }

        Assert.Equal(ReplayPlayback.SpeedsPerSecond[^1], cursor.StepsPerSecond);
        Assert.False(cursor.Apply(Character('>')));
    }

    [Fact]
    public void ARestartKeyAsksTheEpisodeToRestartAndClearsTheFrames()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);
        cursor.Apply(Character('n'));
        cursor.Apply(Character('n'));
        Assert.Equal(2, cursor.Index);

        cursor.Apply(Character('r'));
        episode.Settle();

        Assert.Equal(1, episode.Restarts);
        Assert.Equal(0, cursor.Index);
        Assert.Equal(0, cursor.Live!.ProducedTicks);
        Assert.True(cursor.IsPaused);
    }

    [Fact]
    public void ARestartWaitingOnARunningTurnShowsTheMessageAndNoNewEpisode()
    {
        var episode = new FakeEpisode(Maximum: 10) { RestartsBlock = true };
        var cursor = new LivePlayback(episode);
        cursor.Apply(Character('n'));

        cursor.Apply(Character('r'));

        // The old stepper has not been joined, so nothing has been replaced: the
        // reader is told why, and the frames on screen are the ones they had.
        Assert.Equal(0, episode.Restarts);
        Assert.Equal(
            LivePlayback.RestartWaitingNotice,
            cursor.Live!.Notice);
        Assert.Equal(1, cursor.Index);
    }

    [Fact]
    public void AStoppingStepperSaysStoppingAndNotStopped()
    {
        var episode = new FakeEpisode(Maximum: 10) { Status = LiveStepperStatus.Stopping };
        var cursor = new LivePlayback(episode);

        Assert.Equal("stopping", cursor.Live!.Notice);
        Assert.NotEqual("stopped", cursor.Live.Notice);
    }

    [Fact]
    public void AStoppedStepperSaysStoppedBecauseTheThreadWasJoined()
    {
        var episode = new FakeEpisode(Maximum: 10) { Status = LiveStepperStatus.Stopped };
        var cursor = new LivePlayback(episode);

        Assert.Equal("stopped", cursor.Live!.Notice);
    }

    [Fact]
    public void AFinishedEpisodeSaysTheRecordedReasonAndEndsTheRun()
    {
        var episode = new FakeEpisode(Frames: 2, Maximum: 10, Finished: "resources-exhausted");
        var cursor = new LivePlayback(episode);

        Assert.True(cursor.Live!.IsFinished);
        Assert.Equal("resources-exhausted", cursor.Live.FinishedReason);
        Assert.True(cursor.IsFinished);
    }

    [Fact]
    public void AnUnfinishedEpisodeIsNeverReportedAsFinished()
    {
        var episode = new FakeEpisode(Frames: 2, Maximum: 10);
        var cursor = new LivePlayback(episode);

        Assert.False(cursor.Live!.IsFinished);
        Assert.False(cursor.IsFinished);
    }

    [Fact]
    public void AnEpisodeWithNoReasonIsNotFinishedAndSaysNotRecorded()
    {
        // A blank reason is not a reason: claiming an episode ended on the strength
        // of whitespace would be the viewer inventing a verdict.
        var episode = new FakeEpisode(Frames: 2, Maximum: 10, Finished: "  ");
        var cursor = new LivePlayback(episode);

        Assert.False(cursor.Live!.IsFinished);
        Assert.False(cursor.IsFinished);
        Assert.Equal("not recorded", CockpitEpisodes.FinishedReason(cursor.Frame, cursor.Live));

        // And the same for a frame that carries no reason at all.
        Assert.Equal(
            "not recorded",
            CockpitEpisodes.FinishedReason(new ReplayFrame(1, false, Array.Empty<WorldAgent>(), Array.Empty<int>(), "Move(0)", null, false, null, null), null));
    }

    [Fact]
    public void AKeyWithNoBindingChangesNothing()
    {
        var episode = new FakeEpisode(Maximum: 10);
        var cursor = new LivePlayback(episode);

        Assert.False(cursor.Apply(Character('z')));
        Assert.Equal(0, cursor.Index);
        Assert.True(cursor.IsPaused);
    }

    [Fact]
    public void TheFramesOnScreenAreTheSameShapeAReplayViewerWouldDraw()
    {
        var episode = new FakeEpisode(Frames: 3, Maximum: 10);
        var cursor = new LivePlayback(episode);
        cursor.Apply(Character('n'));

        // A start frame, then one per produced tick, and every one of them a value
        // the panes can draw: the replay viewer's history is this list.
        Assert.Equal(4, cursor.Document.Count);
        Assert.True(cursor.Document.Frames[0].IsStart);
        Assert.Null(cursor.Document.Frames[0].Actions);
        for (var i = 1; i < cursor.Document.Count; i++)
        {
            Assert.False(cursor.Document.Frames[i].IsStart);
            Assert.Equal(i, cursor.Document.Frames[i].Tick);
            Assert.NotNull(cursor.Document.Frames[i].Actions);
            Assert.NotEmpty(cursor.Document.Frames[i].Agents);
        }
    }

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    /// <summary>
    /// A stepper stand-in: frames it produces on request, a frontier the test
    /// controls, and the flags the cursor reads. The real one runs a simulation on
    /// its own thread; this one answers immediately, which is what makes the
    /// cursor's rules assertable without a clock.
    /// </summary>
    private sealed class FakeEpisode : ILiveEpisode
    {
        private readonly List<ReplayFrame> _frames;

        internal FakeEpisode(int Frames = 0, int Maximum = 10, string? Finished = null, bool failed = false)
        {
            MaximumTicks = Maximum;
            FinishedReason = Finished;
            Failed = failed;
            _frames = new List<ReplayFrame>();
            Add(0, isStart: true);

            for (var tick = 1; tick <= Frames; tick++)
            {
                Add(tick, isStart: false);
            }
        }

        /// <summary>How many frames each request produces.</summary>
        internal int TicksPerRequest { get; init; } = 1;

        /// <summary>Whether a restart has to wait for the running turn to finish.</summary>
        internal bool RestartsBlock { get; init; }

        /// <summary>What the stepper is doing right now.</summary>
        internal LiveStepperStatus Status { get; set; } = LiveStepperStatus.Running;

        /// <summary>How many single ticks have been asked for.</summary>
        internal int RequestedTicks { get; private set; }

        /// <summary>How many restarts have been accepted.</summary>
        internal int Restarts { get; private set; }

        public IReadOnlyList<ReplayFrame> Frames => _frames;

        public WorldMap Map { get; } = new(
            new[]
            {
                new WorldZone(0, "0", 0, 0),
                new WorldZone(1, "1", 10, 0),
                new WorldZone(2, "2", 20, 0),
                new WorldZone(3, "3", 30, 0),
                new WorldZone(4, "4", 40, 0),
            },
            Array.Empty<WorldResource>(),
            new[] { new WorldEdge(0, 0, 1, Capacity: 2) });

        public int MaximumTicks { get; }

        public string? FinishedReason { get; set; }

        public LiveStepperStatus StepperStatus => Status;

        public bool HasStopped => CockpitEpisodes.HasReason(FinishedReason) || Failed;

        /// <summary>Whether the simulation failed, which ends the run with no reason.</summary>
        internal bool Failed { get; init; }

        public string? Notice => Status switch
        {
            LiveStepperStatus.Stopping => LivePlayback.StoppingNotice,
            LiveStepperStatus.Stopped => LivePlayback.StoppedNotice,
            _ => RestartsBlock ? LivePlayback.RestartWaitingNotice : null,
        };

        public void RequestTick()
        {
            if (FinishedReason is not null || Failed)
            {
                return;
            }

            for (var i = 0; i < TicksPerRequest; i++)
            {
                RequestedTicks++;
                Add(RequestedTicks, isStart: false);
            }
        }

        public void RequestRestart()
        {
            if (RestartsBlock)
            {
                return;
            }

            Restarts++;
            _frames.Clear();
            Add(0, isStart: true);
            RequestedTicks = 0;
        }

        /// <summary>Lets the cursor see whatever the episode has produced.</summary>
        internal void Settle()
        {
        }

        private void Add(int tick, bool isStart) =>
            _frames.Add(new ReplayFrame(
                tick,
                isStart,
                new[]
                {
                    new WorldAgent(0, tick % 4, tick),
                    new WorldAgent(1, 1 + (tick % 3), tick / 2),
                },
                Array.Empty<int>(),
                isStart ? null : $"agent0: Move({tick % 4}); agent1: Wait",
                stateDigest: null,
                isTerminal: false,
                terminalReason: null,
                winnerSlot: null));
    }
}