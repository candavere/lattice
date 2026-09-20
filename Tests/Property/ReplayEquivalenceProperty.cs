using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Replay fidelity, stated as one property: whatever the seeded generators
/// throw at the recording layer, a recorded trajectory must reconstruct into
/// the identical recording and replay byte-for-byte — matching schema and
/// header, contiguous step numbers, final metrics, no action-space violations,
/// and a <see cref="TrajectoryWriter.Write"/> of the read-back reproducing the
/// original text character-for-character. This pins the v2 header format
/// (including the optional dynamic-rules field) and the engine's determinism
/// contract jointly.
/// </summary>
public sealed class ReplayEquivalencePropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void RecordedTrajectory_ReconstructsAndReplays_Identically(int baseSeed)
    {
        PropertyHarness.Run("replay-equivalence", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var scenario = Arbitrary.Scenario(caseSeed);

            var original = new StringWriter();
            var recording = TrajectoryWriter.Record(
                scenario.Map,
                scenario.Config,
                (ulong)(uint)caseSeed,
                scenario.Actions,
                original,
                rules: scenario.Rules);
            var originalText = original.ToString();
            Assert.NotEmpty(originalText);
            Assert.NotEmpty(recording.Steps);

            using var reader = new StringReader(originalText);
            var roundTripped = TrajectoryReader.Read(reader);

            // Schema, header, and recording identity survive the round trip.
            Assert.Equal(TrajectorySchema.CurrentVersion, roundTripped.Header.SchemaVersion);
            Assert.Equal(recording.Steps.Length, roundTripped.Steps.Length);
            Assert.Equal(PropertyEvidence.Json(recording.Header), PropertyEvidence.Json(roundTripped.Header));
            Assert.Equal(PropertyEvidence.Json(recording.Final), PropertyEvidence.Json(roundTripped.Final));

            // Replay: action-space validity, step-count agreement, per-step
            // serialized-result equivalence (the canonical replay gate).
            var problems = TrajectoryReplay.Verify(roundTripped);
            Assert.True(problems.Count == 0, string.Join("\n", problems));

            // Rewriting the read-back must reproduce the original bytes.
            var rewritten = new StringWriter();
            TrajectoryWriter.Write(roundTripped, rewritten);
            Assert.Equal(originalText, rewritten.ToString());

            for (var s = 0; s < roundTripped.Steps.Length; s++)
            {
                Assert.Equal(s + 1, roundTripped.Steps[s].StepNumber);
            }
        });
    }
}