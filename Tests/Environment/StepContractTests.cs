using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Verifies every step-contract type is plain data: JSON serializable to
/// JSONL and back with no custom logic, per AGENTS.md principle 3. Records
/// without arrays compare by value; records carrying arrays (which C# compares
/// by reference) are asserted via byte-identical serialization.
/// </summary>
public class StepContractTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static T[] RoundTripArray<T>(T[] value) =>
        JsonSerializer.Deserialize<T[]>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void Action_RoundTrips_ForEveryKind()
    {
        Assert.Equal(new AgentAction(ActionKind.Wait), RoundTrip(new AgentAction(ActionKind.Wait)));
        Assert.Equal(new AgentAction(ActionKind.Move, ZoneId: 4), RoundTrip(new AgentAction(ActionKind.Move, ZoneId: 4)));
        Assert.Equal(new AgentAction(ActionKind.Collect, ResourceId: 2), RoundTrip(new AgentAction(ActionKind.Collect, ResourceId: 2)));
    }

    [Fact]
    public void AgentState_RoundTrips()
    {
        Assert.Equal(new AgentState(1, 3, 2), RoundTrip(new AgentState(1, 3, 2)));
    }

    [Fact]
    public void Reward_RoundTrips()
    {
        Assert.Equal(new Reward(0, 1.0), RoundTrip(new Reward(0, 1.0)));
        Assert.Equal(new Reward(2, 0.0), RoundTrip(new Reward(2, 0.0)));
    }

    [Fact]
    public void Info_RoundTrips_ForBothTerminalAndRunning()
    {
        Assert.Equal(new Info(1, false, null, null), RoundTrip(new Info(1, false, null, null)));
        Assert.Equal(new Info(50, true, "tick-limit", 1), RoundTrip(new Info(50, true, "tick-limit", 1)));
    }

    [Fact]
    public void Observation_RoundTrips_Identically()
    {
        var map = TestMaps.TriangleWithResources();
        var value = new Observation(
            0,
            map,
            new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 1) },
            new[] { 0, 2 });

        var roundTripped = RoundTrip(value);
        Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(roundTripped));
    }

    [Fact]
    public void StepResult_RoundTrips_Identically()
    {
        var map = TestMaps.TriangleWithResources();
        var agents = new[] { new AgentState(0, 1, 2), new AgentState(1, 1, 0) };
        var claims = new[] { 0, 1 };
        var value = new StepResult(
            new[]
            {
                new Observation(0, map, agents, claims),
                new Observation(1, map, agents, claims),
            },
            new[] { new Reward(0, 1.0), new Reward(1, 0.0) },
            new Info(3, false, null, null));

        var roundTripped = RoundTrip(value);
        Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(roundTripped));
    }

    [Fact]
    public void SimulationState_RoundTrips_Identically()
    {
        var map = TestMaps.TriangleWithResources();
        var value = new SimulationState(
            map,
            new[] { new AgentState(0, 1, 2), new AgentState(1, 2, 0) },
            new[] { 0 },
            7);

        var roundTripped = RoundTrip(value);
        Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(roundTripped));
    }

    [Fact]
    public void StepOutcome_RoundTrips_Identically()
    {
        var map = TestMaps.TriangleWithResources();
        var state = new SimulationState(
            map,
            new[] { new AgentState(0, 1, 2), new AgentState(1, 1, 0) },
            new[] { 0, 1 },
            4);
        var value = new StepOutcome(
            state,
            new StepResult(
                new[] { new Observation(0, map, state.Agents, state.Claims), new Observation(1, map, state.Agents, state.Claims) },
                new[] { new Reward(0, 0.0), new Reward(1, 0.0) },
                new Info(5, true, "tick-limit", 0)));

        var roundTripped = RoundTrip(value);
        Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(roundTripped));
    }

    [Fact]
    public void ActionArray_RoundTrips_Individually()
    {
        var value = new[]
        {
            new AgentAction(ActionKind.Wait),
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 2),
        };

        var roundTripped = RoundTripArray(value);
        Assert.Equal(value, roundTripped);
    }
}