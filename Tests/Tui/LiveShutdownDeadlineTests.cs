using System.Diagnostics;
using Lattice.Agents;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// What a quit costs the reader when an agent is still deciding: one deadline for
/// the whole way out, and the same way out for every key that means "leave".
/// </summary>
/// <remarks>
/// <para>
/// Measured, not inferred. Each probe blocks a real decide on a real episode and
/// reads a wall clock and the bytes the run wrote to the terminal — the two things
/// a reader can observe. Nothing here reads the episode's own join timeout, its
/// status, or any counter the code keeps: a probe that quoted the constant it is
/// checking would agree with any implementation of that constant.
/// </para>
/// <para>
/// The bound asserted here is the reader-facing one from the requirement — a quit
/// is back at the reader's shell within three seconds — not the implementation's
/// internal budget.
/// </para>
/// </remarks>
public class LiveShutdownDeadlineTests
{
    /// <summary>Enough budget that the episode cannot end while an agent is blocked.</summary>
    private const int GenerousBudget = 200;

    /// <summary>What a quit may cost, end to end, from the episode seam.</summary>
    private static readonly TimeSpan QuitBudget = TimeSpan.FromSeconds(3);

    [Fact]
    public void AQuitCostsOneDeadlineEvenWithAnAgentStillDeciding()
    {
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        var episode = Episode(agents, GenerousBudget);

        try
        {
            episode.RequestTick();
            WaitFor(() => agents[0].WasBlocked);

            // Exactly what the command does on the way out: stop the stepper, then
            // dispose as the scope ends. The reader is already back at the shell by
            // the second of those, so the two share one deadline. A per-join budget
            // would let a blocked agent cost twice that, which is what this measures.
            var stopwatch = Stopwatch.StartNew();
            var joined = episode.Stop();
            episode.Dispose();
            stopwatch.Stop();

            Assert.False(joined, "the decide returned, so the probe never raced a blocked agent.");
            Assert.True(agents[0].WasBlocked && !agents[0].Released, "the agent stopped deciding, so the probe proves nothing.");
            Assert.True(
                stopwatch.Elapsed < QuitBudget,
                $"stopping and disposing a blocked episode took {stopwatch.Elapsed.TotalSeconds:0.00}s, " +
                $"which is more than the {QuitBudget.TotalSeconds:0}s a reader waits for a shell.");

            // Nothing may claim the episode stopped while the thread is still deciding.
            Assert.NotEqual(LiveStepperStatus.Stopped, episode.StepperStatus);
        }
        finally
        {
            foreach (var agent in agents)
            {
                agent.Release();
            }

            episode.Join(TimeSpan.FromSeconds(20));
        }
    }

