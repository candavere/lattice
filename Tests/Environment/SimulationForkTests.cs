using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// <see cref="SimulationFork"/>: a fork is a detached sandbox
/// snapshot of an immutable <see cref="SimulationState"/> that can be advanced
/// turn-by-turn without disturbing the source state, its owners, or other
/// forks. These tests pin isolation (byte-identical source), independent
/// divergence, determinism of the sandbox, and terminal awareness.
/// </summary>
public class SimulationForkTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static readonly SimulationConfig Config = new(AgentCount: 2, MaxTicks: 20);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A source state two ticks into a real episode: agent 0 moved into zone 1.
    /// </summary>
    private static SimulationState SourceAfterOneTurn()
    {
        var initial = Simulation.CreateInitial(Map, Config);
        return Simulation.Step(initial, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config).NextState;
    }

    [Fact]
    public void Forking_SnapshotsImmutableState_LeavingSourceByteIdentical()
    {
        var source = SourceAfterOneTurn();
        var sourceJson = Json(source);

        var fork = SimulationFork.Create(source, Config);
        fork.Step(new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) });
        fork.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) });

        Assert.Equal(sourceJson, Json(source));
        Assert.Same(Map, source.Map); // shared immutable graph, never aliased
    }

    [Fact]
    public void Fork_IsDetached_TwoForksDivergeIndependently()
    {
        var source = SourceAfterOneTurn();
        var sourceJson = Json(source);

        var forkA = SimulationFork.Create(source, Config);
        forkA.Step(new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) });

        var forkB = SimulationFork.Create(source, Config);
        forkB.Step(new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) });

        Assert.Equal(1, forkA.Snapshot.Agents[0].Score);
        Assert.Equal(0, forkB.Snapshot.Agents[0].Score);
        Assert.Equal(0, source.Agents[0].Score);
        Assert.NotEqual(Json(forkA.Snapshot), Json(forkB.Snapshot));
        Assert.Equal(sourceJson, Json(source));
    }

    [Fact]
    public void Fork_IsDeterministic_IdenticalSequencesProduceIdenticalStates()
    {
        var source = SourceAfterOneTurn();
        var actions = new[]
        {
            new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Wait) },
        };

        var forkA = SimulationFork.Create(source, Config);
        foreach (var turn in actions)
        {
            forkA.Step(turn);
        }

        var forkB = SimulationFork.Create(source, Config);
        foreach (var turn in actions)
        {
            forkB.Step(turn);
        }

        Assert.Equal(Json(forkA.Snapshot), Json(forkB.Snapshot));
    }

    [Fact]
    public void Fork_ReachingTerminalTicks_IsMarkedTerminal()
    {
        // Agent 0 moves to zone 1 and collects the only resource it can reach
        // quickly; agent 1 takes the second: after both claims the episode ends.
        var initial = Simulation.CreateInitial(Map, Config);
        var mid = Simulation.Step(initial, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, Config).NextState;

        var fork = SimulationFork.Create(mid, Config);
        fork.Step(new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Collect, ResourceId: 1) });

        Assert.False(fork.IsTerminal); // two of three resources claimed
        fork.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) });
        Assert.False(fork.IsTerminal);
        fork.Step(new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Wait) });

        Assert.True(fork.IsTerminal);
        Assert.Equal(3, fork.Snapshot.Claims.Length);
    }

    [Fact]
    public void Fork_OfAnAlreadyTerminalState_IsImmediatelyTerminal()
    {
        var terminal = new SimulationState(
            Map,
            new[] { new AgentState(0, 0, 1), new AgentState(1, 1, 0) },
            new[] { 0, 1, 2 },
            9);

        var fork = SimulationFork.Create(terminal, new SimulationConfig(2, 10));

        Assert.True(fork.IsTerminal);
        Assert.Equal(9, fork.Snapshot.StepCount);
    }
}