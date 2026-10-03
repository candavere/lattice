using System.Text;

namespace Lattice.Tui;

/// <summary>
/// Owns the one live line a long command shows on stderr: it repaints that line
/// as the command progresses and closes it with a success or failure line when
/// the command ends.
/// </summary>
/// <remarks>
/// <para>
/// The decision to write anything at all is made once, in the constructor, from
/// the same <see cref="TerminalCapabilities"/> the screens use: a redirected
/// stderr, or <c>--quiet</c>, means total silence. That gate is what keeps the
/// CLI's existing output byte-identical when it is piped into a file, a test
/// buffer or a CI log, which is where <b>every</b> one of the repository's
/// existing tests runs.
/// </para>
/// <para>
/// The clock is injected rather than read from <see cref="Environment.TickCount"/>
/// so a test can place a known duration on the line instead of sleeping and
/// hoping. Time is still measured, never estimated: <c>Elapsed</c> is the
/// difference between two readings of the real clock.
/// </para>
/// </remarks>
public sealed class WorkingIndicator : IDisposable
{
    private readonly TextWriter _sink;
    private readonly TerminalCapabilities _capabilities;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _startedAt;
    private readonly object _gate = new();

    /// <summary>How often the live line repaints unless the caller says otherwise.</summary>
    public const int DefaultRepaintIntervalMs = 100;

    private readonly int _repaintIntervalMs;

    private bool _active;
    private bool _canRepaint;
    private bool _closed;
    private int _lastFrame = -1;
    private string _lastLine = string.Empty;
    private bool _painted;
    private Timer? _pump;
    private int _frame;
    private Func<CommandProgress?>? _progress;

    /// <summary>
    /// How often the live line repaints, in milliseconds. Fast enough that the
    /// spinner reads as motion, slow enough that a long run costs a negligible
    /// amount of write traffic.
    /// </summary>
    private const int RepaintIntervalMs = DefaultRepaintIntervalMs;

    /// <summary>
    /// Starts repainting the live line on a timer, so the spinner actually moves
    /// while a command runs rather than drawing one frame and sitting still.
    /// </summary>
    /// <remarks>
    /// The timer is a plain <see cref="Timer"/> rather than a render loop: the
    /// work runs on the calling thread and the timer only writes a line. Both
    /// share <see cref="_gate"/>, so a repaint cannot interleave with the closing
    /// line. Repaints are skipped when nothing changed, so an idle command writes
    /// nothing after its first frame.
    /// </remarks>
    public void Start()
    {
        if (!_canRepaint || _pump is not null)
        {
            return;
        }

        _pump = new Timer(_tick, null, _repaintIntervalMs, _repaintIntervalMs);
    }

    /// <summary>
    /// Supplies the progress to show on each repaint, read fresh every time so a
    /// counter the command keeps incrementing reaches the line on its own. Null
    /// leaves the line showing the spinner and elapsed time alone.
    /// </summary>
    public void UseProgress(Func<CommandProgress?>? progress) =>
        Volatile.Write(ref _progress, progress);

    /// <summary>
    /// Returns the writer a command should use for its own stderr, such that
    /// every one of its writes first clears the live row.
    /// </summary>
    /// <remarks>
    /// This is what stops the two writers colliding. The indicator repaints on a
    /// timer thread while the command writes its own diagnostics on the calling
    /// thread, and without arbitration a summary line lands in the middle of a
    /// spinner frame: "evaluate  2/2 matches  0.2sevaluation suite=dev ...".
    /// Suspending before each write moves the cursor off the row first, and the
    /// next repaint starts cleanly on the row the command's newline left behind.
    /// <para>
    /// An inactive indicator returns <paramref name="stderr"/> unchanged, so the
    /// redirected case — every existing test, every script — writes through the
    /// identical object it always did.
    /// </para>
    /// </remarks>
    /// <param name="stderr">The command's own stderr.</param>
    public TextWriter Interleave(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        return _canRepaint ? new LiveRowTextWriter(stderr, this) : stderr;
    }

