using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The Launchpad's own loop: it owns the screen, reads keys, and hands a command the
/// reader asked for to a runner with the terminal fully restored.
/// </summary>
/// <remarks>
/// <para>
/// <b>One screen, one owner.</b> The host asks <see cref="ScreenHost"/> whether the
/// terminal can carry a screen and touches nothing until it has answered. After that
/// the key source is built and the Ctrl-C-as-input setting is taken for the screen's
/// life, and given back before the key source is disposed.
/// </para>
/// <para>
/// <b>A command runs outside the screen.</b> When the reader asks to run, the setting
/// is suspended and the alternate screen is left before the command starts, so
/// Ctrl-C reaches it the way it reaches the same command typed at a shell, and its
/// output lands on a terminal that is not a screen. When it finishes the screen is
/// entered again and its status shown on it.
/// </para>
/// <para>
/// <b>A command that throws is reported, not propagated.</b> The terminal is already
/// back by then, and a reader needs the message rather than a stack trace over their
/// own prompt.
/// </para>
/// </remarks>
public static class LaunchpadHost
{
    /// <summary>The status a refused run reports: the invocation named no terminal.</summary>
    private const int UsageRefused = 2;

    /// <summary>
    /// How long the loop waits for a key before it redraws. Long enough not to be a
    /// spin, short enough that a terminal resized under the screen catches up.
    /// </summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(50);

    /// <summary>Runs the Launchpad until the reader quits, running what they ask for.</summary>
    public static TuiRunResult Run(LaunchpadHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The refusal first, before a key source exists: building one is what asks the
        // process's console for its input mode, and on Windows that ask throws when
        // standard input is a file or a pipe.
        var refusal = TuiHost.RefusalFor(request.Capabilities);
        if (refusal is not null)
        {
            var reported = Refusal(refusal);
            request.Errors.WriteLine(reported);
            return new TuiRunResult(UsageRefused, reported);
        }

        // Declared first so it is disposed last, and the setting second so it goes
        // back first: the setting is restored before the reading thread is left to the
        // process, and both happen after the terminal is back.
        using var keys = request.Console.Keys();
        using var controlC = ControlCAsInputScope.Enter(request.Console.ControlCAsInput);

        var form = LaunchpadForm.For(request.Commands);
        var state = new State(form, request, controlC, keys);
        var host = Enter(request);

        try
        {
            Loop(state, host);
        }
        finally
        {
            host.Dispose();
        }

        return new TuiRunResult(0, null);
    }

    /// <summary>
    /// The one line a redirected run refuses with: the host's own, with the noun
    /// changed to name this screen and a pointer to the viewer. The fact is the
    /// host's alone — two screens asking their own question about redirection is how
    /// they come to disagree about whether a terminal can carry a screen.
    /// </summary>
    private static string Refusal(string hostRefusal) =>
        hostRefusal
            .Replace("the replay viewer", "the launchpad", StringComparison.Ordinal)
            .Replace("lattice tui:", "lattice:", StringComparison.Ordinal);

    /// <summary>
    /// The screen, the form and whatever the reader has been told since. One value so
    /// the loop is a loop over two things rather than over a dozen.
    /// </summary>
    private sealed class State(
        LaunchpadForm form,
        LaunchpadHostRequest request,
        ControlCAsInputScope controlC,
        IKeySource keys)
    {
        internal LaunchpadForm Form { get; } = form;

        internal LaunchpadHostRequest Request { get; } = request;

        internal ControlCAsInputScope ControlC { get; } = controlC;

        internal IKeySource Keys { get; } = keys;

        /// <summary>The line under the form about the last command that ran.</summary>
        internal string? Notice { get; set; }

        /// <summary>How many waits have passed with no key since the last one.</summary>
        internal int Idle { get; set; }

        /// <summary>
        /// Whether a command's status is on screen and waiting to be acknowledged.
        /// The acknowledging key is consumed rather than acted on: on a full-screen
        /// display, a keypress meant for "that is fine" would otherwise move the
        /// selection out from under a reader who was looking at the status.
        /// </summary>
        internal bool AwaitingAcknowledgement { get; set; }
    }

    private static void Loop(State state, ScreenHost host)
    {
        var form = state.Form;

        while (true)
        {
            host.Present(Frame(state, host));

            if (form.HasQuit)
            {
                return;
            }

            // The status from the last command is shown once and then cleared, so a
            // reader who comes back to the form is looking at the form rather than at
            // the last run's exit code — which would otherwise outrank every field
            // error for the rest of the session.
            if (state.Notice is not null)
            {
                state.Notice = null;
            }

            var outcome = state.Keys.Wait(IdleWait, out var key);

            if (outcome == KeyWait.Closed)
            {
                // <b>Only the end of the input ends the screen.</b> A wait that timed
                // out is not that: a real terminal's reader blocks on the next key for
                // as long as the reader sits there and reports nothing until one
                // arrives. Treating a timeout as an ending would close the Launchpad on
                // the first pause, which is every pause.
                if (++state.Idle > state.Request.TrailingIdleFrames)
                {
                    return;
                }

                continue;
            }

            if (outcome != KeyWait.Key)
            {
                // A timeout with the reader still going: nothing to do but look again,
                // which is also what catches a terminal resized under the screen.
                continue;
            }

            state.Idle = 0;

            if (state.AwaitingAcknowledgement)
            {
                state.AwaitingAcknowledgement = false;
                continue;
            }

            form.Apply(key);

            if (!form.WantsToRun)
            {
                continue;
            }

            // The form's own state is reset before the command runs, so a command that
            // fails brings the reader back to the form they filled in rather than to a
            // screen that thinks it is already running.
            form.Acknowledge();
            var status = RunOutsideTheScreen(state, host);
            state.Notice = status;
            state.AwaitingAcknowledgement = true;
            host = Reenter(state.Request, host);
        }
    }

