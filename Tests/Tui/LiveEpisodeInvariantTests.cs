using Lattice.Agents;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The live viewer's promises about <em>how much</em> it computes and <em>which</em>
/// agents it throws away, asserted from outside.
/// </summary>
/// <remarks>
/// <para>
/// These deliberately do not read the code's own counters. Each probe drives the
/// real <see cref="LiveEpisode"/> with a fake agent and then reads
/// <see cref="ILiveEpisode.Frames"/> — the frames a viewer would actually be shown
/// — because "at most one tick beyond what the viewer has consumed" is a claim
/// about what exists, not about how many times a method was called.
/// </para>
/// </remarks>
public class LiveEpisodeInvariantTests
{
    /// <summary>Enough budget that a queue ahead of the viewer is unmistakable.</summary>
    private const int GenerousBudget = 200;

    [Fact]
    public void ARunningViewerAtTheFrontierNeverQueuesMoreThanOneTickAhead()
    {
        var rig = new BlockingRig();
        using var episode = Episode(rig.NewRoster, GenerousBudget);
        var cursor = new LivePlayback(episode);

        cursor.Apply(Character(' '));                       // resume

        // The viewer's own first request: a quarter of a second at four steps per
        // second is not yet a whole tick, so it keeps ticking until one is.
        for (var pass = 0; pass < 5 && !rig.Blocked; pass++)
        {
            cursor.Advance(TimeSpan.FromMilliseconds(150));
        }

        WaitFor(() => rig.Blocked);

        // The viewer is running and parked at the frontier with nothing produced.
        // Every one of these passes is a frame the host would have written.
        for (var pass = 0; pass < 50; pass++)
        {
            cursor.Advance(TimeSpan.FromMilliseconds(150));
        }

        rig.ReleaseAll();
        WaitQuiescent(episode);

        // Stop and join, then count. A backlog only shows if the pump was allowed
        // to run it, so the drain comes first and the join makes the count final.
        Assert.True(episode.Stop(TimeSpan.FromSeconds(20)));

        // Exactly one tick exists, and it is the one that was asked for. Before
        // this was fixed the same sequence produced every queued tick behind the
        // blocked agent: 50 passes became 50 requests.
        Assert.Equal(1, Produced(episode));
    }

    [Fact]
    public void APausedViewerAsksForNothingButTheTickItNames()
    {
        var rig = new BlockingRig();
        using var episode = Episode(rig.NewRoster, GenerousBudget);
        var cursor = new LivePlayback(episode);

        // Navigation keys, pressed many times, while paused on the start frame.
        foreach (var key in new[]
        {
            Character(']'), Character('['),
            new TuiKey(TuiKeyKind.End), new TuiKey(TuiKeyKind.Home),
            new TuiKey(TuiKeyKind.Right), new TuiKey(TuiKeyKind.PageDown),
        })
        {
            cursor.Apply(key);
        }

        Assert.Equal(0, Produced(episode));
        Assert.Equal(0, rig.Decides);

        // One n, one tick — and only one, however many times the key is pressed
        // while that request is still outstanding.
        cursor.Apply(Character('n'));
        cursor.Apply(Character('n'));
        cursor.Apply(Character('n'));

        rig.ReleaseAll();
        WaitQuiescent(episode);
        Assert.True(episode.Stop(TimeSpan.FromSeconds(20)));

        Assert.Equal(1, Produced(episode));
    }

    [Fact]
    public void EndWhilePausedOnlyWalksFramesThatAlreadyExist()
    {
        var rig = new CountingRig();
        using var episode = Episode(rig.NewRoster, 50);
        var cursor = new LivePlayback(episode);

        Produce(episode, 3);
        Assert.Equal(3, Produced(episode));

        cursor.Apply(new TuiKey(TuiKeyKind.End));

        // On the last produced frame, and the stepper was not asked for anything:
        // a live episode has no end to jump to, so End goes as far as it exists.
        Assert.Equal(3, cursor.Index);
        Assert.Equal(3, Produced(episode));
        Assert.Equal(6, rig.Decides);

        cursor.Apply(new TuiKey(TuiKeyKind.Right));

        Assert.Equal(3, cursor.Index);
        Assert.Equal(3, Produced(episode));
    }

