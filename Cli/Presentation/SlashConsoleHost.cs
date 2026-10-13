using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>Everything one slash-console run is composed from.</summary>
/// <param name="Output">Where frames go. In a real run, the process's own stdout.</param>
/// <param name="Errors">Where a refusal goes. In a real run, the process's own stderr.</param>
/// <param name="Capabilities">What the terminal can do, resolved once.</param>
/// <param name="Console">The console the screen is composed from.</param>
/// <param name="Session">The terminal seam.</param>
/// <param name="Clock">How elapsed time is measured.</param>
/// <param name="Runner">How a command is started once the reader asks for it.</param>
/// <param name="WorkingDirectory">The directory commands run in and the bottom line shows.</param>
/// <param name="TrailingIdleFrames">How many redraws after keys run out before giving up (tests only).</param>
/// <param name="Ascii">Whether to force ASCII glyphs whatever the locale says.</param>
public sealed record SlashConsoleHostRequest(
    TextWriter Output,
    TextWriter Errors,
    TerminalCapabilities Capabilities,
    TuiConsole Console,
    ITerminalSessionFactory Session,
    IUiClock Clock,
    ISlashConsoleRunner Runner,
    string WorkingDirectory,
    int TrailingIdleFrames = 0,
    bool Ascii = false);

/// <summary>
/// The slash console's own loop: it owns the screen, reads keys, and starts what the
/// reader asks for as a child process of the same program.
/// </summary>
/// <remarks>
/// <para>
/// <b>One screen, one owner.</b> The host asks <see cref="ScreenHost"/> whether the
/// terminal can carry a screen and touches nothing until it has answered: no key source,
/// no console input property, no session. A refused run prints one line and exits 2.
/// </para>
/// <para>
/// <b>Captured commands keep the screen.</b> A run command's stdout/stderr are redirected,
/// so the console stays up showing a working line with elapsed time only — never an
/// invented progress fraction. Esc or Ctrl-C cancels the child's whole process tree.
/// </para>
/// <para>
/// <b>Watch screens borrow the terminal.</b> A screen command leaves the alternate screen,
/// hands the key source off, suspends Ctrl-C-as-input so Ctrl-C reaches the child as a
/// signal (a child sharing the console receives it there), runs the <c>tui</c> vector
/// inherited, then resumes, re-enters and rebuilds the key source — as the Launchpad
/// host already does. The console never reads keys while the child owns the terminal.
/// </para>
/// <para>
/// <b>A cancelled or failed run restores the terminal.</b> The screen is disposed and the
/// setting given back on every ending; quitting at idle exits 0.
/// </para>
/// </remarks>
public static class SlashConsoleHost
{
    /// <summary>The status a refused run reports: the invocation named no terminal.</summary>
    private const int UsageRefused = 2;

    /// <summary>How long the loop waits for a key before it redraws (and re-polls a run).</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(50);

    /// <summary>Runs the console until the reader quits.</summary>
    public static TuiRunResult Run(SlashConsoleHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The refusal first, before a key source exists: building one is what asks the
        // process's console for its input mode, and on Windows that ask throws when
        // standard input is a file or a pipe. Nothing here may touch Console key or
        // TreatControlCAsInput APIs on the way to refusing a run.
        var refusal = TuiHost.RefusalFor(request.Capabilities);
        if (refusal is not null)
        {
            var reported = Refusal(refusal);
            request.Errors.WriteLine(reported);
            return new TuiRunResult(UsageRefused, reported);
        }

        var keys = request.Console.Keys();

        try
        {
            using var controlC = ControlCAsInputScope.Enter(request.Console.ControlCAsInput);

            var state = new SlashConsoleState();
            var run = new ActiveRun();
            var result = new ResultPanel();
            var host = Enter(request);

            try
            {
                Loop(state, run, result, request, controlC, keys, host);
            }
            finally
            {
                if (run.Active is not null)
                {
                    run.Active.Cancel();
                    PollRun(run, result);
                }

                host.Dispose();
                keys = run.Keys ?? keys;
            }
        }
        finally
        {
            keys.Dispose();
        }

        return new TuiRunResult(0, null);
    }

