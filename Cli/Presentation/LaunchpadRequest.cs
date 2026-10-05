using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>How one command is started once the reader asks for it.</summary>
/// <param name="Arguments">
/// The command line as an argument vector, command name first — the shape
/// <c>CliApp.Run</c> takes, so the Launchpad is never a second parser for a command
/// line it did not build.
/// </param>
/// <param name="CommandLine">The same command line as text, for diagnostics.</param>
/// <param name="Output">Where the command's own stdout goes. In a real run, the process's own.</param>
/// <param name="Errors">Where the command's own stderr goes. In a real run, the process's own.</param>
/// <param name="Mode">
/// How the reader asked for it: watched as a screen, or run as typed. Beside the
/// argument vector and not inside it, because the vector is the reader's command
/// line and the mode is a choice about which of the command's two paths is taken.
/// </param>
public readonly record struct LaunchpadRunRequest(
    string[] Arguments,
    string CommandLine,
    TextWriter Output,
    TextWriter Errors,
    RunMode Mode = RunMode.Run)
{
    /// <summary>
    /// The same request with a different argument vector. Used by the one place that
    /// rewrites a vector — putting <c>tui</c> in front of the cockpit's — so the
    /// reader's own command line and streams travel with the rewrite rather than being
    /// reconstructed beside it.
    /// </summary>
    public LaunchpadRunRequest WithArguments(string[] arguments) => this with { Arguments = arguments };
}

/// <summary>
/// Starts a command the reader has configured. A seam because the ordering the
/// terminal and the console setting need is the same whichever command it is, and
/// because a test can assert that ordering without running a command.
/// </summary>
/// <remarks>
/// The implementation does <b>not</b> run while the alternate screen is up and does
/// <b>not</b> run with Ctrl-C swallowed as a key: see <see cref="LaunchpadHost"/>.
/// </remarks>
public interface ILaunchpadRunner
{
    /// <summary>
    /// Runs the command and returns its exit status. Throwing is allowed and is
    /// reported by the host; a runner that swallowed a failure would leave the
    /// reader with a screen that says nothing went wrong.
    /// </summary>
    int Run(LaunchpadRunRequest request);
}

/// <summary>Everything one Launchpad run is composed from.</summary>
/// <param name="Commands">The commands to offer, in the order the list shows them.</param>
/// <param name="Output">Where frames go. In a real run, the process's own stdout.</param>
/// <param name="Errors">Where a refusal goes. In a real run, the process's own stderr.</param>
/// <param name="Capabilities">What the terminal can do, resolved once.</param>
/// <param name="Console">The console the screen is composed from.</param>
/// <param name="Session">The terminal seam.</param>
/// <param name="Clock">How elapsed time is measured.</param>
/// <param name="Runner">How a command is started once the reader asks for it.</param>
/// <param name="TrailingIdleFrames">
/// How many times the loop redraws after its keys run out before it gives up. A real
/// terminal's reader blocks rather than ending, so this is only reachable from a
/// test, and it exists so the loop has a bounded ending rather than none.
/// </param>
public sealed record LaunchpadHostRequest(
    IReadOnlyList<LaunchpadCommand> Commands,
    TextWriter Output,
    TextWriter Errors,
    TerminalCapabilities Capabilities,
    TuiConsole Console,
    ITerminalSessionFactory Session,
    IUiClock Clock,
    ILaunchpadRunner Runner,
    int TrailingIdleFrames = 0,
    bool Ascii = false);
