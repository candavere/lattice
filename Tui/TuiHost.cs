namespace Lattice.Tui;

/// <summary>
/// One entered terminal session: the alternate screen is active, and disposing
/// the session puts it back.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Writes one frame's worth of terminal bytes.</summary>
    void Write(string text);

    /// <summary>
    /// Raised when the reader interrupts the process. The terminal has already
    /// been restored by the time this fires, so a host only has to stop drawing.
    /// </summary>
    event Action? Interrupted;
}

/// <summary>
/// The two things a host does to a terminal, around its frames. A seam so a test
/// can record the order they happen in; the production implementation is the one
/// that talks to <see cref="TerminalGuard"/>.
/// </summary>
public interface ITerminalSessionFactory
{
    /// <summary>
    /// Asks the console for UTF-8 output. Called once per run, before any session
    /// is entered.
    /// </summary>
    bool RequestUtf8Output();

    /// <summary>Enters the alternate screen and hides the cursor.</summary>
    ITerminalSession Enter(TextWriter output);
}

/// <summary>
/// The production terminal seam: <see cref="TerminalGuard.TryUseUtf8"/> and
/// <see cref="TerminalGuard.Enter"/>, and nothing else. This type is the only
/// caller of either on the interactive path.
/// </summary>
public sealed class TerminalGuardSessionFactory : ITerminalSessionFactory
{
    /// <inheritdoc />
    public bool RequestUtf8Output() => TerminalGuard.TryUseUtf8();

    /// <inheritdoc />
    public ITerminalSession Enter(TextWriter output) => new GuardSession(output);

    /// <summary>
    /// The real session: <see cref="TerminalGuard"/> owns the escape sequences and
    /// the restore, and this adds only the interrupt notification the guard does not
    /// raise. The guard's own subscription is the one it hands the relay, so the
    /// restore still runs on Ctrl-C and still runs exactly once.
    /// </summary>
    private sealed class GuardSession : ITerminalSession
    {
        private readonly IDisposable _guard;
        private readonly TextWriter _output;

        internal GuardSession(TextWriter output)
        {
            _output = output;
            _guard = TerminalGuard.Enter(output, new InterruptRelay(this));
        }

        /// <inheritdoc />
        public event Action? Interrupted;

        /// <inheritdoc />
        public void Write(string text) => _output.Write(text);

        /// <inheritdoc />
        public void Dispose() => _guard.Dispose();

        /// <summary>
        /// Forwards the guard's restore onto the console interrupt path and then says
        /// the run was interrupted. Restore first: by the time a host hears about an
        /// interrupt, its terminal must already be usable again.
        /// </summary>
        private sealed class InterruptRelay : ICancelSubscription
        {
            private readonly GuardSession _session;
            private readonly TerminalGuard.ConsoleCancelSubscription _inner = new();
            private Action? _restore;

            internal InterruptRelay(GuardSession session) => _session = session;

            public void Register(Action onCancel)
            {
                _restore = onCancel;
                _inner.Register(Interrupt);
            }

            public void Unregister()
            {
                _restore = null;
                _inner.Unregister();
            }

            private void Interrupt()
            {
                _restore?.Invoke();
                _session.Interrupted?.Invoke();
            }
        }
    }
}

/// <summary>
/// A clock a host measures elapsed time with, as a value so a test can place a
/// known duration on the replay instead of sleeping and hoping.
/// </summary>
public interface IUiClock
{
    /// <summary>Monotonic time since the clock was created.</summary>
    TimeSpan Now { get; }
}

/// <summary>
/// The production clock: process uptime, not wall-clock, because a wall clock can
/// step backwards mid-run and render a negative frame interval.
/// </summary>
public sealed class MonotonicClock : IUiClock
{
    private readonly long _startedAt = Environment.TickCount64;

    /// <inheritdoc />
    public TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAt);
}