    /// <summary>
    /// Clears the live row so another writer can use it, and forgets that
    /// anything was painted so the next repaint begins from column zero.
    /// </summary>
    internal void SuspendLiveRow()
    {
        lock (_gate)
        {
            if (!_painted)
            {
                return;
            }

            _write(CommandLifecycle.ReturnToLineStart + CommandLifecycle.EraseToEndOfLine);
            _painted = false;
        }
    }

    /// <summary>
    /// Writes to the underlying sink, swallowing a failure.
    /// </summary>
    /// <remarks>
    /// The repaint runs on a thread-pool thread, where an unhandled exception
    /// takes the whole process down — and the process here is a benchmark
    /// someone asked for, not the indicator. A terminal that has gone away, or a
    /// writer already disposed by a caller that returned early, must cost at
    /// most the indicator's last frame.
    /// </remarks>
    private void _write(string text)
    {
        try
        {
            _sink.Write(text);
            _sink.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _canRepaint = false;
        }
    }

    /// <summary>
    /// The command's stderr, routed so that each of its writes clears the live
    /// row first. Only the members the CLI actually uses are overridden; the rest
    /// of <see cref="TextWriter"/> funnels through them.
    /// </summary>
    private sealed class LiveRowTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly WorkingIndicator _owner;

        public LiveRowTextWriter(TextWriter inner, WorkingIndicator owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _owner.SuspendLiveRow();
            _inner.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            _owner.SuspendLiveRow();
            _inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            _owner.SuspendLiveRow();
            _inner.Write(buffer, index, count);
        }

