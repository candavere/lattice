using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Stage 2: the results workspace. A run lands in a scrollable workspace that
/// keeps stdout and stderr separate, names the effective command and working
/// directory, states its limits honestly, opens a raw viewer on Tab, saves on
/// demand, and hands the console back with the input line intact.
/// </summary>
public class SlashConsoleWorkspaceTests
{
    private static readonly TerminalCapabilities Terminal = new(
        ColorDepth.TrueColor, Utf8: true, InputRedirected: false, OutputRedirected: false, Width: 100, Height: 30);

    // ---- Pure workspace ----

    [Fact]
    public void FromRunSeparatesStreamsAndNamesCommandAndWorkdir()
    {
        var result = new CapturedRunResult(
            0, "OUT\n", "ERR\n", false, TimeSpan.FromSeconds(1), false,
            "dotnet lattice.dll generate --seed 42", "/tmp/w");

        var view = SlashConsoleWorkspace.FromRun(result, "generate", "lattice generate").BuildView();

        Assert.StartsWith("exit 0 ok", view.Title, StringComparison.Ordinal);
        Assert.Equal("dotnet lattice.dll generate --seed 42", view.CommandLine);
        Assert.Equal("/tmp/w", view.WorkingDirectory);
        var stdout = Assert.Single(view.Sections, section => section.Heading == "stdout");
        Assert.Equal(["OUT"], stdout.Lines);
        var stderr = Assert.Single(view.Sections, section => section.Heading == "stderr");
        Assert.Equal(["ERR"], stderr.Lines);
        Assert.Empty(view.LimitNotices);
        Assert.False(view.ShowingRaw);
        Assert.Equal(0, view.Scroll);
    }

    [Fact]
    public void EmptyStreamsShowAnExplicitPlaceholder()
    {
        var result = new CapturedRunResult(0, "", "", false, TimeSpan.FromSeconds(1), false, "lattice", "/tmp/w");

        var view = SlashConsoleWorkspace.FromRun(result, "run", "lattice").BuildView();

        Assert.All(view.Sections, section => Assert.Equal(["(empty)"], section.Lines));
    }

    [Fact]
    public void ScrollNeverGoesNegative()
    {
        var workspace = SlashConsoleWorkspace.FromMessage("exit 0 ok", ["a"]);
        workspace.ScrollBy(-5);

        Assert.Equal(0, workspace.BuildView().Scroll);
    }

    [Fact]
    public void ToggleRawLoadsHeadPlusSpool()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");
        File.WriteAllText(spool, "SPILLED\n");
        var result = new CapturedRunResult(
            0, "HEAD\n", "", false, TimeSpan.FromSeconds(1), true,
            "lattice", "/tmp/w", 0, 0, spool, null, false, true);

        var workspace = SlashConsoleWorkspace.FromRun(result, "run", "lattice");
        workspace.ToggleRaw();
        var view = workspace.BuildView();

        Assert.True(view.ShowingRaw);
        Assert.Contains(view.RawLines, line => string.Equals(line, "HEAD", StringComparison.Ordinal));
        Assert.Contains(view.RawLines, line => string.Equals(line, "SPILLED", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSpoolReadsHonestlyWithoutThrowing()
    {
        var result = new CapturedRunResult(
            0, "HEAD\n", "", false, TimeSpan.FromSeconds(1), true,
            "lattice", "/tmp/w", 0, 0, "/tmp/does-not-exist.log", null, false, true);

        var workspace = SlashConsoleWorkspace.FromRun(result, "run", "lattice");
        workspace.ToggleRaw();

        Assert.Contains(workspace.BuildView().RawLines, line => string.Equals(line, "HEAD", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitNoticesNameTheLossPerStream()
    {
        var result = new CapturedRunResult(
            0, "HEAD", "", false, TimeSpan.FromSeconds(1), true,
            "lattice", "/tmp/w", 5, 0, "/tmp/s.log", null, false, true);

        var view = SlashConsoleWorkspace.FromRun(result, "run", "lattice").BuildView();

        var notice = Assert.Single(view.LimitNotices);
        Assert.Contains("stdout", notice, StringComparison.Ordinal);
        Assert.Contains("dropped 5 characters", notice, StringComparison.Ordinal);
    }

    // ---- Host journeys ----

    [Fact]
    public void PageDownScrollsAndHomeReturnsToTop()
    {
        var body = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line-{i:00}")) + "\n";
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, body, ""));
        var keys = Text("/generate --seed 42");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, screen) = Drive(keys, runner: runner);
        Assert.Equal(0, exit);
        Assert.Contains("line-00", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("line-39", screen, StringComparison.Ordinal);

        var scrolled = Text("/generate --seed 42");
        scrolled.Add(new TuiKey(TuiKeyKind.Enter));
        scrolled.Add(new TuiKey(TuiKeyKind.PageDown));
        (_, screen) = Drive(scrolled, runner: runner);
        Assert.Contains("line-20", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("line-00", screen, StringComparison.Ordinal);

        var bottom = Text("/generate --seed 42");
        bottom.Add(new TuiKey(TuiKeyKind.Enter));
        bottom.Add(new TuiKey(TuiKeyKind.End));
        (_, screen) = Drive(bottom, runner: runner);
        Assert.Contains("line-39", screen, StringComparison.Ordinal);

        var top = Text("/generate --seed 42");
        top.Add(new TuiKey(TuiKeyKind.Enter));
        top.Add(new TuiKey(TuiKeyKind.End));
        top.Add(new TuiKey(TuiKeyKind.Home));
        (_, screen) = Drive(top, runner: runner);
        Assert.Contains("line-00", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void TabEntersRawViewerAndQReturnsWithInputIntact()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "MAP\n", "note\n"));
        var keys = Text("/generate --seed 42");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Tab));
        var (exit, screen) = Drive(keys, runner: runner);

        Assert.Equal(0, exit);
        Assert.Contains("s save", screen, StringComparison.Ordinal);
        Assert.Contains("MAP", screen, StringComparison.Ordinal);
        Assert.Contains("note", screen, StringComparison.Ordinal);

        keys.Add(new TuiKey(TuiKeyKind.Character, 'q'));
        (exit, screen) = Drive(keys, runner: runner);

        Assert.Equal(0, exit);
        Assert.Contains("tab raw", screen, StringComparison.Ordinal);
        Assert.Contains("/generate --seed 42", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveWritesFullRawToWorkingDirectory()
    {
        using var temp = new TempDir();
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "SAVED-OUT\n", "SAVED-ERR\n"));
        var keys = Text("/generate --seed 42");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Tab));
        keys.Add(new TuiKey(TuiKeyKind.Character, 's'));
        var (exit, screen) = Drive(keys, runner: runner, workdir: temp.Path);

