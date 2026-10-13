using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Stage 4: the console state machine, commit parsing over the catalog, and the host loop
/// with scripted keys, sessions and runners. Real processes are asserted in
/// Tests/Cli/ProcessInvocationTests; everything here runs with no terminal and no child.
/// </summary>
public class SlashConsoleHostTests
{
    private static readonly TerminalCapabilities Terminal = new(
        ColorDepth.TrueColor, Utf8: true, InputRedirected: false, OutputRedirected: false, Width: 100, Height: 30);

    // ---- Pure state ----

    [Fact]
    public void TypingSlashOpensTheMenuAndQNevVerQuits()
    {
        var state = new SlashConsoleState();
        state.Apply(new TuiKey(TuiKeyKind.Character, '/'));

        Assert.True(state.MenuOpen);
        Assert.Equal("/", state.Input);

        state.Apply(new TuiKey(TuiKeyKind.Character, 'q'));

        Assert.Equal("/q", state.Input);
        Assert.False(state.HasQuit);
    }

    [Fact]
    public void EscapeClosesTheMenuThenClearsThenQuits()
    {
        var state = new SlashConsoleState();
        state.Apply(new TuiKey(TuiKeyKind.Character, '/'));
        state.SetMenu(SlashConsoleMenu.Filter(state.Input));

        state.Apply(new TuiKey(TuiKeyKind.Escape));
        Assert.False(state.MenuOpen);
        Assert.Equal("/", state.Input);

        state.Apply(new TuiKey(TuiKeyKind.Escape));
        Assert.Equal("", state.Input);

        state.Apply(new TuiKey(TuiKeyKind.Escape));
        Assert.True(state.HasQuit);
    }

    [Fact]
    public void TabCompletesAndEnterCommitsTheSelection()
    {
        var state = new SlashConsoleState();
        state.Apply(new TuiKey(TuiKeyKind.Character, '/'));
        state.Apply(new TuiKey(TuiKeyKind.Character, 's'));
        state.SetMenu(SlashConsoleMenu.Filter(state.Input));
        state.Apply(new TuiKey(TuiKeyKind.Down));

        state.Apply(new TuiKey(TuiKeyKind.Tab));

        Assert.StartsWith("/simulate", state.Input, StringComparison.Ordinal);

        state.Apply(new TuiKey(TuiKeyKind.Enter));

        Assert.True(state.WantsToCommit);
        Assert.False(state.MenuOpen);
    }

    [Fact]
    public void SelectionClampsAtBothEnds()
    {
        var state = new SlashConsoleState();
        state.Apply(new TuiKey(TuiKeyKind.Character, '/'));
        state.SetMenu(SlashConsoleMenu.Filter("/"));

        state.Apply(new TuiKey(TuiKeyKind.Down));
        state.Apply(new TuiKey(TuiKeyKind.Down));
        state.Apply(new TuiKey(TuiKeyKind.Down));
        for (var i = 0; i < 20; i++)
        {
            state.Apply(new TuiKey(TuiKeyKind.Down));
        }

        Assert.Equal(state.Menu.Count - 1, state.SelectedIndex);

        for (var i = 0; i < 20; i++)
        {
            state.Apply(new TuiKey(TuiKeyKind.Up));
        }

        Assert.Equal(0, state.SelectedIndex);
    }

    [Fact]
    public void BackspacePastTheSlashClosesTheMenu()
    {
        var state = new SlashConsoleState();
        foreach (var c in "/a")
        {
            state.Apply(new TuiKey(TuiKeyKind.Character, c));
        }

        Assert.True(state.MenuOpen);

        state.Apply(new TuiKey(TuiKeyKind.Backspace));
        state.Apply(new TuiKey(TuiKeyKind.Backspace));

        Assert.Equal("", state.Input);
        Assert.False(state.MenuOpen);
    }

    [Fact]
    public void InterruptQuitsAndControlCharactersAreIgnored()
    {
        var state = new SlashConsoleState();
        state.Apply(new TuiKey(TuiKeyKind.Character, '\u0001'));

        Assert.Equal("", state.Input);

        state.Apply(new TuiKey(TuiKeyKind.Interrupt));

        Assert.True(state.HasQuit);
    }

