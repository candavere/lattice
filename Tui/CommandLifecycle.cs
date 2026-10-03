using System.Collections.Immutable;
using System.Globalization;

namespace Lattice.Tui;

/// <summary>
/// How far along a command is, as the two real counters the caller has: work
/// completed and work budgeted, with the unit those counters are counted in.
/// </summary>
/// <remarks>
/// The counters are recorded rather than derived, and the record validates that
/// they agree. A component that reported a percentage, a bar, or an unbounded
/// counter would be inventing a quantity; a ratio of two counters somebody
/// actually incremented is not, and validating the pair is what stops a caller's
/// own bookkeeping bug from reaching the terminal as "41/40 matches".
/// </remarks>
public readonly record struct CommandProgress
{
    /// <summary>Two spaces between fields: wide enough to read as a column gap,
    /// narrow enough that the line stays short on an 80-column terminal.</summary>
    internal const string FieldSeparator = "  ";

    /// <summary>Work completed so far.</summary>
    public long Completed { get; }

    /// <summary>Work budgeted for the whole command.</summary>
    public long Total { get; }

    /// <summary>What the counters count, in the singular: "matches", "steps".</summary>
    public string Unit { get; }

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="total"/> is not positive, or <paramref name="completed"/>
    /// is negative or exceeds it.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="unit"/> is empty.</exception>
    public CommandProgress(long completed, long total, string unit)
    {
        if (total < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(total), total, "total must be at least 1: a budget of zero describes no work.");
        }

        if (completed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completed), completed, "completed cannot be negative.");
        }

        if (completed > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completed), completed, $"completed cannot exceed the budget of {total}.");
        }

        if (string.IsNullOrEmpty(unit))
        {
            throw new ArgumentException("unit cannot be empty.", nameof(unit));
        }

        Completed = completed;
        Total = total;
        Unit = unit;
    }

    /// <summary>The counter pair as it appears on the working line.</summary>
    public string Describe()
    {
        var invariant = CultureInfo.InvariantCulture;
        return $"{Completed.ToString(invariant)}/{Total.ToString(invariant)} {Unit}";
    }
}

/// <summary>
/// Renders the three lines a long command puts on stderr: the one live working
/// line, and the success or failure line that closes it. Pure <see cref="string"/>
/// formatting over the theme — no terminal is touched and no clock is read, so
/// the same inputs always produce the same bytes and every branch is reachable
/// from a test.
/// </summary>
/// <remarks>
/// The spinner frames are held here rather than added to <see cref="Glyphs"/>
/// because that table is the cell-grid's glyph vocabulary and is walked by a
/// test that pins its exact size. A spinner is a time-varying mark rather than a
/// grid cell, and folding it into that table would change a contract the grid
/// already depends on. The frames are held to the same three-block restriction
/// the grid uses, and <c>CommandLifecycleTests</c> asserts it for them directly.
/// </remarks>
public static class CommandLifecycle
{
    /// <summary>How many distinct spinner frames there are.</summary>
    public const int SpinnerFrameCount = 4;

    /// <summary>
    /// Erases from the cursor to the end of the line. Repainting a line that got
    /// shorter would otherwise leave the tail of the longer previous line showing.
    /// </summary>
    public const string EraseToEndOfLine = "\u001b[K";

    /// <summary>The carriage return that returns to the start of the live line.</summary>
    public const string ReturnToLineStart = "\r";

    /// <summary>
    /// The spinner frames: the quadrant circle rotating anticlockwise. Chosen from
    /// the Geometric Shapes block rather than the Braille Patterns block a
    /// terminal spinner is more usually drawn from, because Geometric Shapes is
    /// one of the three blocks <see cref="Glyphs"/> admits and Braille is not —
    /// a fourth block would weaken that restriction for every later screen, and
    /// the quadrant reads as motion just as clearly at one character per frame.
    /// </summary>
    private static readonly ImmutableArray<char> Frames = ImmutableArray.Create('◐', '◓', '◑', '◒');

    /// <summary>The single-character ASCII stand-in for each frame.</summary>
    private static readonly ImmutableArray<char> AsciiFrames = ImmutableArray.Create('|', '/', '-', '\\');

    /// <summary>
    /// The spinner character for <paramref name="frame"/>, wrapping, or its ASCII
    /// stand-in when the terminal cannot encode UTF-8. Each stand-in is one
    /// character so the fallback never shifts the line.
    /// </summary>
    public static string Spinner(int frame, bool utf8)
    {
        var index = ((frame % SpinnerFrameCount) + SpinnerFrameCount) % SpinnerFrameCount;
        return (utf8 ? Frames[index] : AsciiFrames[index]).ToString();
    }

    /// <summary>
    /// The live working line: spinner, command name, the real progress when the
    /// command has a counter to report, and the measured elapsed time. The
    /// command name and the progress share the accent so the eye lands on the
    /// verb first and the numbers second.
    /// </summary>
    public static string Working(
        string command,
        int frame,
        TimeSpan elapsed,
        CommandProgress? progress,
        ColorDepth depth,
        bool utf8)
    {
        // The spinner is one glyph, not a field: it takes a single space so the
        // verb starts one column in rather than sitting in a gap.
        var head = Spinner(frame, utf8) + " " +
            Style(
                progress is null
                    ? command
                    : command + CommandProgress.FieldSeparator + progress.Value.Describe(),
                Palette.Accent,
                depth);

        return Join(head, Elapsed(elapsed));
    }

    /// <summary>The line that closes a command which finished its work.</summary>
    public static string Succeeded(string command, TimeSpan elapsed, ColorDepth depth) =>
        Join(
            Style(command, Palette.TextPrimary, depth),
            Style("ok", Palette.Success, depth),
            Elapsed(elapsed));

    /// <summary>
    /// The line that closes a command which did not finish, carrying the reason
    /// the caller actually reported and the exit code it is about to return, so
    /// the two can never disagree on the terminal.
    /// </summary>
    public static string Failed(
        string command,
        string reason,
        int exitCode,
        TimeSpan elapsed,
        ColorDepth depth)
    {
        var invariant = CultureInfo.InvariantCulture;
        return Join(
            Style(command, Palette.TextPrimary, depth),
            Style($"failed (exit {exitCode.ToString(invariant)})", Palette.Error, depth),
            Elapsed(elapsed),
            reason);
    }

    /// <summary>
    /// A measured duration: tenths of a second below a minute, whole seconds
    /// above it. Two shapes rather than one because this line repaints in place,
    /// and a duration that changes width mid-run would move the cursor's column
    /// and make the line jitter.
    /// </summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        var invariant = CultureInfo.InvariantCulture;
        var milliseconds = elapsed.TotalMilliseconds < 0 ? 0 : elapsed.TotalMilliseconds;

        return milliseconds < 60_000
            ? $"{(milliseconds / 1000.0).ToString("0.0", invariant)}s"
            : string.Format(
                invariant,
                "{0}m {1:00}s",
                (long)elapsed.TotalMinutes,
                elapsed.Seconds);
    }

    private static string Join(params string[] fields) =>
        string.Join(CommandProgress.FieldSeparator, fields);

    /// <summary>
    /// Wraps one field in the given colour. At <see cref="ColorDepth.None"/> the
    /// field is returned byte-for-byte, which is what keeps a <c>NO_COLOR</c> run
    /// free of escape sequences while still allowing the plain status text.
    /// </summary>
    private static string Style(string text, Rgb color, ColorDepth depth) =>
        depth == ColorDepth.None ? text : ColorMapper.Foreground(color, depth) + text + ColorMapper.Reset;
}
