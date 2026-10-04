using System.Reflection;
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

    [Fact]
    public void AnEndedEpisodeShowsOnlyAReasonTheSimulationRecorded()
    {
        // A terminal tick carries the engine's own reason, and that is what is
        // shown: the words the engine used, not a paraphrase of them.
        var collectors = new CollectingRig();
        using (var episode = Episode(collectors.NewRoster, 50))
        {
            Produce(episode, 10);

            Assert.NotNull(episode.FinishedReason);
            Assert.Equal("resources-exhausted", episode.FinishedReason);
        }

        // A budget that ran out is a terminal tick too, and the engine records
        // "tick-limit" on it. That is the engine's word, so that is what is shown —
        // not the viewer's own sentence about the budget.
        var waiters = new CountingRig();
        using (var episode = Episode(waiters.NewRoster, 4))
        {
            Produce(episode, 10);

            Assert.Equal("tick-limit", episode.FinishedReason);
            Assert.Equal(episode.FinishedReason, new LivePlayback(episode).Live!.FinishedReason);
        }

        // The viewer's own words are not a reason. Where the simulation recorded none,
        // the value is the recording's own words for an absence — the same words the
        // scoreboard's end row prints — and the two panes' agreement on that is
        // proven where the panes are drawn, in CockpitLiveStateTests.
        Assert.Equal("not recorded", CockpitEpisodes.NotRecorded);
    }

    [Fact]
    public void ABudgetThatRanOutCarriesTheEnginesOwnReasonAndNothingElse()
    {
        // The whole of the fabricated-reason surface, checked against a run: every
        // reason this episode ever reports must be one the engine recorded, and the
        // viewer's own budget sentence must appear nowhere in it.
        var rig = new CountingRig();
        using var episode = Episode(rig.NewRoster, 3);

        var seen = new List<string?>();
        for (var tick = 0; tick < 6 && episode.FinishedReason is null; tick++)
        {
            seen.Add(episode.FinishedReason);
            var before = episode.Frames.Count;
            episode.RequestTick();
            WaitFor(() => episode.Frames.Count != before || episode.FinishedReason is not null);
            seen.Add(episode.FinishedReason);
        }

        Assert.Equal("tick-limit", episode.FinishedReason);
        Assert.All(seen.Where(reason => reason is not null), reason =>
        {
            Assert.Equal("tick-limit", reason);
            Assert.DoesNotContain("step limit", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("--steps", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("budget", reason, StringComparison.OrdinalIgnoreCase);
        });

        // The budget the viewer was given is still shown, as the viewer's own fact,
        // and the episode produced exactly that many ticks before it ended.
        Assert.Equal(3, episode.MaximumTicks);
        Assert.Equal(3, episode.Frames.Count - 1);
    }

    [Fact]
    public void TheEpisodeCarriesNoReasonWordingOfItsOwn()
    {
        // The fabricated fallback has to be gone rather than merely unreached, or the
        // next person to read this file finds a sentence that looks like a verdict the
        // simulation never reached. Checked against the type's own surface: the only
        // strings it publishes are about being still deciding and about nothing at
        // all, and none of them names an ending.
        var published = typeof(LiveEpisode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => value is not null)
            .ToArray();

        Assert.NotEmpty(published);
        Assert.All(published, wording =>
        {
            foreach (var verdict in new[] { "limit", "exhausted", "reason", "ended", "finished" })
            {
                Assert.DoesNotContain(verdict, wording, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void ARestartDisposesTheRosterThatDecidedBeforeItIsReplaced()
    {
        var rig = new DisposableRig();
        using var episode = Episode(rig.NewRoster, 6);
        var cursor = new LivePlayback(episode);

        cursor.Apply(Character('n'));
        WaitFor(() => Produced(episode) >= 1);

        // The instances that decided in the first episode, by identity.
        var first = rig.Decided.ToArray();
        Assert.Equal(2, first.Length);
        Assert.Empty(rig.Disposed);

        cursor.Apply(Character('r'));
        WaitFor(() => Produced(episode) == 0);

        // Disposed exactly once each, and they are the instances that ran — a restart
        // that dropped them without disposing would leak whatever they own.
        Assert.Equal(first, rig.Disposed.ToArray());

        // Nothing may be disposed while a decide of that agent is running.
        Assert.All(rig.Disposals, disposal => Assert.Equal(0, disposal.Deciding));

        // The new episode's roster is a different set of instances, and disposing the
        // outgoing pair did not dispose them.
        cursor.Apply(Character('n'));
        WaitFor(() => Produced(episode) >= 1);

        var second = rig.Decided.Skip(first.Length).ToArray();
        Assert.Equal(2, second.Length);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(first, rig.Disposed.ToArray());
    }

    [Fact]
    public void ARestartAskedForMidTurnDisposesNothingUntilThatTurnHasEnded()
    {
        var rig = new BlockingDisposableRig();
        var episode = Episode(rig.NewRoster, GenerousBudget);
        var cursor = new LivePlayback(episode);

        try
        {
            cursor.Apply(Character(' '));
            episode.RequestTick();
            WaitFor(() => rig.Blocked);

            cursor.Apply(Character('r'));

            // The turn is still deciding, so the restart is still waiting and nothing
            // at all has been disposed: killing a decide is the one thing disposal
            // must never do.
            Thread.Sleep(250);
            Assert.Empty(rig.Disposed);
            Assert.Equal(LivePlayback.RestartWaitingNotice, episode.Notice);

            rig.ReleaseAll();

            // Only now, between turns, is the roster that decided disposed.
            WaitFor(() => rig.Disposed.Count == 2);
            Assert.All(rig.Disposals, disposal => Assert.Equal(0, disposal.Deciding));
        }
        finally
        {
            rig.ReleaseAll();
            episode.Stop(TimeSpan.FromSeconds(20));
            episode.Dispose();
        }
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

        /// <summary>How many decides are inside an agent right now.</summary>
        internal int Deciding;

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

    /// <summary>Two agents that collect the map's one resource, so it ends early.</summary>
    private sealed class CollectingRig
    {
        internal IAgent[] NewRoster() => new IAgent[] { new Fake(0), new Fake(1) };

        private sealed class Fake : IAgent
        {
            internal Fake(int agentId) => AgentId = agentId;

            public int AgentId { get; }

            public AgentAction Decide(Observation observation)
            {
                var me = observation.AgentStates.First(agent => agent.AgentId == AgentId);
                foreach (var resource in observation.Map.Resources.OrderBy(resource => resource.Id))
                {
                    if (resource.ZoneId == me.ZoneId && !observation.Claims.Contains(resource.Id))
                    {
                        return new AgentAction(ActionKind.Collect, ResourceId: resource.Id);
                    }
                }

                return new AgentAction(ActionKind.Wait);
            }
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
    /// that decided are the instances disposed" is checkable rather than assumed,
    /// and how many decides were running when each one was disposed.
    /// </summary>
    private sealed class DisposableRig
    {
        private static int _nextSerial;

        private readonly List<IAgent> _decided = new();
        private readonly List<IAgent> _disposed = new();
        private readonly List<Disposal> _disposals = new();
        private int _deciding;

        internal IReadOnlyList<IAgent> Decided
        {
            get { lock (_decided) { return _decided.ToArray(); } }
        }

        internal IReadOnlyList<IAgent> Disposed
        {
            get { lock (_disposed) { return _disposed.ToArray(); } }
        }

        internal IReadOnlyList<Disposal> Disposals
        {
            get { lock (_disposals) { return _disposals.ToArray(); } }
        }

        internal IAgent[] NewRoster() => new IAgent[] { new Fake(this, 0), new Fake(this, 1) };

        internal AgentAction Decide(IAgent self)
        {
            Interlocked.Increment(ref _deciding);
            try
            {
                lock (_decided)
                {
                    _decided.Add(self);
                }

                return new AgentAction(ActionKind.Wait);
            }
            finally
            {
                Interlocked.Decrement(ref _deciding);
            }
        }

        internal void Dispose(IAgent self)
        {
            lock (_disposed)
            {
                _disposed.Add(self);
                _disposals.Add(new Disposal(self, Volatile.Read(ref _deciding)));
            }
        }

        /// <summary>One disposal, and what the agent was doing when it happened.</summary>
        internal sealed record Disposal(IAgent Agent, int Deciding);

        private sealed class Fake : IAgent, IDisposable
        {
            private readonly DisposableRig _rig;

            /// <summary>
            /// A serial in the printed name, so a failed identity assertion shows
            /// which instances differ instead of two identical-looking pairs.
            /// </summary>
            private readonly int _serial = Interlocked.Increment(ref _nextSerial);

            internal Fake(DisposableRig rig, int agentId)
            {
                _rig = rig;
                AgentId = agentId;
            }

            public int AgentId { get; }

            public AgentAction Decide(Observation observation) => _rig.Decide(this);

            public void Dispose() => _rig.Dispose(this);

            public override string ToString() => $"Fake#{_serial}";
        }
    }

    /// <summary>Disposable agents that also block, so disposal order can be checked.</summary>
    private sealed class BlockingDisposableRig : BlockingRig
    {
        private readonly List<IAgent> _disposed = new();
        private readonly List<DisposableRig.Disposal> _disposals = new();

        internal new IAgent[] NewRoster() => new IAgent[]
        {
            new DisposableFake(this, 0), new DisposableFake(this, 1),
        };

        internal IReadOnlyList<IAgent> Disposed
        {
            get { lock (_disposed) { return _disposed.ToArray(); } }
        }

        internal IReadOnlyList<DisposableRig.Disposal> Disposals
        {
            get { lock (_disposals) { return _disposals.ToArray(); } }
        }

        internal void Dispose(IAgent self)
        {
            lock (_disposed)
            {
                _disposed.Add(self);
                _disposals.Add(new DisposableRig.Disposal(self, Volatile.Read(ref this.Deciding)));
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

            public AgentAction Decide(Observation observation)
            {
                Interlocked.Increment(ref _rig.Deciding);
                try
                {
                    return base.Decide(observation);
                }
                finally
                {
                    Interlocked.Decrement(ref _rig.Deciding);
                }
            }

            public void Dispose() => _rig.Dispose(this);
        }
    }
}