    /// <summary>
    /// The one line a redirected run refuses with: the host's own, with the noun
    /// changed to name this screen. One screen asking its own question is how two
    /// screens come to disagree about whether a terminal can carry a screen.
    /// </summary>
    internal static string Refusal(string hostRefusal) =>
        hostRefusal
            .Replace("the replay viewer", "the console", StringComparison.Ordinal)
            .Replace("lattice tui:", "lattice:", StringComparison.Ordinal);

    /// <summary>A captured child in flight, or none.</summary>
    private sealed class ActiveRun
    {
        internal ICapturedRun? Active { get; set; }

        internal TimeSpan StartedAt { get; set; }

        internal string Display { get; set; } = string.Empty;

        internal int Spinner { get; set; }

        internal IKeySource? Keys { get; set; }
    }

    /// <summary>The result panel on show.</summary>
    private sealed class ResultPanel
    {
        internal string? Title { get; set; }

        internal IReadOnlyList<string>? Lines { get; set; }

        internal bool Truncated { get; set; }
    }

    private static void Loop(
        SlashConsoleState state,
        ActiveRun run,
        ResultPanel result,
        SlashConsoleHostRequest request,
        ControlCAsInputScope controlC,
        IKeySource keys,
        ScreenHost host)
    {
        var current = host;
        var idle = 0;

        while (true)
        {
            current.Present(Frame(state, run, result, request, current));

            if (state.HasQuit && run.Active is null)
            {
                return;
            }

            var outcome = keys.Wait(IdleWait, out var key);

            if (outcome == KeyWait.Closed)
            {
                // Only the end of the input ends the screen. A run that finished in
                // the meantime is still collected into the panel first; a run still
                // in flight is cancelled and collected rather than left behind.
                if (PollRun(run, result))
                {
                    idle = 0;
                    continue;
                }

                if (++idle > request.TrailingIdleFrames)
                {
                    if (run.Active is not null)
                    {
                        run.Active.Cancel();
                        PollRun(run, result);
                    }

                    return;
                }

                continue;
            }

            if (outcome != KeyWait.Key)
            {
                if (PollRun(run, result))
                {
                    idle = 0;
                }

                continue;
            }

            idle = 0;

            if (run.Active is not null)
            {
                // While a captured command runs, Esc or Ctrl-C cancels its whole
                // process tree; any other key is discarded, not queued behind it.
                if (key.Kind is TuiKeyKind.Escape or TuiKeyKind.Interrupt)
                {
                    run.Active.Cancel();
                }

                PollRun(run, result);
                continue;
            }

            state.Apply(key);
            state.SetMenu(SlashConsoleMenu.Filter(state.Input));

            if (!state.WantsToCommit)
            {
                continue;
            }

            state.AcknowledgeCommit();
            var plan = SlashConsoleCommit.ParseCommit(state.Input);

            if (plan.Kind == CommitKind.Exit)
            {
                return;
            }

            if (plan.Kind == CommitKind.Error)
            {
                result.Title = "exit 2";
                result.Lines = [plan.ErrorMessage ?? "refused"];
                result.Truncated = false;
                continue;
            }

            if (plan.Kind == CommitKind.Screen)
            {
                var watch = RunWatchOutsideTheScreen(request, controlC, keys, run, plan);
                result.Title = $"exit {watch.ExitCode} {CommandLifecycle.Elapsed(watch.Elapsed)}";
                result.Lines = null;
                result.Truncated = false;

                if (Reenter(request) is not { } next)
                {
                    return;
                }

                current = next;
                keys = run.Keys ?? keys;
                continue;
            }

            try
            {
                run.Active = request.Runner.StartCaptured(new CapturedRunRequest(plan.Arguments, plan.DisplayLine, request.WorkingDirectory));
                run.StartedAt = request.Clock.Now;
                run.Display = plan.DisplayLine;
                run.Spinner = 0;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                result.Title = "exit 1";
                result.Lines = [exception.Message];
                result.Truncated = false;
            }
        }
    }

