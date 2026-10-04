using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The one entry point every interactive path goes through, asserted through its
/// seam: what it asks the terminal for and in what order, what it refuses to do,
/// and that it writes nothing when nothing has changed.
/// </summary>
public class TuiHostTests
{
    [Fact]
    public void TheHostAsksForUtf8ThenEntersTheAlternateScreenThenWritesFramesThenRestores()
    {
        var surface = new RecordingSurface();

        var result = TuiHost.Run(Request(surface, new ScriptedKeys("q")));

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Refusal);

        // Exactly one encoding request and exactly one entry, and the encoding
        // request first: changing the console encoding after the alternate screen
        // is active resets console state on Windows and drops the mode the guard
        // just set.
        Assert.Equal(1, surface.Utf8Requests);
        Assert.Equal(1, surface.Entries);
        Assert.Equal("utf8", surface.Events[0]);
        Assert.Equal("enter", surface.Events[1]);
        Assert.Contains("frame", surface.Events);
        Assert.Equal("restore", surface.Events[^1]);

        var firstFrame = surface.Events.IndexOf("frame");
        Assert.True(firstFrame > surface.Events.IndexOf("enter"));
        Assert.True(firstFrame < surface.Events.IndexOf("restore"));
    }

    [Fact]
    public void TheHostWritesTheFirstFrameBeforeAnyKeyIsPressed()
    {
        var surface = new RecordingSurface();

        var result = TuiHost.Run(Request(surface, new ScriptedKeys()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, surface.Writes);
        Assert.Contains("WORLD", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithNothingToDoWritesNothingMore()
    {
        var surface = new RecordingSurface();

        TuiHost.Run(Request(surface, new ScriptedKeys("q")));

        // One frame, and no more: a paused replay on a clock that never moves has
        // no reason to write again.
        Assert.Equal(1, surface.Writes);
    }

    [Fact]
    public void AKeyThatMovesTheCursorCausesOneMoreFrameAndAKeyThatDoesNotCausesNone()
    {
        var moving = new RecordingSurface();
        TuiHost.Run(Request(moving, new ScriptedKeys("n", "n", "q")));

        // The first frame, one per step, and nothing for the quit key itself.
        Assert.Equal(3, moving.Writes);

        var idle = new RecordingSurface();
        TuiHost.Run(Request(idle, new ScriptedKeys("z", "z", "z", "q")));

        Assert.Equal(1, idle.Writes);
    }

    [Fact]
    public void ASpeedKeyAtTheTopOfTheLadderStopsChangingTheFrame()
    {
        var surface = new RecordingSurface();

        // Two presses move the speed up the ladder and are visible on the timeline;
        // the rest are at the top already and change nothing.
        TuiHost.Run(Request(surface, new ScriptedKeys(">", ">", ">", ">", ">", "q")));

        Assert.Equal(3, surface.Writes);
    }

    [Fact]
    public void TheHostRefusesToEnterTheAlternateScreenWhenStandardInputIsRedirected()
    {
        var surface = new RecordingSurface();

        var result = TuiHost.Run(Request(surface, new ScriptedKeys("q"), Interactive() with { InputRedirected = true }));

        Assert.Equal(2, result.ExitCode);
        Assert.NotNull(result.Refusal);
        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Utf8Requests);
        Assert.Equal(0, surface.Writes);
        Assert.Equal("", surface.Text);
        Assert.Single(surface.ErrorsText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("standard input", surface.ErrorsText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostRefusesToEnterTheAlternateScreenWhenStandardOutputIsRedirected()
    {
        var surface = new RecordingSurface();

        var result = TuiHost.Run(Request(surface, new ScriptedKeys("q"), Interactive() with { OutputRedirected = true }));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("standard output", result.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, surface.Entries);
        Assert.Equal("", surface.Text);
    }

    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    public void AQuitKeyIsAcceptedInEitherCase(string key)
    {
        var surface = new RecordingSurface();

        Assert.Equal(0, TuiHost.Run(Request(surface, new ScriptedKeys(key))).ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
    }

    [Fact]
    public void AnInterruptQuitsAndStillRestoresTheTerminal()
    {
        var surface = new RecordingSurface();
        var keys = new ScriptedKeys { OnWait = surface.Interrupt };

        Assert.Equal(0, TuiHost.Run(Request(surface, keys)).ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
        Assert.Equal(1, surface.Writes);
    }

    [Fact]
    public void ASessionThatFailsToWriteDoesNotTakeTheReplayDown()
    {
        var surface = new RecordingSurface { FailWrites = true };

        var result = TuiHost.Run(Request(surface, new ScriptedKeys("q")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("restore", surface.Events[^1]);
    }

    [Fact]
    public void ATerminalWithoutUtf8GetsTheAsciiGlyphSetForTheWholeRun()
    {
        var surface = new RecordingSurface();

        TuiHost.Run(Request(surface, new ScriptedKeys("q"), Interactive() with { Utf8 = false }));

        foreach (var line in surface.Text.Split('\n'))
        {
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }
    }

    [Fact]
    public void AForcedAsciiRequestBeatsATerminalThatCanEncodeUtf8()
    {
        var surface = new RecordingSurface();

        TuiHost.Run(Request(surface, new ScriptedKeys("q"), forceAscii: true));

        foreach (var line in surface.Text.Split('\n'))
        {
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }
    }

    [Fact]
    public void TheFrameRateIsCappedAtThirtyFramesPerSecond()
    {
        Assert.Equal(30, TuiHost.DefaultMaxFramesPerSecond);
    }

    private static TuiHostRequest Request(
        RecordingSurface surface,
        IKeySource keys,
        TerminalCapabilities? capabilities = null,
        bool forceAscii = false) =>
        new(
            Document(),
            surface.Writer(),
            surface.Errors,
            capabilities ?? Interactive(),
            forceAscii,
            surface,
            keys,
            new ManualClock());

    private static TerminalCapabilities Interactive() => new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    private static ReplayDocument Document()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 4; tick++)
        {
            frames.Add(new ReplayFrame(
                tick,
                isStart: tick == 0,
                new[] { new WorldAgent(0, 0, tick) },
                Array.Empty<int>(),
                tick == 0 ? null : "agent0: Wait",
                stateDigest: null,
                isTerminal: tick == 4,
                terminalReason: tick == 4 ? "tick-limit" : null,
                winnerSlot: null));
        }

        return new ReplayDocument(
            new WorldMap(
                new[] { new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 10, 10) },
                Array.Empty<WorldResource>(),
                new[] { new WorldEdge(0, 0, 1) }),
            new ReplayHeader(42, 5, null, null, 4, null, false),
            frames);
    }

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class ManualClock : IUiClock
    {
        public TimeSpan Now { get; set; }
    }

    /// <summary>Keys a test hands the host one at a time, then end of input.</summary>
    private sealed class ScriptedKeys : IKeySource
    {
        private readonly Queue<TuiKey> _keys;

        public ScriptedKeys(params string[] keys) => _keys = new Queue<TuiKey>(
            keys.Select(key => new TuiKey(TuiKeyKind.Character, key[0])));

        /// <summary>Runs on every wait, which is where a test raises an interrupt.</summary>
        public Action? OnWait { get; init; }

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            OnWait?.Invoke();

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

    /// <summary>
    /// The terminal seam, recorded: which of the two things a host does to a
    /// terminal it did, in which order, and what it wrote in between.
    /// </summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _errors = new();
        private readonly RecordingSession _session;

        public RecordingSurface() => _session = new RecordingSession(this);

        public List<string> Events { get; } = new();

        public int Utf8Requests { get; private set; }

        public int Entries { get; private set; }

        public int Writes => _session.Writes;

        public string Text => _output.ToString();

        public string ErrorsText => _errors.ToString();

        public bool FailWrites { get; init; }

        public TextWriter Writer() => _output;

        public TextWriter Errors => _errors;

        public void Interrupt() => _session.RaiseInterrupt();

        public bool RequestUtf8Output()
        {
            Utf8Requests++;
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

            public int Writes { get; private set; }

            public event Action? Interrupted;

            public void Write(string text)
            {
                if (_owner.FailWrites)
                {
                    throw new IOException("the terminal went away");
                }

                Writes++;
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

            internal void RaiseInterrupt() => Interrupted?.Invoke();
        }
    }
}