using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-1 coverage for the <c>validate-scenario</c> subcommand itself: the
/// command's exit status, its success report, its per-fault diagnostics, and —
/// the property that makes it safe to run in a pipeline — that it has no
/// simulation or write side effect even when it succeeds.
/// </summary>
public class ValidateScenarioCommandTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Committed(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
    }

    [Theory]
    [InlineData("scenarios/collection-skirmish.json")]
    [InlineData("scenarios/bottleneck-contention.json")]
    [InlineData("scenarios/dungeon-infiltration.json")]
    [InlineData("scenarios/gated-vault-duel.json")]
    [InlineData("scenarios/skeleton-with-override.json")]
    public void AValidDescriptor_ExitsZeroAndReportsWhatItDeclares(string relative)
    {
        var (exit, stdout, stderr) = Run("validate-scenario", Committed(relative));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("is valid (schema v1)", stderr, StringComparison.Ordinal);
        Assert.Contains("sha256", stderr, StringComparison.Ordinal);
        Assert.Contains("simulation", stderr, StringComparison.Ordinal);
        Assert.Contains("victory", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidDescriptor_PrintsItsDigestOnStdErr()
    {
        var path = Committed("scenarios/collection-skirmish.json");
        var (_, _, stderr) = Run("validate-scenario", path);
        var (_, digest) = ScenarioLoader.LoadFile(path);

        Assert.Contains(digest, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOutFlag_IsAcceptedAsThePathToo()
    {
        var path = Committed("scenarios/collection-skirmish.json");
        var positional = Run("validate-scenario", path);
        var flagged = Run("validate-scenario", "--out", path);

        Assert.Equal(0, positional.ExitCode);
        Assert.Equal(0, flagged.ExitCode);
        Assert.Equal(positional.Stderr, flagged.Stderr);
    }

    [Fact]
    public void AnInvalidDescriptor_ExitsNonZeroAndNamesTheField()
    {
        var (exit, _, stderr) = Run(
            "validate-scenario", Committed("Tests/fixtures/scenarios/invalid/unknown_field.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("scenario error: Weather:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPath_ExitsNonZero()
    {
        var (exit, _, stderr) = Run("validate-scenario");

        Assert.NotEqual(0, exit);
        Assert.Contains("missing scenario path", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedExtraPositional_ExitsNonZero()
    {
        // Only the last extra positional is named, and it is named rather than
        // silently dropped: a caller that passed two paths meant something.
        var (exit, _, stderr) = Run(
            "validate-scenario", Committed("scenarios/collection-skirmish.json"), "extra.json");

        Assert.NotEqual(0, exit);
        Assert.Contains("extra.json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFlag_IsRejected()
    {
        var (exit, _, stderr) = Run("validate-scenario", "--strict", Committed("scenarios/collection-skirmish.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("unknown flag '--strict'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentFile_ExitsNonZeroWithoutAnUnhandledFault()
    {
        var (exit, _, stderr) = Run("validate-scenario", Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulValidation_WritesNoArtifactAndNoTrajectory()
    {
        // The command reads the descriptor and reports on stderr. It must not
        // write an artifact anywhere, and must not emit a trajectory on stdout.
        // Checked by snapshotting the temp directory's file set around the call
        // and by asserting stdout stayed empty.
        var before = new HashSet<string>(Directory.GetFiles(Path.GetTempPath()));

        var (exit, stdout, stderr) = Run("validate-scenario", Committed("scenarios/collection-skirmish.json"));

        var after = Directory.GetFiles(Path.GetTempPath());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("is valid", stderr, StringComparison.Ordinal);
        Assert.Empty(after.Except(before));
    }

    [Fact]
    public void TheCommandIsDispatchedByName()
    {
        var (exit, stdout, _) = Run("--help");

        Assert.Equal(0, exit);
        Assert.Contains("validate-scenario", stdout, StringComparison.Ordinal);
    }
}