/// <summary>Everything one host run is composed from.</summary>
/// <param name="Document">The replay to show.</param>
/// <param name="Output">Where frames go. In a real run, the process's own stdout.</param>
/// <param name="Errors">Where a refusal goes. In a real run, the process's own stderr.</param>
/// <param name="Capabilities">What the terminal can do, resolved once at start-up.</param>
/// <param name="ForceAscii">Whether the caller asked for ASCII glyphs whatever the locale says.</param>
/// <param name="Session">The terminal seam.</param>
/// <param name="Keys">Where keys come from.</param>
/// <param name="Clock">How elapsed time is measured.</param>
/// <param name="MaxFramesPerSecond">The ceiling on how often a frame may be written.</param>
/// <param name="Cursor">
/// The cursor to move, or null to play <paramref name="Document"/> from the start.
/// A live episode hands its own cursor in; a recording lets the host build a
/// <see cref="ReplayPlayback"/>, because a recording needs nothing else.
/// </param>
public sealed record TuiHostRequest(
    ReplayDocument Document,
    TextWriter Output,
    TextWriter Errors,
    TerminalCapabilities Capabilities,
    bool ForceAscii,
    ITerminalSessionFactory Session,
    IKeySource Keys,
    IUiClock Clock,
    int MaxFramesPerSecond = TuiHost.DefaultMaxFramesPerSecond,
    ICockpitCursor? Cursor = null);

/// <summary>How a host run ended: the status to return, and why it refused if it did.</summary>
/// <param name="ExitCode">0 on a normal quit, 2 on a refusal.</param>
/// <param name="Refusal">The one-line reason a refusal printed, or null after a normal quit.</param>
public readonly record struct TuiRunResult(int ExitCode, string? Refusal);

/// <summary>
/// The single entry point for every interactive screen in this library: it asks
/// the console for UTF-8, enters the alternate screen, draws the cockpit, reads
/// keys on another thread, and puts the terminal back on the way out.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry, one order.</b> The encoding request happens once and before the
/// alternate screen is entered, because on Windows changing the console encoding
/// resets console state and would drop the mode the guard sets. Nothing else in
/// this library enters the alternate screen: a screen that needs a terminal goes
/// through here, whether it is playing a recording or showing a live episode —
/// the two differ only in the <see cref="ICockpitCursor"/> they move.
/// </para>
/// <para>
/// <b>Redraw only on change.</b> A frame is composed and diffed against the one on
/// screen, and only a difference is written, so a paused replay costs no write
/// traffic at all and a running one is capped at the frame rate rather than
/// spinning.
/// </para>
/// <para>
/// <b>Refuses a redirected stream.</b> A viewer whose input or output is a file, a
/// pipe or a test buffer is not a viewer: entering the alternate screen would put
/// control sequences into whatever the redirect was for. It prints one line of
/// reason and returns the usage status instead, having touched nothing.
/// </para>
/// </remarks>
public static class TuiHost
{
    /// <summary>
    /// The ceiling on how often a frame is written. A terminal that cannot keep up
    /// is better served by a frame it can finish than by the newest one it cannot.
    /// </summary>
    public const int DefaultMaxFramesPerSecond = 30;

    /// <summary>The status a refused run reports: the invocation named no terminal.</summary>
    private const int UsageRefused = 2;

    /// <summary>
    /// The one line these capabilities refuse, or null when the terminal can carry
    /// the cockpit. Public so a caller that owns something to start — a live
    /// episode's stepper thread, say — can ask before it starts it, and get the
    /// host's own answer rather than a second opinion about redirection.
    /// </summary>
    public static string? RefusalFor(TerminalCapabilities capabilities) => Refusal(capabilities);

    /// <summary>Runs the cockpit until the reader quits.</summary>
    public static TuiRunResult Run(TuiHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusal = Refusal(request.Capabilities);
        if (refusal is not null)
        {
            request.Errors.WriteLine(refusal);
            return new TuiRunResult(UsageRefused, refusal);
        }

        if (request.MaxFramesPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.MaxFramesPerSecond,
                "A host must be allowed at least one frame per second.");
        }

