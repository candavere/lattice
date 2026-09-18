using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Exercises the pure <see cref="Simulation.Step"/> conflict-resolution and
/// reward rules against a hand-built triangle map (docs/adr-002.md):
/// agent 0 starts in zone 0, agent 1 in zone 1; zones 0/1/2 are pairwise
/// adjacent; resources 0 and 1 live in zone 1, resource 2 in zone 2.
/// </summary>
public class SimulationStepTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static readonly SimulationConfig Config = new(AgentCount: 4, MaxTicks: 20);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void Wait_PreservesState_AndAwardsNoReward()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(0, outcome.Result.Rewards[0].Value);
        Assert.Equal(0, outcome.Result.Rewards[1].Value);
        Assert.Equal(0, outcome.NextState.Agents[0].ZoneId);
        Assert.Equal(1, outcome.NextState.Agents[1].ZoneId);
        Assert.Equal(0, outcome.NextState.Agents[0].Score);
        Assert.Equal(1, outcome.NextState.StepCount);
        Assert.False(outcome.Result.Info.IsTerminal);
    }

    [Fact]
    public void ValidMove_ChangesTheAssignedZone()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(1, outcome.NextState.Agents[0].ZoneId);
        Assert.Equal(1, outcome.NextState.Agents[1].ZoneId);
    }

    [Fact]
    public void Move_ToUnknownZone_IsNoOp()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 99), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(0, outcome.NextState.Agents[0].ZoneId);
    }

    [Fact]
    public void Move_ToOwnZone_IsNoOp()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 0), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(0, outcome.NextState.Agents[0].ZoneId);
    }

    [Fact]
    public void Collect_FromOwningCorrectZone_GrantsOneReward()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var afterMove = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config);
        var outcome = Simulation.Step(afterMove.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(1, outcome.NextState.Agents[0].Score);
        Assert.Equal(1, outcome.Result.Rewards[0].Value);
        Assert.Contains(0, outcome.NextState.Claims);
    }

    [Fact]
    public void Collect_WhileNotInTheResourceZone_IsNoOp()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(0, outcome.NextState.Agents[0].Score);
        Assert.Equal(0, outcome.Result.Rewards[0].Value);
        Assert.Empty(outcome.NextState.Claims);
    }

    [Fact]
    public void Collect_UnknownResource_IsNoOp()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Collect, ResourceId: 999) }, Config);

        Assert.Equal(0, outcome.NextState.Agents[1].Score);
        Assert.Empty(outcome.NextState.Claims);
    }

    [Fact]
    public void ContendedCollect_LowestPriorityRankWins()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var afterMove = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config);
        var outcome = Simulation.Step(afterMove.NextState, new[]
        {
            new AgentAction(ActionKind.Collect, ResourceId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 1),
        }, Config);

        Assert.Equal(1, outcome.NextState.Agents[0].Score);
        Assert.Equal(0, outcome.NextState.Agents[1].Score);
        Assert.Equal(1, outcome.Result.Rewards[0].Value);
        Assert.Equal(0, outcome.Result.Rewards[1].Value);
        Assert.Contains(1, outcome.NextState.Claims);
    }

    [Fact]
    public void AlreadyClaimedResource_CannotBeCollectedAgain()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var firstCollect = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Collect, ResourceId: 1) }, Config);
        var afterMove = Simulation.Step(firstCollect.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config);
        var outcome = Simulation.Step(afterMove.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Wait) }, Config);

        Assert.Equal(1, outcome.NextState.Agents[1].Score);
        Assert.Equal(0, outcome.NextState.Agents[0].Score);
        Assert.Single(outcome.NextState.Claims);
    }

    [Fact]
    public void MissingActions_TreatTheAgentAsWaiting()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1) }, Config);

        Assert.Equal(1, outcome.NextState.Agents[0].ZoneId);
        Assert.Equal(1, outcome.NextState.Agents[1].ZoneId);
        Assert.Equal(0, outcome.NextState.Agents[1].Score);
    }

    [Fact]
    public void Step_DoesNotMutateTheInputState()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var before = Json(state);

        Simulation.Step(state, new[]
        {
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 1),
        }, Config);

        Assert.Equal(before, Json(state));
    }

    [Fact]
    public void Step_IsDeterministic_ForIdenticalInputs()
    {
        var first = Simulation.Step(Simulation.CreateInitial(Map, Config), new[]
        {
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 1),
        }, Config);

        var second = Simulation.Step(Simulation.CreateInitial(Map, Config), new[]
        {
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 1),
        }, Config);

        Assert.Equal(Json(first.Result), Json(second.Result));
        Assert.Equal(Json(first.NextState), Json(second.NextState));
    }

    [Fact]
    public void TickLimit_EndsTheSimulation()
    {
        var shortConfig = new SimulationConfig(2, 1);
        var state = Simulation.CreateInitial(Map, shortConfig);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, shortConfig);

        Assert.True(outcome.Result.Info.IsTerminal);
        Assert.Equal("tick-limit", outcome.Result.Info.Reason);
        Assert.Equal(1, outcome.Result.Info.StepNumber);
        Assert.Equal(0, outcome.Result.Info.WinnerAgentId);
    }

    [Fact]
    public void TickLimit_WinnerTieBreaksToLowestAgentId()
    {
        var shortConfig = new SimulationConfig(2, 1);
        var state = Simulation.CreateInitial(Map, shortConfig);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, shortConfig);

        Assert.Equal(0, outcome.Result.Info.WinnerAgentId);
    }

    [Fact]
    public void ResourceExhaustion_EndsTheSimulation()
    {
        var singleResourceMap = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 10)),
            },
            new[]
            {
                new ResourceNode(0, 1, new GridPoint(1, 9)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
            });

        var state = Simulation.CreateInitial(singleResourceMap, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Collect, ResourceId: 0) }, Config);

        Assert.True(outcome.Result.Info.IsTerminal);
        Assert.Equal("resources-exhausted", outcome.Result.Info.Reason);
        Assert.Equal(1, outcome.Result.Info.WinnerAgentId);
    }

    [Fact]
    public void Observations_CoverEveryAgentWithFullState()
    {
        var state = Simulation.CreateInitial(Map, Config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Collect, ResourceId: 0) }, Config);

        Assert.Equal(4, outcome.Result.Observations.Length);
        for (var i = 0; i < 4; i++)
        {
            var observation = outcome.Result.Observations[i];
            Assert.Equal(i, observation.AgentId);
            Assert.Same(Map, observation.Map);
            Assert.Equal(4, observation.AgentStates.Length);
            Assert.Equal(outcome.NextState.Claims, observation.Claims);
        }

        Assert.Equal(1, outcome.NextState.Agents[1].Score);
    }

    [Fact]
    public void CreateInitial_Throws_OnEmptyMap()
    {
        var emptyMap = new MapGraph(Array.Empty<Zone>(), Array.Empty<ResourceNode>(), Array.Empty<ChokePoint>());

        Assert.Throws<ArgumentException>(() => Simulation.CreateInitial(emptyMap, Config));
    }
}