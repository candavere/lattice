using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Generator well-formedness: the seeded generators must stay inside the
/// engine's documented contract, or the invariant properties above would be
/// testing the generator instead of the engine. States as a property: every
/// generated topology is connected with well-formed zone/choke identities and
/// at-least-one capacity for every agent's home; every config lies in the
/// documented range; every rule targets a real choke; and every episode turn
/// is fully in the action space and within the tick budget.
/// </summary>
public sealed class GeneratorWellformednessPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void GeneratedScenario_IsWithinTheEngineContract(int baseSeed)
    {
        PropertyHarness.Run("generator-wellformedness", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng(caseSeed);
            var agentCount = Arbitrary.AgentCount(rng);
            var map = Arbitrary.Topology(rng, agentCount);
            var config = Arbitrary.Config(rng, agentCount);
            var rules = Arbitrary.DynamicRules(rng, map);
            var actions = Arbitrary.Episode(rng, map, agentCount, config.MaxTicks);

            AssertTopology(map, agentCount);
            AssertConfig(config);
            AssertRules(rules, map);
            AssertActions(actions, map, agentCount, config.MaxTicks);
        });
    }

    private static void AssertTopology(MapGraph map, int agentCount)
    {
        Assert.True(map.Zones.Length >= agentCount, "generator must place at least one agent per zone initially");
        for (var z = 0; z < map.Zones.Length; z++)
        {
            Assert.Equal(z, map.Zones[z].Id);
            Assert.InRange(map.Zones[z].MaxOccupancy, 1, int.MaxValue);
            Assert.InRange(map.Zones[z].Position.X, 0, 1000);
            Assert.InRange(map.Zones[z].Position.Y, 0, 1000);
        }

        for (var r = 0; r < map.Resources.Length; r++)
        {
            Assert.Equal(r, map.Resources[r].Id);
            Assert.InRange(map.Resources[r].ZoneId, 0, map.Zones.Length - 1);
        }

        for (var c = 0; c < map.ChokePoints.Length; c++)
        {
            var choke = map.ChokePoints[c];
            Assert.Equal(c, choke.Id);
            Assert.InRange(choke.FromZoneId, 0, map.Zones.Length - 1);
            Assert.InRange(choke.ToZoneId, 0, map.Zones.Length - 1);
            Assert.NotEqual(choke.FromZoneId, choke.ToZoneId);
        }

        // No duplicate undirected edges.
        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            for (var j = i + 1; j < map.ChokePoints.Length; j++)
            {
                Assert.False(PropertyEvidence.IsSameEdge(map.ChokePoints[j], map.ChokePoints[i].FromZoneId, map.ChokePoints[i].ToZoneId));
            }
        }

        // Connectivity: every zone reachable from zone 0 (the spanning tree).
        var distances = PropertyEvidence.HopDistances(map, 0);
        for (var z = 0; z < map.Zones.Length; z++)
        {
            Assert.True(distances[z] >= 0, $"zone {z} is unreachable from zone 0");
        }
    }

    private static void AssertConfig(SimulationConfig config)
    {
        Assert.InRange(config.AgentCount, 2, 4);
        Assert.InRange(config.MaxTicks, 1, int.MaxValue);
        Assert.True(
            config.Vision == SimulationConfig.UnboundedVision || config.Vision >= 1,
            $"vision {config.Vision} outside documented range");
        Assert.True(
            config.TransitSpeed == SimulationConfig.InstantTransit || config.TransitSpeed >= 1,
            $"transit speed {config.TransitSpeed} outside documented range");
    }

    private static void AssertRules(DynamicMapRuleSet? rules, MapGraph map)
    {
        if (rules is null)
        {
            return;
        }

        // Every rule must target a choke that actually exists on the map (the
        // rule set's own validation enforces exactly this).
        rules.ValidateFor(map);
    }

    private static void AssertActions(AgentAction[][] actions, MapGraph map, int agentCount, int maxTicks)
    {
        Assert.InRange(actions.Length, 1, Math.Min(maxTicks, 26));
        foreach (var turn in actions)
        {
            Assert.Equal(agentCount, turn.Length);
            foreach (var action in turn)
            {
                Assert.Empty(ActionSpace.Validate(action, map));
                if (action.Kind == ActionKind.Collect)
                {
                    Assert.InRange(action.ResourceId, 0, Math.Max(0, map.Resources.Length - 1));
                }

                if (action.Kind == ActionKind.Move)
                {
                    Assert.InRange(action.ZoneId, 0, map.Zones.Length - 1);
                }
            }
        }
    }
}