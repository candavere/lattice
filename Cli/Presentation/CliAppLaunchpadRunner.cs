using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The cockpit, as the Launchpad starts it: the recorded trajectory's read-only
/// viewer, and the live episode's.
/// </summary>
/// <remarks>
/// This is the seam the Launchpad uses rather than the commands themselves, because
/// the cockpit is a screen and not a command: it needs a terminal, it reads keys,
/// and it runs no simulation of its own. Both subcommands go through
/// <see cref="TuiHost"/>'s own refusal, so a redirected run is refused before
/// anything is built.
/// </remarks>
public interface ICockpitRunner
{
    /// <summary>
    /// Opens the cockpit for these arguments and returns its exit status. The
    /// arguments are the <c>lattice tui</c> argument vector, so the reader's command
    /// line reaches the cockpit without being re-parsed here.
    /// </summary>
    int Run(LaunchpadRunRequest request);
}

/// <summary>
/// The one runner there is: the two commands that are a screen open the cockpit, and
/// every other command is handed to <see cref="CliApp.Run"/> unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>One dispatch, one argument vector.</b> The two screen commands are recognised
/// by their own names and everything else goes to <c>CliApp.Run</c> verbatim. A
/// per-command runner would be a second implementation of each command's behaviour,
/// and the stdout bytes and exit codes of eight commands are frozen.
/// </para>
/// <para>
/// <b>The cockpit keeps its own refusal.</b> <c>lattice tui replay</c> and
/// <c>lattice tui simulate</c> decide for themselves whether the terminal can carry
/// them, which is the same question the Launchpad already answered. Asking again is
/// deliberate: the cockpit's own path is what a reader of <c>lattice tui</c> gets, and
/// this way the Launchpad gets the same one.
/// </para>
/// </remarks>
public sealed class CliAppLaunchpadRunner : ILaunchpadRunner
{
    /// <summary>The argument vector of the two commands that are a screen rather than a run.</summary>
    private const string Tui = "tui";

    private readonly ICockpitRunner _cockpit;
    private readonly CliTerminal _terminal;
    private readonly TuiConsole _console;

    /// <summary>The cockpit the Launchpad starts.</summary>
    /// <param name="terminal">
    /// What the CLI believes about the terminal, resolved once so every lifecycle
    /// line this runner produces is decided by the same value.
    /// </param>
    /// <param name="console">
    /// The console the cockpit is composed from. Defaults to the real one, so a
    /// Launchpad run reads keys from the reader's own terminal.
    /// </param>
    public CliAppLaunchpadRunner(
        ICockpitRunner? cockpit = null,
        CliTerminal? terminal = null,
        TuiConsole? console = null)
    {
        _terminal = terminal ?? CliTerminal.For(Console.Error);
        _console = console ?? TuiConsole.Default;
        _cockpit = cockpit ?? new CliAppCockpitRunner(_console);
    }

    /// <inheritdoc />
    public int Run(LaunchpadRunRequest request)
    {
        // The reader's choice of how to run it, not the command's name: a simulate
        // watched live is a screen and a simulate recorded is a command, and only the
        // reader knows which they filled the form for.
        return request.Mode == RunMode.Screen
            ? _cockpit.Run(request.WithArguments(CockpitArguments(request.Arguments)))
            : CliApp.Run(request.Arguments, request.Output, request.Errors, _terminal, _console);
    }

    /// <summary>
    /// The cockpit's own argument vector: the same arguments with <c>tui</c> in
    /// front. Prefixed rather than re-parsed, so the cockpit's refusal, its flags and
    /// its cockpit-vs-batch choices are all the ones a reader of <c>lattice tui</c>
    /// already gets.
    /// </summary>
    public static string[] CockpitArguments(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0] == Tui ? [.. arguments] : [Tui, .. arguments];

    /// <summary>
    /// Whether an argument vector names a screen by default. <c>replay</c> is the
    /// cockpit's read-only viewer, <c>simulate</c> can be watched live, and
    /// <c>ledger</c> is the Ledger — so all three open a screen unless the reader asked
    /// otherwise.
    /// <para>
    /// The decision is on the subcommand's own name and nothing else, and a leading
    /// <c>tui</c> is stepped over first because that is the form the Launchpad builds for
    /// a screen that is not a top-level command. A near miss here would run a viewer as
    /// a command and print its frames into a file, so the recognised set is written out
    /// rather than derived.
    /// </para>
    /// </summary>
    public static bool IsCockpit(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return false;
        }

        var subcommand = arguments[0] == Tui && arguments.Count > 1 ? arguments[1] : arguments[0];
        return subcommand is "replay" or "simulate" or "ledger";
    }
}

/// <summary>
/// The cockpit over the CLI's own <c>tui</c> command, so <c>lattice tui replay</c>
/// reached from the Launchpad is the same code path a reader gets from a shell —
/// including its refusal, its key source and its Ctrl-C-as-input scope.
/// </summary>
public sealed class CliAppCockpitRunner : ICockpitRunner
{
    private readonly TuiConsole _console;

    /// <summary>The cockpit over the CLI's own <c>lattice tui</c> command.</summary>
    /// <param name="console">
    /// The console the cockpit reads keys from. Defaults to the real one, so a
    /// Launchpad run reads keys from the reader's own terminal.
    /// </param>
    public CliAppCockpitRunner(TuiConsole? console = null) =>
        _console = console ?? TuiConsole.Default;

    /// <inheritdoc />
    public int Run(LaunchpadRunRequest request)
    {
        return CliApp.Run(request.Arguments, request.Output, request.Errors, CliTerminal.For(request.Errors), _console);
    }
}
