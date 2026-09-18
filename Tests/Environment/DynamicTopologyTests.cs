using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Exercises dynamic map topology: time- and event-driven choke capacity
/// overrides that ride inside the otherwise-pure <see cref="SimulationState"/>.
/// The contract under test is that dynamics are deterministic, are consulted
/// by the movement gateway, advance with the tick, and round-trip through the
/// same JSON that every other step contract uses. The fixtures use a real
/// (multi-tick) transit speed because choke capacity gates actual crossings —
/// a one-tick edge is instantaneous and never occupies a choke.
/// </summary>
public class DynamicTopologyTests
{
    private static readonly SimulationConfig Config = new(AgentCount: 2, MaxTicks: 50, TransitSpeed: 4);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>Three zones in a line, spacing 8 (two ticks at speed 4), one resource in zone 1.</summary>
    private static MapGraph Line()
    {
        return new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 8)),
                new Zone(2, new GridPoint(0, 16)),
            },
            new[] { new ResourceNode(0, 1, new GridPoint(0, 8)) },
            new[]
            {
                new ChokePoint(0, 0, 1, MaxOccupancy: 1),
                new ChokePoint(1, 1, 2),
            });
    }

    [Fact]
    public void TimedPortcullis_TogglesOnItsSchedule()
    {
        var rule = new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1);

        Assert.Equal(1, rule.EffectiveChokeCapacity(0, stepCount: 0, Array.Empty<int>()));
        Assert.Equal(0, rule.EffectiveChokeCapacity(0, stepCount: 1, Array.Empty<int>()));
        Assert.Equal(1, rule.EffectiveChokeCapacity(0, stepCount: 2, Array.Empty<int>()));
        Assert.Equal(0, rule.EffectiveChokeCapacity(0, stepCount: 3, Array.Empty<int>()));
        Assert.Null(rule.EffectiveChokeCapacity(1, stepCount: 0, Array.Empty<int>()));
    }

    [Fact]
    public void EventLockedChoke_LocksOnlyAfterItsTriggerIsClaimed()
    {
        var rule = new EventLockedChokeRule(ChokeId: 0, TriggerResourceId: 7);

        Assert.Null(rule.EffectiveChokeCapacity(0, stepCount: 4, Array.Empty<int>()));
        Assert.Equal(0, rule.EffectiveChokeCapacity(0, stepCount: 4, new[] { 7 }));
        Assert.Null(rule.EffectiveChokeCapacity(1, stepCount: 4, new[] { 7 }));
    }

    [Fact]
    public void RuleSet_StoresOnlyOverridesThatDifferFromTheBaseMap()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 2, ClosedTicks: 2, OpenCapacity: 1, ClosedCapacity: 0),
        });

        // Base choke 0 already allows one agent, so the open ticks store nothing.
        Assert.Empty(rules.ComputeChokeCapacities(0, Array.Empty<int>(), map));
        Assert.Empty(rules.ComputeChokeCapacities(1, Array.Empty<int>(), map));

        // The closed tick differs from the base, so only that override is stored.
        var closed = rules.ComputeChokeCapacities(2, Array.Empty<int>(), map);
        Assert.Equal(0, Assert.Single(closed).Value);
    }

    [Fact]
    public void ClosedPortcullis_BlocksMovement_AndReopensOnSchedule()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1),
        });
        var state = Simulation.CreateInitial(map, Config, rules);

        // Tick 0 is open; idling carries us to tick 1, which is closed.
        var wait = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait) }, Config);
        Assert.Equal(1, wait.NextState.StepCount);

        // Tick 1 is a closed tick: the crossing into zone 1 is refused.
        var blocked = Simulation.Step(wait.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1) }, Config);
        Assert.Equal(0, blocked.NextState.Agents[0].ZoneId);
        Assert.Null(blocked.NextState.Agents[0].Transit);

        // Tick 2 reopens the portcullis: the same move now begins its crossing.
        var reopened = Simulation.Step(blocked.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 1) }, Config);
        Assert.Equal(0, reopened.NextState.Agents[0].ZoneId);
        Assert.NotNull(reopened.NextState.Agents[0].Transit);
    }

    [Fact]
    public void EventLock_SealsTheChokeOnceTheTriggerIsClaimed()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new EventLockedChokeRule(ChokeId: 0, TriggerResourceId: 0),
        });
        var state = Simulation.CreateInitial(map, Config, rules);

        var moved = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1) }, Config);
        var arrived = Simulation.Step(moved.NextState, new[] { new AgentAction(ActionKind.Wait) }, Config);
        Assert.Equal(1, arrived.NextState.Agents[0].ZoneId);

        var collected = Simulation.Step(arrived.NextState, new[] { new AgentAction(ActionKind.Collect, ResourceId: 0) }, Config);
        Assert.Contains(0, collected.NextState.Claims);

        // The claim triggered the lock, so the agent can no longer return.
        var sealedOff = Simulation.Step(collected.NextState, new[] { new AgentAction(ActionKind.Move, ZoneId: 0) }, Config);
        Assert.Equal(1, sealedOff.NextState.Agents[0].ZoneId);
    }

    [Fact]
    public void DynamicOverrides_DoNotAlterTheBaseMap()
    {
        var map = Line();
        // An always-felt override (open capacity 0 on a base-capacity-1 choke):
        // the dynamic snapshot reads 0 at the owning tick while the map's base
        // topology stays untouched at 1.
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1, OpenCapacity: 0, ClosedCapacity: 0),
        });

        var state = Simulation.CreateInitial(map, Config, rules);
        Assert.Equal(0, state.Dynamics.EffectiveChokeCapacity(map, 0));
        Assert.Equal(1, map.ChokePoints[0].MaxOccupancy);
    }

    [Fact]
    public void TimedPortcullis_RejectsInvalidSchedules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedPortcullisRule(ChokeId: 0, OpenTicks: 0, ClosedTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedPortcullisRule(ChokeId: -1, OpenTicks: 1, ClosedTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1, OpenCapacity: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1, ClosedCapacity: -1));
    }

    [Fact]
    public void TimedPortcullis_RejectsCycleThatOverflowsInt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimedPortcullisRule(ChokeId: 0, OpenTicks: int.MaxValue, ClosedTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: int.MaxValue));

        // The largest schedule that still fits an int cycle is accepted.
        _ = new TimedPortcullisRule(ChokeId: 0, OpenTicks: int.MaxValue - 1, ClosedTicks: 1);
    }

    [Fact]
    public void EventLockedChoke_RejectsInvalidParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventLockedChokeRule(ChokeId: -1, TriggerResourceId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventLockedChokeRule(ChokeId: 0, TriggerResourceId: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventLockedChokeRule(ChokeId: 0, TriggerResourceId: 0, LockedCapacity: -1));
    }

    [Fact]
    public void RuleSet_RejectsChokeIdsAbsentFromTheTopology()
    {
        var map = Line(); // chokes are ids 0 and 1 only

        var unknownPortcullis = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 7, OpenTicks: 1, ClosedTicks: 1),
        });
        Assert.Throws<ArgumentException>(
            () => Simulation.CreateInitial(map, Config, unknownPortcullis));

        var unknownLock = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new EventLockedChokeRule(ChokeId: 9, TriggerResourceId: 0),
        });
        Assert.Throws<ArgumentException>(
            () => Simulation.CreateInitial(map, Config, unknownLock));
    }

    [Fact]
    public void DynamicState_RoundTripsThroughJsonIdentically()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 3, ClosedTicks: 2),
            new EventLockedChokeRule(ChokeId: 1, TriggerResourceId: 0, LockedCapacity: 0),
        });
        var state = Simulation.CreateInitial(map, Config, rules);

        var roundTripped = JsonSerializer.Deserialize<SimulationState>(JsonSerializer.Serialize(state))!;

        Assert.Equal(Json(state), Json(roundTripped));
    }

    [Fact]
    public void DynamicEpisode_IsDeterministicAcrossRuns()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1),
        });
        var turns = new[]
        {
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1) },
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1) },
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0) },
        };

        var first = SimulationDriver.Play(map, Config, turns, rules);
        var second = SimulationDriver.Play(map, Config, turns, rules);

        Assert.Equal(Json(first), Json(second));
    }

    [Fact]
    public void EnvironmentReset_KeepsTheDynamicRules()
    {
        var map = Line();
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 1, OpenCapacity: 0, ClosedCapacity: 0),
        });
        var environment = new LatticeEnvironment(map, Config, rules);

        environment.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 1) });
        environment.Reset();

        // A fresh step from the reset state must still see the closed choke.
        var blocked = environment.Step(new[] { new AgentAction(ActionKind.Move, ZoneId: 1) });
        Assert.Equal(0, blocked.Observations[0].AgentStates[0].ZoneId);
    }
}
