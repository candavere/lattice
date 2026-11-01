namespace Lattice.Cli.Presentation;

/// <summary>
/// Owns one command's lifecycle: it runs the command and returns the exit code
/// the command actually returned.
/// </summary>
/// <remarks>
/// <para>
/// This type is the inert remnant of the interactive working line that was
/// removed with the TUI (<c>tui-wip-2026-10-24</c> archives it): the spinner,
/// the success/failure lines and the reason capture all lived in the
/// interactive working indicator, which existed only for an interactive
/// stderr. Nothing a headless or research command observes ever depended on it —
/// a redirected stderr always took the inert path, which is what kept the pinned
/// stdout bytes and exit codes of every subcommand identical with and without a
/// live line — so the inert path is now the only path.
/// </para>
/// <para>
/// The scope stays as the seam the commands hang their execution on; the
/// redirected-stderr purity contract (no lifecycle bytes on a captured stderr,
/// stdout byte-identical, exit codes preserved) is pinned by
/// <c>CommandLifecycleScopeTests</c>.
/// </para>
/// </remarks>
public sealed class CommandLifecycleScope : IDisposable
{
    private CommandLifecycleScope()
    {
    }

    /// <summary>
    /// Opens a scope for <paramref name="command"/>. Always inert: the
    /// interactive working line was removed with the TUI.
    /// </summary>
    /// <param name="command">The verb being run.</param>
    /// <param name="stderr">The command's stderr.</param>
    /// <param name="terminal">What the CLI believes about the terminal.</param>
    /// <param name="quiet">Whether <c>--quiet</c> was passed, when the command accepts it.</param>
    public static CommandLifecycleScope Begin(
        string command,
        TextWriter stderr,
        CliTerminal terminal,
        bool quiet = false)
    {
        _ = command;
        _ = stderr;
        _ = terminal;
        _ = quiet;
        return new CommandLifecycleScope();
    }

    /// <summary>
    /// The writer a command should use for its own stderr: the one it was given,
    /// unchanged. The interactive interleaving was removed with the TUI.
    /// </summary>
    /// <param name="stderr">The command's stderr.</param>
    public TextWriter Interleave(TextWriter stderr) => stderr;

    /// <summary>
    /// Runs <paramref name="command"/> and returns the status it returned.
    /// </summary>
    public int Run(Func<int> command) => command();

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
