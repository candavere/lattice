using System.Text;

namespace Lattice.Tui;

/// <summary>
/// Registers the restore action to run when the process is interrupted, and
/// takes it back off again. Split into register and unregister because the
/// handler holds a reference to the session's output writer: leaving it
/// attached past disposal would keep a flushed <see cref="StringWriter"/>-backed
/// buffer alive and, worse, let a later session's Ctrl-C restore the wrong
/// session.
/// </summary>
public interface ICancelSubscription
{
    /// <summary>Attaches <paramref name="onCancel"/> to the interrupt path.</summary>
    void Register(Action onCancel);

    /// <summary>Detaches it. Must be safe to call when nothing is attached.</summary>
    void Unregister();
}

/// <summary>
/// Puts the terminal into full-screen mode and guarantees it is put back.
///
/// Entering means switching to the alternate screen buffer and hiding the
/// cursor; restoring means leaving the alternate buffer and showing the cursor
/// again. Leaving the alternate buffer is what discards the drawn frame, so a
/// restore that never runs would leave the user's scrollback covered in UI.
///
/// Restoration happens on all three exits: a normal return from the body, a
/// Ctrl-C, and an exception propagating out. The three are covered by three
/// separate mechanisms because each one bypasses the others: <c>finally</c>
/// does not run if the process is taken down, a Ctrl-C handler does not run
/// for a fault, and neither runs for an unhandled fault on a background thread.
/// So the restore is idempotent and every path is allowed to call it.
/// </summary>
public static class TerminalGuard
{
    /// <summary>SGI private mode 1049: switch to the alternate screen buffer.</summary>
    public const string EnterAlternateScreen = "\u001b[?1049h";

    /// <summary>SGI private mode 1049: switch back to the main screen buffer.</summary>
    public const string LeaveAlternateScreen = "\u001b[?1049l";

    /// <summary>SGR mode 25: hide the cursor.</summary>
    public const string HideCursor = "\u001b[?25l";

    /// <summary>SGR mode 25: show the cursor.</summary>
    public const string ShowCursor = "\u001b[?25h";

