using Lattice.Cli;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of <see cref="CliApp.Run"/> argument parsing: adversarial
/// argument vectors (unknown flags, dangling flags, malformed seeds and step
/// counts, invalid scenario/agent/format identifiers, mutated rule and replay
/// files, missing paths) must be rejected with graceful typed failures and an
/// exit code of exactly 0 or 1. No argument vector may cause an uncaught
/// exception to escape <c>Run</c>, because a real CLI crash surfaces as a
/// non-zero runtime failure where <c>Report</c> should have produced a typed
/// message instead. Execution-heavy branches (<c>--agent mcts</c>,
/// <c>benchmark</c>, <c>evaluate</c>, and strict <c>--min-fairness</c> retry
/// loops) are excluded so the suite stays runtime-bounded; those surfaces are
/// covered by dedicated integration tests in the CLI suite.
/// </summary>
public sealed class CliArgumentFuzzTests
{
    [Theory]
    [InlineData(1010)]
    [InlineData(987654)]
    public void CliApp_RejectsOrAcceptsEveryArgumentVector_WithoutEscapingFaults(int baseSeed)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"lattice-fuzz-cli-{baseSeed}");
        Directory.CreateDirectory(workspace);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            FuzzHarness.Run("cli-arguments", baseSeed, FuzzHarness.DefaultIterations, caseSeed =>
            {
                var rng = new Rng(caseSeed);
                var vector = CliArgumentVectors.Generate(rng, caseSeed, workspace, (path, content) =>
                {
                    if (File.Exists(path))
                    {
                        return;
                    }

                    File.WriteAllText(path, content);
                });

                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();

                var exitCode = -1;
                FuzzHarness.ExpectGraceful("CliApp.Run(arguments)", () =>
                {
                    exitCode = CliApp.Run(vector, stdout, stderr);
                });

                if (exitCode is not 0 and not 1)
                {
                    throw new FuzzCheckException($"CliApp.Run returned unexpected exit code {exitCode} for [{string.Join(' ', vector)}].");
                }
            });
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}