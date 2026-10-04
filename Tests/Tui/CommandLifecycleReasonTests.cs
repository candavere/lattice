using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins that the failure line names the reason the command actually printed,
/// rather than pointing at output the reader has to go and find.
/// </summary>
/// <remarks>
/// The reason is never invented here: it is a bounded repeat of a line the
/// command already wrote to stderr, captured through the writer the command was
/// given. So each test asserts two things together — the closing line carries
/// the reason, and the command's own line is still on stderr, complete and
/// untouched. A change that satisfied only the first would be a change that
/// swallowed the command's output.
/// </remarks>
public class CommandLifecycleReasonTests
{
    private const char Escape = '\u001b';

    /// <summary>
    /// A character outside the basic multilingual plane, so it occupies a
    /// surrogate pair: a high surrogate at some index <c>n</c> and a low one at
    /// <c>n + 1</c>. Nothing this CLI writes today contains one, which is exactly
    /// why the cap's handling of them is pinned here rather than left for a real
    /// failure to exercise.
    /// </summary>
    private const string Emoji = "\U0001F600";

    private static TerminalEnvironment TerminalEnvironmentOf(bool noColor = false) =>
        new(
            "truecolor",
            "xterm-256color",
            noColor ? "1" : null,
            "en_US.UTF-8",
            InputRedirected: false,
            OutputRedirected: false,
            120,
            40);

    private static CliTerminal InteractiveTerminal => new(
        CapabilityDetector.Detect(TerminalEnvironmentOf()),
        InteractiveStderr: true);

    /// <summary>
    /// An interactive terminal that cannot style or repaint anything, so there
    /// is no live row and no spinner. The reason still has to reach the line,
    /// because this is the case that used to hand the command the bare writer.
    /// </summary>
    private static CliTerminal NoColorTerminal => new(
        CapabilityDetector.Detect(TerminalEnvironmentOf(noColor: true)),
        InteractiveStderr: true);

    /// <summary>A redirected stderr: not a terminal, so no lifecycle line at all.</summary>
    private static CliTerminal RedirectedTerminal => new(
        CapabilityDetector.Detect(TerminalEnvironmentOf()),
        InteractiveStderr: false);

    /// <summary>
    /// The rows a reader sees: each row with the erase sequence a repainting
    /// line leaves in front of it removed, because that sequence is where one
    /// row ends and the next begins on a terminal.
    /// </summary>
    private static List<string> Rows(string stderr) =>
        stderr.Split('\n')
            .Select(row => row.TrimEnd('\r'))
            .Select(row =>
            {
                var erase = row.LastIndexOf(CommandLifecycle.EraseToEndOfLine, StringComparison.Ordinal);

                return erase < 0
                    ? row
                    : row[(erase + CommandLifecycle.EraseToEndOfLine.Length)..].TrimStart('\r');
            })
            .Where(row => row.Length > 0)
            .ToList();

    /// <summary>The row that closed the command.</summary>
    private static string FailedRow(string stderr) =>
        Assert.Single(
            Rows(stderr),
            row => row.Contains("failed (exit", StringComparison.Ordinal));