    /// <summary>
    /// The cursor visibility setter for the current platform, exposed so a
    /// later stage can drive it from a test. Every platform in scope implements
    /// <see cref="Console.CursorVisible"/>; the guarded wrapper is what keeps a
    /// host that throws (a redirected stream, a Windows container without a
    /// console) from taking the process down over cosmetics.
    /// </summary>
    public static bool TrySetCursorVisible(bool visible)
    {
        try
        {
            Console.CursorVisible = visible;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Switches to the alternate screen and hides the cursor, and returns a
    /// handle that restores both exactly once however many times it is
    /// disposed.
    ///
    /// Call <see cref="TryUseUtf8"/> <em>before</em> this, not after: on
    /// Windows changing the console encoding resets console state, so doing it
    /// once the alternate screen is active drops the mode set here. The guard
    /// does not do it for you and nothing enforces the order.
    /// </summary>
    /// <param name="output">
    /// Where the escape sequences and the frame go. The escapes are written
    /// unconditionally, to whatever writer is supplied: the guard is handed a
    /// <see cref="TextWriter"/> rather than reaching for
    /// <see cref="Console.Out"/>, so redirection of <em>that</em> writer is not
    /// something the guard can see or should guess at. Deciding whether a
    /// terminal is attached is the caller's job, and
    /// <see cref="TerminalCapabilities.OutputRedirected"/> is the value to
    /// decide it with — a caller rendering into a redirected stream should not
    /// enter the alternate screen at all. Note that
    /// <see cref="Console.IsOutputRedirected"/> describes the process's own
    /// standard output and says nothing about this writer, so it is not a
    /// substitute for the capability record.
    /// </param>
    /// <param name="subscription">
    /// The Ctrl-C hook to register the restore with. Defaults to
    /// <see cref="ConsoleCancelSubscription"/>, which attaches to
    /// <see cref="Console.CancelKeyPress"/>; on a host that cannot raise that
    /// event, registration reports failure instead of throwing, and the normal
    /// exit and exception paths still restore. Tests pass their own so the
    /// interrupt path can be driven directly.
    /// </param>
    public static IDisposable Enter(TextWriter output, ICancelSubscription? subscription = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        var session = new Session(output);
        session.Enter();
        session.Attach(subscription ?? new ConsoleCancelSubscription());

        return session;
    }

    /// <summary>
    /// Runs <paramref name="body"/> in the alternate screen and restores
    /// afterwards, including when the body throws. The exception is not
    /// swallowed: restoration is a courtesy to the terminal, not a decision
    /// about the program's failure.
    /// </summary>
    public static void Run(
        TextWriter output,
        Action body,
        ICancelSubscription? subscription = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var guard = Enter(output, subscription);
        body();
    }

    /// <summary>
    /// Asks the console for UTF-8 output, ignoring a host that will not allow
    /// it. Call this <em>before</em> <see cref="Enter"/> or
    /// <see cref="Run"/>: on Windows the encoding change resets console state,
    /// so doing it afterwards would drop the mode the guard just set. The
    /// ordering is the caller's responsibility; nothing in the guard enforces
    /// it, and it is the step most likely to be missed, because a caller that
    /// writes nothing but ASCII cannot tell it was skipped.
    ///
    /// Returns <c>false</c>, without throwing, when output is redirected or the
    /// host refuses the encoding change.
    /// </summary>
    public static bool TryUseUtf8()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }

            Console.OutputEncoding = Encoding.UTF8;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
/// Attaches the restore to <see cref="Console.CancelKeyPress"/>. Both the
/// attach and the detach are guarded because the event does not exist on every
/// host — a Windows service or a container without a console raises
/// <see cref="PlatformNotSupportedException"/> — and a UI that cannot restore
/// its own Ctrl-C path is a degraded UI, not a crashed one.
/// </summary>
public sealed class ConsoleCancelSubscription : ICancelSubscription
{
    private ConsoleCancelEventHandler? _handler;

    /// <inheritdoc />
    public void Register(Action onCancel)
    {
        ArgumentNullException.ThrowIfNull(onCancel);

        ConsoleCancelEventHandler handler = (_, args) =>
        {
            // Cancel is deliberately left false: letting the runtime terminate
            // is correct, and the restore above it is what keeps the terminal
            // usable on the way out.
            args.Cancel = false;
            onCancel();
        };

        try
        {
            Console.CancelKeyPress += handler;
            _handler = handler;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
            or InvalidOperationException)
        {
            _handler = null;
        }
    }

    /// <inheritdoc />
    public void Unregister()
    {
        var handler = _handler;
        if (handler is null)
        {
            return;
        }

        _handler = null;

        try
        {
            Console.CancelKeyPress -= handler;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
            or InvalidOperationException)
        {
            // Already detached, or the event never existed.
        }
    }
}

    /// <summary>
    /// One entered session. Restoration is guarded by a flag rather than by
    /// disposal counting, because the Ctrl-C path and the <c>finally</c> path
    /// are genuinely independent and either may arrive first.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly TextWriter _output;
        private ICancelSubscription? _subscription;
        private bool _restored;

        internal Session(TextWriter output) => _output = output;

        internal void Enter()
        {
            _output.Write(EnterAlternateScreen);
            _output.Write(HideCursor);
            _output.Flush();
            TrySetCursorVisible(false);
        }

        internal void Attach(ICancelSubscription subscription)
        {
            try
            {
                subscription.Register(Restore);
                _subscription = subscription;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException
                or InvalidOperationException)
            {
                // Registration is best-effort; the other two paths remain.
            }
        }

        public void Dispose()
        {
            Restore();

            var subscription = _subscription;
            _subscription = null;
            subscription?.Unregister();
        }

        private void Restore()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            try
            {
                _output.Write(ShowCursor);
                _output.Write(LeaveAlternateScreen);
                _output.Flush();
                TrySetCursorVisible(true);
            }
            catch (Exception exception) when (exception is IOException
                or ObjectDisposedException
                or PlatformNotSupportedException)
            {
                // The terminal is already gone. There is nothing left to restore
                // and nowhere to report it.
            }
        }
    }
}