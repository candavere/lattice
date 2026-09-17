using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="ScenarioRunner"/>: agent-set validation, budget
/// truncation vs. true termination, contention accounting (attempts, not
/// outcomes), per-agent score/move/efficiency metrics, and byte-identical
/// reuse regardless of agent polling order — the guarantees the batch
/// evaluation harness relies on.
/// </summary>
public class ScenarioRunnerTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();
    private static readonly SimulationConfig TwoPlayer = new(AgentCount: 2, MaxTicks: 40);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static AgentAction Collect(int resourceId) => new(ActionKind.Collect, ResourceId: resourceId);
    private static AgentAction Move(int zoneId) => new(ActionKind.Move, zoneId);

    [Fact]
    public void ThrowsWhenAgentCountMismatchesConfig()
    {
        var threeAgents = new IAgent[]
        {
            new WaitAgent(0),
            new WaitAgent(1),
            new WaitAgent(2),
        };

        Assert.Throws<ArgumentException>(() =>
            ScenarioRunner.Run(TriangleMap, TwoPlayer, threeAgents, 10));
    }

    [Fact]
    public void ThrowsWhenAgentIdsAreNonContiguous()
    {
        var agents = new IAgent[] { new WaitAgent(0), new WaitAgent(0) };

        Assert.Throws<ArgumentException>(() =>
            ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, 10));
    }

    [Fact]
    public void ThrowsWhenMaxStepsBelowOne()
    {
        var agents = new IAgent[] { new WaitAgent(0), new WaitAgent(1) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, 0));
    }

    [Fact]
    public void BudgetCut_ReportsNotTerminatedWithoutReason()
    {
        var agents = new IAgent[] { new WaitAgent(0), new WaitAgent(1) };
        var result = ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, maxSteps: 5);

        Assert.False(result.Metrics.Terminated);
        Assert.Null(result.Metrics.TerminationReason);
        Assert.Equal(5, result.Metrics.TotalSteps);
        Assert.Equal(5, result.Metrics.MaxSteps);
        Assert.Equal(0.0, result.Metrics.ContentionRate);
    }

    [Fact]
    public void Exhaustion_TerminatedWithReasonAndWithinBudget()
    {
        var agents = new IAgent[] { new GreedyCollectorAgent(0), new GreedyCollectorAgent(1) };
        var result = ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, maxSteps: 40);

        Assert.True(result.Metrics.Terminated);
        Assert.Equal("resources-exhausted", result.Metrics.TerminationReason);
        Assert.True(result.Metrics.TotalSteps <= 40);
        Assert.Equal(3, result.Metrics.Agents.Sum(a => a.Score));
    }

    [Fact]
    public void Contention_CountsTicksWhereTwoAgentsTargetSameResource()
    {
        // Agent 0 spends tick 1 moving to zone 1; agent 1 collects in zone 1.
        // Only tick 2 (both Collecting resource 0) is a contended tick.
        var agents = new IAgent[]
        {
            new ScriptedAgent(0, Move(1), Collect(0)),
            new ScriptedAgent(1, Collect(0), Collect(0)),
        };
        var result = ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, maxSteps: 2);

        Assert.Equal(2, result.Metrics.TotalSteps);
        Assert.Equal(1, result.Metrics.ContendedTicks);
        Assert.Equal(0.5, result.Metrics.ContentionRate);
    }

    [Fact]
    public void Contention_ExcludesTicksWhereOnlyOneAgentClaims()
    {
        // Same shape as above, but agent 1 only Collects once, so tick 2's
        // lone Collect against an already-claimed resource is not contention.
        var agents = new IAgent[]
        {
            new ScriptedAgent(0, Move(1), Collect(0)),
            new ScriptedAgent(1, Collect(0)),
        };
        var result = ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, maxSteps: 2);

        Assert.Equal(0, result.Metrics.ContendedTicks);
        Assert.Equal(0.0, result.Metrics.ContentionRate);
    }

    [Fact]
    public void Metrics_ScoreMovesAndEfficiencyPerAgent()
    {
        // Agent 0: move to zone 1, collect resource 0, collect resource 1 ->
        // score 2 from 1 move (both collects happen after the single move).
        var agents = new IAgent[]
        {
            new ScriptedAgent(0, Move(1), Collect(0), Collect(1)),
            new WaitAgent(1),
        };
        var result = ScenarioRunner.Run(TriangleMap, TwoPlayer, agents, maxSteps: 3);

        var agent0 = result.Metrics.Agents[0];
        var agent1 = result.Metrics.Agents[1];
        Assert.Equal(2, agent0.Score);
        Assert.Equal(1, agent0.Moves);
        Assert.Equal(2.0, agent0.Efficiency, precision: 6);
        Assert.Equal(0, agent1.Score);
        Assert.Equal(0, agent1.Moves);
        Assert.Equal(0.0, agent1.Efficiency);
    }

    [Fact]
    public void SameSeed_AscendingAndShuffledAgentOrder_ByteIdentical()
    {
        const ulong seed = 0xFEEDUL;

        IAgent[] MakePair()
        {
            return new IAgent[]
            {
                new RandomAgent(0, new Rng(seed)),
                new RandomAgent(1, new Rng(seed ^ 0x5DEECE66DUL)),
            };
        }

        var ascending = new IAgent[] { MakePair()[0], MakePair()[1] };
        var shuffled = new IAgent[] { MakePair()[1], MakePair()[0] };

        var first = ScenarioRunner.Run(TriangleMap, TwoPlayer, ascending, 40);
        var second = ScenarioRunner.Run(TriangleMap, TwoPlayer, shuffled, 40);

        Assert.Equal(Json(first), Json(second));
    }

    [Fact]
    public void SameSeededActors_TwoRuns_ByteIdenticalTurnsResultsAndMetrics()
    {
        const ulong seed = 0x1234UL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(seed, generatorConfig);

        ScenarioResult RunOnce()
        {
            var agents = new IAgent[]
            {
                new RandomAgent(0, new Rng(seed)),
                new GreedyCollectorAgent(1),
            };
            return ScenarioRunner.Run(map, TwoPlayer, agents, 40);
        }

        Assert.Equal(Json(RunOnce()), Json(RunOnce()));
    }
}