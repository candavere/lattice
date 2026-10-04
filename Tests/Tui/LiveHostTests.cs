using Lattice.Agents;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Trajectories;
using Lattice.Tui;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The live viewer, driven the way a terminal drives it: a scripted key sequence
/// through the real host, a stepper whose agent can be made to block, and the exit
/// statuses a caller can act on.
/// </summary>
/// <remarks>
/// The blocking agent is the point of most of these. It is the one case where the
/// cancellation contract is visible from outside: a decide already running cannot
/// be interrupted, so the keys must stay live, a close must not wait for ever, a
/// restart must not overlap, and "stopped" must not appear until the turn has
/// returned and the thread has been joined.
/// </remarks>
public class LiveHostTests
{
    private static readonly TerminalCapabilities Interactive = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    [Fact]
    public void TheHostKeepsHandlingKeysWhileAnAgentIsStillDeciding()
    {
        var surface = new RecordingSurface();
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);
        var keys = new ScriptedSteps(
            agents,
            (() => true, Character(' ')),
            (() => true, (TuiKey?)null),
            (() => agents[0].WasBlocked, Character('q')));

        var result = Run(surface, episode, keys);
        episode.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
        Assert.True(agents[0].WasBlocked, "the agent was never blocked, so the test proves nothing.");

        // The keys were handled while the decide was running, and the state the
        // episode reports for it is "stopping" and never "stopped" until a join.
        Assert.Equal(LiveStepperStatus.Stopping, episode.StepperStatus);
        Assert.False(episode.IsJoined);
    }

    [Fact]
    public void AQuitDuringABlockedTurnRestoresTheTerminalWithoutWaitingForTheAgent()
    {
        var surface = new RecordingSurface();
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);
        var keys = new ScriptedSteps(
            agents,
            (() => true, Character(' ')),
            (() => true, (TuiKey?)null),
            (() => agents[0].WasBlocked, Character('q')));

        var result = Run(surface, episode, keys);
        episode.Stop();

        // The run ended on the quit key, not on the agent: the thread is background
        // and the process is about to exit, so there is nothing to wait for.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
        Assert.Equal(1, surface.Entries);
        Assert.False(agents[0].Released, "the agent was released, so the close did not race a decide.");
        Assert.Equal(LiveStepperStatus.Stopping, episode.StepperStatus);
    }

    [Fact]
    public void StoppedIsOnlyEverReportedAfterTheTurnReturnsAndTheThreadIsJoined()
    {
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);
        var keys = new ScriptedSteps(
            agents,
            (() => true, Character(' ')),
            (() => true, (TuiKey?)null),
            (() => agents[0].WasBlocked, Character('q')));

        Run(new RecordingSurface(), episode, keys);
        episode.Stop();

        // Still deciding: not joined, and therefore not "stopped".
        Assert.Equal(LiveStepperStatus.Stopping, episode.StepperStatus);
        Assert.False(episode.IsJoined);

        foreach (var agent in agents)
        {
            agent.Release();
        }

        Assert.True(episode.Join(TimeSpan.FromSeconds(10)));
        Assert.True(episode.IsJoined);
        Assert.Equal(LiveStepperStatus.Stopped, episode.StepperStatus);
    }

    [Fact]
    public void ARestartDuringABlockedTurnWaitsAndNeverOverlaps()
    {
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);

        episode.RequestTick();
        WaitFor(() => agents[0].WasBlocked);

        // A restart asked for while a turn is deciding is held, and says why. The
        // stepper thread that was deciding is the one that will carry it out, so it
        // cannot be replaced while that decide is in progress.
        episode.RequestRestart();
        episode.RequestRestart();

        Assert.Equal(LivePlayback.RestartWaitingNotice, episode.Notice);
        Assert.Equal(0, episode.Frames.Count - 1);

        foreach (var agent in agents)
        {
            agent.Release();
        }

        // The turn the thread was in finishes first, and only then is the pending
        // restart carried out: the notice clears, which is the only thing that can
        // have cleared it.
        foreach (var agent in agents)
        {
            agent.Release();
        }

        WaitFor(() => episode.Notice is null);

        var beforeTheNewTick = agents[0].Decides;
        episode.RequestTick();
        WaitFor(() => agents[0].Decides > beforeTheNewTick);
        Assert.True(episode.Stop());

        // The history is the new episode's: a start frame, and the one tick that came
        // after the restart, decided by a stepper built after it.
        Assert.True(episode.Frames[0].IsStart);
        Assert.Equal(0, episode.Frames[0].Tick);
        Assert.Equal(2, episode.Frames.Count);
        Assert.Equal(1, episode.Frames[1].Tick);
    }

    [Fact]
    public void TheScreenSaysARestartIsWaitingRatherThanSwallowingTheKey()
    {
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);
        var cursor = new LivePlayback(episode);

        episode.RequestTick();
        WaitFor(() => agents[0].WasBlocked);

        // A restart that cannot happen yet is still a change the reader must see:
        // the cursor reports one and the pane draws the reason.
        Assert.True(cursor.Apply(Character('r')));
        var rows = CockpitLayout.Render(new CockpitRequest(
            cursor.Document,
            cursor.Index,
            new PaneSize(100, 30),
            GlyphMode.Unicode,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: false, StepsPerSecond: 4.0),
            Live: cursor.Live))
            .ToLines();

        Assert.Contains(LivePlayback.RestartWaitingNotice, string.Join("\n", rows), StringComparison.Ordinal);
    }

    [Fact]
    public void ARestartFromIdleReplacesTheEpisodeAndClearsItsHistory()
    {
        var agents = new IAgent[] { new CountingAgent(0), new CountingAgent(1) };
        using var episode = Episode(agents, maxSteps: 6);

        // Produce three ticks without a blocking agent, then restart.
        Produce(episode, 3);
        Assert.Equal(3, episode.Frames.Count - 1);

        episode.RequestRestart();
        WaitFor(() => episode.Frames.Count == 1);

        Assert.Equal(0, episode.Frames.Count - 1);
        Assert.Null(episode.FinishedReason);
    }

    [Fact]
    public void AThrowingAgentEndsTheRunWithARestoredTerminalAndTheFailureToReport()
    {
        var surface = new RecordingSurface();
        var agents = new IAgent[] { new ThrowingAgent(0, "the agent gave up"), new CountingAgent(1) };
        using var episode = Episode(agents, maxSteps: 4);
        var keys = new ScriptedSteps(
            Array.Empty<BlockingAgent>(),
            (() => true, Character(' ')),
            (() => true, (TuiKey?)null),
            (() => true, (TuiKey?)null),
            (() => episode.Failure is not null, (TuiKey?)null),
            (() => episode.Failure is not null, Character('q')));

        var result = Run(surface, episode, keys);

        // The host restored the terminal exactly once and stopped drawing: a failed
        // simulation ends the run rather than leaving a last frame looking live.
        Assert.Equal("restore", surface.Events[^1]);
        Assert.Equal(1, surface.Entries);
        Assert.True(episode.HasStopped);

        // The host does not decide the status; the command turns what the episode
        // ended with into one, and says nothing to stderr until the terminal is back.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, CliApp.LiveExitCode(episode.Failure));
        Assert.Contains("the agent gave up", episode.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal("", surface.ErrorsText);
    }

    [Fact]
    public void AFinishedEpisodeEndsTheRunWithTheSuccessStatusAndNoQuitKey()
    {
        var surface = new RecordingSurface();
        var agents = new IAgent[] { new CountingAgent(0), new CountingAgent(1) };
        using var episode = Episode(agents, maxSteps: 2);
        var keys = new PlainKeys(Character(' '));

        var result = Run(surface, episode, keys);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
        Assert.Equal("tick-limit", episode.FinishedReason);
        Assert.Contains("LIVE", surface.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live frames are the batch run's frames: the same setup, stepped by its
    /// own thread, is the episode <see cref="ScenarioRunner"/> produces.
    /// </summary>
    [Fact]
    public void TheLiveFramesAreTheBatchRunsFrames()
    {
        var setup = LiveEpisodeSetup.Infiltration(42, 100);
        var batch = ScenarioRunner.Run(
            setup.Map,
            setup.Config,
            setup.Roster,
            setup.MaxSteps,
            setup.Rules,
            recordPerceptions: true);

        using var episode = new LiveEpisode(setup);
        Produce(episode, setup.MaxSteps);
        var frames = episode.Frames;

        Assert.Equal(batch.Results.Length + 1, frames.Count);
        Assert.True(frames[0].IsStart);

        for (var i = 0; i < batch.Results.Length; i++)
        {
            var live = frames[i + 1];
            var result = batch.Results[i];
            var observation = result.Observations[0];

            Assert.Equal(result.Info.StepNumber, live.Tick);
            Assert.Equal(result.Info.IsTerminal, live.IsTerminal);
            Assert.Equal(result.Info.Reason, live.TerminalReason);
            Assert.Equal(result.Info.WinnerAgentId, live.WinnerSlot);
            Assert.Equal(
                observation.AgentStates.Select(agent => (agent.AgentId, agent.ZoneId, agent.Score)),
                live.Agents.Select(agent => (agent.Slot, agent.ZoneId, agent.Score)));
            Assert.Equal(observation.Claims, live.Claims);
            Assert.Equal(
                TrajectoryPlayback.FormatActions(batch.Turns[i]),
                live.Actions);
        }
    }

    /// <summary>
    /// The equivalence a reader can check: the live viewer's setup, stepped and
    /// hashed tick by tick, is the artifact <c>lattice simulate</c> wrote. The
    /// recording comes from the untouched batch command, so this is not a
    /// comparison of two paths written side by side.
    /// </summary>
    [Theory]
    [InlineData("--seed", "42", "--steps", "12")]
    [InlineData("--seed", "7", "--steps", "8", "--agent", "random")]
    [InlineData("--seed", "42", "--steps", "100", "--scenario", "infiltration")]
    public void TheLiveSetupTicksAndHashesExactlyAsTheBatchCommandRecorded(params string[] flags)
    {

        using var batchOut = new StringWriter();
        using var batchErr = new StringWriter();
        var exit = CliApp.Run(["simulate", "--quiet", .. flags], batchOut, batchErr);
        Assert.Equal(0, exit);

        var recording = TrajectoryReader.Read(new StringReader(batchOut.ToString()));
        var setup = SetupFor(flags);

        var stepper = setup.NewStepper();
        var hashes = new List<string>();
        while (stepper.CanStep)
        {
            stepper.Step();
            hashes.Add(SimulationStateHash.Compute(stepper.State, setup.Seed));
        }

        Assert.Equal(recording.Steps.Length, hashes.Count);
        for (var i = 0; i < hashes.Count; i++)
        {
            Assert.Equal(
                TrajectoryPlayback.FormatActions(recording.Steps[i].Actions),
                TrajectoryPlayback.FormatActions(stepper.Turns[i]));
            Assert.Equal(recording.Steps[i].StateHash, hashes[i]);
        }
    }

    /// <summary>
    /// Both subcommands go through the same host, so both are refused the same way
    /// for the same reason: one line of reason, the usage status, and nothing
    /// entered, written or started. The refusal is asked of the host's own
    /// predicate rather than of the process's real console, so the test states the
    /// rule instead of depending on where the suite happens to run.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AViewerWithARedirectedStreamIsRefusedWithTheUsageStatusAndNothingEntered(
        bool inputRedirected,
        bool outputRedirected)
    {
        var capabilities = Interactive with
        {
            InputRedirected = inputRedirected,
            OutputRedirected = outputRedirected,
        };
        var surface = new RecordingSurface();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = TuiHost.Run(new TuiHostRequest(
            new LivePlayback(Episode(new IAgent[] { new CountingAgent(0), new CountingAgent(1) }, 2)).Document,
            stdout,
            stderr,
            capabilities,
            ForceAscii: false,
            Session: surface,
            Keys: new PlainKeys(Character('q')),
            Clock: new TickingClock()));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.NotNull(result.Refusal);
        Assert.Equal("", stdout.ToString());
        Assert.Equal(0, surface.Entries);
        Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("redirected", result.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageLineNamesBothSubcommandsAndStaysOnStderr()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["tui"], stdout, stderr);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("tui replay", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("tui simulate", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveRunRefusesTheFlagThatWouldAskItToWriteAFile()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["tui", "simulate", "--seed", "42", "--out", "x.jsonl"], stdout, stderr);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("--out", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--agent", "bogus")]
    [InlineData("--scenario", "bogus")]
    [InlineData("--seed")]
    public void ALiveRunRefusesArgumentsItCannotRunWithTheUsageStatus(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["tui", "simulate", .. args], stdout, stderr);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.NotEqual("", stderr.ToString());
    }

    private static TuiRunResult Run(RecordingSurface surface, LiveEpisode episode, IKeySource keys)
    {
        var cursor = new LivePlayback(episode);
        return TuiHost.Run(new TuiHostRequest(
            cursor.Document,
            surface.Writer(),
            surface.Errors,
            Interactive,
            ForceAscii: false,
            Session: surface,
            keys,
            new TickingClock(),
            Cursor: cursor));
    }

    /// <summary>
    /// An episode over a real map, whose two roster slots the test supplies: a
    /// blocking one where the test needs a slow agent, a counting one where it does
    /// not.
    /// </summary>
    private static LiveEpisode Episode(IReadOnlyList<IAgent> roster, int maxSteps) =>
        new(new LiveEpisodeSetup(
            MapGenerator.Generate(42, new GeneratorConfig(2, 2, 1, 1, 1, GeneratorConfig.DefaultRetryCap)),
            new SimulationConfig(AgentCount: 2, MaxTicks: maxSteps),
            () => (IAgent[])roster,
            maxSteps,
            Seed: 42,
            Label: "test-episode",
            Roles: new[] { "First", "Second" },
            Rules: DynamicMapRuleSet.None));

    /// <summary>
    /// The live setup for the same flags <c>lattice simulate</c> was given, built by
    /// the same factories the command line uses.
    /// </summary>
    private static LiveEpisodeSetup SetupFor(string[] flags)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < flags.Length; i += 2)
        {
            values[flags[i]] = flags[i + 1];
        }

        var seed = ulong.Parse(values["--seed"], System.Globalization.CultureInfo.InvariantCulture);
        var steps = values.TryGetValue("--steps", out var text)
            ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
            : 100;

        return values.TryGetValue("--scenario", out _)
            ? LiveEpisodeSetup.Infiltration(seed, steps)
            : LiveEpisodeSetup.Skirmish(
                seed,
                steps,
                values.TryGetValue("--agent", out var agent) ? agent : "greedy",
                DynamicMapRuleSet.None);
    }

    /// <summary>Asks for each tick in turn and waits for the frame to arrive.</summary>
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
    /// Waits for a condition the stepper's own thread will make true. Bounded, and
    /// failing rather than hanging: a test that waits for ever is a test that lies.
    /// </summary>
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
    /// A clock that advances a fixed amount on every reading and never consults
    /// the wall. A run therefore advances at a rate the test owns, which is what
    /// lets a resumed episode reach its end without the test sleeping.
    /// </summary>
    private sealed class TickingClock : IUiClock
    {
        /// <summary>
        /// Large enough that one pass of the host's loop is a whole tick at the
        /// default four steps per second, so a scripted run needs one timed-out read
        /// to get a tick asked for and does not depend on how many passes a tick takes.
        /// </summary>
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(150);

        private TimeSpan _now;

        public TimeSpan Now
        {
            get
            {
                _now += Tick;
                return _now;
            }
        }
    }

    /// <summary>
    /// Keys handed to the host one at a time, each preceded by a condition that has
    /// to hold before it is delivered. The condition is what makes a blocked-decide
    /// test honest: without it the quit key can arrive before the stepper thread has
    /// so much as entered <c>Decide</c>, and the test would be asserting about a
    /// race it won by accident.
    /// </summary>
    private sealed class ScriptedSteps : IKeySource
    {
        private readonly Queue<(Func<bool> Gate, TuiKey? Key)> _steps;
        private readonly BlockingAgent[] _agents;
        private bool _quitDelivered;

        internal ScriptedSteps(BlockingAgent[] agents, params (Func<bool> Gate, TuiKey? Key)[] steps)
        {
            _agents = agents;
            _steps = new Queue<(Func<bool>, TuiKey?)>(steps);
        }

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            if (_steps.Count > 0)
            {
                var (gate, next) = _steps.Peek();
                WaitUntil(gate);

                _steps.Dequeue();
                if (next is not { } key1)
                {
                    // A timed-out read: the host advances, which is how a scripted
                    // run lets the simulation compute without pressing anything.
                    key = default;
                    return KeyWait.TimedOut;
                }

                key = key1;
                _quitDelivered |= key1.IsQuit;
                return KeyWait.Key;
            }

            // Released only once the quit key has been handed over, so the run ends
            // on a decide that is genuinely still outstanding and the agent is then
            // let finish, which is what the process exit would do for it anyway.
            if (_quitDelivered)
            {
                foreach (var agent in _agents)
                {
                    agent.Release();
                }
            }

            key = default;
            Yield(timeout);
            return KeyWait.TimedOut;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Waits out a read that has nothing to deliver, or at least yields once.
    /// <para>
    /// Returning at once turns the host's loop into a spin: every pass reads the
    /// episode under its lock, and a stepper thread trying to publish a frame can be
    /// starved long enough that the run looks hung. Measured before this: the
    /// finished-episode test aborted on 1 run in 8, with and without any change to
    /// the episode; 0 in 8 after. A real console blocks in its reader instead, which
    /// is why a live viewer never spins.
    /// </para>
    /// </summary>
    private static void Yield(TimeSpan timeout) =>
        Thread.Sleep(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1));

    /// <summary>Waits for a condition, bounded, failing rather than hanging.</summary>
    private static void WaitUntil(Func<bool> condition)
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

    /// <summary>Keys with nothing to release, for an episode that does not block.</summary>
    private sealed class PlainKeys : IKeySource
    {
        private readonly Queue<TuiKey> _keys;

        internal PlainKeys(params TuiKey[] keys) => _keys = new Queue<TuiKey>(keys);

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            if (_keys.Count > 0)
            {
                key = _keys.Dequeue();
                return KeyWait.Key;
            }

            key = default;
            Yield(timeout);
            return KeyWait.TimedOut;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An agent that blocks inside <see cref="Decide"/> until released. This is the
    /// only faithful stand-in for a slow MCTS search or an external process: the
    /// decide really is running when the keys arrive.
    /// </summary>
    private sealed class BlockingAgent : IAgent
    {
        private readonly ManualResetEventSlim _released = new(false);

        internal BlockingAgent(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        /// <summary>How many decides this agent has been asked for.</summary>
        internal int Decides { get; private set; }

        /// <summary>Whether a decide was ever outstanding.</summary>
        internal bool WasBlocked { get; private set; }

        /// <summary>Whether the test let this agent finish.</summary>
        internal bool Released => _released.IsSet;

        internal void Release() => _released.Set();

        public AgentAction Decide(Observation observation)
        {
            Decides++;
            WasBlocked = !_released.IsSet;
            if (!_released.Wait(TimeSpan.FromSeconds(30)))
            {
                Assert.Fail("a blocking agent was never released; the test would otherwise pass by timing out.");
            }

            return new AgentAction(ActionKind.Wait);
        }
    }

    /// <summary>An agent that fails inside its decide, as a broken agent would.</summary>
    private sealed class ThrowingAgent : IAgent
    {
        private readonly string _message;

        internal ThrowingAgent(int agentId, string message)
        {
            AgentId = agentId;
            _message = message;
        }

        public int AgentId { get; }

        public AgentAction Decide(Observation observation) =>
            throw new InvalidOperationException(_message);
    }

    /// <summary>An agent that always waits, so an episode ends on its budget.</summary>
    private sealed class CountingAgent : IAgent
    {
        internal CountingAgent(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public AgentAction Decide(Observation observation) => new(ActionKind.Wait);
    }

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    /// <summary>
    /// The terminal seam, recorded: what the host did to the terminal and in what
    /// order, and everything it wrote.
    /// </summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _errors = new();
        private readonly RecordingSession _session;

        internal RecordingSurface() => _session = new RecordingSession(this);

        internal List<string> Events { get; } = new();

        internal int Entries { get; private set; }

        internal string Text => _output.ToString();

        internal string ErrorsText => _errors.ToString();

        internal TextWriter Writer() => _output;

        internal TextWriter Errors => _errors;

        public bool RequestUtf8Output()
        {
            Events.Add("utf8");
            return true;
        }

        public ITerminalSession Enter(TextWriter output)
        {
            Entries++;
            Events.Add("enter");
            return _session;
        }

        private sealed class RecordingSession : ITerminalSession
        {
            private readonly RecordingSurface _owner;
            private bool _restored;

            internal RecordingSession(RecordingSurface owner) => _owner = owner;

            public event Action? Interrupted { add { } remove { } }

            public void Write(string text)
            {
                _owner.Events.Add("frame");
                _owner._output.Write(text);
            }

            public void Dispose()
            {
                if (_restored)
                {
                    return;
                }

                _restored = true;
                _owner.Events.Add("restore");
            }
        }
    }
}