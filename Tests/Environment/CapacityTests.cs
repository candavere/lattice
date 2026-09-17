using System.Text.Json;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Phase 8 (T8.2) spatial capacity: zones and choke edges carry a
/// MaxOccupancy that binds the Phase-1 move gateway. Resolution is a total
/// deterministic function — agents are triaged in ascending id, so when
/// capacity is contested the lowest id wins and the loser stays put. Node
/// capacity is a same-tick entry gate (bookings and same-tick arrivals count
/// against it, departures do not free their slot mid-tick); choke capacity
/// persists across ticks while a transit is on the edge and is only consulted
/// for crossings that actually take more than one tick.
/// </summary>
public class CapacityTests
{
    /// <summary>
    /// Zones 0 and 1 side by side, chokes (0-1) and (0-2). Default
    /// (unbounded) occupancy except where a specific test overrides it.
    /// </summary>
    private static MapGraph TwoZoneMap(int zone1Occupancy = MapLimits.Unlimited, int chokeOccupancy = MapLimits.Unlimited) => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(10, 0), zone1Occupancy),
        },
        Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1, chokeOccupancy),
        });

    private static SimulationState TwoAgentsAt(MapGraph map, int zoneId) => new(
        map,
        new[]
        {
            new AgentState(0, zoneId, 0),
            new AgentState(1, zoneId, 0),
        },
        Array.Empty<int>(),
        0);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void ZoneMaxOccupancy_FullZone_RejectsLaterMover_LowestIdWins()
    {
        var map = TwoZoneMap(zone1Occupancy: 1);
        var config = new SimulationConfig(2, 20);
        var outcome = Simulation.Step(
            TwoAgentsAt(map, 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);

        Assert.Equal(1, outcome.NextState.Agents[0].ZoneId);
        Assert.Equal(0, outcome.NextState.Agents[1].ZoneId);
        Assert.Null(outcome.NextState.Agents[0].Transit);
        Assert.Null(outcome.NextState.Agents[1].Transit);
    }

    [Fact]
    public void ZoneMaxOccupancy_Zero_IsImpassable_EvenWhenEmpty()
    {
        var map = TwoZoneMap(zone1Occupancy: 0);
        var config = new SimulationConfig(2, 20);
        var outcome = Simulation.Step(
            new SimulationState(map, new[] { new AgentState(0, 0, 0) }, Array.Empty<int>(), 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);

        Assert.Equal(0, outcome.NextState.Agents[0].ZoneId);
    }

    [Fact]
    public void ZoneCapacity_ReservesForSameTickArrivals_AndTransitBookings()
    {
        var map = TwoZoneMap(zone1Occupancy: 1, chokeOccupancy: MapLimits.Unlimited);
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);

        var outcome = Simulation.Step(
            TwoAgentsAt(map, 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);

        // Agent 0's booking fills the capped zone; agent 1 is denied on the
        // node gate alone (the choke is unbounded).
        Assert.Equal(new InTransit(0, 1, 3), outcome.NextState.Agents[0].Transit);
        Assert.Equal(0, outcome.NextState.Agents[1].ZoneId);
        Assert.Null(outcome.NextState.Agents[1].Transit);
    }

    [Fact]
    public void ChokeMaxOccupancy_OccupiedChoke_BlocksNewDeparture_UntilTheEdgeIsFree()
    {
        var map = TwoZoneMap(chokeOccupancy: 1);
        var config = new SimulationConfig(2, 20, TransitSpeed: 3);

        var state = TwoAgentsAt(map, 0);
        var t1 = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);
        var t2 = Simulation.Step(t1.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);

        // While agent 0 transits, agent 1's departure is rejected — the edge
        // stays occupied for the whole tick, including the arrival tick.
        Assert.Equal(new InTransit(0, 1, 3), t1.NextState.Agents[0].Transit);
        Assert.Equal(new InTransit(0, 1, 2), t2.NextState.Agents[0].Transit);
        Assert.Equal(0, t1.NextState.Agents[1].ZoneId);
        Assert.Equal(0, t2.NextState.Agents[1].ZoneId);

        var t3 = Simulation.Step(t2.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);
        var t4 = Simulation.Step(t3.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);

        // Agent 0 arrives at tick 4; the choke belongs to the whole of tick 4,
        // so agent 1 can only start a real crossing from tick 5.
        Assert.Null(t4.NextState.Agents[0].Transit);
        Assert.Equal(1, t4.NextState.Agents[0].ZoneId);
        Assert.Equal(0, t4.NextState.Agents[1].ZoneId);

        var t5 = Simulation.Step(t4.NextState, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Move, ZoneId: 1) }, config);
        Assert.Equal(new InTransit(0, 1, 3), t5.NextState.Agents[1].Transit);
    }

    [Fact]
    public void EdgeCapacity_IsNotConsulted_ForInstantMoves()
    {
        var map = TwoZoneMap(chokeOccupancy: 1);
        var config = new SimulationConfig(2, 20); // instant transit: no real crossing

        var outcome = Simulation.Step(
            TwoAgentsAt(map, 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);

        Assert.Equal(1, outcome.NextState.Agents[0].ZoneId);
        Assert.Equal(1, outcome.NextState.Agents[1].ZoneId);
    }

    [Fact]
    public void CapacityResolution_IsDeterministic_ForIdenticalInputs()
    {
        var map = TwoZoneMap(zone1Occupancy: 1);
        var config = new SimulationConfig(2, 20);

        var first = Simulation.Step(
            TwoAgentsAt(map, 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);
        var second = Simulation.Step(
            TwoAgentsAt(map, 0),
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Move, ZoneId: 1) },
            config);

        Assert.Equal(Json(first.NextState), Json(second.NextState));
        Assert.Equal(Json(first.Result), Json(second.Result));
    }

    [Fact]
    public void SaturatedMove_IsRecordedAndReplayed_WithRejectionVisible()
    {
        // Agent 1 already occupies the capped zone 1, so agent 0's entry is
        // rejected deterministically; the rejected (but action-space-valid)
        // move must survive the trajectory round-trip.
        var map = TwoZoneMap(zone1Occupancy: 1);
        var config = new SimulationConfig(2, 20);
        var actions = new[]
        {
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
        };

        var sink = new StringWriter();
        var recording = TrajectoryWriter.Record(map, config, seed: 7, actions, sink);

        Assert.Equal(0, recording.Steps[0].Result.Observations[0].AgentStates[0].ZoneId);
        Assert.Equal(1, recording.Steps[0].Result.Observations[0].AgentStates[1].ZoneId);

        using var reader = new StringReader(sink.ToString());
        var roundTrip = TrajectoryReader.Read(reader);
        Assert.Empty(TrajectoryReplay.Verify(roundTrip));
    }
}