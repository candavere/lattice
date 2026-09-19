using System.Linq;
using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates the procedural contention-bearing bottleneck evaluation topology
/// family: the structural invariant (every resource behind a capacity-1 choke,
/// reached only through a shared single-lane vault gate), that distinct seeds
/// yield distinct topologies, that the shared gate produces transit-denial
/// contention, and that greedy paired runs on the family report non-zero
/// contention saturation and terminate by resource exhaustion.
/// </summary>
public class BottleneckScenarioTests
{
    private static MapGraph Map() => BottleneckScenario.Build();

    [Fact]
    public void Map_PlacesEveryResourceBehindCapacityOneChokes()
    {
        var map = Map();

        var antechamber = Antechamber(map);
        Assert.True(map.Zones.Length >= 3);

        // Both spawns can reach the shared antechamber, and the antechamber
        // reaches a vault through the single shared gate — no shortcut bypasses
        // the funnel.
        Assert.True(Reachable(map, SpawnLeft, antechamber), "left spawn must reach the antechamber.");
        Assert.True(Reachable(map, SpawnRight, antechamber), "right spawn must reach the antechamber.");
        Assert.True(map.Zones.Any(zone => zone.Role == BottleneckScenario.VaultRole)
            && map.ChokePoints.Any(choke =>
                (choke.FromZoneId == antechamber && map.Zones[choke.ToZoneId].Role == BottleneckScenario.VaultRole)
                || (choke.ToZoneId == antechamber && map.Zones[choke.FromZoneId].Role == BottleneckScenario.VaultRole)),
            "the antechamber must connect to a vault through the shared gate.");

        // Every choke is a capacity-1 single-lane gate.
        Assert.NotEmpty(map.ChokePoints);
        Assert.All(map.ChokePoints, choke => Assert.Equal(1, choke.MaxOccupancy));

        // Every resource lives in a vault zone, reachable only through the
        // capacity-1 gates funneling into the vault subgraph.
        Assert.NotEmpty(map.Resources);
        Assert.All(map.Resources, resource =>
            Assert.Equal(BottleneckScenario.VaultRole, map.Zones[resource.ZoneId].Role));
    }

    [Fact]
    public void DistinctSeeds_ProduceDistinctTopologies()
    {
        var seedA = 101UL;
        var seedB = 102UL;

        var mapA = BottleneckScenario.ForSeed(seedA);
        var mapB = BottleneckScenario.ForSeed(seedB);

        Assert.NotEqual(
            (mapA.Zones.Length, mapA.Resources.Length, mapA.ChokePoints.Length),
            (mapB.Zones.Length, mapB.Resources.Length, mapB.ChokePoints.Length));
    }

    [Fact]
    public void SameSeed_ProducesByteIdenticalTopology()
    {
        var first = BottleneckScenario.ForSeed(4242UL);
        var second = BottleneckScenario.ForSeed(4242UL);

        Assert.Equal(first.Zones.Length, second.Zones.Length);
        Assert.Equal(first.Resources.Length, second.Resources.Length);
        Assert.Equal(first.ChokePoints.Length, second.ChokePoints.Length);
        for (var i = 0; i < first.Zones.Length; i++)
        {
            Assert.Equal(first.Zones[i], second.Zones[i]);
        }
    }

    [Fact]
    public void OpposingCrossingsOnTheSharedCapacityOneGate_AreCountedAsTransitDenial()
    {
        // A minimal funnel: two spawns (0, 1) both enter a shared antechamber
        // (2) over capacity-1 gates, which leads to the vault (3) through one
        // shared capacity-1 gate. Both agents request the shared gate on the
        // same tick, so the single lane denies the later resolver — a transit
        // denial that must surface as contention.
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, TransitSpeed: 4);
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(8, 0)),
                new Zone(2, new GridPoint(4, 0)),
                new Zone(3, new GridPoint(4, 8)),
            },
            new[] { new ResourceNode(0, 3, new GridPoint(4, 9)) },
            new[]
            {
                new ChokePoint(0, 0, 2, MaxOccupancy: 1),
                new ChokePoint(1, 1, 2, MaxOccupancy: 1),
                new ChokePoint(2, 2, 3, MaxOccupancy: 1),
            });
        var agents = new IAgent[]
        {
            new ScriptedAgent(0, Move(2), Move(3), Wait(), Wait()),
            new ScriptedAgent(1, Move(2), Move(3), Wait(), Wait()),
        };

        var result = ScenarioRunner.Run(map, config, agents, maxSteps: 5);

        Assert.True(result.Metrics.ContendedTicks > 0,
            $"expected transit-denial contention on the shared gate; got {result.Metrics.ContendedTicks} contended ticks.");
        Assert.True(result.Metrics.ContentionRate > 0.0);
    }

    [Fact]
    public void GreedyPairedRunOnBottleneckMaps_ReportsNonZeroContention()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 200, TransitSpeed: 4);
        var teams = new IAgentFactory[]
        {
            new GreedyCollectorAgentFactory(),
            new GreedyCollectorAgentFactory(),
        };
        var spec = new EvaluationSpec(
            seeds: new[] { 1UL, 2UL, 3UL },
            mapFactory: BottleneckScenario.ForSeed,
            simulationConfig: config,
            teams,
            new[] { (TeamA: 0, TeamB: 1) },
            maxSteps: 200);

        var batch = EvaluationHarness.Evaluate(spec);

        Assert.All(batch.Matches, match => Assert.True(match.ContentionRate > 0.0,
            $"seed {match.Seed} produced zero contention on its procedural bottleneck map."));
        Assert.All(batch.Pairings, pairing => Assert.True(pairing.MeanContentionRate > 0.0));
    }

    [Fact]
    public void GreedyRunOnBottleneckMap_TerminatesByResourceExhaustion()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 200, TransitSpeed: 4);
        var agents = new IAgent[]
        {
            new GreedyCollectorAgent(0),
            new GreedyCollectorAgent(1),
        };

        var result = ScenarioRunner.Run(Map(), config, agents, maxSteps: 200);

        Assert.True(result.Metrics.Terminated);
        Assert.Equal("resources-exhausted", result.Metrics.TerminationReason);
        Assert.Equal(Map().Resources.Length, result.Metrics.Agents.Sum(agent => agent.Score));
    }

    private static int SpawnLeft => 0;

    private static int SpawnRight => 1;

    private static int Antechamber(MapGraph map) =>
        map.Zones.First(zone => zone.Role == BottleneckScenario.AntechamberRole).Id;

    private static bool Reachable(MapGraph map, int from, int to)
    {
        var visited = new HashSet<int> { from };
        var frontier = new Queue<int>();
        frontier.Enqueue(from);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var neighbor in Neighbors(map, current))
            {
                if (neighbor == to)
                {
                    return true;
                }

                if (visited.Add(neighbor))
                {
                    frontier.Enqueue(neighbor);
                }
            }
        }

        return false;
    }

    private static IEnumerable<int> Neighbors(MapGraph map, int zone)
    {
        var result = new List<int>();
        foreach (var choke in map.ChokePoints)
        {
            if (choke.FromZoneId == zone)
            {
                result.Add(choke.ToZoneId);
            }
            else if (choke.ToZoneId == zone)
            {
                result.Add(choke.FromZoneId);
            }
        }

        return result.OrderBy(x => x);
    }

    private static AgentAction Move(int zoneId) => new(ActionKind.Move, zoneId);

    private static AgentAction Wait() => new(ActionKind.Wait);
}