    [Fact]
    public void TheAgentsThatDecidedAreTheAgentsDisposedAndOnlyAfterTheJoin()
    {
        var rig = new DisposableRig();
        using (var episode = Episode(rig.NewRoster, 4))
        {
            var cursor = new LivePlayback(episode);
            cursor.Apply(Character('n'));
            WaitFor(() => Produced(episode) >= 1);
        }

        // Identity, not type: the instances that ran Decide are the instances that
        // were disposed, each exactly once. A roster factory called at dispose time
        // hands back a fresh pair that never decided anything, and those are the
        // instances that would be disposed.
        Assert.Equal(2, rig.Decided.Count);
        Assert.Equal(2, rig.Disposed.Count);
        Assert.Equal(rig.Decided, rig.Disposed);
    }

    [Fact]
    public void AnAgentIsNotDisposedWhileItsStepperThreadIsStillDeciding()
    {
        var rig = new BlockingDisposableRig();
        var episode = Episode(rig.NewRoster, GenerousBudget);
        var cursor = new LivePlayback(episode);

        cursor.Apply(Character(' '));
        episode.RequestTick();
        WaitFor(() => rig.Blocked);

        // A close that cannot join leaves the deciding turn alone: nothing is
        // disposed, and nothing claims the stepper stopped.
        Assert.False(episode.Stop(TimeSpan.FromMilliseconds(200)));
        Assert.Empty(rig.Disposed);
        Assert.Equal(LiveStepperStatus.Stopping, episode.StepperStatus);

        rig.ReleaseAll();
        Assert.True(episode.Join(TimeSpan.FromSeconds(20)));
        Assert.True(episode.IsJoined);
        Assert.Equal(LiveStepperStatus.Stopped, episode.StepperStatus);

        episode.Dispose();
        Assert.Equal(2, rig.Disposed.Count);
    }

    private static int Produced(LiveEpisode episode) => episode.Frames.Count - 1;

    private static void Produce(LiveEpisode episode, int ticks)
    {
        for (var tick = 0; tick < ticks && episode.FinishedReason is null && episode.Failure is null; tick++)
        {
            var before = episode.Frames.Count;
            episode.RequestTick();
            WaitFor(() => episode.Frames.Count != before || episode.FinishedReason is not null);
        }
    }

    /// <summary>
    /// Waits until the stepper has produced nothing new for two consecutive polls.
    /// This is how a queue behind a blocked agent becomes visible: it is drained
    /// the moment the agent is released, so counting before it drains would hide it.
    /// </summary>
    private static void WaitQuiescent(LiveEpisode episode)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var last = -1;
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            var now = episode.Frames.Count;
            stable = now == last ? stable + 1 : 0;
            last = now;

