using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The <c>--hide-panels</c> start-up flag: accepted beside <c>--ascii</c> on the
/// two cockpit subcommands, refused as an unknown flag on the Ledger, and never
/// ahead of the redirected-stream refusal.
/// </summary>
public class TuiHidePanelsCommandTests
{
    [Fact]
    public void ReplayHidePanelsOnRedirectedStreamsIsStillRefused()
    {
        foreach (var args in new[]
        {
            new[] { "tui", "replay", "--hide-panels", Committed("site", "demo.jsonl") },
            new[] { "tui", "replay", Committed("site", "demo.jsonl"), "--hide-panels" },
        })
        {
            var (exit, stdout, stderr) = Run(args);

            Assert.Equal(UsageError.ExitCode, exit);
            Assert.Equal("", stdout);
            Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("redirected", stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SimulateHidePanelsOnRedirectedStreamsIsStillRefused()
    {
        var (exit, stdout, stderr) = Run("tui", "simulate", "--seed", "42", "--steps", "2", "--hide-panels");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ARedirectedReplayWithHidePanelsTouchesTheConsoleInputSettingZeroTimes()
    {
        var controlC = new ThrowingControlC();

        var (exit, stdout, stderr) = RunWith(controlC, "tui", "replay", "--hide-panels", Committed("site", "demo.jsonl"));

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
        Assert.Equal(0, controlC.Calls);
    }

    [Fact]
    public void LedgerHidePanelsIsAUsageError()
    {
        var (exit, stdout, stderr) = Run("tui", "ledger", "--hide-panels", "artifact.json");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown flag", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulateHidePanelsWithoutASeedIsAUsageError()
    {
        var (exit, stdout, stderr) = Run("tui", "simulate", "--hide-panels");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("--seed", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageLineNamesHidePanelsOnTheTwoCockpitSubcommands()
    {
        var (exit, _, stderr) = Run("tui");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("--hide-panels", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void StartViewerOpensHiddenWhenAsked()
    {
        var hidden = StartViewer(hidePanels: true);

        Assert.Equal(0, hidden.ExitCode);
        Assert.Contains("WORLD", hidden.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SCOREBOARD", hidden.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StartViewerOpensNormallyByDefault()
    {
        var normal = StartViewer(hidePanels: false);

        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("SCOREBOARD", normal.Text, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Text) StartViewer(bool hidePanels)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var keys = new ScriptedKeys(new TuiKey(TuiKeyKind.Character, 'q'));

        var result = CliApp.StartViewer(
            Document(),
            stdout,
            stderr,
            Interactive(),
            ascii: false,
            new TuiConsole(new NoopControlC(), () => keys),
            cursor: null,
            new RecordingSessionFactory(),
            new StaticClock(),
            hidePanels: hidePanels);

        return (result.ExitCode, stdout.ToString());
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

    private static TerminalCapabilities Interactive() => new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return (CliApp.Run(args, stdout, stderr), stdout.ToString(), stderr.ToString());
    }

    private static (int ExitCode, string Stdout, string Stderr) RunWith(IControlCAsInput controlC, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return (
            CliApp.Run(args, stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default with { ControlCAsInput = controlC }),
            stdout.ToString(),
            stderr.ToString());
    }

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

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

    /// <summary>The console input setting, throwing the way a redirected Windows console does.</summary>
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

    /// <summary>The console input setting that accepts everything and records nothing.</summary>
    private sealed class NoopControlC : IControlCAsInput
    {
        public bool Get() => false;

        public void Set(bool value)
        {
        }
    }

    /// <summary>Keys a test hands over one at a time, then end of input.</summary>
    private sealed class ScriptedKeys : IKeySource
    {
        private readonly Queue<TuiKey> _keys;

        public ScriptedKeys(params TuiKey[] keys) => _keys = new Queue<TuiKey>(keys);

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            if (_keys.Count > 0)
            {
                key = _keys.Dequeue();
                return KeyWait.Key;
            }

            key = default;
            return KeyWait.Closed;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A clock that never advances, so a scripted run needs no sleep to pass time.</summary>
    private sealed class StaticClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.Zero;
    }

    /// <summary>A terminal that writes frames into the run's own output.</summary>
    private sealed class RecordingSessionFactory : ITerminalSessionFactory
    {
        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output) => new Session(output);

        private sealed class Session : ITerminalSession
        {
            private readonly TextWriter _output;

            internal Session(TextWriter output) => _output = output;

            public event Action? Interrupted { add { } remove { } }

            public void Write(string text) => _output.Write(text);

            public void Dispose()
            {
            }
        }
    }
}
