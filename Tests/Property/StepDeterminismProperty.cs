using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Step determinism: applying the same actions to the same immutable state
/// twice — literally re-invoking <see cref="Simulation.Step"/> — must yield
/// byte-identical next states and step results (no hidden mutable state, no
/// hash-ordering nondeterminism, per docs/adr-002.md). This is the primitive
/// underneath the trajectory-replay guarantee and the generator determinism
/// claim, asserted directly across every generated scenario and turn.
/// </summary>
public sealed class StepDeterminismPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void SameState_SameActions_YieldIdenticalOutcomes(int baseSeed)
    {
        PropertyHarness.Run("step-determinism", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var scenario = Arbitrary.Scenario(caseSeed);
            var state = scenario.CreateInitial();

            foreach (var turn in scenario.Actions)
            {
                var first = Simulation.Step(state, turn, scenario.Config);
                var second = Simulation.Step(state, turn, scenario.Config);

                Assert.Equal(PropertyEvidence.Json(first.NextState), PropertyEvidence.Json(second.NextState));
                Assert.Equal(PropertyEvidence.Json(first.Result), PropertyEvidence.Json(second.Result));
                Assert.Equal(first.Result.Info.StepNumber, second.Result.Info.StepNumber);

                state = first.NextState;
            }
        });
    }
}