    /// <summary>
    /// Fails when <paramref name="text"/> holds a surrogate that is not part of a
    /// pair: a high surrogate with no low surrogate after it, or a low surrogate
    /// with no high surrogate before it.
    /// </summary>
    /// <remarks>
    /// The rule is about whole characters, so this is the right way to say it
    /// whatever the cut did. Asserting on a code point count would pass on a row
    /// carrying half a character, because half of a pair is not a character the
    /// text has — it is a replacement glyph the terminal prints.
    /// </remarks>
    private static void AssertNoLoneSurrogates(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(
                    index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]),
                    $"a high surrogate at index {index} has no low surrogate after it.");
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                Assert.True(
                    index > 0 && char.IsHighSurrogate(text[index - 1]),
                    $"a low surrogate at index {index} has no high surrogate before it.");
            }
        }
    }

    /// <summary>
    /// Runs a command that writes exactly the lines given, so the capture can be
    /// driven with reason shapes no real scenario happens to produce.
    /// </summary>
    private static (int Exit, string Err) RunWriting(
        CliTerminal terminal,
        int exit,
        params string[] lines)
    {
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, terminal);
        var commandErrors = scope.Interleave(stderr);

        var result = scope.Run(() =>
        {
            foreach (var line in lines)
            {
                commandErrors.WriteLine(line);
            }

            return exit;
        });

        return (result, stderr.ToString());
    }

    private static string MissingTrajectory() =>
        Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.jsonl");

    /// <summary>
    /// A reason as the failure line shows it: the command's own line, bounded by
    /// the cap. The detail is never shortened on stderr — only the repeat is.
    /// </summary>
    private static string Bounded(string reason) =>
        reason.Length <= CommandLifecycleScope.MaxReasonCharacters
            ? reason
            : reason[..CommandLifecycleScope.MaxReasonCharacters] + "...";

    private static (int Exit, string Out, string Err) AnalyzeInteractively(
        CliTerminal terminal,
        params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr, terminal);

        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void TheFailedLineRepeatsTheCommandsOwnReason()
    {
        var result = AnalyzeInteractively(
            InteractiveTerminal,
            "analyze",
            "--trajectory",
            MissingTrajectory());

        Assert.Equal(1, result.Exit);

        var rows = Rows(result.Err);
        var own = Assert.Single(rows, row => row.StartsWith("error:", StringComparison.Ordinal));

        // The failure line carries the text the command printed after "error:",
        // so a reader watching a spinner learns what went wrong without losing
        // the live row.
        Assert.Contains(Bounded(own), FailedRow(result.Err), StringComparison.Ordinal);
        Assert.DoesNotContain("see the error above", FailedRow(result.Err), StringComparison.Ordinal);

        // And the command's own line is still on stderr, once, complete and
        // unabridged — the cap bounds the repeat, never the detail.
        Assert.Equal(1, rows.Count(row => string.Equals(row, own, StringComparison.Ordinal)));
    }

    [Fact]
    public void ATerminalThatCannotRepaintStillCarriesTheReason()
    {
        var result = AnalyzeInteractively(
            NoColorTerminal,
            "analyze",
            "--trajectory",
            MissingTrajectory());

        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain(Escape, result.Err);

        var rows = Rows(result.Err);
        var own = Assert.Single(rows, row => row.StartsWith("error:", StringComparison.Ordinal));

        Assert.Contains(Bounded(own), FailedRow(result.Err), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstReasonIsShownAndTheRestAreCounted()
    {
        var result = RunWriting(
            InteractiveTerminal,
            exit: 1,
            "scenario error: budget.spawn: must be at least 1",
            "scenario error: budget.tick: must be at least 1",
            "not a reason: progress 40%");

        Assert.Equal(1, result.Exit);

        var line = FailedRow(result.Err);
        Assert.Contains("scenario error: budget.spawn: must be at least 1", line, StringComparison.Ordinal);
        Assert.Contains("(+1 more above)", line, StringComparison.Ordinal);

        // A line that is not one of the commands' own failure prefixes is never
        // treated as a reason — and it is still on stderr.
        Assert.DoesNotContain("not a reason", line, StringComparison.Ordinal);
        Assert.Contains("not a reason: progress 40%", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonIsStrippedOfControlCharactersAndItsWhitespaceCollapsed()
    {
        // An escape sequence and a run of whitespace in the middle of the
        // command's own line.
        var noisy = "error: a" + Escape + "[31mred" + Escape + "[0m  \t b";

        var result = RunWriting(NoColorTerminal, exit: 1, noisy);

        // Escape removal and whitespace collapsed to one space: the visible text
        // of the reason, with nothing left in it that could move the cursor.
        Assert.Contains("error: a[31mred[0m b", FailedRow(result.Err), StringComparison.Ordinal);

        // Only the repeat is stripped: the command's own bytes, escapes and all,
        // are exactly what it wrote.
        Assert.Contains(noisy, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongReasonIsCappedAtTheNamedLimit()
    {
        var long_ = "error: " + new string('x', 300);

        var result = RunWriting(NoColorTerminal, exit: 1, long_);

        // The first hundred UTF-16 code units, then one marker.
        Assert.Contains(
            "error: " + new string('x', CommandLifecycleScope.MaxReasonCharacters - "error: ".Length) + "...",
            FailedRow(result.Err),
            StringComparison.Ordinal);

        // The detail itself is untouched: the cap bounds the repeat, not the
        // command's own output.
        Assert.Contains(long_, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AUsageErrorCarriesItsOwnReasonAndStatus()
    {
        var result = AnalyzeInteractively(InteractiveTerminal, "analyze");

        Assert.Equal(2, result.Exit);

        var rows = Rows(result.Err);
        var own = Assert.Single(rows, row => row.StartsWith("error:", StringComparison.Ordinal));

        Assert.Contains("exit 2", FailedRow(result.Err), StringComparison.Ordinal);
        Assert.Contains(Bounded(own), FailedRow(result.Err), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithNoReasonOfItsOwnKeepsTheGenericText()
    {
        // A command that fails without printing one of the failure prefixes — an
        // escaped exception takes a different path, and an add-in command may
        // print nothing at all. There is no reason to show, so the line must not
        // present one as if there were.
        var result = RunWriting(InteractiveTerminal, exit: 1, "still working on it");

        Assert.Contains("see the error above", FailedRow(result.Err), StringComparison.Ordinal);
        Assert.DoesNotContain("still working on it", FailedRow(result.Err), StringComparison.Ordinal);
        Assert.Contains("still working on it", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedExceptionIsShownThroughTheSameCap()
    {
        var message = "error: " + new string('y', 400);

        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, NoColorTerminal);

        var caught = Assert.Throws<InvalidOperationException>(() =>
        {
            scope.Run(() => throw new InvalidOperationException(message));
        });

        // The fault keeps carrying its own message to the runtime, unchanged.
        Assert.Equal(message, caught.Message);

        Assert.Contains(
            "error: " + new string('y', CommandLifecycleScope.MaxReasonCharacters - "error: ".Length) + "...",
            FailedRow(stderr.ToString()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscapedExceptionStraddlingASurrogatePairLosesTheWholePair()
    {
        // The exception path shares the same cap, so it shares the same cut. The
        // message arrives with a pair split by the boundary, and the line must not
        // be left holding one half of it.
        var message = "error: " + new string('y', 92) + Emoji + "tail";

        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, NoColorTerminal);

        var caught = Assert.Throws<InvalidOperationException>(() =>
        {
            scope.Run(() => throw new InvalidOperationException(message));
        });

        // The fault still carries its own message to the runtime, unchanged.
        Assert.Equal(message, caught.Message);

        var row = FailedRow(stderr.ToString());
        Assert.Contains("error: " + new string('y', 92) + "...", row, StringComparison.Ordinal);
        Assert.DoesNotContain(Emoji, row, StringComparison.Ordinal);
        AssertNoLoneSurrogates(row);
    }

    [Fact]
    public void ACapStraddlingASurrogatePairLosesTheWholePair()
    {
        // The real boundary: the kept text is indices 0..99, so a pair whose high
        // surrogate sits at 99 is cut in half by a cut at 100. Half a character is
        // not a character, and the terminal prints a replacement glyph for it.
        var reason = "error: " + new string('x', 92) + Emoji + "tail";

        var result = RunWriting(NoColorTerminal, exit: 1, reason);
        var row = FailedRow(result.Err);

        Assert.Contains("error: " + new string('x', 92) + "...", row, StringComparison.Ordinal);
        Assert.DoesNotContain(Emoji, row, StringComparison.Ordinal);
        AssertNoLoneSurrogates(row);

        // The detail itself is untouched: the cut bounds the repeat only.
        Assert.Contains(reason, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapEndingJustBeforeASurrogatePairKeepsEveryCharacter()
    {
        // A pair that begins exactly where the kept text stops is not cut at all:
        // both of its halves fall beyond the cap, so no character has to be
        // sacrificed to keep it whole.
        var reason = "error: " + new string('x', 93) + Emoji;

        var result = RunWriting(NoColorTerminal, exit: 1, reason);
        var row = FailedRow(result.Err);

        Assert.Contains("error: " + new string('x', 93) + "...", row, StringComparison.Ordinal);
        AssertNoLoneSurrogates(row);

        Assert.Contains(reason, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void ASurrogatePairWhollyInsideTheCapIsKeptIntact()
    {
        // The pair ends well before the cap, so the cut falls on an ordinary
        // character after it and the pair survives whole.
        var reason = "error: " + new string('x', 90) + Emoji + new string('y', 20);

        var result = RunWriting(NoColorTerminal, exit: 1, reason);
        var row = FailedRow(result.Err);

        Assert.Contains("error: " + new string('x', 90) + Emoji + "y...", row, StringComparison.Ordinal);
        AssertNoLoneSurrogates(row);

        Assert.Contains(reason, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonWrittenInPiecesIsStillOneCompleteLine()
    {
        // TextWriter.WriteLine is not the only shape a line arrives in: a command
        // that builds its message up and writes the newline last still wrote one
        // line, and the pieces are only complete once the newline lands.
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, NoColorTerminal);
        var commandErrors = scope.Interleave(stderr);

        var exit = scope.Run(() =>
        {
            commandErrors.Write("error: a reason ");
            commandErrors.Write("written in ");
            commandErrors.Write("pieces");
            commandErrors.Write(System.Environment.NewLine);

            return 1;
        });

        Assert.Equal(1, exit);
        Assert.Contains("error: a reason written in pieces", FailedRow(stderr.ToString()), StringComparison.Ordinal);
    }

    [Fact]
    public void APartialLineIsNotAReason()
    {
        // A command that dies before finishing its line has not finished saying
        // anything, and half a reason is not one: the failure line falls back
        // rather than repeating a fragment.
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, NoColorTerminal);
        var commandErrors = scope.Interleave(stderr);

        var exit = scope.Run(() =>
        {
            commandErrors.Write("error: half a line");

            return 1;
        });

        Assert.Equal(1, exit);
        Assert.Contains("see the error above", FailedRow(stderr.ToString()), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInactiveScopeHandsTheCommandTheIdenticalWriter()
    {
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("analyze", stderr, RedirectedTerminal);

        Assert.Same(stderr, scope.Interleave(stderr));
    }

    [Fact]
    public void ACommandTooShortForALineHandsTheCommandTheIdenticalWriter()
    {
        // "generate" is not on the long-running list, so it gets no line at all
        // even on an interactive terminal — and so no writer wrapper either.
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("generate", stderr, InteractiveTerminal);

        Assert.Same(stderr, scope.Interleave(stderr));
    }

    [Fact]
    public void AQuietScopeHandsTheCommandTheIdenticalWriter()
    {
        using var stderr = new StringWriter();
        using var scope = CommandLifecycleScope.Begin("simulate", stderr, InteractiveTerminal, quiet: true);

        Assert.Same(stderr, scope.Interleave(stderr));
    }

    [Fact]
    public void ARedirectedStderrGetsNothingButTheCommandsOwnLine()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["analyze", "--trajectory", MissingTrajectory()], stdout, stderr);

        Assert.Equal(1, exit);

        var text = stderr.ToString();

        // One line, the command's own, byte-for-byte what it wrote before the
        // reason was repeated anywhere.
        var own = Assert.Single(Rows(text));
        Assert.StartsWith("error:", own, StringComparison.Ordinal);
        Assert.Equal(own + System.Environment.NewLine, text);
        Assert.DoesNotContain(text, Escape.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("failed", text, StringComparison.Ordinal);
    }
}