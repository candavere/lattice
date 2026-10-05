using Lattice.Agents;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tui;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The order a viewer run starts in, and the process-wide console setting it owns
/// while it runs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IControlCAsInput"/> exists because
/// <c>Console.TreatControlCAsInput</c> is process-wide and because on Windows its
/// setter needs a real console input handle: with standard input redirected, the
/// <c>GetConsoleMode</c> behind it fails and the setter throws. A run that was
/// going to be refused anyway must therefore never reach it, and a run that is
/// not refused must put back the value it found when it ends.
/// </para>
/// <para>
/// The first group of tests is that Windows failure, reproduced with a fake whose
/// getter and setter both throw. Nothing else is needed to make it red: the replay
/// path used to build its key source before the host was asked whether the run
/// could happen at all, and building one is what asks for the input mode.
/// </para>
/// </remarks>
public class TuiConsoleScopeTests
{
    /// <summary>A terminal that can carry the cockpit, which a test host's own is not.</summary>
    private static readonly TerminalCapabilities Interactive = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>A committed recording, so the replay path gets as far as the viewer.</summary>
    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    /// <summary>
    /// A redirected run must not touch the console input setting at all: the
    /// refusal is decided from the capabilities, before anything is constructed. The
    /// fake throws from both members exactly as a redirected Windows console does,
    /// so a path that reaches either one fails loudly rather than quietly leaving a
    /// process-wide setting changed.
    /// </summary>
    [Theory]
    [InlineData("demo.jsonl")]
    [InlineData("infiltration.jsonl")]
    public void ARedirectedReplayTouchesTheConsoleInputSettingZeroTimes(string recording)
    {
        var controlC = new ThrowingControlC();

        var (exit, stdout, stderr) = Run(controlC, "tui", "replay", Committed("site", recording));

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
        Assert.Equal(0, controlC.Calls);
    }

    /// <summary>
    /// The same rule for the live subcommand, which already refused before building
    /// its key source. Stated here so the two paths cannot drift apart: neither
    /// reaches the fake.
    /// </summary>
    [Fact]
    public void ARedirectedLiveRunTouchesTheConsoleInputSettingZeroTimes()
    {
        var controlC = new ThrowingControlC();

        var (exit, stdout, stderr) = Run(controlC, "tui", "simulate", "--seed", "42", "--steps", "2");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
        Assert.Equal(0, controlC.Calls);
    }