    [Theory]
    [InlineData('q')]
    [InlineData('\u0003')]
    public void EveryQuitKeyRestoresTheTerminalOnceAndReturnsTheSameWay(char glyph)
    {
        var agents = new[] { new BlockingAgent(0), new BlockingAgent(1) };
        var episode = Episode(agents, GenerousBudget);
        var surface = new RecordingSurface();
        var key = glyph == 'q'
            ? new TuiKey(TuiKeyKind.Character, 'q')
            : new TuiKey(TuiKeyKind.Interrupt);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = RunHost(surface, episode, new ScriptedKeys(agents, key));
            var joined = episode.Stop();
            episode.Dispose();
            stopwatch.Stop();

            Assert.Equal(0, result.ExitCode);
            Assert.Null(result.Refusal);
            Assert.False(joined, "the decide returned, so the probe never raced a blocked agent.");

            // Restored exactly once: one entry, one leave, one show. A restore that
            // ran twice would put two leave sequences into the reader's scrollback.
            Assert.Equal(1, surface.Entries);
            Assert.Equal(1, Count(surface.Text, TerminalGuard.LeaveAlternateScreen));
            Assert.Equal(1, Count(surface.Text, TerminalGuard.ShowCursor));
            Assert.Equal(1, Count(surface.Text, TerminalGuard.EnterAlternateScreen));
            Assert.Equal(1, Count(surface.Text, TerminalGuard.HideCursor));

            Assert.True(
                stopwatch.Elapsed < QuitBudget,
                $"a quit on '{glyph}' took {stopwatch.Elapsed.TotalSeconds:0.00}s end to end, " +
                $"which is more than the {QuitBudget.TotalSeconds:0}s a reader waits for a shell.");
        }
        finally
        {
            foreach (var agent in agents)
            {
                agent.Release();
            }

            episode.Join(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public void TheQuitKeyAndCtrlCAgreeOnWhatAQuitIs()
    {
        // The host branches on one predicate, so a key that means "leave" and a
        // signal that means "leave" cannot end the run differently. Asserted
        // because the two arrive by different routes: the character through the key
        // loop, Ctrl-C either as that character or as a console cancel event.
        Assert.True(new TuiKey(TuiKeyKind.Character, 'q').IsQuit);
        Assert.True(new TuiKey(TuiKeyKind.Character, 'Q').IsQuit);
        Assert.True(new TuiKey(TuiKeyKind.Interrupt).IsQuit);
        Assert.False(new TuiKey(TuiKeyKind.Character, 'n').IsQuit);
        Assert.False(new TuiKey(TuiKeyKind.Right).IsQuit);
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static TuiRunResult RunHost(RecordingSurface surface, LiveEpisode episode, IKeySource keys)
    {
        var cursor = new LivePlayback(episode);

        using (keys)
        {
            return TuiHost.Run(new TuiHostRequest(
                cursor.Document,
                surface.Writer(),
                surface.Errors,
                Interactive,
                ForceAscii: true,
                surface,
                keys,
                new TickingClock(),
                Cursor: cursor));
        }
    }

    /// <summary>
    /// A clock that advances a known 150 ms per reading, so one timed-out pass of the
    /// host's loop is a whole tick at the default speed. The probe then depends on no
    /// real timing to get a decide asked for, which leaves the wall clock free to
    /// measure the thing the probe is actually about.
    /// </summary>
    private sealed class TickingClock : IUiClock
    {
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

    private static readonly TerminalCapabilities Interactive = new(
        Depth: ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    private static LiveEpisode Episode(IReadOnlyList<IAgent> roster, int maxSteps) =>
        new(new LiveEpisodeSetup(
            MapGenerator.Generate(42, new GeneratorConfig(2, 2, 1, 1, 1, GeneratorConfig.DefaultRetryCap)),
            new SimulationConfig(AgentCount: 2, MaxTicks: maxSteps),
            () => roster.ToArray(),
            maxSteps,
            Seed: 42,
            Label: "shutdown-probe",
            Roles: new[] { "First", "Second" },
            Rules: DynamicMapRuleSet.None));

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

    /// <summary>An agent that never returns until the probe lets it, so a decide is genuinely outstanding.</summary>
    private sealed class BlockingAgent : IAgent
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _blocked;

        internal BlockingAgent(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        internal bool WasBlocked => Volatile.Read(ref _blocked) > 0;

        internal bool Released => _release.IsSet;

        internal void Release() => _release.Set();

        public AgentAction Decide(Observation observation)
        {
            Interlocked.Increment(ref _blocked);

            // Bounded so a probe that never releases fails as a timeout rather than
            // hanging the run it is trying to measure.
            _release.Wait(TimeSpan.FromSeconds(30));
            return new AgentAction(ActionKind.Wait);
        }
    }

    /// <summary>Two keys in: one to start, one to leave, gated on the decide being outstanding.</summary>
    private sealed class ScriptedKeys : IKeySource
    {
        private readonly BlockingAgent[] _agents;
        private readonly TuiKey _quit;
        private int _step;

        internal ScriptedKeys(BlockingAgent[] agents, TuiKey quit)
        {
            _agents = agents;
            _quit = quit;
        }

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            switch (Interlocked.Increment(ref _step))
            {
                case 1:
                    key = new TuiKey(TuiKeyKind.Character, ' ');
                    return KeyWait.Key;
                case 2:
                    key = default;
                    return KeyWait.TimedOut;
                default:
                    WaitUntil(() => _agents[0].WasBlocked);
                    key = _quit;
                    return KeyWait.Key;
            }
        }

        public void Dispose()
        {
        }
    }

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

    /// <summary>A terminal that records what it was sent, so escape sequences can be counted.</summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _errors = new();
        private readonly RecordingSession _session;

        internal RecordingSurface() => _session = new RecordingSession(this);

        internal int Entries { get; private set; }

        internal string Text => _output.ToString();

        internal TextWriter Writer() => _output;

        internal TextWriter Errors => _errors;

        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output)
        {
            Entries++;
            _output.Write(TerminalGuard.EnterAlternateScreen);
            _output.Write(TerminalGuard.HideCursor);
            return _session;
        }

        private sealed class RecordingSession : ITerminalSession
        {
            private readonly RecordingSurface _owner;
            private bool _restored;

            internal RecordingSession(RecordingSurface owner) => _owner = owner;

            public event Action? Interrupted { add { } remove { } }

            public void Write(string text) => _owner._output.Write(text);

            public void Dispose()
            {
                if (_restored)
                {
                    return;
                }

                _restored = true;
                _owner._output.Write(TerminalGuard.ShowCursor);
                _owner._output.Write(TerminalGuard.LeaveAlternateScreen);
            }
        }
    }
}