        // The screen seam owns the refusal's second half — the order in which a
        // terminal is entered and put back — so a screen that is not a replay gets
        // that order without re-deriving it. The refusal above is asked first
        // because this call builds nothing until it has answered.
        var opened = ScreenHost.Open(
            request.Session,
            request.Capabilities,
            request.ForceAscii,
            request.Output);

        if (opened.Screen is not { } screen)
        {
            return new TuiRunResult(UsageRefused, opened.Refusal);
        }

        using var host = screen;
        var quit = false;
        host.Interrupted += () => quit = true;

        var fill = request.Capabilities.PanelFill;
        var playback = request.Cursor ?? new ReplayPlayback(request.Document);
        var frameInterval = TimeSpan.FromSeconds(1.0 / request.MaxFramesPerSecond);

        // The first frame is always written: a viewer that shows nothing until the
        // reader presses a key looks broken.
        Compose(request, playback, fill, host);
        var lastTickAt = request.Clock.Now;

        while (!quit && !playback.IsFinished)
        {
            var wait = frameInterval - (request.Clock.Now - lastTickAt);
            var key = default(TuiKey);
            KeyWait outcome;

            if (wait > TimeSpan.Zero)
            {
                outcome = request.Keys.Wait(wait, out key);
            }
            else
            {
                // The frame interval has already elapsed, so the pass is late rather
                // than early. The key that is waiting is still the reader's, and
                // dropping it because the screen is behind would lose the very key
                // that ends the run.
                outcome = request.Keys.Wait(TimeSpan.Zero, out key);
            }

            if (outcome == KeyWait.Closed)
            {
                break;
            }

            if (outcome == KeyWait.Key)
            {
                // The quit key ends the run here rather than being left to the input
                // running out: a real terminal's reader blocks on the next key for as
                // long as the reader sits there, so waiting for an end of input that
                // never comes is how a viewer hangs on a terminal that is working
                // exactly as intended.
                if (key.IsQuit)
                {
                    quit = true;
                    continue;
                }

                if (playback.Apply(key))
                {
                    Compose(request, playback, fill, host);
                }

                continue;
            }

            var now = request.Clock.Now;
            var elapsed = now - lastTickAt;
            lastTickAt = now;

            if (playback.Advance(elapsed))
            {
                Compose(request, playback, fill, host);
            }
        }

        return new TuiRunResult(0, null);
    }

    /// <summary>
    /// The one line a refused run prints, or <c>null</c> when the terminal can
    /// carry the cockpit. Both conditions are named because "it did nothing" is
    /// not a diagnosis.
    /// </summary>
    private static string? Refusal(TerminalCapabilities capabilities)
    {
        if (capabilities.InputRedirected && capabilities.OutputRedirected)
        {
            return "lattice tui: standard input and standard output are redirected; the replay viewer needs a terminal.";
        }

        if (capabilities.InputRedirected)
        {
            return "lattice tui: standard input is redirected; the replay viewer needs a terminal to read keys from.";
        }

        return capabilities.OutputRedirected
            ? "lattice tui: standard output is redirected; the replay viewer needs a terminal to draw on."
            : null;
    }

    /// <summary>
    /// Composes the cockpit and hands it to the screen seam, which writes only what
    /// differs from what is on show.
    /// </summary>
    private static void Compose(
        TuiHostRequest request,
        ICockpitCursor playback,
        Rgb? fill,
        ScreenHost host)
    {
        host.Present(CockpitLayout.Render(new CockpitRequest(
            playback.Document,
            playback.Index,
            host.Size,
            host.Glyphs,
            fill,
            playback.Phase,
            new PlaybackState(playback.IsPaused, playback.StepsPerSecond),
            playback.Live)));
    }
}