    // ---- Commit parsing ----

    [Fact]
    public void ExitAndQuitLeaveHoweverCased()
    {
        Assert.Equal(CommitKind.Exit, SlashConsoleCommit.ParseCommit("/exit").Kind);
        Assert.Equal(CommitKind.Exit, SlashConsoleCommit.ParseCommit("/quit").Kind);
        Assert.Equal(CommitKind.Exit, SlashConsoleCommit.ParseCommit("/EXIT").Kind);
    }

    [Fact]
    public void UnknownCommandsRunRawForParity()
    {
        var plan = SlashConsoleCommit.ParseCommit("/frobnicate --x");

        Assert.Equal(CommitKind.Raw, plan.Kind);
        Assert.Equal(["frobnicate", "--x"], plan.Arguments);
    }

    [Fact]
    public void GenerateRunsWithCanonicalArgv()
    {
        var plan = SlashConsoleCommit.ParseCommit("/GENERATE --seed 42");

        Assert.Equal(CommitKind.Run, plan.Kind);
        Assert.Equal(["generate", "--seed", "42"], plan.Arguments);
    }

    [Fact]
    public void SimulateDefaultsToWatchAndRunIsExplicit()
    {
        var watch = SlashConsoleCommit.ParseCommit("/simulate --seed 1");

        Assert.Equal(CommitKind.Screen, watch.Kind);
        Assert.Equal(["tui", "simulate", "--seed", "1"], watch.Arguments);

        var run = SlashConsoleCommit.ParseCommit("/simulate run --seed 1");

        Assert.Equal(CommitKind.Run, run.Kind);
        Assert.Equal(["simulate", "--seed", "1"], run.Arguments);

        var alias = SlashConsoleCommit.ParseCommit("/simulate WATCH --seed 1");

        Assert.Equal(CommitKind.Screen, alias.Kind);
        Assert.Equal(["tui", "simulate", "--seed", "1"], alias.Arguments);
    }

    [Fact]
    public void LedgerIsTheTuiScreen()
    {
        var plan = SlashConsoleCommit.ParseCommit("/ledger a.json b.json");

        Assert.Equal(CommitKind.Screen, plan.Kind);
        Assert.Equal(["tui", "ledger", "a.json", "b.json"], plan.Arguments);
    }

    [Fact]
    public void WatchOnlyCommandsRefuseRunAndRunOnlyRefuseWatch()
    {
        var replay = SlashConsoleCommit.ParseCommit("/replay run x.jsonl");

        Assert.Equal(CommitKind.Error, replay.Kind);
        Assert.Contains("replay", replay.ErrorMessage, StringComparison.Ordinal);

        var generate = SlashConsoleCommit.ParseCommit("/generate screen --seed 1");

        Assert.Equal(CommitKind.Error, generate.Kind);
        Assert.Contains("generate", generate.ErrorMessage, StringComparison.Ordinal);

        var replayWatch = SlashConsoleCommit.ParseCommit("/replay watch x.jsonl");

        Assert.Equal(CommitKind.Screen, replayWatch.Kind);
    }

    [Fact]
    public void AnUnterminatedQuoteIsAnError()
    {
        var plan = SlashConsoleCommit.ParseCommit("/generate \"abc");

        Assert.Equal(CommitKind.Error, plan.Kind);
    }

    // ---- Host with fakes ----

