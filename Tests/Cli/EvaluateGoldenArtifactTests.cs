using System.Text.Json;
using Lattice.Cli;
using Lattice.Tests.Fuzz;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// The golden expectation for the study-JSON artifact that <c>evaluate --out</c>
/// writes. This is the tripwire that makes "evaluate output is unchanged"
/// checkable: any change to the evaluation harness, the MCTS/Scout policies, the
/// paired statistics, the decision rule, or the artifact record itself turns
/// this red, and the diff names the field that moved.
///
/// <para>
/// The fixture is
/// <c>Tests/fixtures/golden_evaluate_study_artifact.json</c>, captured verbatim
/// from a real run of the exact invocation below on this checkout — no value in
/// it is transcribed or hand-written. The invocation is the smallest one that
/// still exercises the whole artifact shape: the standard scenario, the
/// development suite, and the first two seeds of the canonical
/// <c>SeedRange(1001, 50)</c> suite at the same 32 rollouts per action the
/// committed standard-suite figures were produced with. Two seeds is the
/// floor — the sample standard deviation and the 95% confidence interval are
/// undefined below it, so the <c>Statistics</c> block would not be fully
/// populated. Seeds 1001 and 1002 reproduce the corresponding <c>PerSeed</c> rows
/// of the committed <c>benchmarks/mcts_evaluation_results.json</c> exactly, so
/// the golden is a truncation of the published study rather than a different
/// experiment.
/// </para>
///
/// <para>
/// <c>--commit</c> is pinned so that <c>CommitSha</c> is part of the compared
/// payload instead of an ignored field; it records the code state
/// (<c>8c9b9f9</c>) the fixture was captured at.
/// </para>
///
/// <para>
/// Equality is asserted field by field, by raw JSON text, so a change in a
/// single digit of a single statistic fails the test. Exactly two categories of
/// field are not compared, and neither carries evaluation content:
/// <c>CreatedAtUtc</c> is <c>DateTime.UtcNow</c> and is the only field observed
/// to differ between two consecutive identical runs; <c>Runtime</c>,
/// <c>Os</c>, <c>Cores</c> and <c>Architecture</c> are host provenance read from
/// <c>RuntimeInformation</c> and <c>ProcessorCount</c>, which differ per machine
/// rather than per run. The suite runs on Linux, Windows and macOS, so pinning
/// this checkout's host facts would make the test a host-locking assertion
/// rather than an evaluation one. Both categories are still required to be
/// present and non-empty, so a field cannot be dropped unnoticed, and the
/// top-level field set is pinned exactly, so a newly added field fails here
/// rather than passing unremarked.
/// </para>
/// </summary>
public class EvaluateGoldenArtifactTests
{
    private const string GoldenFixtureName = "golden_evaluate_study_artifact.json";

    /// <summary>The code state the golden fixture was captured at.</summary>
    private const string PinnedCommit = "8c9b9f9";

    /// <summary>
    /// The exact invocation the fixture was captured from, minus <c>--out</c>.
    /// </summary>
    private static readonly string[] GoldenInvocation =
    {
        "evaluate",
        "--scenario", "standard",
        "--seed-set", "dev",
        "--seeds", "2",
        "--rollouts", "32",
        "--commit", PinnedCommit,
    };

    /// <summary>
    /// Every field the artifact carries, in serialized order. Asserting the set
    /// exactly is what stops the golden from going stale in silence when a
    /// field is added or renamed.
    /// </summary>
    private static readonly string[] ExpectedFields =
    {
        "CommitSha",
        "CreatedAtUtc",
        "Runtime",
        "Os",
        "Cores",
        "Architecture",
        "Studies",
    };

    /// <summary>
    /// The fields excluded from the equality assertion, split by reason so the
    /// exclusion stays auditable. See the class remarks.
    /// </summary>
    private static readonly string[] ClockField = { "CreatedAtUtc" };

    private static readonly string[] HostProvenanceFields = { "Runtime", "Os", "Cores", "Architecture" };

    [Fact]
    public void Evaluate_StudyArtifact_MatchesGoldenFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-evaluate-golden-{Guid.NewGuid():N}.json");
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = CliApp.Run([.. GoldenInvocation, "--out", path], stdout, stderr);

            Assert.Equal(0, exitCode);

            using var golden = JsonDocument.Parse(File.ReadAllText(FixtureResolver.Fixture(GoldenFixtureName)));
            using var fresh = JsonDocument.Parse(File.ReadAllText(path));

            // Shape lock: both documents must carry exactly the expected fields.
            Assert.Equal(ExpectedFields, FieldNames(golden.RootElement));
            Assert.Equal(ExpectedFields, FieldNames(fresh.RootElement));

            // The pinned commit is compared, not excluded.
            Assert.Equal(PinnedCommit, fresh.RootElement.GetProperty("CommitSha").GetString());

            var excluded = ClockField.Concat(HostProvenanceFields).ToArray();
            foreach (var field in ExpectedFields.Where(field => !excluded.Contains(field)))
            {
                Assert.Equal(
                    golden.RootElement.GetProperty(field).GetRawText(),
                    fresh.RootElement.GetProperty(field).GetRawText());
            }

            // The excluded fields must still be there and must still carry a
            // value: they are uncompared because they are environment, not
            // because they are allowed to go missing.
            foreach (var field in excluded)
            {
                var value = fresh.RootElement.GetProperty(field);
                Assert.True(
                    value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String,
                    $"excluded field '{field}' has unexpected kind {value.ValueKind}.");
                Assert.False(
                    value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()),
                    $"excluded field '{field}' was written empty.");
                Assert.False(
                    value.ValueKind == JsonValueKind.Number && value.GetInt32() <= 0,
                    $"excluded field '{field}' was written non-positive.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] FieldNames(JsonElement root) =>
        root.EnumerateObject().Select(property => property.Name).ToArray();
}