        Assert.Equal(0, exit);
        Assert.Contains("saved ", screen, StringComparison.Ordinal);
        var saved = Assert.Single(Directory.GetFiles(temp.Path, "lattice-console-*.log"));
        var text = File.ReadAllText(saved);
        Assert.Contains("SAVED-OUT", text, StringComparison.Ordinal);
        Assert.Contains("SAVED-ERR", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EscClearsResultAndConsoleAcceptsTheNextCommand()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(0, "MAP\n", ""));
        var keys = Text("/generate --seed 42");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Escape));
        keys.Add(new TuiKey(TuiKeyKind.Escape));
        keys.AddRange(Text("/generate --seed 7"));
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Escape));
        keys.Add(new TuiKey(TuiKeyKind.Escape));
        keys.Add(new TuiKey(TuiKeyKind.Escape));
        var (exit, _) = Drive(keys, runner: runner);

        Assert.Equal(0, exit);
        Assert.Equal(2, runner.Captured.Count);
    }

    [Fact]
    public void StderrIsShownSeparatelyFromStdout()
    {
        var runner = new FakeRunner(_ => FakeCapturedRun.Done(1, "OUT\n", "ERR\n"));
        var keys = Text("/generate --seed 1");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, screen) = Drive(keys, runner: runner);

        Assert.Equal(0, exit);
        Assert.Contains("exit 1 failed", screen, StringComparison.Ordinal);
        Assert.Contains("OUT", screen, StringComparison.Ordinal);
        Assert.Contains("ERR", screen, StringComparison.Ordinal);
        Assert.Contains("stderr", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchResultStatesNothingWasRecorded()
    {
        var runner = new FakeRunner();
        var keys = Text("/simulate --seed 1");
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        var (exit, screen) = Drive(keys, runner: runner, extraKeys: 1);
        Assert.Equal(0, exit);
        Assert.Single(runner.Watched);
        Assert.Contains("records nothing", screen, StringComparison.Ordinal);
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
        FakeRunner? runner = null,
        string workdir = "/tmp/console-test",
        int extraKeys = 0)
    {
        var all = new List<TuiKey>(keys);
        for (var i = 0; i < extraKeys; i++)
        {
            all.Add(new TuiKey(TuiKeyKind.Escape));
        }

        var queue = new Queue<TuiKey>(all);
        var keySource = new KeyQueue(() => queue.Count > 0 ? queue.Dequeue() : (TuiKey?)null);
        var sources = new Queue<IKeySource>();
        sources.Enqueue(keySource);
        sources.Enqueue(new KeyQueue(() => (TuiKey?)null));
        sources.Enqueue(new KeyQueue(() => (TuiKey?)null));
        var factory = new RecordingSession();
        var request = new SlashConsoleHostRequest(
            new StringWriter(),
            new StringWriter(),
            Terminal,
            new TuiConsole(new RecordingControlC(), () => sources.Dequeue()),
            factory,
            new FixedClock(),
            runner ?? new FakeRunner(),
            workdir,
            TrailingIdleFrames: 1);

        var exit = SlashConsoleHost.Run(request).ExitCode;
        return (exit, factory.Written);
    }

    private sealed class FixedClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.FromSeconds(5);
    }

    private sealed class RecordingControlC : IControlCAsInput
    {
        public bool Get() => false;
        public void Set(bool value) { }
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

    private sealed class FakeCapturedRun : ICapturedRun
    {
        private readonly bool _done;
        private readonly CapturedRunResult _result;
        public FakeCapturedRun(bool done, CapturedRunResult result)
        {
            _done = done;
            _result = result;
        }
        public bool IsDone => _done;
        public static FakeCapturedRun Done(int exit, string stdout, string stderr) =>
            new(true, new CapturedRunResult(exit, stdout, stderr, false, TimeSpan.FromSeconds(1), false));
        public CapturedRunResult Finish() => _result;
        public bool TryTakeFinished(out CapturedRunResult result)
        {
            if (!_done)
            {
                result = default!;
                return false;
            }
            result = _result;
            return true;
        }
        public void Cancel() { }
    }

    private sealed class TempDir : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lattice-workspace-" + Guid.NewGuid().ToString("N"));

        internal TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
