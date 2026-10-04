using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The <c>lattice tui</c> command line: what is runnable, what is not, and what
/// each failure puts on which stream. Nothing here may reach the alternate screen
/// — the test host's streams are all redirected, which is exactly the case the
/// host has to refuse.
/// </summary>
public class TuiCommandTests
{
    [Fact]
    public void NoSubcommandIsAUsageErrorWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = Run("tui");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tui replay", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("record")]
    [InlineData("play")]
    public void AnUnknownSubcommandIsAUsageErrorWithNothingOnStdout(string subcommand)
    {
        var (exit, stdout, stderr) = Run("tui", subcommand);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tui replay", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpUnderTuiBehavesAsItDoesUnderEveryOtherCommand()
    {
        var (exit, stdout, _) = Run("tui", "--help");

        Assert.Equal(0, exit);
        Assert.Contains("usage: lattice <command> [options]", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayWithoutAPathIsAUsageErrorWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = Run("tui", "replay");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tui replay", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayWithTwoPathsIsAUsageErrorWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = Run("tui", "replay", "a.jsonl", "b.jsonl");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unexpected argument", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFlagIsAUsageErrorWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = Run("tui", "replay", "--speed", "2", "a.jsonl");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown flag", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRecordingIsARunnableFailureWithNothingOnStdout()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lattice-tui-missing-{Guid.NewGuid():N}.jsonl");

        var (exit, stdout, stderr) = Run("tui", "replay", missing);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("error:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotATrajectoryIsARunnableFailureWithNothingOnStdout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-tui-garbage-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "this is not a trajectory\n");

        try
        {
            var (exit, stdout, stderr) = Run("tui", "replay", path);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout);
            Assert.Contains("error:", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AValidRecordingOnRedirectedStreamsIsRefusedWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = Run("tui", "replay", Committed("site", "demo.jsonl"));

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAsciiFlagIsAcceptedOnItsOwnAndStillRefusesARedirectedRun()
    {
        var (exit, stdout, stderr) = Run("tui", "replay", "--ascii", Committed("site", "infiltration.jsonl"));

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("redirected", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoArgumentsStillPrintsTheSameUsageAndExitCode()
    {
        var (exit, stdout, stderr) = Run();

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("Lattice: An auditable multi-agent research", stderr, StringComparison.Ordinal);
        foreach (var command in new[]
                 {
                     "generate", "simulate", "render", "analyze", "replay", "benchmark", "evaluate",
                     "validate-scenario",
                 })
        {
            Assert.Contains(command, stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnknownCommandIsStillAUsageError()
    {
        var (exit, stdout, stderr) = Run("frobnicate");

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown command", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionIsUnchanged()
    {
        var (exit, stdout, _) = Run("--version");

        Assert.Equal(0, exit);
        Assert.Equal("3.1.0", stdout.Trim());
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return (CliApp.Run(args, stdout, stderr), stdout.ToString(), stderr.ToString());
    }

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}