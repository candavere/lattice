using System.Text.Json;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Multi-tick edge traversal: a Move between adjacent zones
/// takes an integer number of ticks (Manhattan length / cruise speed, minimum
/// 1) and the agent is represented by an <see cref="InTransit"/> state until
/// it arrives. Transit must be exact, deterministic, wired into the JSONL
/// trajectory format (so replay still verifies per-step serialized
/// <see cref="StepResult"/> equivalence, and repeated same-host recordings
/// remain byte-identical under the normalized JSONL byte contract), and
/// invisible to in-transit agents — they cannot act until arrival.
/// </summary>
public class TransitTests
{
    /// <summary>
    /// Three zones spaced 10 Manhattan units apart along the X axis, a single
    /// resource in zone 1, and chokes (0-1) and (1-2). Positions are chosen so
    /// edge length is exact integer arithmetic at common speeds.
    /// </summary>
    private static MapGraph LineMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(10, 0)),
            new Zone(2, new GridPoint(20, 0)),
        },
        new[]
        {
            new ResourceNode(0, 1, new GridPoint(11, 0)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    /// <summary>
    /// Zones 0-1-2 spaced 10 apart with a resource in zone 0 (a departure
    /// node) and unbounded chokes (0-1), (1-2).
    /// </summary>
    private static MapGraph DepartureResourceMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(10, 0)),
            new Zone(2, new GridPoint(20, 0)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(1, 0)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    /// <summary>
    /// Zones 0-1-2 spaced 10 apart; choke (0-1) is capped at one agent while
    /// choke (1-2) is unbounded.
    /// </summary>
    private static MapGraph CappedReverseMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(10, 0)),
            new Zone(2, new GridPoint(20, 0)),
        },
        Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1, MaxOccupancy: 1),
            new ChokePoint(1, 1, 2),
        });

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void TransitTicks_IsIntegerCeilingOfManhattanLengthOverSpeed()
    {
        var map = LineMap();

        Assert.Equal(0, Simulation.TransitTicks(map, 0, 1, SimulationConfig.InstantTransit));
        Assert.Equal(1, Simulation.TransitTicks(map, 0, 1, 10));
        Assert.Equal(4, Simulation.TransitTicks(map, 0, 1, 3));
        Assert.Equal(3, Simulation.TransitTicks(map, 0, 1, 4));
        Assert.Equal(10, Simulation.TransitTicks(map, 0, 1, 1));
        Assert.Equal(2, Simulation.TransitTicks(map, 0, 2, 10));
        Assert.Equal(1, Simulation.TransitTicks(map, 0, 1, 100));
    }

    [Fact]
    public void InstantTransit_Default_MovesSameTick_WithNoInTransitState()
    {
        var config = new SimulationConfig(2, 20);
        var state = Simulation.CreateInitial(LineMap(), config);
        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, config);

        Assert.Equal(1, outcome.NextState.Agents[0].ZoneId);
        Assert.Null(outcome.NextState.Agents[0].Transit);
        Assert.All(outcome.NextState.Agents, agent => Assert.Null(agent.Transit));
    }

    [Fact]
    public void SlowSpeed_EntersTransit_AndArrivesAfterExactlyTransitTicks()
    {
        var config = new SimulationConfig(2, 20, TransitSpeed: 3); // 0->1: ceil(10/3) = 4 ticks
        var state = Simulation.CreateInitial(LineMap(), config);

        var t1 = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 3), t1.NextState.Agents[0].Transit);
        Assert.Equal(0, t1.NextState.Agents[0].ZoneId);

        var t2 = Simulation.Step(t1.NextState, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 2), t2.NextState.Agents[0].Transit);

        var t3 = Simulation.Step(t2.NextState, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 1), t3.NextState.Agents[0].Transit);

        var t4 = Simulation.Step(t3.NextState, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, config);
        Assert.Null(t4.NextState.Agents[0].Transit);
        Assert.Equal(1, t4.NextState.Agents[0].ZoneId);
    }

    [Fact]
    public void InTransitAgent_CannotMoveOrCollect_UntilArrival()
    {
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);
        var state = Simulation.CreateInitial(LineMap(), config);

        var t1 = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, config);

        // Mid-crossing: agent 0 tries to move back AND to collect the zone-1
        // resource; neither may take effect while the agent is on the edge.
        var t2 = Simulation.Step(t1.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 2), t2.NextState.Agents[0].Transit);
        Assert.Equal(0, t2.NextState.Agents[0].ZoneId);
        Assert.Equal(0, t2.NextState.Agents[0].Score);
        Assert.Empty(t2.NextState.Claims);

        var t3 = Simulation.Step(t2.NextState, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, config);
        var t4 = Simulation.Step(t3.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) }, config);

        // The collection on the arrival tick succeeds: arrival precedes the
        // collect phase, so the agent may use its destination normally.
        Assert.Equal(1, t4.NextState.Agents[0].ZoneId);
        Assert.Null(t4.NextState.Agents[0].Transit);
        Assert.Equal(1, t4.NextState.Agents[0].Score);
        Assert.Contains(0, t4.NextState.Claims);
    }

    [Fact]
    public void TransitingAgent_CannotCollectFromItsDepartureNode()
    {
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);
        var state = Simulation.CreateInitial(DepartureResourceMap(), config);

        var t1 = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 3), t1.NextState.Agents[0].Transit);

        // Mid-crossing, agent 0 tries to collect the resource in the node it
        // departed. Transiting agents are on the edge and cannot collect, even
        // from their departure node.
        var t2 = Simulation.Step(t1.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) }, config);
        Assert.Equal(new InTransit(0, 1, 2), t2.NextState.Agents[0].Transit);
        Assert.Equal(0, t2.NextState.Agents[0].Score);
        Assert.Empty(t2.NextState.Claims);
    }

    [Fact]
    public void ReverseTraversal_ResolvesTheChokeThatActuallyBindsItsEdge()
    {
        // Both agents cross into zone 1, agent 1 from the far side (2->1). A
        // reverse traversal must key its capacity gate off choke (1-2) — not
        // off the (0-1) choke agent 0 is already occupying at capacity.
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);
        var map = CappedReverseMap();
        var state = new SimulationState(
            map,
            new[]
            {
                new AgentState(0, 0, 0),
                new AgentState(1, 2, 0),
            },
            Array.Empty<int>(),
            0);

        var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);

        Assert.Equal(new InTransit(0, 1, 3), outcome.NextState.Agents[0].Transit);
        Assert.Equal(new InTransit(2, 1, 3), outcome.NextState.Agents[1].Transit);
        Assert.Equal(2, outcome.NextState.Agents[1].ZoneId);
    }

    [Fact]
    public void SimultaneousTransit_ArrivesSameTick_Deterministically()
    {
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);
        var map = LineMap();
        var initial = new SimulationState(
            map,
            new[]
            {
                new AgentState(0, 0, 0),
                new AgentState(1, 2, 0),
            },
            Array.Empty<int>(),
            0);

        // Agent 0 crosses 0->1 and agent 1 crosses 2->1; both take 4 ticks.
        var first = RunCrossing(initial, config);
        var second = RunCrossing(initial, config);

        var t3 = first[2];
        Assert.Equal(new InTransit(0, 1, 1), t3.NextState.Agents[0].Transit);
        Assert.Equal(new InTransit(2, 1, 1), t3.NextState.Agents[1].Transit);

        var arrival = first[3].NextState;
        Assert.Equal(1, arrival.Agents[0].ZoneId);
        Assert.Equal(1, arrival.Agents[1].ZoneId);
        Assert.Null(arrival.Agents[0].Transit);
        Assert.Null(arrival.Agents[1].Transit);

        Assert.Equal(Json(first[3].NextState), Json(second[3].NextState));
    }

    [Fact]
    public void TransitEpisode_RecordsInTransitFrames_AndReplaysByteIdentically()
    {
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);
        var map = LineMap();
        var actions = new[]
        {
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
        };

        var sink = new StringWriter();
        var recording = TrajectoryWriter.Record(map, config, seed: 42, actions, sink);
        var text = sink.ToString();

        Assert.Equal(4, recording.Steps.Length);
        Assert.Equal("resources-exhausted", recording.Final.Reason);
        Assert.Contains("Transit", text); // in-transit frames are captured, not collapsed

        using var reader = new StringReader(text);
        var roundTrip = TrajectoryReader.Read(reader);

        Assert.Empty(TrajectoryReplay.Verify(roundTrip));

        var again = new StringWriter();
        TrajectoryWriter.Record(map, config, 42, actions, again);
        Assert.Equal(text, again.ToString()); // byte-identical across independent recordings
    }

    private static List<StepOutcome> RunCrossing(SimulationState initial, SimulationConfig config)
    {
        var results = new List<StepOutcome>();
        var state = initial;
        for (var tick = 0; tick < 5; tick++)
        {
            var outcome = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);
            results.Add(outcome);
            state = outcome.NextState;
            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return results;
    }
}