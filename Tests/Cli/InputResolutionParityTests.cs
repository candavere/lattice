using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Shell parity for the extracted pure resolvers: the CLI's missing-input
/// text, both-supplied arity text, output and exit codes are unchanged by the
/// extraction. Runs the real <c>CliApp.Run</c>, not the form.
/// </summary>
public class InputResolutionParityTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ReplayMissingIsUsageWithTheCliText()
    {
        var (exit, stdout, stderr) = Run("replay");

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("missing trajectory path (pass a positional path or --trajectory <file>).", stderr);
    }

    [Fact]
    public void ReplayBothSuppliedIsUsageNamingThePositional()
    {
        var (exit, stdout, stderr) = Run("replay", "a.jsonl", "--trajectory", "b.jsonl");

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unexpected argument 'a.jsonl'.", stderr);
    }

    [Fact]
    public void ValidateMissingIsUsageWithTheCliText()
    {
        var (exit, stdout, stderr) = Run("validate-scenario");

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("missing scenario path (pass a positional path or --out <file>).", stderr);
    }

    [Fact]
    public void ValidateBothSuppliedIsUsageNamingThePositional()
    {
        var (exit, stdout, stderr) = Run("validate-scenario", "a.json", "--out", "b.json");

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unexpected argument 'a.json'.", stderr);
    }

    [Fact]
    public void ResolversAgreeWithTheCliOnPresence()
    {
        Assert.Equal(
            "missing trajectory path (pass a positional path or --trajectory <file>).",
            Probe(() => CliInputResolution.ResolveReplayTrajectory(new(), [])));
        Assert.Equal(
            "missing scenario path (pass a positional path or --out <file>).",
            Probe(() => CliInputResolution.ResolveValidateScenarioPath(new(), [])));

        Assert.Equal(
            "a.jsonl",
            CliInputResolution.ResolveReplayTrajectory(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["--trajectory"] = "a.jsonl" }, []));
        Assert.Equal(
            "b.json",
            CliInputResolution.ResolveValidateScenarioPath(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["--out"] = "b.json" }, []));
    }

    private static string? Probe(Func<object?> action)
    {
        try
        {
            action();
            return null;
        }
        catch (UsageError error)
        {
            return error.Message;
        }
    }
}
