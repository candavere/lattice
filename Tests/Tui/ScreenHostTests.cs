using System.Text;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The seam every interactive screen in this library draws through: it decides
/// whether the terminal can carry a full-screen view at all, asks for UTF-8,
/// enters the alternate screen, writes a frame only when it differs from the one
/// on show, and puts the terminal back.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of <see cref="TuiHost"/> that is not about a replay. The
/// cockpit is one consumer and the setup screen is another, and neither should
/// have to re-derive the order — ask for UTF-8 before entering, enter before
/// writing, restore on the way out — or re-decide for itself whether a
/// redirected stream can carry a screen.
/// </para>
/// <para>
/// The assertions are about that order and about the diff, not about any one
/// screen's contents; the cockpit's own goldens pin what it draws.
/// </para>
/// </remarks>
public class ScreenHostTests
{
    /// <summary>
    /// A redirected run must be refused before anything is built, because building
    /// a key source is what asks the process's console for its input mode and on
    /// Windows that ask throws when standard input is a file or a pipe. The fake
    /// session factory counts every call so "nothing was touched" is an assertion
    /// rather than a claim.
    /// </summary>
    [Theory]
    [InlineData(true, true, "standard input and standard output")]
    [InlineData(true, false, "standard input")]
    [InlineData(false, true, "standard output")]
    public void ARedirectedTerminalIsRefusedWithNothingBuiltAndNothingAsked(bool input, bool output, string expected)
    {
        var surface = new RecordingSurface();

        var opened = ScreenHost.Open(surface, Interactive() with { InputRedirected = input, OutputRedirected = output }, forceAscii: false, Output());

        Assert.Null(opened.Screen);
        Assert.Contains(expected, opened.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, surface.Utf8Requests);
        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Writes);
        Assert.Equal("", surface.Text);
    }

    /// <summary>
    /// The refusal is the host's own line, not a second opinion: a screen that
    /// reworded it would make the same fault report differently depending on which
    /// screen hit it.
    /// </summary>
    [Fact]
    public void TheRefusalIsExactlyTheLineTheHostWouldHavePrinted()
    {
        var capabilities = Interactive() with { InputRedirected = true };

        var opened = ScreenHost.Open(new RecordingSurface(), capabilities, forceAscii: false, Output());

        Assert.Equal(TuiHost.RefusalFor(capabilities), opened.Refusal);
    }

    [Fact]
    public void TheEncodingRequestPrecedesEnteringAndTheFrameAndTheRestoreFollowIt()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Present(new CellBuffer(4, 2));
        screen.Dispose();

        // Changing the console encoding after the alternate screen is active resets
        // console state on Windows and drops the mode the guard set, so this order
        // is a correctness requirement rather than a preference.
        Assert.Equal(["utf8", "enter", "frame", "restore"], surface.Events);
    }

    [Fact]
    public void AFrameIdenticalToTheOneOnScreenIsNotWritten()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        var first = Grid("hello");
        screen.Present(first);
        screen.Present(first);

        Assert.Equal(1, surface.Writes);
    }

    /// <summary>
    /// The first present must write every cell of the frame it was given, because
    /// there is no previous frame to diff against: a screen that started empty and
    /// assumed a blank terminal would draw its borders over whatever was there.
    /// </summary>
    [Fact]
    public void TheFirstFrameIsWrittenWhole()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Present(Grid("hello"));

        Assert.Contains("hello", surface.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two frames differing in one cell send one cell. The written bytes name the
    /// absolute position they are for and carry nothing else, which is what makes
    /// a frame cheap enough to redraw on every keystroke.
    /// </summary>
    [Fact]
    public void OnlyTheChangedCellIsRewritten()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Present(Grid("hello"));
        surface.ClearText();
        screen.Present(Grid("help!"));

        var frame = surface.Text;
        Assert.Contains("!", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("hel", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("lo", frame, StringComparison.Ordinal);
    }

    /// <summary>
    /// A terminal that has gone away costs the screen nothing: the failure is
    /// swallowed and the previous grid is kept, so a run carries on quietly until
    /// the reader quits rather than taking the process down over an unplugged
    /// display.
    /// </summary>
    [Fact]
    public void ATerminalThatCannotBeWrittenToDoesNotRaise()
    {
        var surface = new RecordingSurface { FailWrites = true };
        var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Present(Grid("hello"));
        screen.Present(Grid("help!"));

        Assert.Equal(0, surface.Writes);
    }

    /// <summary>
    /// A write that failed must not become the screen's idea of what is on show.
    /// If it did, the next frame would diff against a change the terminal never
    /// received and the reader would be left looking at a stale cell.
    /// </summary>
    [Fact]
    public void AFailedWriteIsRetriedBecauseThePreviousFrameIsKept()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Present(Grid("hello"));
        surface.FailWrites = true;
        screen.Present(Grid("hellp"));
        surface.FailWrites = false;
        surface.ClearText();
        screen.Present(Grid("hellq"));

        // The third present had the whole difference to send — the cell the failed
        // write was carrying — rather than nothing, because the failed frame was
        // never remembered as being on show.
        Assert.Contains("q", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("p", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposingTwiceRestoresTheTerminalOnce()
    {
        var surface = new RecordingSurface();
        var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;

        screen.Dispose();
        screen.Dispose();

        Assert.Single(surface.Events.FindAll(entry => entry == "restore"));
    }

    [Theory]
    [InlineData(true, false, GlyphMode.Unicode)]
    [InlineData(false, false, GlyphMode.Ascii)]
    [InlineData(true, true, GlyphMode.Ascii)]
    public void TheGlyphVocabularyIsChosenOnceForTheWholeScreen(bool utf8, bool forceAscii, GlyphMode expected)
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive() with { Utf8 = utf8 }, forceAscii, Output()).Screen!;

        Assert.Equal(expected, screen.Glyphs);
    }

    /// <summary>
    /// A console asked for a size it does not have answers zero or a negative
    /// number rather than failing, so the value is checked before it reaches a
    /// grid: <see cref="CellBuffer"/> throws on a zero dimension.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-4, 30)]
    public void ATerminalThatReportsNoUsableSizeStillGetsAWholeGrid(int width, int height)
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive() with { Width = width, Height = height }, forceAscii: false, Output()).Screen!;

        Assert.True(screen.Size.Width >= 1);
        Assert.True(screen.Size.Height >= 1);
        screen.Present(new CellBuffer(screen.Size.Width, screen.Size.Height));
        Assert.Equal(1, surface.Writes);
    }

    [Fact]
    public void AnInterruptRaisedByTheTerminalIsForwardedToTheScreen()
    {
        var surface = new RecordingSurface();
        using var screen = ScreenHost.Open(surface, Interactive(), forceAscii: false, Output()).Screen!;
        var interrupts = 0;
        screen.Interrupted += () => interrupts++;

        surface.Interrupt();

        Assert.Equal(1, interrupts);
    }

    /// <summary>The writer a screen frames into; the fake session never reads it.</summary>
    private static TextWriter Output() => TextWriter.Null;

    private static CellBuffer Grid(string text)
    {
        var cells = new CellBuffer(6, 2);
        cells.DrawText(0, 0, text, new Cell(' ', null, null));
        return cells;
    }

    private static TerminalCapabilities Interactive() => new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>The terminal seam, recorded: what was asked and in what order.</summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly StringBuilder _output = new();
        private readonly RecordingSession _session;

        internal RecordingSurface() => _session = new RecordingSession(this);

        internal List<string> Events { get; } = new();

        internal int Utf8Requests { get; private set; }

        internal int Entries { get; private set; }

        internal int Writes => _session.Writes;

        internal string Text => _output.ToString();

        internal bool FailWrites { get; set; }

        /// <summary>Forgets the bytes written so far, so the next frame's are on their own.</summary>
        internal void ClearText() => _output.Clear();

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

        internal void Interrupt() => _session.RaiseInterrupt();

        private sealed class RecordingSession : ITerminalSession
        {
            private readonly RecordingSurface _owner;
            private bool _restored;

            internal RecordingSession(RecordingSurface owner) => _owner = owner;

            internal int Writes { get; private set; }

            public event Action? Interrupted;

            public void Write(string text)
            {
                if (_owner.FailWrites)
                {
                    throw new IOException("the terminal went away");
                }

                Writes++;
                _owner.Events.Add("frame");
                _owner._output.Append(text);
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
