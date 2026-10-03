using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the command-lifecycle lines: the spinner frames, the working line, and
/// the two closing lines. These are exact-string assertions because the whole
/// point of the component is the bytes it puts on a terminal, and a reader
/// diagnosing a garbled indicator needs the expected shape written down.
/// </summary>
public class CommandLifecycleTests
{
    [Fact]
    public void TheSpinnerCyclesThroughFourFramesAndWraps()
    {
        var frames = Enumerable.Range(0, CommandLifecycle.SpinnerFrameCount)
            .Select(frame => CommandLifecycle.Spinner(frame, utf8: true))
            .ToArray();

        Assert.Equal(frames.Length, frames.Distinct().Count());
        Assert.Equal(frames[0], CommandLifecycle.Spinner(
            CommandLifecycle.SpinnerFrameCount, utf8: true));
    }

    [Fact]
    public void EverySpinnerFrameFallsBackToOneAsciiCharacter()
    {
        for (var frame = 0; frame < CommandLifecycle.SpinnerFrameCount; frame++)
        {
            var ascii = CommandLifecycle.Spinner(frame, utf8: false);

            Assert.Equal(1, ascii.Length);
            Assert.True(ascii[0] < 128, $"frame {frame} falls back to non-ASCII '{ascii}'.");
        }
    }

    [Fact]
    public void EverySpinnerFrameComesFromAnAllowedGlyphBlock()
    {
        // The same three blocks Glyphs restricts itself to, asserted here because
        // these frames deliberately do not live in that table (see the type's
        // remarks) and so are not covered by its own block test.
        for (var frame = 0; frame < CommandLifecycle.SpinnerFrameCount; frame++)
        {
            var glyph = CommandLifecycle.Spinner(frame, utf8: true)[0];
            var point = (int)glyph;

            var allowed =
                point >= Glyphs.BoxDrawingBlockStart && point <= Glyphs.BoxDrawingBlockEnd
                || point >= Glyphs.BlockElementsBlockStart && point <= Glyphs.BlockElementsBlockEnd
                || point >= Glyphs.GeometricShapesBlockStart && point <= Glyphs.GeometricShapesBlockEnd;

            Assert.True(allowed, $"U+{point:X4} is outside the three allowed blocks.");
        }
    }

    [Fact]
    public void TheWorkingLineNamesTheCommandAndTheElapsedTime()
    {
        var line = CommandLifecycle.Working(
            "simulate", frame: 0, elapsed: TimeSpan.FromMilliseconds(1400),
            progress: null, depth: ColorDepth.None, utf8: true);

        Assert.Equal("◐ simulate  1.4s", line);
    }

    [Fact]
    public void TheWorkingLineShowsRealProgressWhenThereIsSome()
    {
        var line = CommandLifecycle.Working(
            "evaluate", frame: 1, elapsed: TimeSpan.FromSeconds(2),
            progress: new CommandProgress(12, 40, "matches"),
            depth: ColorDepth.None, utf8: true);

        Assert.Equal("◓ evaluate  12/40 matches  2.0s", line);
    }

    [Fact]
    public void ProgressIsRenderedFromTheCountersAndNeverAsAPercentage()
    {
        // A bar or a percentage would be an invention; the ratio of two real
        // counters is not. This asserts the ratio form and that no '%' appears.
        var line = CommandLifecycle.Working(
            "evaluate", frame: 0, elapsed: TimeSpan.FromSeconds(1),
            progress: new CommandProgress(1, 3, "matches"),
            depth: ColorDepth.None, utf8: true);

        Assert.Contains("1/3 matches", line);
        Assert.DoesNotContain("%", line);
    }

    [Fact]
    public void AProgressCountOfZeroIsStillProgressAndIsStillShown()
    {
        // The first tick of a run genuinely has completed nothing. Hiding the
        // ratio until it is non-zero would make the line flicker between two
        // shapes, so a zero numerator renders as 0 rather than being elided.
        var line = CommandLifecycle.Working(
            "evaluate", frame: 0, elapsed: TimeSpan.FromMilliseconds(100),
            progress: new CommandProgress(0, 40, "matches"),
            depth: ColorDepth.None, utf8: true);

        Assert.Contains("0/40 matches", line);
    }

