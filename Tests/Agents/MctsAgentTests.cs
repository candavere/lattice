using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Coverage for <see cref="MctsAgent"/>: constructor validation, search
/// determinism, action-space legality of everything the search emits, the
/// strict bottlenecked-map superiority claim over
/// <see cref="GreedyCollectorAgent"/>, and the factory/harness wiring.
/// </summary>
public class MctsAgentTests
{
    private static readonly SimulationConfig BottleneckConfig = new(AgentCount: 2, MaxTicks: 200, TransitSpeed: 8);
    private static readonly MapGraph JamLaneMap = JamLaneFixture();

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// "JamLane": both agents sit one choke away from the single-lane corridor
    /// 2-3 that leads to the only stash (R2-R4), on equidistant access chokes
    /// (0-2 and 1-2, three ticks each). Greedy slot 1 cannot skip its starting
    /// resource (own-zone collect bias), so it reaches the corridor gate one
    /// tick after slot 0, loses the single-lane choke to ascending-id
    /// resolution, and arrives at the stash after it is emptied — baseline
    /// score 1. MCTS prices real transit timing in its rollouts, takes the
    /// corridor first, and sweeps the stash: score 4. Every choke on the
    /// contested path is single-lane and every crossing two or more ticks, so
    /// no capacity bound is skipped.
    /// </summary>
    private static MapGraph JamLaneFixture() => new(
        new[]
        {
            new Zone(0, new GridPoint(16, 0)),
            new Zone(1, new GridPoint(0, 0)),
            new Zone(2, new GridPoint(8, 16)),
            new Zone(3, new GridPoint(24, 16)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(16, 0)),
            new ResourceNode(1, 1, new GridPoint(0, 0)),
            new ResourceNode(2, 3, new GridPoint(24, 16)),
            new ResourceNode(3, 3, new GridPoint(24, 17)),
            new ResourceNode(4, 3, new GridPoint(24, 18)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 0, 2, MaxOccupancy: 1),
            new ChokePoint(2, 1, 2, MaxOccupancy: 1),
            new ChokePoint(3, 2, 3, MaxOccupancy: 1),
        });

    private static int ScoreOf(ScenarioResult result, int agentId)
    {
        foreach (var metrics in result.Metrics.Agents)
        {
            if (metrics.AgentId == agentId)
            {
                return metrics.Score;
            }
        }

        throw new InvalidOperationException($"No metrics for agent {agentId}.");
    }

    [Fact]
    public void Construction_RejectsInvalidSearchBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MctsSearchConfig(0, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MctsSearchConfig(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MctsSearchConfig(-1, 12));
    }