    [Fact]
    public void TypingRunsACapturedCommandAndShowsItsResult()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "MAP\n", ""));
        var (exit, screen) = Drive(Text("/generate --seed 42"), enter: true, runner: runner);

        Assert.Equal(0, exit);
        Assert.Single(runner.Captured);
        Assert.Equal(["generate", "--seed", "42"], runner.Captured[0].Arguments);
        Assert.Equal("/tmp/console-test", runner.Captured[0].WorkingDirectory);
        Assert.Contains("exit 0 ok", screen, StringComparison.Ordinal);
        Assert.Contains("MAP", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchLeavesAndReentersWithSuspendedControlC()
    {
        var order = new List<string>();
        var controlC = new OrderControlC(order);
        var runner = new FakeRunner(order: order);
        var (exit, _) = Drive(Text("/simulate --seed 1"), enter: true, runner: runner, controlC: controlC, extraKeys: 1);

        Assert.Equal(0, exit);
        Assert.Single(runner.Watched);
        Assert.Equal(["tui", "simulate", "--seed", "1"], runner.Watched[0].Arguments);

        var suspendAt = order.IndexOf("set-False");
        var watchAt = order.IndexOf("watch");
        var resumeAt = order.IndexOf("set-True", suspendAt + 1);
        Assert.True(suspendAt >= 0 && watchAt > suspendAt && resumeAt > watchAt,
            $"expected suspend, watch, resume in order, got: {string.Join(',', order)}");
    }

    [Fact]
    public void EscapeDuringARunCancelsTheTree()
    {
        var run = FakeCapturedRun.Manual(0, "", "");
        var runner = new FakeRunner(_ => run);
        var (exit, screen) = Drive(Text("/generate --seed 1"), enter: true, runner: runner, cancel: true);

        Assert.Equal(0, exit);
        Assert.True(run.Cancelled, "Esc during a run must cancel the child's whole tree.");
        Assert.Contains("cancelled", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlCAtIdleExitsZeroWithTheTerminalRestored()
    {
        var session = new RecordingSession();
        var (exit, _) = Drive([new TuiKey(TuiKeyKind.Interrupt)], session: session);

        Assert.Equal(0, exit);
        Assert.Equal(1, session.Enters);
        Assert.Equal(1, session.Disposes);
    }

    [Fact]
    public void RedirectedTouchesNeitherConsoleNorSession()
    {
        var request = new SlashConsoleHostRequest(
            new StringWriter(),
            new StringWriter(),
            Terminal with { OutputRedirected = true },
            new TuiConsole(new ThrowingControlC(), () => throw new InvalidOperationException("keys touched")),
            new ThrowingSessionFactory(),
            new FixedClock(),
            new FakeRunner(),
            "/tmp/console-test");

        var result = SlashConsoleHost.Run(request);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("console", result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    public void ExitWordsLeave(string word)
    {
        var (exit, _) = Drive(Text(word), enter: true);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void EscapeOnAnEmptyLineLeaves()
    {
        var (exit, _) = Drive([new TuiKey(TuiKeyKind.Escape)]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void UnknownCommandsReachTheRunnerRaw()
    {
        var runner = new FakeRunner();
        var (exit, _) = Drive(Text("/frobnicate"), enter: true, runner: runner);

        Assert.Equal(0, exit);
        Assert.Single(runner.Captured);
        Assert.Equal(["frobnicate"], runner.Captured[0].Arguments);
    }

    [Fact]
    public void ARefusedRunNeverReachesTheRunner()
    {
        var runner = new FakeRunner();
        var (_, screen) = Drive(Text("/replay run x"), enter: true, runner: runner);

        Assert.Empty(runner.Captured);
        Assert.Empty(runner.Watched);
        Assert.Contains("can only be watched", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingRunShowsItsFailure()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(1, "", "boom\n"));
        var (_, screen) = Drive(Text("/generate"), enter: true, runner: runner);

        Assert.Contains("exit 1 failed", screen, StringComparison.Ordinal);
        Assert.Contains("boom", screen, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static List<TuiKey> Text(string text)
    {
        var keys = new List<TuiKey>();
        foreach (var c in text)
        {
            keys.Add(new TuiKey(TuiKeyKind.Character, c));
        }

        return keys;
    }

    private static (int Exit, string Screen) Drive(
        List<TuiKey> keys,
        bool enter = false,
        FakeRunner? runner = null,
        IControlCAsInput? controlC = null,
        RecordingSession? session = null,
        int extraKeys = 0,
        bool cancel = false)
    {
        var all = new List<TuiKey>(keys);
        if (enter)
        {
            all.Add(new TuiKey(TuiKeyKind.Enter));
        }

        if (cancel)
        {
            all.Add(new TuiKey(TuiKeyKind.Escape));
        }

        for (var i = 0; i < extraKeys; i++)
        {
            all.Add(new TuiKey(TuiKeyKind.Escape));
        }

        var scripted = Scripted(all);
        var sources = new Queue<IKeySource>();
        sources.Enqueue(scripted);
        sources.Enqueue(Scripted([]));
        sources.Enqueue(Scripted([]));

        var factory = session ?? new RecordingSession();
        var request = new SlashConsoleHostRequest(
            new StringWriter(),
            new StringWriter(),
            Terminal,
            new TuiConsole(controlC ?? new RecordingControlC(), () => sources.Dequeue()),
            factory,
            new FixedClock(),
            runner ?? new FakeRunner(),
            "/tmp/console-test",
            TrailingIdleFrames: 1);

        var exit = SlashConsoleHost.Run(request).ExitCode;
        return (exit, factory.Written);
    }

    private static KeyQueue Scripted(List<TuiKey> keys)
    {
        var queue = new Queue<TuiKey>(keys);
        return new KeyQueue(() => queue.Count > 0 ? queue.Dequeue() : (TuiKey?)null);
    }

    private sealed class FixedClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.FromSeconds(5);
    }

    private sealed class RecordingControlC : IControlCAsInput
    {
        public bool Get() => false;

        public void Set(bool value)
        {
        }
    }

    private sealed class OrderControlC(List<string> order) : IControlCAsInput
    {
        public bool Get()
        {
            order.Add("get");
            return false;
        }

        public void Set(bool value) => order.Add(value ? "set-True" : "set-False");
    }

    private sealed class ThrowingControlC : IControlCAsInput
    {
        public bool Get() => throw new InvalidOperationException("control-C touched on a refused run");

        public void Set(bool value) => throw new InvalidOperationException("control-C touched on a refused run");
    }

    private sealed class ThrowingSessionFactory : ITerminalSessionFactory
    {
        public bool RequestUtf8Output() => throw new InvalidOperationException("session touched on a refused run");

        public ITerminalSession Enter(TextWriter output) => throw new InvalidOperationException("session touched on a refused run");
    }

    private sealed class RecordingSession : ITerminalSessionFactory
    {
        private readonly StringBuilder _written = new();

        internal int Enters { get; private set; }

        internal int Disposes { get; private set; }

        internal string Written
        {
            get
            {
                lock (_written)
                {
                    return _written.ToString();
                }
            }
        }

        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output)
        {
            Enters++;
            return new Inner(_written, () => Disposes++);
        }

        private sealed class Inner(StringBuilder written, Action disposed) : ITerminalSession
        {
            public event Action? Interrupted
            {
                add { }
                remove { }
            }

            public void Write(string text)
            {
                lock (written)
                {
                    written.Append(text);
                }
            }

            public void Dispose() => disposed();
        }
    }

    private sealed class FakeRunner(
        Func<CapturedRunRequest, ICapturedRun>? captured = null,
        List<string>? order = null) : ISlashConsoleRunner
    {
        internal readonly List<CapturedRunRequest> Captured = [];
        internal readonly List<WatchRunRequest> Watched = [];

        public ICapturedRun StartCaptured(CapturedRunRequest request)
        {
            Captured.Add(request);
            return captured is null ? FakeCapturedRun.Done(0, "", "") : captured(request);
        }

        public WatchRunResult RunWatch(WatchRunRequest request)
        {
            Watched.Add(request);
            order?.Add("watch");
            return new WatchRunResult(0, TimeSpan.FromSeconds(1));
        }
    }

    private sealed class FakeCapturedRun(bool done, bool cancelled, CapturedRunResult result) : ICapturedRun
    {
        private bool _done = done;
        private bool _cancelled = cancelled;

        internal bool Cancelled => _cancelled;

        internal static FakeCapturedRun Done(int exit, string stdout, string stderr) =>
            new(true, false, new CapturedRunResult(exit, stdout, stderr, false, TimeSpan.FromSeconds(1), false));

        internal static FakeCapturedRun Manual(int exit, string stdout, string stderr) =>
            new(false, false, new CapturedRunResult(exit, stdout, stderr, false, TimeSpan.FromSeconds(1), false));

        public bool IsDone => _done;

        public void Cancel()
        {
            _cancelled = true;
            _done = true;
        }

        public CapturedRunResult Finish() => _result with { Cancelled = _cancelled };

        private readonly CapturedRunResult _result = result;
    }
}
