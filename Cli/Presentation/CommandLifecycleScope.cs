using System.Globalization;
using System.Text;
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
/// The reason on a failure line is the command's own — its first
/// <c>error:</c>, <c>scenario error:</c> or <c>replay error:</c> line, read
/// off the writer the command writes through and repeated with its prefix. It is
/// never a summary this type wrote: the point of the failure line is that a
/// reader who missed the detail still learns what went wrong, and an invented
/// summary would be a second, competing account of the same failure.
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

    /// <summary>
    /// How much of a reason the failure line repeats, in display characters.
    /// Long enough to name the failure, short enough to leave the closing line
    /// readable on an 80-column terminal beside the verb, the status and the
    /// elapsed time.
    /// </summary>
    internal const int MaxReasonCharacters = 100;

    /// <summary>
    /// The prefixes a failure line of this CLI's own begins with. Read off
    /// <c>CliApp</c> rather than guessed: these are the three literal strings
    /// its commands write when they fail, and a line that does not start with one
    /// of them is progress, a summary or usage text, none of which is a reason.
    /// </summary>
    private static readonly string[] ReasonPrefixes =
    [
        "error:",
        "scenario error:",
        "replay error:",
    ];

    /// <summary>
    /// The text a failure with no reason of its own falls back to. It names where
    /// the detail is rather than claiming to be it.
    /// </summary>
    private const string UnreportedReason = "see the error above";

    /// <summary>
    /// What a capped reason ends with, so a cut is never mistaken for the whole
    /// message.
    /// </summary>
    private const string TruncationMarker = "...";

    /// <summary>
    /// How much of one line is held while waiting to learn whether the line is a
    /// reason. Four times the display cap: far more than any reason this CLI
    /// writes needs, and small enough that a command printing megabytes of
    /// diagnostics cannot turn the capture into a leak.
    /// </summary>
    private const int MaxCapturedReasonCharacters = MaxReasonCharacters * 4;

    private readonly WorkingIndicator? _indicator;
    private readonly ReasonCapture? _reasons;

    private CommandLifecycleScope(WorkingIndicator? indicator)
    {
        _indicator = indicator;

        // Only a live lifecycle line has a reason to show, so the capture exists
        // exactly when the indicator does and costs nothing otherwise.
        _reasons = indicator is null ? null : new ReasonCapture();
    }

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
    /// it was given when no lifecycle line is live — a redirected stderr, a
    /// <c>--quiet</c> run, a command too short to be worth a line; otherwise a
    /// pass-through that first clears the repainted row and reads the command's
    /// failure reasons off what it writes.
    /// </summary>
    /// <param name="stderr">The command's stderr.</param>
    public TextWriter Interleave(TextWriter stderr) =>
        _indicator is { IsActive: true } && _reasons is not null
            ? new CapturingTextWriter(_indicator.Interleave(stderr), _reasons)
            : stderr;

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
            // is rethrown so the exit status stays the runtime's decision. The
            // message goes through the same sanitising and cap as a reason the
            // command printed, because it lands on the same line.
            _indicator.Fail(Sanitise(exception.Message), 1);
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

        _indicator!.Fail(ReportedReason(_reasons!), exit);
    }

    /// <summary>
    /// The reason to repeat on the failure line: the command's own first reason,
    /// with how many further reasons it printed after it. A command that printed
    /// none keeps the pointer at the output above, because a line that names no
    /// reason is better than one that invents a reason it did not have.
    /// </summary>
    private static string ReportedReason(ReasonCapture reasons)
    {
        if (reasons.First is not { } first)
        {
            return UnreportedReason;
        }

        return reasons.More == 0
            ? first
            : string.Create(CultureInfo.InvariantCulture, $"{first} (+{reasons.More} more above)");
    }

    /// <summary>
    /// The text safe to repeat on a failure line: control characters dropped,
    /// every run of whitespace collapsed to one space, both ends trimmed, and at
    /// most <see cref="MaxReasonCharacters"/> display characters.
    /// </summary>
    private static string Sanitise(string reason)
    {
        var clean = new StringBuilder(reason.Length);

        foreach (var character in reason)
        {
            // Whitespace first: a tab or a newline inside a reason is spacing,
            // and every run of it — leading, trailing or interior — collapses to
            // one space, so a padded message cannot open a gap on a line that has
            // to stay one readable row.
            if (char.IsWhiteSpace(character))
            {
                if (clean.Length > 0 && clean[^1] != ' ')
                {
                    clean.Append(' ');
                }

                continue;
            }

            // Every other control character is dropped rather than escaped. The
            // failure line is plain status on the closing row, and an escape
            // surviving into it would move the cursor and print the rest of the
            // line somewhere else entirely.
            if (!char.IsControl(character))
            {
                clean.Append(character);
            }
        }

        var text = clean.ToString().TrimEnd();

        if (text.Length <= MaxReasonCharacters)
        {
            return text;
        }

        // Never cut between a surrogate pair: half of one is not a character, and
        // the terminal would be handed a replacement glyph for it.
        //
        // The pair at risk is the one straddling the cut: a high surrogate at
        // MaxReasonCharacters-1 whose low half is the character just past it.
        var cut = char.IsHighSurrogate(text[MaxReasonCharacters - 1])
            ? MaxReasonCharacters - 1
            : MaxReasonCharacters;

        return string.Concat(text.AsSpan(0, cut), TruncationMarker);
    }

    /// <summary>
    /// The command's own failure reasons, read off the writer it writes through
    /// as the command runs.
    /// </summary>
    /// <remarks>
    /// One writer — the thread running the command — and one reader — the same
    /// thread, once the command has returned. The lock is the same discipline
    /// <see cref="WorkingIndicator"/> uses for its own repaint state: it costs
    /// nothing on the single-writer path and keeps the summary readable if a
    /// command ever hands its writer to a worker of its own. The repaint thread
    /// never looks in here.
    /// </remarks>
    private sealed class ReasonCapture
    {
        private readonly object _gate = new();
        private readonly StringBuilder _line = new();
        private string? _first;
        private int _more;

        /// <summary>The first complete line that began with one of the reasons.</summary>
        public string? First
        {
            get
            {
                lock (_gate)
                {
                    return _first;
                }
            }
        }

        /// <summary>How many further complete lines began with one of the reasons.</summary>
        public int More
        {
            get
            {
                lock (_gate)
                {
                    return _more;
                }
            }
        }

        /// <summary>Takes one character of the command's own stderr.</summary>
        public void Write(char character)
        {
            lock (_gate)
            {
                if (character == '\n')
                {
                    _closeLine();
                    return;
                }

                if (_line.Length < MaxCapturedReasonCharacters)
                {
                    _line.Append(character);
                }
            }
        }

        /// <summary>
        /// Settles the line just finished. Only a complete line counts: a command
        /// that dies mid-line has not finished saying anything, and half a reason
        /// is not one.
        /// </summary>
        private void _closeLine()
        {
            if (_isReason())
            {
                if (_first is null)
                {
                    _first = Sanitise(_line.ToString());
                }
                else
                {
                    _more++;
                }
            }

            _line.Clear();
        }

        /// <summary>
        /// Whether the line just finished is one of the commands' own failure
        /// lines. A progress line, a summary and the usage text all fail here, so
        /// none of them can be repeated as a reason.
        /// </summary>
        private bool _isReason()
        {
            foreach (var prefix in ReasonPrefixes)
            {
                if (prefix.Length <= _line.Length && _startsWith(prefix))
                {
                    return true;
                }
            }

            return false;
        }

        private bool _startsWith(string prefix)
        {
            for (var index = 0; index < prefix.Length; index++)
            {
                if (_line[index] != prefix[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The command's stderr with its failure reasons read off it. A pass-through
    /// in the strict sense: every character reaches the inner writer first,
    /// unchanged and in order, and none is held back, held over or rewritten.
    /// </summary>
    private sealed class CapturingTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly ReasonCapture _capture;

        public CapturingTextWriter(TextWriter inner, ReasonCapture capture)
        {
            _inner = inner;
            _capture = capture;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            _capture.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            _inner.Write(value);

            foreach (var character in value)
            {
                _capture.Write(character);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            _inner.Write(buffer, index, count);

            for (var offset = 0; offset < count; offset++)
            {
                _capture.Write(buffer[index + offset]);
            }
        }

        public override void Flush() => _inner.Flush();
    }

    /// <inheritdoc />
    public void Dispose() => _indicator?.Dispose();
}