        public override void Flush() => _inner.Flush();
    }

    private void _tick(object? state)
    {
        // A thread-pool callback must never let an exception escape: an unhandled
        // one terminates the process, and the work this indicator is reporting
        // on would die with it.
        try
        {
            if (!_canRepaint || _closed)
            {
                return;
            }

            Repaint(Interlocked.Increment(ref _frame), Volatile.Read(ref _progress)?.Invoke());
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _canRepaint = false;
        }
    }

    /// <summary>Stops repainting. Safe to call more than once.</summary>
    public void Stop()
    {
        var pump = Interlocked.Exchange(ref _pump, null);
        pump?.Dispose();
    }

    /// <summary>
    /// Opens an indicator for <paramref name="command"/>, writing to
    /// <paramref name="sink"/> — which must be the command's real stderr.
    /// </summary>
    /// <param name="command">The command name shown on the line.</param>
    /// <param name="sink">The stream to write to, normally <c>Console.Error</c>.</param>
    /// <param name="capabilities">What the attached terminal can do.</param>
    /// <param name="quiet">Whether <c>--quiet</c> was passed.</param>
    /// <param name="clock">
    /// Reads elapsed time since the indicator opened. Defaults to a real
    /// monotonic reading; tests substitute a manual one.
    /// </param>
    public WorkingIndicator(
        string command,
        TextWriter sink,
        TerminalCapabilities capabilities,
        bool quiet,
        Func<TimeSpan>? clock = null,
        int repaintIntervalMs = DefaultRepaintIntervalMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ArgumentNullException.ThrowIfNull(sink);

        if (repaintIntervalMs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repaintIntervalMs), repaintIntervalMs, "The repaint interval must be at least 1ms.");
        }

        _repaintIntervalMs = repaintIntervalMs;
        Command = command;
        _sink = sink;
        _capabilities = capabilities;
        _clock = clock ?? DefaultClock;
        _startedAt = _clock();

        // The gate. A redirected stderr is not a terminal: a carriage return
        // there corrupts a file, and NO_COLOR leaves plain text but no styling.
        _active = capabilities is { OutputRedirected: false } && !quiet;

        // The live line additionally needs the erase sequence, which is the one
        // escape a repainting line cannot do without: rewriting a row in place
        // means returning to its start and clearing what the longer previous
        // frame left behind. A run with styling off gets no live line at all —
        // plain status still, on the closing line, which is what the rule asks
        // for, and not a spinner that cannot erase itself.
        _canRepaint = _active && capabilities.Depth != ColorDepth.None;
    }

    /// <summary>The command this indicator is reporting on.</summary>
    public string Command { get; }

    private static TimeSpan DefaultClock()
    {
        // Process uptime, not wall-clock: a wall clock can jump backwards
        // mid-command and render a negative duration on the line.
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    /// <summary>
    /// Repaints the live line for spinner <paramref name="frame"/>, with
    /// <paramref name="progress"/> shown when the command has a real counter to
    /// report and omitted entirely when it does not. A repaint of the frame
    /// already on screen is skipped, so an idle command does not spin the CPU
    /// writing identical bytes.
    /// </summary>
    public void Repaint(int frame, CommandProgress? progress = null)
    {
        if (!_canRepaint || _closed || frame == _lastFrame)
        {
            return;
        }

        var line = CommandLifecycle.Working(
            Command,
            frame,
            _clock() - _startedAt,
            progress,
            _capabilities.Depth,
            _capabilities.Utf8);

        lock (_gate)
        {
            // Re-checked inside the lock: a repaint queued behind the closing
            // line must not write over it.
            if (_closed || line == _lastLine)
            {
                return;
            }

            _paint(line);
            _lastFrame = frame;
            _lastLine = line;
        }
    }

    /// <summary>Closes the line with a success report.</summary>
    public void Succeed()
    {
        if (!_active || _closed)
        {
            return;
        }

        _close(CommandLifecycle.Succeeded(Command, _clock() - _startedAt, _capabilities.Depth));
    }

    /// <summary>
    /// Closes the line with a failure report carrying the reason and the exit
    /// code the command is returning.
    /// </summary>
    public void Fail(string reason, int exitCode)
    {
        if (!_active || _closed)
        {
            return;
        }

        _close(
            CommandLifecycle.Failed(
                Command,
                reason,
                exitCode,
                _clock() - _startedAt,
                _capabilities.Depth));
    }

    /// <summary>
    /// Writes the closing line in place of the live row. Erasing first is what
    /// keeps the closing line from inheriting the tail of a longer working line:
    /// without it, "evaluate  40/40 matches  12.4s" followed by "ok" leaves the
    /// residue of the first on the same row.
    /// </summary>
    private void _close(string line)
    {
        // Stop the pump before taking the lock: a repaint queued behind this one
        // would otherwise write a spinner frame over the closing line.
        Stop();
        _closed = true;

        lock (_gate)
        {
            var builder = new StringBuilder();

            if (_painted)
            {
                builder.Append(CommandLifecycle.ReturnToLineStart);
                builder.Append(CommandLifecycle.EraseToEndOfLine);
            }

            builder.Append(line);
            try
            {
                _sink.WriteLine(builder.ToString());
                _sink.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _canRepaint = false;
            }

            _painted = false;
        }
    }

    private void _paint(string line)
    {
        var builder = new StringBuilder();
        builder.Append(CommandLifecycle.ReturnToLineStart);

        if (_painted)
        {
            builder.Append(CommandLifecycle.EraseToEndOfLine);
        }

        builder.Append(line);
        _write(builder.ToString());
        _painted = true;
    }

    /// <summary>
    /// Closes the line if the command ended without closing it, which is what an
    /// exception between the last repaint and the close would otherwise leave
    /// half-drawn on the terminal.
    /// </summary>
    public void Dispose()
    {
        Stop();

        if (_active && !_closed)
        {
            lock (_gate)
            {
                _closed = true;

                if (_painted)
                {
                    // Terminate the live row so the shell prompt does not land
                    // on top of it.
                    try
                    {
                        _sink.Write(CommandLifecycle.ReturnToLineStart);
                        _sink.WriteLine();
                        _sink.Flush();
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        // The stream the command was given is already gone; there
                        // is nothing left to restore.
                    }
                }
            }
        }
    }
}
