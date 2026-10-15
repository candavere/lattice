using System.Diagnostics;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage 1: bounded streaming capture. Each stream is drained incrementally under
/// a memory cap with overflow spooled to a file under a disk cap; the pipes are
/// always drained to EOF so the child can never block, and whatever was dropped
/// is counted exactly. The result names the effective command quoted from the
/// argv actually passed to the child, with the working directory beside it.
/// </summary>
public class SlashConsoleCaptureTests
{
    [Fact]
    public async Task ShortStreamIsKeptWholeAndComplete()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader("abc"), 32768, spool, 1024);

        Assert.Equal("abc", captured.MemoryText);
        Assert.Equal(3, captured.TotalChars);
        Assert.Equal(0, captured.DroppedChars);
        Assert.Equal(0, captured.SpooledChars);
        Assert.Null(captured.SpoolPath);
        Assert.False(captured.Truncated);
        Assert.False(captured.SpoolFailed);
        Assert.True(captured.MemoryWhole);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task EmptyStreamIsComplete()
    {
        using var temp = new TempDir();

        var captured = await BoundedStreamCapture.CaptureAsync(
            new StringReader(string.Empty), 16, Path.Combine(temp.Path, "s.log"), 1024);

        Assert.Equal(string.Empty, captured.MemoryText);
        Assert.Equal(0, captured.TotalChars);
        Assert.True(captured.MemoryWhole);
    }

    [Fact]
    public async Task OverflowPastMemoryCapSpoolsRemainderAndReconstructsFully()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");
        var input = new string('x', 100);

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(input), 16, spool, 1024);

        Assert.Equal(new string('x', 16), captured.MemoryText);
        Assert.Equal(100, captured.TotalChars);
        Assert.Equal(0, captured.DroppedChars);
        Assert.Equal(84, captured.SpooledChars);
        Assert.Equal(spool, captured.SpoolPath);
        Assert.True(captured.Truncated);
        Assert.False(captured.MemoryWhole);
        Assert.True(captured.FullOutputRetained);
        Assert.False(captured.SpoolFailed);
        Assert.Equal(input, captured.MemoryText + await File.ReadAllTextAsync(spool));
    }

    [Fact]
    public async Task OverflowPastDiskCapDropsExactlyAndKeepsDraining()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");
        var input = new string('y', 100);

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(input), 8, spool, 10);

        Assert.Equal(new string('y', 8), captured.MemoryText);
        Assert.Equal(100, captured.TotalChars);
        Assert.Equal(10, captured.SpooledChars);
        Assert.Equal(82, captured.DroppedChars);
        Assert.True(captured.Truncated);
        Assert.False(captured.MemoryWhole);
        Assert.False(captured.FullOutputRetained);
        Assert.Equal(input, captured.MemoryText + await File.ReadAllTextAsync(spool) + new string('y', 82));
    }

    [Fact]
    public async Task NoSpoolPathMeansOverflowIsDroppedButStreamStillDrained()
    {
        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(new string('z', 50)), 8, null, 1024);

        Assert.Equal(50, captured.TotalChars);
        Assert.Equal(42, captured.DroppedChars);
        Assert.Null(captured.SpoolPath);
        Assert.True(captured.Truncated);
        Assert.Equal(
            "stdout exceeded the memory cap (8 characters); spooling off; dropped 42 characters.",
            CaptureLimits.Describe("stdout", captured, 8, 1024));
    }

    [Fact]
    public async Task FailedSpoolWriteMarksFailureAndDropsWithoutPromising()
    {
        using var temp = new TempDir();
        var badSpool = Path.Combine(temp.Path, "\0invalid.log");

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(new string('w', 50)), 8, badSpool, 1024);

        Assert.Equal(50, captured.TotalChars);
        Assert.Equal(42, captured.DroppedChars);
        Assert.True(captured.SpoolFailed);
        Assert.False(captured.MemoryWhole);
        Assert.False(captured.FullOutputRetained);
        Assert.True(captured.Truncated);
    }

    [Fact]
    public async Task DescribeIsNullWhenTheStreamIsWhole()
    {
        using var temp = new TempDir();

        var captured = await BoundedStreamCapture.CaptureAsync(
            new StringReader("abc"), 32768, Path.Combine(temp.Path, "s.log"), 1048576);

        Assert.Null(CaptureLimits.Describe("stdout", captured, 32768, 1048576));
    }

    [Fact]
    public async Task DescribeNamesTheSpoolFileWhenTheMemoryCapIsHit()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(new string('x', 100)), 16, spool, 1024);

        Assert.Equal(
            $"stdout exceeded the memory cap (16 characters); full output in {spool}.",
            CaptureLimits.Describe("stdout", captured, 16, 1024));
    }

    [Fact]
    public async Task DescribeNamesDroppedCountsWhenTheDiskCapIsHit()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(new string('y', 100)), 8, spool, 10);

        Assert.Equal(
            $"stdout exceeded the memory cap (8 characters) and the disk cap (10 characters); " +
            $"kept 8 in memory and 10 spooled in {spool}; dropped 82 characters.",
            CaptureLimits.Describe("stdout", captured, 8, 10));
    }

    [Fact]
    public async Task DescribeNamesTheFailureWhenTheSpoolWriteFails()
    {
        using var temp = new TempDir();
        var badSpool = Path.Combine(temp.Path, "\0invalid.log");

        var captured = await BoundedStreamCapture.CaptureAsync(new StringReader(new string('w', 50)), 8, badSpool, 1024);

        Assert.Equal(
            "stdout spool write failed; kept 8 characters in memory; dropped 42 characters; output is partial.",
            CaptureLimits.Describe("stdout", captured, 8, 1024));
    }

    [Fact]
    public void QuoteCommandLeavesSimpleArgvUnquoted()
    {
        Assert.Equal(
            "lattice generate --seed 42",
            ArgvDisplay.QuoteCommand(["lattice", "generate", "--seed", "42"]));
    }

    [Fact]
    public void QuoteCommandQuotesSpacesQuotesAndEmpty()
    {
        Assert.Equal(
            "prog 'a b' 'c'\\''d' ''",
            ArgvDisplay.QuoteCommand(["prog", "a b", "c'd", string.Empty]));
    }

    [Fact]
    public void QuoteCommandPassesUnicodeThrough()
    {
        Assert.Equal("lattice --out 'täst-😀.json'", ArgvDisplay.QuoteCommand(["lattice", "--out", "täst-😀.json"]));
    }

    [Fact]
    public void RunnerReportsEffectiveCommandAndWorkingDirectory()
    {
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();
        string[] argv = ["--version"];

        var finished = runner.StartCaptured(
            new CapturedRunRequest(argv, "lattice --version", temp.Path, ChildInvocation())).Finish();

        Assert.Equal(0, finished.ExitCode);
        Assert.Equal(temp.Path, finished.WorkingDirectory);
        Assert.Contains("--version", finished.EffectiveCommand, StringComparison.Ordinal);
        Assert.True(finished.StdoutMemoryWhole);
        Assert.True(finished.StderrMemoryWhole);
        Assert.False(finished.Truncated);
    }

    [Fact]
    public async Task RunnerLargeOutputKeepsHeadSpoolsRestWithoutDeadlock()
    {
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();
        string[] argv = ["simulate", "--seed", "1", "--steps", "200"];

        var run = runner.StartCaptured(
            new CapturedRunRequest(argv, "lattice simulate", temp.Path, ChildInvocation()));
        var finished = await Task.Run(() => run.Finish()).WaitAsync(TimeSpan.FromSeconds(180));

        Assert.Equal(0, finished.ExitCode);
        Assert.True(finished.Truncated);
        Assert.Equal(SlashConsoleProcessRunner.CapturedMaxCharsPerStream, finished.Stdout.Length);
        Assert.NotNull(finished.StdoutSpoolPath);
        var full = finished.Stdout + await File.ReadAllTextAsync(finished.StdoutSpoolPath!);
        Assert.Equal(0, finished.StdoutDroppedChars);

        using var reader = new StringReader(full);
        var recording = TrajectoryReader.Read(reader);
        Assert.True(recording.Steps.Length > 0);
        Assert.Equal(1UL, recording.Header.Seed);
    }

    private static ProcessInvocation ChildInvocation() =>
        ProcessInvocationResolver.ForPaths("dotnet", LatticeDll());

    private static string LatticeDll()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "lattice.dll");
        Assert.True(File.Exists(dll), $"the CLI was not found at '{dll}'.");
        return dll;
    }

    private sealed class TempDir : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lattice-capture-" + Guid.NewGuid().ToString("N"));

        internal TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
