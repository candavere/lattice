namespace Lattice.Tui;

/// <summary>
/// One full-screen draw loop's share of a terminal: the alternate screen is
/// entered, frames go to it, and disposing puts it back.
/// </summary>
/// <remarks>
/// <para>
/// A screen — the cockpit, or the setup screen in front of it — does not talk to
/// <see cref="TerminalGuard"/> directly. It asks for a <see cref="ScreenHost"/>,
/// which is where the order lives: refuse first and touch nothing, then ask the
/// console for UTF-8, then enter, then write. That order is not a preference. The
/// encoding request has to precede the entry because changing the console
/// encoding resets console state on Windows and drops the mode the guard set, and
/// the refusal has to precede everything because on Windows asking a redirected
/// console for its input mode throws.
/// </para>
/// <para>
/// <b>Redraw only on change.</b> Each composed frame is diffed against the one on
/// show and only the difference is written, so a screen that has nothing to say
/// costs no write traffic at all.
/// </para>
/// </remarks>
public sealed class ScreenHost : IDisposable
{
    private readonly ITerminalSession _session;
    private readonly ColorDepth _depth;
    private CellBuffer? _previous;
    private bool _disposed;

    /// <summary>The grid a screen should compose for: the terminal's own size, never degenerate.</summary>
    public PaneSize Size { get; }

    /// <summary>The glyph vocabulary for the whole screen, chosen once at the edge.</summary>
    public GlyphMode Glyphs { get; }

    /// <summary>
    /// Raised when the reader interrupts the process. The terminal has already
    /// been restored by the time this fires, so a screen only has to stop drawing.
    /// </summary>
    public event Action? Interrupted;

    private ScreenHost(ITerminalSession session, PaneSize size, GlyphMode glyphs, ColorDepth depth)
    {
        _session = session;
        Size = size;
        Glyphs = glyphs;
        _depth = depth;
        _session.Interrupted += OnInterrupted;
    }

    /// <summary>
    /// Enters the alternate screen, or returns the one line a redirected run
    /// refuses with and builds nothing.
    /// </summary>
    /// <param name="factory">The terminal seam, which is asked only once the run is known to be possible.</param>
    /// <param name="capabilities">What the terminal can do, resolved once.</param>
    /// <param name="forceAscii">Whether the caller asked for ASCII glyphs whatever the locale says.</param>
    /// <param name="output">Where frames go. In a real run, the process's own stdout.</param>
    public static ScreenOpenResult Open(
        ITerminalSessionFactory factory,
        TerminalCapabilities capabilities,
        bool forceAscii,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(output);

        var refusal = TuiHost.RefusalFor(capabilities);
        if (refusal is not null)
        {
            return new ScreenOpenResult(null, refusal);
        }

        // Once per screen, and before the alternate screen: see the remarks.
        factory.RequestUtf8Output();

        var screen = new ScreenHost(
            factory.Enter(output),
            new PaneSize(Math.Max(1, capabilities.Width), Math.Max(1, capabilities.Height)),
            GlyphModes.Resolve(capabilities.Utf8, forceAscii),
            capabilities.Depth);

        return new ScreenOpenResult(screen, null);
    }

    /// <summary>
    /// Writes what changed since the last frame, and remembers this frame as the
    /// one on show. A terminal that has gone away costs the screen nothing: the
    /// failure is swallowed and the previous frame is kept, so the caller's loop
    /// carries on quietly.
    /// </summary>
    public void Present(CellBuffer composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var diff = FrameDiff.Render(_previous, composed, _depth);
        if (diff.Length == 0)
        {
            _previous = composed;
            return;
        }

        try
        {
            _session.Write(diff);
            _previous = composed;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Deliberately not remembering the frame: the terminal never received it,
            // so diffing against it next time would hide exactly the cell that is
            // still wrong on screen.
        }
    }

    /// <summary>Forgets what is on show, so the next <see cref="Present"/> repaints every cell.</summary>
    /// <remarks>
    /// <see cref="FrameDiff"/> only ever writes cells and cannot erase one, so a
    /// screen that has been resized has to ask for a whole repaint. That is the
    /// supported way to clear the extent it no longer covers.
    /// </remarks>
    public void RepaintEverything() => _previous = null;

    /// <summary>Puts the terminal back. Safe to call more than once; the guard restores exactly once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Interrupted -= OnInterrupted;
        _session.Dispose();
    }

    private void OnInterrupted() => Interrupted?.Invoke();
}

/// <summary>
/// What opening a screen produced: the screen, or the one-line reason there is none.
/// <para>
/// A value rather than an exception or a nullable screen, so a caller cannot
/// accidentally draw to nothing, and so the refusal and the screen are two halves
/// of one answer.
/// </para>
/// </summary>
/// <param name="Screen">The entered screen, or <c>null</c> when the run was refused.</param>
/// <param name="Refusal">The one line to print and the status to return, or <c>null</c> when a screen was entered.</param>
public readonly record struct ScreenOpenResult(ScreenHost? Screen, string? Refusal)
{
    /// <summary>Whether a screen was entered.</summary>
    public bool Entered => Screen is not null;
}
