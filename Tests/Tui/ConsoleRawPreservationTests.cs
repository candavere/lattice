using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Raw lines must not lose tokens when a form opens or confirms: unknown
/// flags, duplicate flags, quoted paths, positionals and mode words survive
/// through the form's argv.
/// </summary>
public class ConsoleRawPreservationTests
{
    private static readonly TerminalCapabilities Terminal = new(
        ColorDepth.TrueColor, Utf8: true, InputRedirected: false, OutputRedirected: false, Width: 100, Height: 30);

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
        FakeRunner? runner = null)
    {
        runner ??= new FakeRunner(_ => FakeCapturedRun.Done(0, "", ""));
        var all = new List<TuiKey>(keys);
        var queue = new Queue<TuiKey>(all);
        var keySource = new KeyQueue(() => queue.Count > 0 ? queue.Dequeue() : (TuiKey?)null);
        var factory = new RecordingSession();
        var output = new StringWriter();
        var request = new SlashConsoleHostRequest(
            output,
            new StringWriter(),
            Terminal,
            new TuiConsole(new RecordingControlC(), () => keySource),
            factory,
            new FixedClock(),
            runner,
            "/tmp/console-test",
            TrailingIdleFrames: 2);
        var exit = SlashConsoleHost.Run(request).ExitCode;
        return (exit, factory.Written);
    }

    [Fact]
    public void UnknownFlagSurvivesFormConfirm()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "MAP\n", ""));
        var keys = Text("/generate --seed 5 --bogus x");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, _) = Drive(keys, runner);

        Assert.Equal(0, exit);
        var run = Assert.Single(runner.Captured);
        Assert.Contains("--bogus", run.Arguments);
        Assert.Contains("x", run.Arguments);
        Assert.Contains("--seed", run.Arguments);
    }

    [Fact]
    public void QuotedPositionalSurvivesAsOneArgv()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "", ""));
        var keys = Text("/replay \"my dir/traj.jsonl\"");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, _) = Drive(keys, runner);

        Assert.Equal(0, exit);
        // Screen path for replay: positional with a space stays one argv entry.
        var watch = Assert.Single(runner.Watched);
        Assert.Contains("my dir/traj.jsonl", watch.Arguments);
    }

    [Fact]
    public void ModeWordSurvivesPrefill()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "", ""));
        var keys = Text("/simulate run");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.AddRange(Text("7"));
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, _) = Drive(keys, runner);

        Assert.Equal(0, exit);
        var run = Assert.Single(runner.Captured);
        Assert.Equal(["simulate", "--seed", "7"], run.Arguments);
        Assert.Empty(runner.Watched);
    }

    private sealed class RecordingControlC : IControlCAsInput
    {
        public bool Get() => false;
        public void Set(bool value) { }
    }

    private sealed class FixedClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.FromSeconds(5);
    }

    private sealed class RecordingSession : ITerminalSessionFactory
    {
        private readonly StringBuilder _written = new();
        internal string Written { get { lock (_written) { return _written.ToString(); } } }
        public bool RequestUtf8Output() => true;
        public ITerminalSession Enter(TextWriter output) => new Inner(_written);
        private sealed class Inner(StringBuilder written) : ITerminalSession
        {
            public event Action? Interrupted { add { } remove { } }
            public void Write(string text) { lock (written) { written.Append(text); } }
            public void Dispose() { }
        }
    }

    private sealed class KeyQueue(Func<TuiKey?> next) : IKeySource
    {
        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            var got = next();
            if (got is null) { key = default; return KeyWait.Closed; }
            key = got.Value;
            return KeyWait.Key;
        }

        public void Dispose() { }
    }

    private sealed class FakeRunner(Func<CapturedRunRequest, ICapturedRun>? captured = null) : ISlashConsoleRunner
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
            return new WatchRunResult(0, TimeSpan.FromSeconds(1));
        }
    }

    private sealed class FakeCapturedRun(bool done, bool cancelled, CapturedRunResult result) : ICapturedRun
    {
        public bool IsDone => done;
        public static FakeCapturedRun Done(int exit, string stdout, string stderr) =>
            new(true, false, new CapturedRunResult(exit, stdout, stderr, false, TimeSpan.FromSeconds(1), false));
        public CapturedRunResult Finish() => result;
        public void Cancel() { }

        // The fake's collection is instant, so the poll and Finish agree.
        public bool TryTakeFinished(out CapturedRunResult result)
        {
            if (!IsDone)
            {
                result = default!;
                return false;
            }

            result = Finish();
            return true;
        }
    }
}
