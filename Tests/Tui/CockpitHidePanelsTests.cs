using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The cockpit's hide-panels mode: one key hides the side panes so the world
/// pane gets the room, and the same key brings them back with nothing lost.
/// Hiding is display only — it never moves the cursor, the speed, the pause
/// state or any recorded byte — and the normal-mode hint row is byte-identical
/// to the frame without it, so every existing cockpit golden is unchanged.
/// </summary>
public class CockpitHidePanelsTests
{
    private static readonly PaneSize Cockpit = new(100, 30);
    private static readonly PaneSize Fallback = new(80, 25);

    /// <summary>The character that hides and shows the side panes, in one place.</summary>
    private static TuiKey HideKey() => new(TuiKeyKind.Character, TuiHost.HidePanelsKey);

    /// <summary>Words no new golden may use, the same list the Ledger goldens scan.</summary>
    private static readonly string[] ForbiddenClaimWords = [
        "better",
        "worse",
        "improved",
        "improve",
        "regressed",
        "regression",
        "inferior",
        "superior",
        "outperforms",
    ];

    [Fact]
    public void HiddenModeShowsOnlyTheWorldPane()
    {
        var lines = Render(Document(), 3, hidden: true);

        Assert.Contains("WORLD", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain("SCOREBOARD", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT LOG", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain("TIMELINE", string.Join("\n", lines), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHiddenWorldPaneHoldsTheRendererOutputAtTheSizeTheLayoutGivesIt()
    {
        var document = Document();
        var cells = Cells(document, 3, hidden: true);
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[3],
            document.TrailBefore(3, 3),
            new PaneSize(96, 26),
            GlyphMode.Unicode,
            Palette.PanelBackground));

        // The hidden world pane is the full terminal minus its own frame: inner
        // 96x26 at offset (2, 2), the renderer's grid copied in, not redrawn.
        for (var y = 0; y < 26; y++)
        {
            for (var x = 0; x < 96; x++)
            {
                Assert.Equal(world.Cells[x, y], cells[x + 2, y + 2]);
            }
        }
    }

    [Fact]
    public void TheNormalHintRowIsByteIdenticalWithTheFlagOff()
    {
        var withoutFlag = Render(Document(), 3, hidden: false);
        var withFlagOff = Cells(Document(), 3, hidden: false).ToLines();

        Assert.Equal(withoutFlag, withFlagOff);
        Assert.Equal(
            CockpitLayout.KeyHints,
            withoutFlag[^1].Substring(2, CockpitLayout.KeyHints.Length));
    }

    [Fact]
    public void TheHiddenHintRowNamesTheWayBackAndStaysAscii()
    {
        var replay = Render(Document(), 3, hidden: true);
        var replayHints = replay[^1].Substring(2, CockpitLayout.HiddenKeyHints.Length);

        Assert.Equal(CockpitLayout.HiddenKeyHints, replayHints);
        Assert.All(CockpitLayout.HiddenKeyHints, glyph => Assert.True(glyph < 128));

        var live = RenderLive(hidden: true);
        var liveHints = live[^1].Substring(2, CockpitLayout.HiddenLiveKeyHints.Length);

        Assert.Equal(CockpitLayout.HiddenLiveKeyHints, liveHints);
        Assert.All(CockpitLayout.HiddenLiveKeyHints, glyph => Assert.True(glyph < 128));
    }

    [Fact]
    public void HiddenThenShownReturnsTheFrameFromBeforeHiding()
    {
        var document = Document();
        var before = Render(document, 3, hidden: false);
        var hidden = Render(document, 3, hidden: true);
        var shown = Render(document, 3, hidden: false);

        Assert.NotEqual(before, hidden);
        Assert.Equal(before, shown);
    }

    [Fact]
    public void TheFallbackAlreadyShowsOnlyTheWorldSoTheFlagChangesNothingThere()
    {
        var document = Document();
        var normal = CockpitLayout.Render(Request(document, 3, Fallback, GlyphMode.Unicode, hidden: false)).ToLines();
        var hidden = CockpitLayout.Render(Request(document, 3, Fallback, GlyphMode.Unicode, hidden: true)).ToLines();

        Assert.Equal(normal, hidden);
        Assert.Contains("WORLD", string.Join("\n", hidden), StringComparison.Ordinal);
    }

    [Fact]
    public void AHiddenAsciiFrameIsAllAscii()
    {
        var cells = CockpitLayout.Render(Request(Document(), 3, Cockpit, GlyphMode.Ascii, hidden: true));

        foreach (var line in cells.ToLines())
        {
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }
    }

    [Fact]
    public void ALongActionListStaysInsideItsPaneWhenHidden()
    {
        var cells = Cells(Document(new string('m', 200)), 3, hidden: true);

        Assert.Equal('│', cells[99, 15].Glyph);
        Assert.Equal('│', cells[99, 27].Glyph);
        Assert.All(cells.ToLines(), row => Assert.True(row.Length <= Cockpit.Width));
    }

    [Fact]
    public void BothCursorsIgnoreTheHideKey()
    {
        var document = Document();
        var replay = new ReplayPlayback(document);

        Assert.False(replay.Apply(HideKey()));
        Assert.Equal(document.FirstIndex, replay.Index);
        Assert.True(replay.IsPaused);
        Assert.Equal(4.0, replay.StepsPerSecond);

        var live = new LivePlayback(new StubEpisode(document));

        Assert.False(live.Apply(HideKey()));
        Assert.Equal(0, live.Index);
        Assert.True(live.IsPaused);
        Assert.Equal(4.0, live.StepsPerSecond);
    }

    [Fact]
    public void TheHostConsumesTheHideKeyAndLeavesTheCursorAlone()
    {
        var surface = new RecordingSurface();
        var cursor = new SpyCursor(Document());
        using var keys = new ScriptedKeys(HideKey(), HideKey(), new TuiKey(TuiKeyKind.Character, 'q'));

        var result = TuiHost.Run(new TuiHostRequest(
            cursor.Document,
            surface.Writer(),
            surface.Errors,
            Interactive(),
            ForceAscii: false,
            surface,
            keys,
            new ManualClock(),
            Cursor: cursor));

        Assert.Equal(0, result.ExitCode);

        // The first frame, one per toggle, and nothing for the quit key itself.
        Assert.Equal(3, surface.Writes);

        // The host consumed both toggles: the cursor never saw them, and its
        // position, pause and speed state are exactly as the run opened them.
        Assert.Empty(cursor.Seen);
        Assert.Equal(cursor.Document.FirstIndex, cursor.Index);
        Assert.True(cursor.IsPaused);
        Assert.Equal(4.0, cursor.StepsPerSecond);
    }

    [Fact]
    public void AHostStartedHiddenShowsOnlyTheWorldOnItsFirstFrame()
    {
        var surface = new RecordingSurface();
        using var keys = new ScriptedKeys(new TuiKey(TuiKeyKind.Character, 'q'));

        var result = TuiHost.Run(new TuiHostRequest(
            Document(),
            surface.Writer(),
            surface.Errors,
            Interactive(),
            ForceAscii: false,
            surface,
            keys,
            new ManualClock(),
            HidePanels: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, surface.Writes);
        Assert.Contains("WORLD", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SCOREBOARD", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenReplayIsTheCommittedFrame()
    {
        var document = Document();
        var lines = Render(document, 3, hidden: true);

        Assert.Equal("LATTICE TUI  replay  hidden-episode", lines[0].Substring(2, 35));
        Assert.Equal(" WORLD ", lines[1].Substring(3, 7));
        Assert.Equal(CockpitLayout.HiddenKeyHints, lines[^1].Substring(2, CockpitLayout.HiddenKeyHints.Length));
        AssertWorldRows(lines, document, frameIndex: 3, width: 96, height: 26);
        Assert.Equal(Golden("cockpit-hidden-100x30"), string.Join("\n", lines));
    }

    [Fact]
    public void HiddenReplayAsciiIsTheCommittedFrame()
    {
        var lines = CockpitLayout.Render(Request(Document(), 3, Cockpit, GlyphMode.Ascii, hidden: true)).ToLines();

        Assert.Equal(CockpitLayout.HiddenKeyHints, lines[^1].Substring(2, CockpitLayout.HiddenKeyHints.Length));
        Assert.Equal(Golden("cockpit-hidden-ascii-100x30"), string.Join("\n", lines));
    }

    [Fact]
    public void HiddenLiveIsTheCommittedFrame()
    {
        var lines = RenderLive(hidden: true);

        Assert.Equal("LATTICE TUI  live  live-episode", lines[0].Substring(2, 31));
        Assert.Equal(" WORLD ", lines[1].Substring(3, 7));
        Assert.Equal(CockpitLayout.HiddenLiveKeyHints, lines[^1].Substring(2, CockpitLayout.HiddenLiveKeyHints.Length));
        Assert.DoesNotContain("TIMELINE", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.Equal(Golden("live-hidden-100x30"), string.Join("\n", lines));
    }

    [Fact]
    public void NoNewGoldenUsesAClaimWord()
    {
        foreach (var name in new[] { "cockpit-hidden-100x30", "cockpit-hidden-ascii-100x30", "live-hidden-100x30" })
        {
            var text = Golden(name).ToLowerInvariant();
            foreach (var word in ForbiddenClaimWords)
            {
                Assert.DoesNotContain(word, text, StringComparison.Ordinal);
            }
        }
    }

    private static string[] Render(ReplayDocument document, int index, bool hidden) =>
        Cells(document, index, hidden).ToLines();

    private static CellBuffer Cells(ReplayDocument document, int index, bool hidden) =>
        CockpitLayout.Render(Request(document, index, Cockpit, GlyphMode.Unicode, hidden));

    private static string[] RenderLive(bool hidden)
    {
        var document = LiveDocument();
        var live = new LiveState(
            ProducedTicks: 7,
            MaximumTicks: 30,
            Seed: 7,
            AgentRoles: new[] { "Sentry", "Infiltrator" });

        return CockpitLayout.Render(new CockpitRequest(
            document,
            5,
            Cockpit,
            GlyphMode.Unicode,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: false, StepsPerSecond: 4.0),
            Live: live,
            HidePanels: hidden)).ToLines();
    }

    private static CockpitRequest Request(
        ReplayDocument document,
        int index,
        PaneSize size,
        GlyphMode glyphs,
        bool hidden) =>
        new(
            document,
            index,
            size,
            glyphs,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0),
            HidePanels: hidden);

    private static void AssertWorldRows(string[] lines, ReplayDocument document, int frameIndex, int width, int height)
    {
        var world = WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[frameIndex],
            document.TrailBefore(frameIndex, CockpitLayout.TrailFrames),
            new PaneSize(width, height),
            GlyphMode.Unicode,
            Palette.PanelBackground));

        for (var y = 0; y < height; y++)
        {
            Assert.Equal(
                string.Join(string.Empty, world.Cells.ToLines()[y]),
                lines[y + 2].Substring(2, width));
        }
    }

    private static ReplayDocument Document(string? actions = null)
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[]
                {
                    new WorldAgent(0, 0, tick),
                    new WorldAgent(1, 1, tick / 2, tick == 2 ? new WorldTransit(1, 2, 1, 2) : null),
                },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : actions ?? "agent0: Move(1); agent1: Collect(0)",
                stateDigest: null,
                isTerminal: tick == 6,
                terminalReason: tick == 6 ? "tick-limit" : null,
                winnerSlot: tick == 6 ? 0 : null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[]
                {
                    new WorldZone(0, "0", 0, 0, Role: "EntryHall"),
                    new WorldZone(1, "1", 10, 0, Role: "Corridor"),
                    new WorldZone(2, "2", 20, 10, Role: "Vault"),
                },
                new[] { new WorldResource(0, 0, 2, 2), new WorldResource(1, 1, 12, 2) },
                new[] { new WorldEdge(0, 0, 1, Capacity: 1), new WorldEdge(1, 1, 2, Capacity: 2) }),
            new ReplayHeader(42, 5, "hidden-episode", new[] { "Sentry", "Infiltrator" }, 6, null, 0),
            frames);
    }

    private static ReplayDocument LiveDocument()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 7; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick), new WorldAgent(1, 2, tick / 2) },
                tick == 0 ? Array.Empty<int>() : new[] { 0 },
                tick == 0 ? null : "agent0: Move(2); agent1: Wait",
                stateDigest: null,
                isTerminal: false,
                terminalReason: null,
                winnerSlot: null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[]
                {
                    new WorldZone(0, "0", 4, 4, Role: "SentryPost"),
                    new WorldZone(1, "1", 30, 4, Role: "EntryHall"),
                    new WorldZone(2, "2", 30, 16, Role: "Corridor"),
                    new WorldZone(3, "3", 52, 16, Role: "ChokeDoorway"),
                },
                new[]
                {
                    new WorldResource(0, 1, 30, 8),
                    new WorldResource(1, 2, 34, 16),
                    new WorldResource(2, 3, 52, 20),
                },
                new[]
                {
                    new WorldEdge(0, 0, 1, Capacity: 1),
                    new WorldEdge(1, 1, 2, Capacity: 2),
                    new WorldEdge(2, 2, 3, Capacity: 1),
                }),
            new ReplayHeader(7, 5, "live-episode", new[] { "Sentry", "Infiltrator" }, 30, null, 0),
            frames);
    }

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "Tests",
            "Tui",
            "Goldens",
            name + ".txt")).TrimEnd('\n');

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

    private static TerminalCapabilities Interactive() => new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>A clock that never advances, so a scripted run needs no sleep to pass time.</summary>
    private sealed class ManualClock : IUiClock
    {
        public TimeSpan Now { get; set; }
    }

    /// <summary>Keys a test hands the host one at a time, then end of input.</summary>
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

    /// <summary>A cursor that records every key the host forwards to it.</summary>
    private sealed class SpyCursor : ICockpitCursor
    {
        private readonly ReplayPlayback _inner;

        internal SpyCursor(ReplayDocument document) => _inner = new ReplayPlayback(document);

        internal List<TuiKey> Seen { get; } = new();

        public ReplayDocument Document => _inner.Document;

        public int Index => _inner.Index;

        public bool IsPaused => _inner.IsPaused;

        public double StepsPerSecond => _inner.StepsPerSecond;

        public double Phase => _inner.Phase;

        public LiveState? Live => _inner.Live;

        public bool IsFinished => _inner.IsFinished;

        public bool Apply(TuiKey key)
        {
            Seen.Add(key);
            return _inner.Apply(key);
        }

        public bool Advance(TimeSpan elapsed) => _inner.Advance(elapsed);
    }

    /// <summary>A live episode stand-in with a fixed frame list and no stepper.</summary>
    private sealed class StubEpisode : ILiveEpisode
    {
        private readonly ReplayDocument _document;

        internal StubEpisode(ReplayDocument document) => _document = document;

        public WorldMap Map => _document.Map;

        public IReadOnlyList<ReplayFrame> Frames => _document.Frames;

        public int MaximumTicks => 6;

        public ulong? Seed => 42;

        public string Label => "hidden-episode";

        public IReadOnlyList<string>? AgentRoles => new[] { "Sentry", "Infiltrator" };

        public string? FinishedReason => null;

        public LiveStepperStatus StepperStatus => LiveStepperStatus.Running;

        public bool HasStopped => false;

        public string? Notice => null;

        public void RequestTick()
        {
        }

        public void RequestRestart()
        {
        }
    }

    /// <summary>The terminal seam, recorded: what the host wrote in between.</summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly StringWriter _output = new();
        private readonly RecordingSession _session;

        public RecordingSurface() => _session = new RecordingSession(this);

        public int Writes => _session.Writes;

        public string Text => _output.ToString();

        public TextWriter Writer() => _output;

        public TextWriter Errors { get; } = new StringWriter();

        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output) => _session;

        private sealed class RecordingSession : ITerminalSession
        {
            private readonly RecordingSurface _owner;

            internal RecordingSession(RecordingSurface owner) => _owner = owner;

            public int Writes { get; private set; }

            public event Action? Interrupted { add { } remove { } }

            public void Write(string text)
            {
                Writes++;
                _owner._output.Write(text);
            }

            public void Dispose()
            {
            }
        }
    }
}