    [Theory]
    [InlineData(0, "0.0s")]
    [InlineData(1400, "1.4s")]
    [InlineData(59999, "60.0s")]
    [InlineData(60000, "1m 00s")]
    [InlineData(62000, "1m 02s")]
    [InlineData(3600000, "60m 00s")]
    public void ElapsedTimeIsFormattedFromTheMeasuredDuration(int milliseconds, string expected)
    {
        Assert.Equal(expected, CommandLifecycle.Elapsed(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void TheSuccessLineNamesTheCommandAndSaysItSucceeded()
    {
        var line = CommandLifecycle.Succeeded("simulate", TimeSpan.FromMilliseconds(1400), ColorDepth.None);

        Assert.Equal("simulate  ok  1.4s", line);
    }

    [Fact]
    public void TheFailureLineCarriesTheRealReasonAndTheExitCode()
    {
        var line = CommandLifecycle.Failed(
            "analyze", "trajectory not found", exitCode: 1,
            elapsed: TimeSpan.FromMilliseconds(300), depth: ColorDepth.None);

        Assert.Equal("analyze  failed (exit 1)  0.3s  trajectory not found", line);
    }

    [Fact]
    public void AUsageFailureReportsItsOwnExitCode()
    {
        // Exit 2 means the command line was not runnable, which is a different
        // fact from exit 1, so the line has to show the code it actually returned
        // rather than a fixed one.
        var line = CommandLifecycle.Failed(
            "simulate", "missing required flag '--seed'", exitCode: 2,
            elapsed: TimeSpan.Zero, depth: ColorDepth.None);

        Assert.Contains("exit 2", line);
        Assert.Contains("missing required flag '--seed'", line);
    }

    [Fact]
    public void NoEscapeSequenceIsEmittedAtColourDepthNone()
    {
        // NO_COLOR resolves to ColorDepth.None, and the rule is that plain status
        // is still allowed but styling is not. Asserted for every produced line so
        // a future colour role cannot leak an escape into a NO_COLOR run.
        var lines = new[]
        {
            CommandLifecycle.Working("evaluate", 0, TimeSpan.FromSeconds(1),
                new CommandProgress(1, 2, "matches"), ColorDepth.None, utf8: true),
            CommandLifecycle.Succeeded("evaluate", TimeSpan.FromSeconds(1), ColorDepth.None),
            CommandLifecycle.Failed("evaluate", "boom", 1, TimeSpan.FromSeconds(1), ColorDepth.None),
        };

        foreach (var line in lines)
        {
            Assert.DoesNotContain('\u001b', line);
        }
    }

    [Theory]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.TrueColor)]
    public void EveryColourDepthThatReportsColourAlsoEmitsStyling(ColorDepth depth)
    {
        // The complement of the NO_COLOR rule: a depth that reports colour must
        // actually use it, or the theme silently stops reaching the terminal.
        Assert.Contains(
            '\u001b',
            CommandLifecycle.Succeeded("simulate", TimeSpan.FromSeconds(1), depth));
    }

    [Fact]
    public void StyledLinesCarryEscapeSequencesAndPlainLinesDoNot()
    {
        var styled = CommandLifecycle.Succeeded("simulate", TimeSpan.FromSeconds(1), ColorDepth.TrueColor);

        Assert.Contains('\u001b', styled);
        Assert.Contains("simulate", styled);
    }

    [Fact]
    public void TheEraseSequenceIsCarriedOnItsOwnSoNoCallerHasToKnowIt()
    {
        // Repainting a shrinking line without this leaves the tail of the longer
        // previous line visible, so the sequence belongs to the component that
        // owns the repaint rather than to each call site.
        Assert.Equal("\u001b[K", CommandLifecycle.EraseToEndOfLine);
    }

    [Fact]
    public void ProgressRejectsATotalThatCannotDescribeWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandProgress(0, 0, "matches"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandProgress(0, -1, "matches"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandProgress(1, 0, "matches"));
    }

    [Fact]
    public void ProgressRejectsACounterThatOverrunsItsBudget()
    {
        // Completed above Total means the caller's counter and its budget
        // disagree, which is a bug in the caller. Rendering it anyway would put
        // an impossible "41/40 matches" on a terminal and quietly mislead.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandProgress(41, 40, "matches"));
    }

    [Fact]
    public void ProgressRejectsAnEmptyUnit()
    {
        Assert.Throws<ArgumentException>(() => new CommandProgress(0, 40, string.Empty));
    }
}