            if (stable >= 3)
            {
                return;
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the stepper thread never went quiet within 20 seconds.");
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("the stepper thread did not reach the expected state within 20 seconds.");
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// An episode over a real map with a generous budget, so a queue ahead of the
    /// viewer is not silently bounded by the episode ending.
    /// </summary>
    /// <summary>
    /// An episode whose roster is a <em>factory</em>, exactly as the shipped setup's
    /// is: every call builds a fresh pair. That is what makes "the roster property
    /// hands back the agents that actually decided" a real question rather than a
    /// tautology.
    /// </summary>
    private static LiveEpisode Episode(Func<IAgent[]> roster, int maxSteps) =>
        new(new LiveEpisodeSetup(
            MapGenerator.Generate(42, new GeneratorConfig(2, 2, 1, 1, 1, GeneratorConfig.DefaultRetryCap)),
            new SimulationConfig(AgentCount: 2, MaxTicks: maxSteps),
            roster,
            maxSteps,
            Seed: 42,
            Label: "invariant-episode",
            Roles: new[] { "First", "Second" },
            Rules: DynamicMapRuleSet.None));

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    /// <summary>
    /// What two fake agents share: whether a decide is outstanding, how often each
    /// was asked, and the one event that lets them finish. Shared on purpose — the
    /// invariant under test is about the pair as a tick.
    /// </summary>
    private class BlockingRig
    {
        private readonly ManualResetEventSlim _released = new(false);
        private readonly List<IAgent> _built = new();
        private readonly List<IAgent> _decided = new();
        private int _decides;
        private int _blocked;

        /// <summary>A fresh pair per call, as the shipped setup's factory is.</summary>
        internal IAgent[] NewRoster()
        {
            var pair = new IAgent[] { new Fake(this, 0), new Fake(this, 1) };
            lock (_built)
            {
                _built.AddRange(pair);
            }

            return pair;
        }

        /// <summary>The instances that were constructed, in order.</summary>
        internal IReadOnlyList<IAgent> Built
        {
            get { lock (_built) { return _built.ToArray(); } }
        }

        /// <summary>The instances that actually ran a decide.</summary>
        internal IReadOnlyList<IAgent> Decided
        {
            get { lock (_decided) { return _decided.ToArray(); } }
        }

        internal int Decides => Volatile.Read(ref _decides);

        internal bool Blocked => Volatile.Read(ref _blocked) > 0;

        internal void ReleaseAll() => _released.Set();

        internal AgentAction Decide(IAgent self)
        {
            Interlocked.Increment(ref _decides);
            lock (_decided)
            {
                _decided.Add(self);
            }

            if (!_released.IsSet)
            {
                Interlocked.Increment(ref _blocked);
            }

            if (!_released.Wait(TimeSpan.FromSeconds(30)))
            {
                Assert.Fail("a blocking agent was never released; the test would otherwise pass on a timeout.");
            }

            return new AgentAction(ActionKind.Wait);
        }

        internal class Fake : IAgent
        {
            private readonly BlockingRig _rig;

            internal Fake(BlockingRig rig, int agentId)
            {
                _rig = rig;
                AgentId = agentId;
            }

            public int AgentId { get; }

            public AgentAction Decide(Observation observation) => _rig.Decide(this);
        }
    }

    /// <summary>Two agents that answer immediately, so a tick really completes.</summary>
    private sealed class CountingRig
    {
        private readonly List<int> _decided = new();

        internal int Decides { get; private set; }

        internal IAgent[] NewRoster() => new IAgent[] { new Fake(this, 0), new Fake(this, 1) };

        internal AgentAction Decide(int agentId)
        {
            lock (_decided)
            {
                _decided.Add(agentId);
                Decides++;
            }

            return new AgentAction(ActionKind.Wait);
        }

        private sealed class Fake : IAgent
        {
            private readonly CountingRig _rig;

            internal Fake(CountingRig rig, int agentId)
            {
                _rig = rig;
                AgentId = agentId;
            }

            public int AgentId { get; }

            public AgentAction Decide(Observation observation) => _rig.Decide(AgentId);
        }
    }

    /// <summary>
    /// Two disposable agents that remember their own identity, so "the instances
    /// that decided are the instances disposed" is checkable rather than assumed.
    /// </summary>
    private sealed class DisposableRig
    {
        private readonly List<IAgent> _decided = new();
        private readonly List<IAgent> _disposed = new();

        internal IReadOnlyList<IAgent> Decided => _decided;

        internal IReadOnlyList<IAgent> Disposed => _disposed;

        internal IAgent[] NewRoster() => new IAgent[] { new Fake(this, 0), new Fake(this, 1) };

        internal AgentAction Decide(IAgent self)
        {
            lock (_decided)
            {
                _decided.Add(self);
            }

            return new AgentAction(ActionKind.Wait);
        }

        internal void Dispose(IAgent self)
        {
            lock (_disposed)
            {
                _disposed.Add(self);
            }
        }

        private sealed class Fake : IAgent, IDisposable
        {
            private readonly DisposableRig _rig;

            internal Fake(DisposableRig rig, int agentId)
            {
                _rig = rig;
                AgentId = agentId;
            }

            public int AgentId { get; }

            public AgentAction Decide(Observation observation) => _rig.Decide(this);

            public void Dispose() => _rig.Dispose(this);
        }
    }

    /// <summary>Disposable agents that also block, so disposal order can be checked.</summary>
    private sealed class BlockingDisposableRig : BlockingRig
    {
        private readonly List<IAgent> _disposed = new();

        internal new IAgent[] NewRoster() => new IAgent[]
        {
            new DisposableFake(this, 0), new DisposableFake(this, 1),
        };

        internal IReadOnlyList<IAgent> Disposed => _disposed;

        internal void Dispose(IAgent self)
        {
            lock (_disposed)
            {
                _disposed.Add(self);
            }
        }

        private sealed class DisposableFake : BlockingRig.Fake, IDisposable
        {
            private readonly BlockingDisposableRig _rig;

            internal DisposableFake(BlockingDisposableRig rig, int agentId)
                : base(rig, agentId)
            {
                _rig = rig;
            }

            public void Dispose() => _rig.Dispose(this);
        }
    }
}