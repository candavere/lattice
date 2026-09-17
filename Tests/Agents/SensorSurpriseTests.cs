using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates the fog-of-war sensor-surprise contract: a belief refuted by the
/// current tick's observation — a resource believed unclaimed observed as
/// claimed, or a choke believed traversable observed occupied — fires a
/// <see cref="SensorSurpriseKind"/> and re-plans within the same tick rather
/// than honoring the stale belief.
/// </summary>
public class SensorSurpriseTests
{
    private static MapGraph LineMap(bool withResource) => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 5)),
            new Zone(2, new GridPoint(0, 10)),
        },
        withResource
            ? new[] { new ResourceNode(0, 2, new GridPoint(0, 11)) }
            : Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    private static Observation Obs(MapGraph map, int[] claims, params AgentState[] states) =>
        new(0, map, states, claims);

    [Fact]
    public void ResourceBelievedUnclaimed_ObservedClaimed_FiresSurpriseAndReplans()
    {
        var scout = new ScoutCollectorAgent(0, vision: 2);
        var map = LineMap(withResource: true);

        // First sighting: resource 0 in zone 2 is unclaimed; the scout sets a
        // plan toward it.
        var first = scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0)));
        Assert.Equal(ActionKind.Move, first.Kind);
        Assert.Equal(1, first.ZoneId);
        Assert.Equal(SensorSurpriseKind.None, scout.Belief.LastSurprise);

        // Same zone again, but the resource's collection is now visible as a
        // claim. The belief refuted must fire a surprise and the scout must
        // drop the stale target instead of continuing toward it.
        var second = scout.Decide(Obs(map, new[] { 0 }, new AgentState(0, 0, 0)));

        Assert.Equal(SensorSurpriseKind.ClaimedTarget, scout.Belief.LastSurprise);
        Assert.Equal(2, scout.Belief.LastSurpriseTick);
        Assert.True(scout.Belief.Resources[0].Claimed);
        Assert.Equal(ActionKind.Wait, second.Kind);
    }

    [Fact]
    public void ChokeBelievedTraversable_ObservedOccupied_FiresSurpriseAndReplans()
    {
        var scout = new ScoutCollectorAgent(0, vision: 2);
        var map = LineMap(withResource: true);

        // First sighting: resource 0 is unclaimed and the direct route to zone
        // 2 exists; the scout moves toward it.
        var first = scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0)));
        Assert.Equal(ActionKind.Move, first.Kind);
        Assert.Equal(1, first.ZoneId);

        // Same tick-2 view, but now a rival (agent 1) is mid-crossing the
        // choke 1 <-> 2. The scout observes the choke occupied, must report a
        // saturated-choke surprise, and must not route into the blocked edge
        // this tick — with no other believed route, it re-evaluates and waits
        // rather than charging into the saturation.
        var rivalOnChoke = new AgentState(1, ZoneId: 1, Score: 0, Transit: new InTransit(1, 2, RemainingTicks: 2));
        var second = scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0), rivalOnChoke));

        Assert.Equal(SensorSurpriseKind.SaturatedChoke, scout.Belief.LastSurprise);
        Assert.Equal(2, scout.Belief.LastSurpriseTick);
        Assert.True(scout.Belief.IsEdgeBusy(1, 2, 2));
        Assert.Equal(ActionKind.Wait, second.Kind);
    }

    [Fact]
    public void OccupancySurprise_Expires_AfterTheSightingTick()
    {
        var scout = new ScoutCollectorAgent(0, vision: 2);
        var map = LineMap(withResource: true);

        // Tick 1 establishes the resource target.
        scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0)));

        // Tick 2 observes the choke occupied -> busy at tick 2 only.
        var rivalOnChoke = new AgentState(1, ZoneId: 1, Score: 0, Transit: new InTransit(1, 2, RemainingTicks: 2));
        scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0), rivalOnChoke));

        // Tick 3: the rival has cleared the choke and now holds the resource
        // zone (an enemy position), so the scout waits for a valid target — but
        // crucially it does so from beliefs, not the stale choke sighting: the
        // occupancy has expired and no surprise is reported.
        var third = scout.Decide(Obs(map, Array.Empty<int>(), new AgentState(0, 0, 0), new AgentState(1, 2, 0)));

        Assert.False(scout.Belief.IsEdgeBusy(1, 2, 3));
        Assert.Equal(SensorSurpriseKind.None, scout.Belief.LastSurprise);
        Assert.Equal(ActionKind.Wait, third.Kind);
    }
}