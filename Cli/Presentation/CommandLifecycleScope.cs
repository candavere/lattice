using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Owns one command's lifecycle lines: it opens the working line before the
/// command runs, closes it with the exit code the command actually returned, and
/// reports the reason when the command failed.
/// </summary>
/// <remarks>
/// <para>
/// The exit code is taken from the command's own return value rather than
/// recomputed, so the line and the process status cannot disagree. A failure
/// line is emitted for every non-zero status, including a usage error's
/// <c>2</c>, because a reader watching a spinner has no other way to tell a
/// crashed run from a mistyped flag.
/// </para>
/// <para>
/// Nothing here writes to stdout, and nothing is written at all unless the
/// terminal reports an interactive stderr, so every existing command's captured
/// bytes are unchanged.
/// </para>
/// </remarks>
public sealed class CommandLifecycleScope : IDisposable
{
    /// <summary>
    /// Publishes the command's real progress for the live line. A command with no
    /// counter simply never calls this, and the line shows the spinner and the
    /// elapsed time alone rather than an invented fraction.
    /// </summary>
    public void Report(Func<CommandProgress?> progress) =>
        _indicator?.UseProgress(progress);

    /// <summary>
    /// The counter a command should publish as it runs, or <c>null</c> when the
    /// lifecycle line is inactive. Returning <c>null</c> rather than a disabled
    /// counter keeps the call sites free of "is this even on" checks.
    /// </summary>
    public MatchCounter? Matches(int budget)
    {
        if (_indicator is null)
        {
            return null;
        }

        var counter = new MatchCounter(budget);
        Report(() => counter.Snapshot());
        return counter;
    }

    /// <summary>
    /// The commands whose work is long enough to be worth a working line. The
    /// short ones (<c>generate</c>, <c>render</c>, <c>validate-scenario</c>) are
    /// excluded because a line that appears and vanishes within a frame is noise.
    /// </summary>
    private static readonly HashSet<string> LongRunning = new(StringComparer.Ordinal)
    {
        "simulate", "evaluate", "benchmark", "replay", "analyze",
    };

    private readonly WorkingIndicator? _indicator;

    private CommandLifecycleScope(WorkingIndicator? indicator) => _indicator = indicator;

    /// <summary>
    /// Opens a scope for <paramref name="command"/>, or an inert one when the
    /// command is short or stderr is not an interactive terminal.
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
        // An unknown verb is reported by UnknownCommand with the usage text;
        // opening a line for it would print a spinner over a usage error.
        if (!LongRunning.Contains(command) || !terminal.InteractiveStderr)
        {
            return new CommandLifecycleScope(null);
        }

        var indicator = new WorkingIndicator(
            command,
            stderr,
            terminal.Capabilities,
            quiet,
            clock: null,
            repaintIntervalMs: terminal.RepaintIntervalMs,
            pumpFactory: terminal.PumpFactory);

        return new CommandLifecycleScope(indicator);
    }

    /// <summary>
    /// The writer a command should use for its own stderr. Identical to the one
    /// it was given unless a live line is being repainted, in which case each
    /// write first clears that row so the two cannot overprint each other.
    /// </summary>
    /// <param name="stderr">The command's stderr.</param>
    public TextWriter Interleave(TextWriter stderr) =>
        _indicator?.Interleave(stderr) ?? stderr;

    /// <summary>
    /// Runs <paramref name="command"/> and closes the lifecycle line with the
    /// status it returned. A command that reports its own failure detail to
    /// stderr has already written it, so the failure line carries the exit code
    /// and points at that output rather than restating a message the caller
    /// never saw.
    /// </summary>
    public int Run(Func<int> command)
    {
        if (_indicator is null)
        {
            return command();
        }

        // Started here rather than in Begin so the first frame is painted only
        // once the command is genuinely about to do work, and so the pump is
        // never running for the usage text an unknown verb prints.
        _indicator.Start();

        try
        {
            var exit = command();
            Report(exit);
            return exit;
        }
        catch (Exception exception)
        {
            // Every command already handles its own faults and returns a status,
            // so reaching here means a fault escaped that handling. The line is
            // closed with the reason rather than left half-drawn, and the fault
            // is rethrown so the exit status stays the runtime's decision.
            _indicator.Fail(exception.Message, 1);
            throw;
        }
    }

    private void Report(int exit)
    {
        if (exit == 0)
        {
            _indicator!.Succeed();
            return;
        }

        // The command has already written its real reason to stderr — the
        // pinned "error: ..." line or the per-problem report. Repeating a
        // message here would either duplicate it or, worse, invent a summary the
        // command never made, so the line names the status and points at the
        // detail above it.
        _indicator!.Fail("see the error above", exit);
    }

    /// <inheritdoc />
    public void Dispose() => _indicator?.Dispose();
}
