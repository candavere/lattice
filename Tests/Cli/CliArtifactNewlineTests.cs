using System.Text;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// The cross-platform byte-determinism guard for JSON artifacts written by the
/// CLI. This is the test that fails on <c>windows-latest</c> when an artifact's
/// newlines are not pinned.
/// </summary>
/// <remarks>
/// <para>
/// The golden-artifact comparison in
/// <see cref="EvaluateGoldenArtifactTests"/> is, by design, insensitive to
/// insignificant whitespace, so it cannot be the thing that notices a
/// platform-dependent newline. That insensitivity is correct — it is what lets
/// one golden serve three operating systems — but it means a CRLF artifact
/// would otherwise reach a consumer whose fixtures are LF-pinned by
/// <c>.gitattributes</c> and only break there. These tests close that gap by
/// asserting the invariant directly on the bytes on disk: a CLI-written JSON
/// artifact contains no <c>CR</c> byte at all.
/// </para>
///
/// <para>
/// The assertion is on raw bytes rather than on a parsed document precisely
/// because the failure mode is invisible after parsing. Every JSON reader
/// accepts CRLF happily; the damage is done to whatever diffs, hashes or
/// byte-compares the file afterwards.
/// </para>
///
/// <para>
/// Both indented artifact writers are covered — <c>evaluate</c> and
/// <c>benchmark</c> — because both go through the same
/// <c>JsonSerializerOptions { WriteIndented = true }</c> path that made Windows
/// emit CRLF. A test that guarded only one of them would let the other regress
/// silently.
/// </para>
/// </remarks>
public class CliArtifactNewlineTests
{
    private const byte CarriageReturn = 0x0D;
    private const byte LineFeed = 0x0A;

    /// <summary>
    /// The smallest <c>evaluate</c> invocation that still writes the full
    /// artifact shape. Two seeds is the floor at which the sample standard
    /// deviation and confidence interval are defined; the artifact being tested
    /// here is the file's byte layout, not the statistics in it.
    /// </summary>
    private static readonly string[] EvaluateInvocation =
    {
        "evaluate",
        "--scenario", "standard",
        "--seed-set", "dev",
        "--seeds", "2",
        "--rollouts", "32",
        "--commit", "newline-guard",
    };

    /// <summary>
    /// A deliberately tiny benchmark. The newline writer is reached on every
    /// invocation regardless of workload size, so shrinking the run keeps this
    /// guard cheap without weakening it.
    /// </summary>
    private static readonly string[] BenchmarkInvocation =
    {
        "benchmark",
        "--runs", "1",
        "--warmup", "40",
        "--steps", "40",
    };

    [Theory]
    [InlineData("evaluate")]
    [InlineData("benchmark")]
    public void Artifact_WrittenByCli_ContainsNoCarriageReturnByte(string command)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-newline-guard-{Guid.NewGuid():N}.json");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var invocation = command == "evaluate" ? EvaluateInvocation : BenchmarkInvocation;

            var exitCode = CliApp.Run([.. invocation, "--out", path], stdout, stderr);

            Assert.Equal(0, exitCode);

            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain(CarriageReturn, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Pins the rest of the artifact's byte layout: it is LF-terminated text,
    /// and it is genuinely indented JSON rather than a single collapsed line.
    /// Without this, "no CR" would also be satisfied by an artifact that had
    /// stopped being indented, which would be a silent format regression.
    /// </summary>
    [Fact]
    public void Evaluate_Artifact_IsIndentedAndLfTerminated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-newline-layout-{Guid.NewGuid():N}.json");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = CliApp.Run([.. EvaluateInvocation, "--out", path], stdout, stderr);

            Assert.Equal(0, exitCode);

            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            Assert.Equal(LineFeed, bytes[^1]);

            // Indented, not collapsed: the artifact's first line is the opening
            // brace alone and the second line is indented by two spaces.
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            Assert.Equal("{", lines[0]);
            Assert.StartsWith("  ", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
