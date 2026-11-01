namespace Lattice.Cli.Presentation;

/// <summary>
/// What the CLI believes about the terminal it is attached to, as a value so
/// that every decision driven by it can be reached from a test without a
/// terminal.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="CliApp.Run(string[], TextWriter, TextWriter)"/>
/// takes its output streams as arguments and is therefore routinely handed a
/// <see cref="StringWriter"/> — by the repository's own tests, and by any
/// script that redirects output. The lifecycle output is gated on
/// <see cref="InteractiveStderr"/>, which the default overload derives by
/// asking whether the writer it was handed is the process's own
/// <see cref="Console.Error"/> — a test's <see cref="StringWriter"/> never is.
/// </para>
/// <para>
/// The colour-capability half of the old record (depth, encoding, repaint
/// plumbing) belonged to the interactive working line and was removed with the
/// TUI (<c>tui-wip-2026-10-24</c> archives it); a redirected stderr never took
/// that path, so headless behavior is unchanged.
/// </para>
/// </remarks>
/// <param name="InteractiveStderr">
/// Whether the command's stderr is a terminal a person is watching.
/// </param>
public readonly record struct CliTerminal(
    bool InteractiveStderr)
{
    /// <summary>
    /// The terminal as the real process sees it: stderr is interactive only when
    /// the writer the caller passed really is <see cref="Console.Error"/> and
    /// that stream was not redirected.
    /// </summary>
    /// <remarks>
    /// The environment probes stay out of this path: no width/height or colour
    /// detection is performed, because nothing headless consumes it (the
    /// consumer was the removed working indicator). The decision needs only the
    /// identity of the stderr writer.
    /// </remarks>
    /// <param name="stderr">The writer the command was given for its stderr.</param>
    public static CliTerminal For(TextWriter stderr) => new(
        ReferenceEquals(stderr, Console.Error) && !Console.IsErrorRedirected);
}