    /// <summary>
    /// The setting is read once, taken once, and the original value given back once,
    /// whichever way the run ends.
    /// </summary>
    [Theory]
    [InlineData("quit")]
    [InlineData("ctrl-c")]
    [InlineData("a failing agent")]
    public void TheOriginalValueIsRestoredExactlyOnceOnEveryEnding(string ending)
    {
        var controlC = new RecordingControlC(original: false);

        var (result, surface) = Start(original: false, ending: ending, controlC: controlC);

        // Whatever ended the run, the terminal was put back first and the setting
        // was given back after it: the reader's terminal is usable again before
        // anything else is tidied up.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            new[] { "keys built", "get", "set true", "utf8", "enter", "frame", "restore", "set false", "keys disposed" },
            surface.Events);
        Assert.Equal(new[] { "get", "set true", "set false" }, controlC.Calls);
        Assert.False(controlC.Value);
    }

    /// <summary>
    /// A run whose input ends, rather than the reader quitting: the end of the keys
    /// is as much an ending as a key press, and the setting still goes back.
    /// </summary>
    [Fact]
    public void TheOriginalValueIsRestoredOnceWhenTheInputEnds()
    {
        var controlC = new RecordingControlC(original: false);

        var (result, _) = Start(original: false, ending: "end of input", controlC: controlC);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "get", "set true", "set false" }, controlC.Calls);
        Assert.False(controlC.Value);
    }

    /// <summary>
    /// A process that had already asked for Ctrl-C as input keeps it: the scope
    /// restores what it found rather than forcing the default back on.
    /// </summary>
    [Fact]
    public void AProcessThatAlreadyTreatsControlCAsInputKeepsIt()
    {
        var controlC = new RecordingControlC(original: true);

        var (result, _) = Start(original: true, ending: "quit", controlC: controlC);

        Assert.Equal(0, result.ExitCode);
        Assert.True(controlC.Value);
        Assert.Equal(new[] { "get", "set true", "set true" }, controlC.Calls);
    }

    /// <summary>
    /// A start-up that throws leaves nothing of the run behind: the setting goes
    /// back, and the failure is the caller's rather than a swallowed one.
    /// </summary>
    [Fact]
    public void AStartUpThatThrowsStillRestoresTheSettingAndRethrows()
    {
        var controlC = new RecordingControlC(original: false);
        var events = new List<string>();

        var thrown = Assert.Throws<InvalidOperationException>(() => CliApp.StartViewer(
            Document(),
            new StringWriter(),
            new StringWriter(),
            Interactive,
            ascii: false,
            Console(controlC, events, Keys("quit")),
            cursor: null,
            FailingSession(),
            new StaticClock()));

        Assert.Equal("the terminal would not start", thrown.Message);
        Assert.False(controlC.Value);
        Assert.Equal(new[] { "get", "set true", "set false" }, controlC.Calls);
    }

    /// <summary>
    /// The whole order in one list. A refused run records nothing at all — no key
    /// source, no setting — and an accepted run builds its key source, takes the
    /// setting, runs, puts the terminal back, gives the setting back, and only then
    /// disposes the key source.
    /// </summary>
    [Fact]
    public void TheRefusalPrecedesEverythingAndTheRestoreFollowsTheTerminalRestore()
    {
        var refusedEvents = new List<string>();
        var refused = new RecordingControlC(original: false, events: refusedEvents);
        var refusedKeys = new RecordingKeys(refusedEvents);

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var result = CliApp.StartViewer(
                Document(),
                stdout,
                stderr,
                Interactive with { InputRedirected = true },
                ascii: false,
                Console(refused, refusedEvents, () => refusedKeys),
                cursor: null,
                FailingSession(),
                new StaticClock());

            Assert.Equal(UsageError.ExitCode, result.ExitCode);
            Assert.Equal("", stdout.ToString());
            Assert.Contains("redirected", result.Refusal!, StringComparison.Ordinal);
        }

        Assert.Equal("", string.Join(",", refusedEvents));

        var events = new List<string>();
        var accepted = new RecordingControlC(original: false, events: events);
        var surface = new RecordingSurface(events);
        var keys = new RecordingKeys(events);
        keys.Queue(Character('q'));

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var result = CliApp.StartViewer(
                Document(),
                stdout,
                stderr,
                Interactive,
                ascii: false,
                Console(accepted, events, keys),
                cursor: null,
                surface,
                new StaticClock());

            Assert.Equal(0, result.ExitCode);
        }

        Assert.Equal(
            new[]
            {
                "keys built", "get", "set true", "utf8", "enter", "frame", "restore", "set false", "keys disposed",
            },
            events);
    }

    /// <summary>
    /// One viewer run, driven the way the host drives one: a scripted key sequence,
    /// a terminal that records what was done to it, and — for the failing-agent
    /// case — an episode whose own agent throws inside its decide.
    /// </summary>
    private static (TuiRunResult Result, RecordingSurface Surface) Start(
        bool original,
        string ending,
        RecordingControlC controlC)
    {
        var events = new List<string>();
        controlC.Events = events;
        var surface = new RecordingSurface(events);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // A failing agent is only a failing agent if the episode is the real one: the
        // stepper's own thread has to be the thing that fails.
        var roster = ending == "a failing agent"
            ? new IAgent[] { new ThrowingAgent(0, "the agent gave up"), new WaitingAgent(1) }
            : new IAgent[] { new WaitingAgent(0), new WaitingAgent(1) };

        using var episode = new LiveEpisode(new LiveEpisodeSetup(
            MapGenerator.Generate(42, new GeneratorConfig(2, 2, 1, 1, 1, GeneratorConfig.DefaultRetryCap)),
            new SimulationConfig(AgentCount: 2, MaxTicks: 4),
            () => roster,
            4,
            Seed: 42,
            Label: "scope-episode",
            Roles: new[] { "First", "Second" },
            Rules: DynamicMapRuleSet.None));

        var cursor = new LivePlayback(episode);
        var keys = new RecordingKeys(events);

        switch (ending)
        {
            case "quit":
                keys.Queue(Character('q'));
                break;
            case "ctrl-c":
                keys.Queue(new TuiKey(TuiKeyKind.Interrupt));
                break;
            case "end of input":
                keys.Close();
                break;
            case "a failing agent":
                // The step has to be asked for: a run that only reads keys never
                // advances the episode on its own, so there would be no decide to
                // fail and the run would simply wait.
                keys.Queue(Character('n'));
                keys.QuitWhen(() => episode.Failure is not null);
                break;
            default:
                Assert.Fail($"unknown ending '{ending}'.");
                break;
        }

        var result = CliApp.StartViewer(
            cursor.Document,
            stdout,
            stderr,
            Interactive,
            ascii: false,
            Console(controlC, events, keys),
            cursor,
            surface,
            new StaticClock());

        episode.Stop();

        return (result, surface);
    }

    /// <summary>
    /// The console a run is composed from, whose key factory records the fact that
    /// it was asked for one — which is the event a refused run must never reach.
    /// </summary>
    private static TuiConsole Console(IControlCAsInput controlC, List<string> events, RecordingKeys keys) =>
        Console(controlC, events, () => keys);

    private static TuiConsole Console(IControlCAsInput controlC, List<string> events, Func<IKeySource> keys) =>
        new(controlC, () =>
        {
            events.Add("keys built");
            return keys();
        });

    private static RecordingKeys Keys(string ending)
    {
        var keys = new RecordingKeys(new List<string>());

        if (ending == "quit")
        {
            keys.Queue(Character('q'));
        }

        return keys;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(IControlCAsInput controlC, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return (
            CliApp.Run(args, stdout, stderr, CliTerminal.For(stderr), TuiConsole.Of(controlC)),
            stdout.ToString(),
            stderr.ToString());
    }

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    /// <summary>
    /// The setting a real console owns, throwing from both members the way a
    /// redirected Windows console does: the setter needs a console input handle and
    /// there is none.
    /// </summary>
    private sealed class ThrowingControlC : IControlCAsInput
    {
        internal int Calls { get; private set; }

        public bool Get()
        {
            Calls++;
            throw new IOException("The handle is invalid.");
        }

        public void Set(bool value)
        {
            Calls++;
            throw new IOException("The handle is invalid.");
        }
    }

    /// <summary>
    /// A setting a test can watch. Every get and set in order, whether or not the
    /// run also records the whole sequence somewhere else.
    /// </summary>
    private sealed class RecordingControlC : IControlCAsInput
    {
        private bool _value;

        internal RecordingControlC(bool original, List<string>? events = null)
        {
            _value = original;
            Events = events ?? new List<string>();
        }

        internal bool Value => _value;

        internal List<string> Events { get; set; }

        internal List<string> Calls { get; } = new();

        public bool Get()
        {
            Calls.Add("get");
            Events.Add("get");
            return _value;
        }

        public void Set(bool value)
        {
            Calls.Add(value ? "set true" : "set false");
            Events.Add(value ? "set true" : "set false");
            _value = value;
        }
    }

    /// <summary>
    /// Keys that report when they are disposed and hand over a scripted sequence,
    /// so the run's order is one list rather than a claim.
    /// </summary>
    private sealed class RecordingKeys : IKeySource
    {
        private readonly List<string> _events;
        private readonly Queue<TuiKey> _keys = new();
        private Func<bool>? _quitWhen;
        private bool _closed;

        internal RecordingKeys(List<string> events) => _events = events;

        internal List<TuiKey> Queued { get; init; } = new();

        internal void Queue(params TuiKey[] keys)
        {
            foreach (var key in keys)
            {
                _keys.Enqueue(key);
            }
        }

        /// <summary>End of input: the host is told nothing more will arrive.</summary>
        internal void Close() => _closed = true;

        /// <summary>Quit, but only once the condition holds, so the run really ends on it.</summary>
        internal void QuitWhen(Func<bool> condition) => _quitWhen = condition;

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            if (_quitWhen is { } condition && condition())
            {
                key = Character('q');
                return KeyWait.Key;
            }

            if (_keys.Count > 0)
            {
                key = _keys.Dequeue();
                return KeyWait.Key;
            }

            if (_closed)
            {
                key = default;
                return KeyWait.Closed;
            }

            // A read that finds nothing: long enough to be a timed-out wait rather
            // than a spin, short enough not to depend on wall-clock timing.
            Thread.Sleep(1);
            key = default;
            return KeyWait.TimedOut;
        }

        public void Dispose() => _events.Add("keys disposed");
    }

    /// <summary>
    /// A terminal that cannot start, which is one of the endings the restore has to
    /// survive: the failure comes after the setting was taken and before the
    /// terminal is entered.
    /// </summary>
    private static ITerminalSessionFactory FailingSession() => new FailingSessionFactory();

    private sealed class FailingSessionFactory : ITerminalSessionFactory
    {
        public bool RequestUtf8Output() =>
            throw new InvalidOperationException("the terminal would not start");

        public ITerminalSession Enter(TextWriter output) =>
            throw new InvalidOperationException("the terminal would not start");
    }

    /// <summary>The terminal seam, recorded: what the host did and in what order.</summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly List<string> _events;

        internal RecordingSurface(List<string> events) => _events = events;

        internal List<string> Events => _events;

        public bool RequestUtf8Output()
        {
            _events.Add("utf8");
            return true;
        }

        public ITerminalSession Enter(TextWriter output)
        {
            _events.Add("enter");
            return new Session(this);
        }

        private sealed class Session : ITerminalSession
        {
            private readonly RecordingSurface _owner;

            internal Session(RecordingSurface owner) => _owner = owner;

            public event Action? Interrupted { add { } remove { } }

            public void Write(string text) => _owner._events.Add("frame");

            public void Dispose() => _owner._events.Add("restore");
        }
    }

    /// <summary>A clock that never advances, so a scripted run needs no sleep to pass time.</summary>
    private sealed class StaticClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.Zero;
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
    private sealed class WaitingAgent : IAgent
    {
        internal WaitingAgent(int agentId) => AgentId = agentId;

        public int AgentId { get; }

        public AgentAction Decide(Observation observation) => new(ActionKind.Wait);
    }

    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 2; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick), new WorldAgent(1, 1, tick / 2) },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : "agent0: Wait",
                stateDigest: null,
                isTerminal: tick == 2,
                terminalReason: tick == 2 ? "tick-limit" : null,
                winnerSlot: null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 10, 10) },
                Array.Empty<WorldResource>(),
                new[] { new WorldEdge(0, 0, 1) }),
            new ReplayHeader(42, 5, "scope", new[] { "First", "Second" }, 2, null, 0),
            frames);
    }

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