    [Fact]
    public void Construction_RejectsOutOfRangeAgentId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MctsAgent(2, BottleneckConfig, 0, new MctsSearchConfig()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MctsAgent(-1, BottleneckConfig, 0, new MctsSearchConfig()));
    }

    [Fact]
    public void Decide_IsDeterministicForIdenticalSeed()
    {
        var search = new MctsSearchConfig(rolloutsPerAction: 8, maxDepth: 20);
        var first = ScenarioRunner.Run(
            JamLaneMap, BottleneckConfig, new IAgent[] { new GreedyCollectorAgent(0), new MctsAgent(1, BottleneckConfig, 42, search) }, maxSteps: 200);
        var second = ScenarioRunner.Run(
            JamLaneMap, BottleneckConfig, new IAgent[] { new GreedyCollectorAgent(0), new MctsAgent(1, BottleneckConfig, 42, search) }, maxSteps: 200);

        Assert.Equal(Json(first.Turns), Json(second.Turns));
        Assert.Equal(Json(first.Results), Json(second.Results));
        Assert.Equal(ScoreOf(first, 1), ScoreOf(second, 1));
    }

    [Fact]
    public void Decide_EveryEmittedActionLiesInActionSpace()
    {
        var search = new MctsSearchConfig(rolloutsPerAction: 8, maxDepth: 20);
        var initial = Simulation.CreateInitial(JamLaneMap, BottleneckConfig);
        var initialObservation = new Observation(1, JamLaneMap, initial.Agents, initial.Claims);
        var result = ScenarioRunner.Run(
            JamLaneMap, BottleneckConfig, new IAgent[] { new GreedyCollectorAgent(0), new MctsAgent(1, BottleneckConfig, 7, search) }, maxSteps: 200);

        for (var t = 0; t < result.Turns.Length; t++)
        {
            var observation = t == 0
                ? initialObservation
                : result.Results[t - 1].Observations[1];
            var action = result.Turns[t][1];
            Assert.True(IsLegal(observation, action), $"illegal action {action} at turn {t}: {Json(observation)}");
        }
    }

    private static bool IsLegal(Observation observation, AgentAction action)
    {
        if (action.Kind == ActionKind.Wait)
        {
            return true;
        }

        var myZone = observation.AgentStates[observation.AgentId].ZoneId;
        if (action.Kind == ActionKind.Move)
        {
            if (action.ZoneId == myZone)
            {
                return false;
            }

            foreach (var choke in observation.Map.ChokePoints)
            {
                if ((choke.FromZoneId == myZone && choke.ToZoneId == action.ZoneId)
                    || (choke.FromZoneId == action.ZoneId && choke.ToZoneId == myZone))
                {
                    return true;
                }
            }

            return false;
        }

        if (action.Kind != ActionKind.Collect)
        {
            return false;
        }

        foreach (var resource in observation.Map.Resources)
        {
            if (resource.Id == action.ResourceId)
            {
                return resource.ZoneId == myZone && !observation.Claims.Contains(resource.Id);
            }
        }

        return false;
    }

    [Fact]
    public void BottleneckMap_MctsStrictlyOutperformsGreedyInSameSlot()
    {
        var baseline = ScenarioRunner.Run(
            JamLaneMap, BottleneckConfig, new IAgent[] { new GreedyCollectorAgent(0), new GreedyCollectorAgent(1) }, maxSteps: 200);
        var mcts = ScenarioRunner.Run(
            JamLaneMap,
            BottleneckConfig,
            new IAgent[] { new GreedyCollectorAgent(0), new MctsAgent(1, BottleneckConfig, 1, new MctsSearchConfig(16, 20)) },
            maxSteps: 200);

        var greedyScore = ScoreOf(baseline, 1);
        var mctsScore = ScoreOf(mcts, 1);

        Assert.Equal(1, greedyScore);
        Assert.Equal(4, mctsScore);
        Assert.True(mctsScore > greedyScore, "MCTS must strictly outscore same-slot Greedy on a bottlenecked map.");
        Assert.True(baseline.Metrics.Terminated, "baseline episode must exhaust every resource");
        Assert.True(mcts.Metrics.Terminated, "MCTS episode must exhaust every resource");
    }

    [Fact]
    public void Factory_SeedsAgentsDeterministically()
    {
        var search = new MctsSearchConfig(rolloutsPerAction: 4, maxDepth: 12);
        var factory = new MctsAgentFactory(BottleneckConfig, search, familySeed: 99);

        var a = factory.Create(1, runSeed: 5);
        var b = factory.Create(1, runSeed: 5);
        var c = factory.Create(1, runSeed: 6);

        var greedy = new GreedyCollectorAgent(0);
        var runA = ScenarioRunner.Run(JamLaneMap, BottleneckConfig, new IAgent[] { greedy, a }, maxSteps: 200);
        var runB = ScenarioRunner.Run(JamLaneMap, BottleneckConfig, new IAgent[] { greedy, b }, maxSteps: 200);
        var runC = ScenarioRunner.Run(JamLaneMap, BottleneckConfig, new IAgent[] { greedy, c }, maxSteps: 200);

        Assert.Equal("MCTS", factory.Name);
        Assert.Equal(Json(runA.Turns), Json(runB.Turns));
        Assert.Equal(4, ScoreOf(runC, 1));
    }

    [Fact]
    public void MctsFactory_WiresIntoEvaluationHarness_Deterministically()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 200, TransitSpeed: 8);
        var search = new MctsSearchConfig(rolloutsPerAction: 4, maxDepth: 12);
        var spec = new EvaluationSpec(
            seeds: new ulong[] { 42, 7 },
            mapFactory: _ => JamLaneMap,
            simulationConfig: config,
            teams: new IAgentFactory[]
            {
                new GreedyCollectorAgentFactory(),
                new MctsAgentFactory(config, search),
            },
            pairings: new[] { (TeamA: 0, TeamB: 1), (TeamA: 1, TeamB: 0) },
            maxSteps: 200);

        var first = EvaluationHarness.Evaluate(spec);
        var second = EvaluationHarness.Evaluate(spec);

        Assert.Equal(Json(first), Json(second));
        Assert.Equal(4, first.Matches.Length);
        foreach (var summary in first.Pairings)
        {
            Assert.Equal(2, summary.Matches);
            Assert.Equal(0.0, summary.TimeoutRate);
        }
    }
}