    /// <summary>
    /// Collects a finished captured run into the result panel. Returns true when a run ended.
    /// </summary>
    private static bool PollRun(ActiveRun run, ResultPanel result)
    {
        if (run.Active is null || !run.Active.IsDone)
        {
            return false;
        }

        var finished = run.Active.Finish();
        run.Active = null;

        var elapsed = CommandLifecycle.Elapsed(finished.Elapsed);
        if (finished.Cancelled)
        {
            result.Title = $"cancelled {elapsed}";
        }
        else if (finished.ExitCode == 0)
        {
            result.Title = $"exit 0 ok {elapsed}";
        }
        else
        {
            result.Title = $"exit {finished.ExitCode} failed {elapsed}";
        }

        var lines = SplitLines(finished.Stdout).Concat(SplitLines(finished.Stderr)).ToList();
        result.Truncated = finished.Truncated || lines.Count > SlashConsoleLayout.MaxResultLinesShown;
        result.Lines = lines.Take(SlashConsoleLayout.MaxResultLinesShown).ToList();
        return true;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var clean = line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line;
            lines.Add(clean);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// Runs a watch screen with the terminal fully restored, mirroring the Launchpad host:
    /// setting suspended, screen left, key source handed off, child inherited, then taken
    /// again and re-entered. The child sharing the console receives Ctrl-C there.
    /// </summary>
    private static WatchRunResult RunWatchOutsideTheScreen(
        SlashConsoleHostRequest request,
        ControlCAsInputScope controlC,
        IKeySource keys,
        ActiveRun run,
        CommitPlan plan)
    {
        controlC.Suspend();

        try
        {
            keys.Dispose();

            try
            {
                return request.Runner.RunWatch(new WatchRunRequest(plan.Arguments, plan.DisplayLine, request.WorkingDirectory));
            }
            catch (Exception)
            {
                return new WatchRunResult(1, TimeSpan.Zero);
            }
            finally
            {
                run.Keys = request.Console.Keys();
            }
        }
        finally
        {
            controlC.Resume();
        }
    }

    private static CellBuffer Frame(
        SlashConsoleState state,
        ActiveRun run,
        ResultPanel result,
        SlashConsoleHostRequest request,
        ScreenHost host)
    {
        string? notice;
        string? title = result.Title;
        IReadOnlyList<string>? lines = result.Lines;
        var truncated = result.Truncated;

        if (run.Active is not null)
        {
            var elapsed = request.Clock.Now - run.StartedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var spinner = CommandLifecycle.Spinner(run.Spinner++ / 4, host.Glyphs == GlyphMode.Unicode);
            notice = $"{spinner} {run.Display}  {CommandLifecycle.Elapsed(elapsed)}";
        }
        else
        {
            notice = ResizeNotice(host.Size, state.MenuOpen);
        }

        return SlashConsoleLayout.Render(new SlashConsoleRequest(
            state.Input,
            state.Menu,
            state.SelectedIndex,
            state.MenuOpen,
            request.WorkingDirectory,
            notice,
            title,
            lines,
            truncated,
            host.Size,
            host.Glyphs,
            request.Capabilities.Depth,
            request.Capabilities.Utf8));
    }

    /// <summary>The resize notice, or null when the terminal carries the console.</summary>
    internal static string? ResizeNotice(PaneSize size, bool menuOpen)
    {
        if (Wordmark.ShouldShowWordmark(size.Width, size.Height, true, GlyphMode.Unicode, menuOpen))
        {
            return null;
        }

        return $"terminal is {size.Width}x{size.Height}; console needs 62x16 (62x30 with menu); resize for it";
    }

    private static ScreenHost Enter(SlashConsoleHostRequest request) =>
        TryEnter(request) ?? throw new LaunchpadRefused(Refused(request));

    private static ScreenHost? Reenter(SlashConsoleHostRequest request)
    {
        try
        {
            var screen = TryEnter(request);
            screen?.RepaintEverything();
            return screen;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or ObjectDisposedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ScreenHost? TryEnter(SlashConsoleHostRequest request)
    {
        var opened = ScreenHost.Open(request.Session, request.Capabilities, request.Ascii, request.Output);

        if (opened.Screen is not { } screen)
        {
            request.Errors.WriteLine(Refused(request));
            return null;
        }

        return screen;
    }

    private static string Refused(SlashConsoleHostRequest request) =>
        Refusal(TuiHost.RefusalFor(request.Capabilities) ?? "the terminal cannot carry the console.");
}
