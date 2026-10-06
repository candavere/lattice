using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>Everything one Ledger run is composed from.</summary>
/// <param name="Paths">The artifact paths, in the order the reader gave them.</param>
/// <param name="Output">Where frames go. In a real run, the process's own stdout.</param>
/// <param name="Errors">Where a refusal goes. In a real run, the process's own stderr.</param>
/// <param name="Capabilities">What the terminal can do, resolved once at start-up.</param>
/// <param name="Console">The console the screen is composed from.</param>
/// <param name="Session">The terminal seam.</param>
/// <param name="Clock">How elapsed time is measured.</param>
/// <param name="Ascii">Whether the reader asked for ASCII glyphs.</param>
/// <param name="TrailingIdleFrames">
/// How many times the loop redraws after its keys run out before it gives up. A real
/// terminal's reader blocks rather than ending, so this is only reachable from a test,
/// and it exists so the loop has a bounded ending rather than none.
/// </param>
public sealed record LedgerHostRequest(
    IReadOnlyList<string> Paths,
    TextWriter Output,
    TextWriter Errors,
    TerminalCapabilities Capabilities,
    TuiConsole Console,
    ITerminalSessionFactory Session,
    IUiClock Clock,
    bool Ascii = false,
    int TrailingIdleFrames = 0);

/// <summary>
/// The Ledger's own loop: it owns the screen, reads keys, and puts the terminal back.
/// </summary>
/// <remarks>
/// <para>
/// <b>One screen, one owner.</b> The host asks <see cref="ScreenHost"/> whether the
/// terminal can carry a screen and touches nothing until it has answered. After that
/// the artifacts are read, the key source is built, and the Ctrl-C-as-input setting is
/// taken for the screen's life and given back before the key source is disposed.
/// </para>
/// <para>
/// <b>The refusal comes before the files.</b> A redirected run has no viewer to be
/// given, so it is refused before a single artifact is opened rather than after: a
/// reader whose output is a pipe deserves the refusal and not a report about a file they
/// asked about on a stream that could not have shown it anyway.
/// </para>
/// <para>
/// <b>Read-only throughout.</b> Nothing here writes a file, starts a process, opens a
/// network connection or reads a console property before the refusal. The only writes
/// are the frames the screen itself puts on the terminal.
/// </para>
/// </remarks>
public static class LedgerHost
{
    /// <summary>The status a refused run reports: the invocation named no terminal.</summary>
    private const int UsageRefused = 2;

    /// <summary>How long the loop waits for a key before it redraws.</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Runs the Ledger over the given artifacts until the reader quits.
    /// </summary>
    /// <exception cref="InvalidDataException">One of the artifacts is not readable.</exception>
    /// <exception cref="System.IO.FileNotFoundException">One of the paths names no file.</exception>
    /// <exception cref="System.IO.IOException">One of the artifacts could not be read.</exception>
    public static TuiRunResult Run(LedgerHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The refusal first, before a key source exists and before a file is opened:
        // building a key source is what asks the process's console for its input mode,
        // and on Windows that ask throws when standard input is a file or a pipe.
        var refusal = TuiHost.RefusalFor(request.Capabilities);
        if (refusal is not null)
        {
            var reported = Refusal(refusal);
            request.Errors.WriteLine(reported);
            return new TuiRunResult(UsageRefused, reported);
        }

        var artifacts = LedgerSource.ReadAll(request.Paths);

        // Declared first so it is disposed last, and the setting second so it goes back
        // first: the setting is restored before the reading thread is left to the
        // process, and both happen after the terminal is back.
        using var keys = request.Console.Keys();
        using var controlC = ControlCAsInputScope.Enter(request.Console.ControlCAsInput);

        var screen = LedgerScreen.For(artifacts);
        var host = Enter(request);
        var quit = false;
        host.Interrupted += () => quit = true;

        try
        {
            Loop(screen, host, keys, request.TrailingIdleFrames, () => quit);
        }
        finally
        {
            host.Dispose();
        }

        return new TuiRunResult(screen.ExitCode, null);
    }

    /// <summary>
    /// The one line a redirected run refuses with: the host's own, with the noun
    /// changed to name this screen. The fact about redirection is the host's alone — two
    /// screens asking their own question about it is how they come to disagree about
    /// whether a terminal can carry a screen — and only the noun differs.
    /// </summary>
    public static string Refusal(string hostRefusal) =>
        hostRefusal.Replace("the replay viewer", "the ledger", StringComparison.Ordinal);

    private static void Loop(
        LedgerScreen screen,
        ScreenHost host,
        IKeySource keys,
        int trailingIdleFrames,
        Func<bool> interrupted)
    {
        var idle = 0;

        while (!interrupted())
        {
            host.Present(Frame(screen, host));

            if (screen.HasQuit)
            {
                return;
            }

            var outcome = keys.Wait(IdleWait, out var key);

            if (outcome == KeyWait.Closed)
            {
                // Only the end of the input ends the screen. A wait that timed out is
                // not that: a real terminal's reader blocks on the next key for as long
                // as the reader sits there, and treating a timeout as an ending would
                // close the Ledger on the first pause, which is every pause.
                if (++idle > trailingIdleFrames)
                {
                    return;
                }

                continue;
            }

            if (outcome != KeyWait.Key)
            {
                continue;
            }

            idle = 0;
            screen.Apply(key);
        }
    }

    /// <summary>
    /// The alternate screen, for a run that has not been refused and so cannot be. A
    /// refusal here would mean the terminal changed its mind mid-run, which is reported
    /// rather than thrown: the reader is looking at a screen.
    /// </summary>
    private static ScreenHost Enter(LedgerHostRequest request)
    {
        var opened = ScreenHost.Open(request.Session, request.Capabilities, request.Ascii, request.Output);

        if (opened.Screen is not { } screen)
        {
            request.Errors.WriteLine(Refused(request));
            throw new LedgerRefused(Refused(request));
        }

        return screen;
    }

    private static string Refused(LedgerHostRequest request) =>
        Refusal(TuiHost.RefusalFor(request.Capabilities) ?? "the terminal cannot carry the ledger.");

    /// <summary>
    /// The frame for the screen's current state, composed from the screen's own state
    /// rather than kept in step with it, so the two cannot drift apart.
    /// </summary>
    private static CellBuffer Frame(LedgerScreen screen, ScreenHost host) =>
        LedgerLayout.Render(new LedgerRequest(
            screen.Artifacts,
            screen.ArtifactIndex,
            screen.StudyIndex,
            screen.SeedIndex,
            screen.Comparing,
            host.Size,
            host.Glyphs,
            null));
}

/// <summary>
/// The terminal stopped being able to carry the screen part way through a run, so there
/// is no screen to return to. Carried out to the caller rather than swallowed: the
/// screen is already gone, and pretending otherwise would leave the reader with a loop
/// that draws nothing.
/// </summary>
public sealed class LedgerRefused : InvalidOperationException
{
    /// <summary>The one line the terminal refused with, which is the message.</summary>
    public LedgerRefused(string refusal)
        : base(refusal)
    {
    }
}
