using System.IO;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Pins the headless-purity contract surviving the TUI removal: a redirected
/// stderr (the default for every scripted/CI caller) produces no lifecycle
/// bytes, stdout is byte-stable, and exit codes are preserved. Ported from the
/// deleted interactive-only Tests/Tui/CommandLifecycleCliTests.cs, restricted to
/// the inert (redirected) path that survives the removal.
/// </summary>
public class CommandLifecycleScopeTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void AnalyzeWritesNothingToStderrBeyondItsOwnOutput()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Lattice.sln")))
            root = root.Parent;
        var trajectoryPath = Path.Combine(root!.FullName, "Tests", "fixtures", "golden_trajectory.jsonl");
        var result = Run("analyze", "--trajectory", trajectoryPath);

        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Err);
        Assert.Contains("Lattice Tactical Report", result.Out);
    }

    [Fact]
    public void SimulateWritesNoLifecycleOutputToARedirectedStderr()
    {
        var result = Run("simulate", "--seed", "7", "--steps", "5", "--quiet");
        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain('\u001b', result.Err);
    }

    [Fact]
    public void AFailingCommandStillReportsOnlyItsExistingStderrLine()
    {
        var result = Run("analyze", "--trajectory", Path.Combine(Path.GetTempPath(), "no-such-file.jsonl"));
        Assert.Equal(1, result.Exit);
        Assert.StartsWith("error:", result.Err);
        Assert.DoesNotContain('\u001b', result.Err);
    }

    [Fact]
    public void ExitCodesAreUnaffectedByTheLifecycleWork()
    {
        Assert.Equal(2, Run("simulate").Exit);
        Assert.Equal(2, Run("nonsense-verb").Exit);
    }
}
