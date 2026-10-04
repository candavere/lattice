using System.Text;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Drives the lifecycle reporter through injected sinks and a manual clock, so
/// no test needs a terminal, a real timer, or a real command. What is being
/// pinned is the decision to stay silent, the repaint discipline, and the
/// guarantee that the closing line carries the exit code the command returned.
/// </summary>
public class WorkingIndicatorTests
{
    /// <summary>A sink that records every write, and whether it was a repaint.</summary>
    private sealed class RecordingSink : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Every character written, in order.</summary>
        public string Text => _text.ToString();

        /// <summary>How many separate writes arrived, so repaints can be counted.</summary>
        public int Writes { get; private set; }

        public override void Write(char value)
        {
            Writes++;
            _text.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var character in value)
            {
                Write(character);
            }
        }
    }

    /// <summary>A clock the test advances by hand, so no test sleeps.</summary>
    private sealed class ManualClock
    {
        private TimeSpan _now;

        public ManualClock(TimeSpan? start = null) => _now = start ?? TimeSpan.Zero;

        public TimeSpan Now => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static TerminalEnvironment Terminal(
        string? colorTerm = "truecolor",
        string? term = "xterm-256color",
        string? noColor = null) =>
        new(colorTerm, term, noColor, "en_US.UTF-8", InputRedirected: false, OutputRedirected: false, 120, 40);

    private static WorkingIndicator Build(
        TextWriter sink,
        TerminalEnvironment? environment = null,
        bool quiet = false) =>
        new(
            "evaluate",
            sink,
            CapabilityDetector.Detect(environment ?? Terminal()),
            quiet);

    [Fact]
    public void TheIndicatorStaysSilentWhenStderrIsRedirected()
    {
        // The single most important property: a redirected stderr is a file, a
        // pipe or a test buffer, and a carriage return there is corruption.
        var sink = new RecordingSink();
        var environment = Terminal() with { OutputRedirected = true };

        using (var indicator = Build(sink, environment))
        {
            indicator.Repaint(0);
            indicator.Succeed();
        }

        Assert.Equal(string.Empty, sink.Text);
    }

    [Fact]
    public void TheIndicatorStaysSilentUnderQuiet()
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink, quiet: true))
        {
            indicator.Repaint(0);
            indicator.Succeed();
        }

        Assert.Equal(string.Empty, sink.Text);
    }

    [Fact]
    public void AttachingACounterPaintsTheRatioWithoutWaitingForTheTimer()
    {
        var sink = new RecordingSink();
        var clock = new ManualClock();

        // A minute-long interval: the repaint timer cannot possibly fire inside
        // this test, so whatever reaches the sink was painted by the call that
        // attached the counter. That is what makes the assertion deterministic
        // rather than a bet that a thread-pool callback got scheduled.
        using var indicator = new WorkingIndicator(
            "evaluate",
            sink,
            CapabilityDetector.Detect(Terminal()),
            quiet: false,
            clock: () => clock.Now,
            repaintIntervalMs: 60_000);

        indicator.Start();
        indicator.UseProgress(() => new CommandProgress(3, 6, "matches"));

        Assert.Contains("3/6 matches", sink.Text);
    }

    [Fact]
    public void StartPaintsTheFirstFrameWithoutWaitingForTheTimer()
    {
        var sink = new RecordingSink();
        var clock = new ManualClock();

        // Same reasoning as above: the interval rules the timer out entirely.
        using var indicator = new WorkingIndicator(
            "simulate",
            sink,
            CapabilityDetector.Detect(Terminal()),
            quiet: false,
            clock: () => clock.Now,
            repaintIntervalMs: 60_000);

        // Elapsed is measured from construction, so the clock has to move after
        // the indicator is built rather than before it.
        clock.Advance(TimeSpan.FromMilliseconds(1400));
        indicator.Start();

        Assert.Contains(CommandLifecycle.Spinner(0, true), sink.Text);
        Assert.Contains("1.4s", sink.Text);

        // No counter has been attached yet, so no ratio may be claimed.
        Assert.DoesNotContain("matches", sink.Text);
    }

    [Fact]
    public void TheIndicatorStaysSilentWhenNoColorDisablesStyling()
    {
        // NO_COLOR means no styling, but the plan still allows plain status. So
        // this case is the one place a line may be written without escapes.
        var sink = new RecordingSink();
        var environment = Terminal(noColor: "1");

        using (var indicator = Build(sink, environment))
        {
            indicator.Repaint(0);
            indicator.Succeed();
        }

        Assert.DoesNotContain('\u001b', sink.Text);
    }

    [Fact]
    public void EachRepaintRewritesTheLineInPlaceRatherThanAppending()
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Repaint(0);
            indicator.Repaint(1);
            indicator.Succeed();
        }

        // Both frames appear, each exactly once, and the whole run ends the row only
        // once. Repainting in place means the two working frames share a row and
        // the closing line terminates it; appending would leave a newline per
        // frame, which is the familiar pile of near-identical spinner lines.
        Assert.Equal(1, CountOccurrences(sink.Text, CommandLifecycle.Spinner(0, true)));
        Assert.Equal(1, CountOccurrences(sink.Text, CommandLifecycle.Spinner(1, true)));
        Assert.Equal(1, CountOccurrences(sink.Text, "\n"));
        Assert.EndsWith("\n", sink.Text);
    }

    [Fact]
    public void TheWorkingLineIsErasedBeforeTheClosingLineReplacesIt()
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Repaint(0);
            indicator.Succeed();
        }

        // Without the erase, the tail of the spinner line would survive on the
        // same row as the success line wherever the second is shorter.
        Assert.Contains(CommandLifecycle.EraseToEndOfLine, sink.Text);
    }

    [Fact]
    public void RepeatedRepaintsAtTheSameFrameDoNotRedraw()
    {
        // The plan caps redraw on change. Two identical frames are one write.
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Repaint(0);
            indicator.Repaint(0);
            indicator.Repaint(0);
            indicator.Succeed();
        }

        // Three repaints of one frame, so one draw. The spinner glyph appearing
        // twice would mean the identical frame was written more than once.
        Assert.Equal(1, CountOccurrences(sink.Text, CommandLifecycle.Spinner(0, true)));
        Assert.Equal(1, CountOccurrences(sink.Text, "\n"));
    }

    [Fact]
    public void TheElapsedTimeOnTheLineIsTheMeasuredOne()
    {
        var sink = new RecordingSink();
        var clock = new ManualClock(TimeSpan.FromMilliseconds(250));

        using (var indicator = new WorkingIndicator("simulate", sink,
                   CapabilityDetector.Detect(Terminal()), quiet: false, clock: () => clock.Now))
        {
            clock.Advance(TimeSpan.FromMilliseconds(1400));
            indicator.Repaint(0);
            indicator.Succeed();
        }

        // The clock starts at 250ms and advances 1400ms, so the measured
        // duration is the difference between the two readings, not the sum.
        Assert.Contains("1.4s", sink.Text);
    }

    [Fact]
    public void TheWorkingLineCarriesTheProgressTheCallerReported()
    {
        var sink = new RecordingSink();
        var clock = new ManualClock();

        using (var indicator = new WorkingIndicator("evaluate", sink,
                   CapabilityDetector.Detect(Terminal()), quiet: false, clock: () => clock.Now))
        {
            indicator.Repaint(0, new CommandProgress(3, 10, "matches"));
            indicator.Succeed();
        }

        Assert.Contains("3/10 matches", sink.Text);
    }

    [Fact]
    public void ACommandWithNoCounterShowsNoRatioAtAll()
    {
        // The plan is explicit: no counter means the spinner and the elapsed
        // time, never an invented number.
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Repaint(0);
            indicator.Succeed();
        }

        Assert.DoesNotContain("/", sink.Text.Replace(CommandLifecycle.EraseToEndOfLine, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void TheSuccessLineReportsTheExitCodeZeroSucceeded()
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Succeed();
        }

        Assert.Contains("ok", sink.Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheFailureLineReportsTheExitCodeTheCommandActuallyReturned(int exitCode)
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Fail("trajectory not found", exitCode);
        }

        Assert.Contains($"exit {exitCode}", sink.Text);
        Assert.Contains("trajectory not found", sink.Text);
    }

    [Fact]
    public void ClosingTwiceDoesNotWriteASecondClosingLine()
    {
        // Succeed() in a finally after an explicit Fail() is the shape the CLI
        // uses, so the second close has to be a no-op rather than a second line.
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            indicator.Fail("boom", 1);
            indicator.Succeed();
        }

        Assert.Equal(1, CountOccurrences(sink.Text, "failed"));
        Assert.DoesNotContain("ok", sink.Text);
    }

    [Fact]
    public void DisposingWithoutClosingStillRestoresTheLine()
    {
        // An exception between the last repaint and the close must not leave a
        // half-drawn spinner row on the terminal.
        var sink = new RecordingSink();

        var indicator = Build(sink);
        indicator.Repaint(0);
        indicator.Dispose();

        Assert.EndsWith("\n", sink.Text);
    }

    [Fact]
    public void NothingIsWrittenBeforeTheFirstRepaint()
    {
        var sink = new RecordingSink();

        using (var indicator = Build(sink))
        {
            _ = indicator.ToString();
        }

        Assert.Equal(string.Empty, sink.Text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
