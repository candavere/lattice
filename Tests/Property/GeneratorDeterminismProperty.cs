using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Cross-seed generator determinism: regenerating a scenario from the same
/// seed twice must yield bit-identical artifacts — map, config, rules, and
/// actions — because every draw is a pure function of the seed. This is what
/// makes seeded case reproduction exact ("print the failing seed, replay the
/// exact scenario"), and it guards the whole suite against a generator that
/// secretly depends on wall-clock or dictionary-ordering state.
/// </summary>
public sealed class GeneratorDeterminismPropertyTests
{
    [Theory]
    [InlineData(20260920)]
    [InlineData(313373)]
    [InlineData(-123456789)]
    public void SameSeed_Twice_ReproducesTheExactScenario(int baseSeed)
    {
        PropertyHarness.Run("generator-determinism", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var first = Arbitrary.Scenario(caseSeed);
            var second = Arbitrary.Scenario(caseSeed);

            Assert.Equal(PropertyEvidence.Json(first.Map), PropertyEvidence.Json(second.Map));
            Assert.Equal(PropertyEvidence.Json(first.Config), PropertyEvidence.Json(second.Config));
            Assert.Equal(PropertyEvidence.Json(first.Rules!), PropertyEvidence.Json(second.Rules!));
            Assert.Equal(PropertyEvidence.Json(first.Actions), PropertyEvidence.Json(second.Actions));
        });
    }
}