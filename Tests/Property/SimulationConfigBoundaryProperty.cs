using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Boundary-value semantics for the configuration contract: hovering a random
/// walk on each documented boundary, the constructor must accept exactly the
/// values the docs promise (AgentCount 2..4, MaxTicks >= 1, Vision unbounded
/// or >= 1, TransitSpeed instant or >= 1) and reject every other edge with
/// <see cref="ArgumentOutOfRangeException"/>. The generated maps in the other
/// properties already exercise these boundary configs through real steps; the
/// corner cases here pin the contract edges directly.
/// </summary>
public sealed class SimulationConfigBoundaryPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    public void ConstructorAcceptsExactlyTheDocumentedRange(int baseSeed)
    {
        PropertyHarness.Run("config-boundaries", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng(caseSeed);

            var agentCount = new[] { 1, 2, 4, 5 }[rng.Next(0, 4)];
            Assert.Equal(agentCount is >= 2 and <= 4, Accepts(agentCount, 20, SimulationConfig.UnboundedVision, SimulationConfig.InstantTransit));

            var maxTicks = new[] { 0, 1, 2 }[rng.Next(0, 3)];
            Assert.Equal(maxTicks >= 1, Accepts(2, maxTicks, SimulationConfig.UnboundedVision, SimulationConfig.InstantTransit));

            var vision = new[] { 0, SimulationConfig.UnboundedVision, 1 }[rng.Next(0, 3)];
            Assert.Equal(vision == SimulationConfig.UnboundedVision || vision >= 1, Accepts(2, 20, vision, SimulationConfig.InstantTransit));

            var transitSpeed = new[] { -1, SimulationConfig.InstantTransit, 1 }[rng.Next(0, 3)];
            Assert.Equal(
                transitSpeed == SimulationConfig.InstantTransit || transitSpeed >= 1,
                Accepts(2, 20, SimulationConfig.UnboundedVision, transitSpeed));
        });
    }

    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    public void CornerMaps_AreAcceptedOrRejected_AsDocumented(int baseSeed)
    {
        PropertyHarness.Run("map-boundaries", baseSeed, 50, _ =>
        {
            // A single-zone map with a resource runs a full terminal episode.
            var single = new MapGraph(
                new[] { new Zone(0, new GridPoint(0, 0), MaxOccupancy: 1) },
                new[] { new ResourceNode(0, 0, new GridPoint(1, 1)) },
                Array.Empty<ChokePoint>());
            var config = new SimulationConfig(2, 1, SimulationConfig.UnboundedVision, SimulationConfig.InstantTransit);
            var state = Simulation.CreateInitial(single, config);
            var outcome = Simulation.Step(
                state,
                new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Collect, ResourceId: 0) },
                config);
            Assert.True(outcome.Result.Info.IsTerminal);
            Assert.Equal(1, outcome.NextState.Agents.Sum(agent => agent.Score));

            // An empty map is rejected by initial state construction.
            var empty = new MapGraph(Array.Empty<Zone>(), Array.Empty<ResourceNode>(), Array.Empty<ChokePoint>());
            Assert.Throws<ArgumentException>(() => Simulation.CreateInitial(empty, new SimulationConfig(2, 1)));
        });
    }

    private static bool Accepts(int agentCount, int maxTicks, int vision, int transitSpeed)
    {
        try
        {
            new SimulationConfig(agentCount, maxTicks, vision, transitSpeed);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}