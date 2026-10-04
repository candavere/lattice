using Lattice.Tui;

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
/// script that redirects output. A spinner on such a stream is not decoration,
/// it is corruption: a carriage return and an erase sequence land in the middle
/// of a captured file. So the lifecycle output is gated on
/// <see cref="InteractiveStderr"/>, which the default overload derives by
/// asking whether the writer it was handed is the process's own
/// <see cref="Console.Error"/> — a test's <see cref="StringWriter"/> never is.
/// </para>
/// <para>
/// That makes the quiet path the default for every existing caller, which is
/// what keeps the pinned stdout bytes and exit codes of the eight existing
/// subcommands exactly as they were.
/// </para>
/// </remarks>
/// <param name="Capabilities">Colour depth, encoding and redirection, resolved once.</param>
/// <param name="InteractiveStderr">
/// Whether the command's stderr is a terminal a person is watching.
/// </param>
/// <param name="RepaintIntervalMs">
/// How often the working line is redrawn. Carried here rather than fixed inside
/// the indicator so a test can make the animation deterministic instead of
/// asserting that a real command happened to run for longer than one tick —
/// which is a property of the machine's speed, not of the code.
/// </param>
public readonly record struct CliTerminal(
    TerminalCapabilities Capabilities,
    bool InteractiveStderr,
    int RepaintIntervalMs = WorkingIndicator.DefaultRepaintIntervalMs)
{
    /// <summary>
    /// Overrides how the working line is repainted. Internal and init-only, so
    /// it is reachable from a test without becoming part of the public shape:
    /// the positional parameters above, the constructor and the deconstructor are
    /// all unchanged. Null means the production timer.
    /// </summary>
    internal RepaintPumpFactory? PumpFactory { get; init; }
    /// <summary>
    /// The terminal as the real process sees it: stderr is interactive only when
    /// the writer the caller passed really is <see cref="Console.Error"/> and
    /// that stream was not redirected.
    /// </summary>
    /// <remarks>
    /// The environment is assembled here rather than taken from
    /// <see cref="TerminalEnvironment.Current"/> because that helper also probes
    /// the console for its width and height, and this decision needs only colour,
    /// encoding and redirection. The size probes are a syscall apiece and
    /// <see cref="CliApp.Run(string[], TextWriter, TextWriter)"/> is called once
    /// per argument vector by the argument fuzzer, so asking a terminal for its
    /// geometry thousands of times to answer a question about colour would make
    /// the fuzzer's runtime depend on the console. Zero is the documented
    /// "not asked for" width, and <see cref="TerminalEnvironment.UsableWidth"/>
    /// would turn it into the conventional fallback if anything ever did read it.
    /// </remarks>
    /// <param name="stderr">The writer the command was given for its stderr.</param>
    public static CliTerminal For(TextWriter stderr) => new(
        CapabilityDetector.Detect(
            new TerminalEnvironment(
                System.Environment.GetEnvironmentVariable("COLORTERM"),
                System.Environment.GetEnvironmentVariable("TERM"),
                System.Environment.GetEnvironmentVariable("NO_COLOR"),
                LocaleFrom(
                    System.Environment.GetEnvironmentVariable("LC_ALL"),
                    System.Environment.GetEnvironmentVariable("LC_CTYPE"),
                    System.Environment.GetEnvironmentVariable("LANG")),
                InputRedirected: Console.IsInputRedirected,
                OutputRedirected: Console.IsErrorRedirected,
                Width: 0,
                Height: 0)),
        ReferenceEquals(stderr, Console.Error) && !Console.IsErrorRedirected);

    private static string? LocaleFrom(string? lcAll, string? lcCtype, string? lang) =>
        TerminalEnvironment.LocaleFrom(lcAll, lcCtype, lang);
}