    /// <summary>
    /// Runs the command with the terminal fully restored, and reports what happened.
    /// <para>
    /// The order is the whole point and it is fixed: the setting suspended, then the
    /// screen left, then the command started. Suspending first means there is no
    /// window in which the reader's Ctrl-C would arrive as a key the command ignores;
    /// leaving the screen second means the command's own output and progress land on
    /// an ordinary terminal rather than inside a full-screen view.
    /// </para>
    /// </summary>
    private static string RunOutsideTheScreen(State state, ScreenHost host)
    {
        state.ControlC.Suspend();

        try
        {
            host.Dispose();

            try
            {
                var status = state.Request.Runner.Run(new LaunchpadRunRequest(
                    state.Form.Arguments,
                    state.Form.CommandLine,
                    state.Request.Output,
                    state.Request.Errors,
                    state.Form.ChosenMode));

                return $"exit {Invariant(status)}";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Reported rather than propagated: the terminal is already back, and a
                // reader needs the message, not a stack trace over their own prompt.
                return exception.Message;
            }
        }
        finally
        {
            // Taken again for the screen's return, whatever the command left behind.
            state.ControlC.Resume();
        }
    }

    /// <summary>
    /// Enters the alternate screen for a run that has not been refused — and which
    /// cannot be, since the check already passed. A refusal here would mean the
    /// terminal changed its mind mid-run, and reporting that over a terminal the
    /// reader is looking at is better than throwing out of the loop.
    /// </summary>
    private static ScreenHost Reenter(LaunchpadHostRequest request, ScreenHost previous)
    {
        if (TryEnter(request) is not { } screen)
        {
            return previous;
        }

        // The screen came back, so whatever the previous one drew is no longer what is
        // on the terminal: the whole frame is repainted rather than diffed.
        screen.RepaintEverything();
        return screen;
    }

    /// <summary>
    /// Enters the alternate screen for the first time. The refusal has been asked
    /// already, so this cannot come back without one; a run that reached here and did
    /// is reported rather than thrown, because the alternative is a stack trace over a
    /// reader's terminal.
    /// </summary>
    private static ScreenHost Enter(LaunchpadHostRequest request) =>
        TryEnter(request) ?? throw new LaunchpadRefused(Refused(request));

    /// <summary>
    /// The alternate screen, or <c>null</c> with the one-line reason already printed.
    /// The single place the screen is entered, so the refusal is worded and placed the
    /// same way for the first entry and for the one after a command.
    /// </summary>
    private static ScreenHost? TryEnter(LaunchpadHostRequest request)
    {
        var opened = ScreenHost.Open(request.Session, request.Capabilities, request.Ascii, request.Output);

        if (opened.Screen is not { } screen)
        {
            request.Errors.WriteLine(Refused(request));
            return null;
        }

        return screen;
    }

    /// <summary>
    /// The one line a refusal is reported with, for the capability set that refused.
    /// </summary>
    private static string Refused(LaunchpadHostRequest request) =>
        Refusal(TuiHost.RefusalFor(request.Capabilities) ?? "the terminal cannot carry the launchpad.");

    /// <summary>
    /// The frame for the screen's current state, composed from the form rather than
    /// kept in step with it, so the two cannot drift apart.
    /// </summary>
    private static CellBuffer Frame(State state, ScreenHost host)
    {
        var form = state.Form;

        return LaunchpadLayout.Render(new LaunchpadRequest(
            [.. form.Commands.Select(command => new LaunchpadCommandView(
                command.Name,
                command.Summary,
                command.Fields.Count))],
            form.SelectedIndex,
            form.Selected,
            form.Mode == LaunchpadMode.Editing,
            [.. form.Fields.Select(field => new LaunchpadFieldView(
                field.Label,
                field.Kind,
                field.Required,
                form.Value(field),
                field.Default is { Length: > 0 },
                form.WasTyped(field)))],
            form.FocusedIndex,
            form.Caret,
            form.CommandLine,
            // The command's own status, when there is one, outranks a field's error:
            // a reader who has just run something is looking for what happened to it.
            state.Notice ?? form.ValidationError,
            host.Size,
            host.Glyphs,
            null));
    }

    private static string Invariant(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The terminal stopped being able to carry the screen part way through a run, so
/// there is no screen to return to. Carried out to the caller rather than swallowed:
/// the screen is already gone, and pretending otherwise would leave the reader with a
/// loop that draws nothing.
/// </summary>
public sealed class LaunchpadRefused : InvalidOperationException
{
    /// <summary>The one line the terminal refused with, which is the message.</summary>
    public LaunchpadRefused(string refusal)
        : base(refusal)
    {
    }
}
