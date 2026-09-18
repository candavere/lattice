using System.Linq;
using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates the contention-bearing bottleneck evaluation topology: the map
/// structure (every resource behind a capacity-1 choke), that the shared
/// single-lane gate produces transit-denial contention under opposing
/// crossings, and that a greedy paired run on the map yields non-zero
/// contention saturation through the evaluation harness.
/// </summary>
public class BottleneckScenarioTests
{
    private static MapGraph Map() => BottleneckScenario.Build();

    [Fact]
    public void Map_PlacesEveryResourceBehindCapacityOneChokes()
    {
        var map = Map();

        // Four zones in a line: spawn A (0), spawn B (1), choke room (2), vault (3).
        Assert.Equal(4, map.Zones.Length);

        // Every choke is a capacity-1 single-lane gate.
        Assert.All(map.ChokePoints, choke => Assert.Equal(1, choke.MaxOccupancy));

        // Both spawns reach the choke room, and the choke room reaches the vault
        // through a single shared gate — no shortcut bypasses the bottleneck.
        Assert.Contains(map.ChokePoints, choke =>
            (choke.FromZoneId == 0 && choke.ToZoneId == 2) ||
            (choke.FromZoneId == 2 && choke.ToZoneId == 0));
        Assert.Contains(map.ChokePoints, choke =>
            (choke.FromZoneId == 1 && choke.ToZoneId == 2) ||
            (choke.FromZoneId == 2 && choke.ToZoneId == 1));
        Assert.Contains(map.ChokePoints, choke =>
            (choke.FromZoneId == 2 && choke.ToZoneId == 3) ||
            (choke.FromZoneId == 3 && choke.ToZoneId == 2));

        // Every resource lives in the vault, behind the capacity-1 chokes.
        Assert.NotEmpty(map.Resources);
        Assert.All(map.Resources, resource => Assert.Equal(3, resource.ZoneId));
    }

    [Fact]
    public void OpposingCrossingsOnTheSharedCapacityOneGate_AreCountedAsTransitDenial()
    {
        // Both agents race into the choke room and then both request the shared
        // capacity-1 gate (2,3) on the same tick to enter the vault; only one
        // may hold the single lane, so the later resolver is denied passage — a
        // transit denial that must surface as contention. Scripts are padded
        // with Waits across the transit window because scripted actions are
        // consumed every tick, whether or not the agent is mid-crossing.
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, TransitSpeed: 4);
        var agents = new IAgent[]
        {
            new ScriptedAgent(0, Move(2), Wait(), Wait(), Move(3)),
            new ScriptedAgent(1, Move(2), Wait(), Wait(), Move(3)),
        };

        var result = ScenarioRunner.Run(Map(), config, agents, maxSteps: 5);

        Assert.True(result.Metrics.ContendedTicks > 0,
            $"expected transit-denial contention on the shared gate; got {result.Metrics.ContendedTicks} contended ticks.");
        Assert.True(result.Metrics.ContentionRate > 0.0);
    }

    [Fact]
    public void GreedyPairedRunOnBottleneckMap_ReportsNonZeroContention()
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
            $"seed {match.Seed} produced zero contention on the bottleneck map."));
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

    private static AgentAction Move(int zoneId) => new(ActionKind.Move, zoneId);
    private static AgentAction Wait() => new(ActionKind.Wait);
}