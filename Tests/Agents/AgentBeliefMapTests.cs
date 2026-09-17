using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentBeliefMap"/>, the T7.2 data structure that
/// folds <see cref="PartialObservation"/> projections into known / stale /
/// unexplored territory, suspected resources, and enemy sightings.
/// </summary>
public class AgentBeliefMapTests
{
    private static readonly GridPoint P = new(0, 0);

    private static ZoneSight Zone(int id, KnowledgeStatus status, int tick, params int[] edges) => new(
        id, status, status == KnowledgeStatus.Unknown ? null : P, status == KnowledgeStatus.Unknown ? -1 : tick, edges);

    private static PartialObservation Obs(
        ZoneSight[] zones,
        ResourceSight[]? resources = null,
        AgentSight[]? agents = null,
        int[]? visibleClaims = null,
        int tick = 1) => new(
        AgentId: 0,
        Tick: tick,
        Vision: 1,
        Zones: zones,
        Resources: resources ?? Array.Empty<ResourceSight>(),
        Agents: agents ?? new[] { new AgentSight(0, KnowledgeStatus.Observed, new AgentState(0, 0, 0), tick) },
        VisibleClaims: visibleClaims ?? Array.Empty<int>());

    [Fact]
    public void ZonesTransition_ObservedToKnown_ThenOutOfSight_ToStale()
    {
        var belief = new AgentBeliefMap(0);
        belief.Update(Obs(new[] { Zone(0, KnowledgeStatus.Observed, 1, 1, 3) }, tick: 1));

        Assert.Equal(TerritoryStatus.Known, belief.Zones[0].Status);
        Assert.Equal(new HashSet<int> { 1, 3 }, belief.Zones[0].KnownEdges);

        belief.Update(Obs(new[]
        {
            Zone(0, KnowledgeStatus.Stale, 1),
            Zone(1, KnowledgeStatus.Observed, 2, 0, 2),
        }, tick: 2));

        Assert.Equal(TerritoryStatus.Stale, belief.Zones[0].Status);
        Assert.Equal(1, belief.Zones[0].LastSeenTick);
    }

    [Fact]
    public void NeverObservedZone_StaysUnexploredAndUnknown()
    {
        var belief = new AgentBeliefMap(0);

        // Observation with 3 zones — zone 2 is Unknown (never observed).
        var obs = new PartialObservation(
            AgentId: 0, Tick: 1, Vision: 1,
            Zones: new[]
            {
                Zone(0, KnowledgeStatus.Observed, 1, 1),
                Zone(1, KnowledgeStatus.Stale, 1),
                Zone(2, KnowledgeStatus.Unknown, -1),
            },
            Resources: Array.Empty<ResourceSight>(),
            Agents: new[] { new AgentSight(0, KnowledgeStatus.Observed, new AgentState(0, 0, 0), 1) },
            VisibleClaims: Array.Empty<int>());
        belief.Update(obs);

        Assert.Equal(TerritoryStatus.Unexplored, belief.Zones[2].Status);
        Assert.Empty(belief.KnownEdges(2));
    }

    [Fact]
    public void UnclaimedResource_FirstVisit_IsNotClaimed_VisibleClaimMarksIt()
    {
        var belief = new AgentBeliefMap(0);
        var sight = new ResourceSight(0, KnowledgeStatus.Observed, 1, P, 1);
        belief.Update(Obs(Array.Empty<ZoneSight>(), resources: new[] { sight }, visibleClaims: Array.Empty<int>(), tick: 1));

        Assert.False(belief.Resources[0].Claimed);
        Assert.Equal(1, belief.Resources[0].ZoneId);

        belief.Update(Obs(Array.Empty<ZoneSight>(), resources: new[] { sight }, visibleClaims: new[] { 0 }, tick: 2));

        Assert.True(belief.Resources[0].Claimed);
    }

    [Fact]
    public void ObservedEnemy_RecordsLastSeenZone_OwnAgentSkipped()
    {
        var belief = new AgentBeliefMap(0);
        belief.Update(Obs(
            Array.Empty<ZoneSight>(),
            agents: new[]
            {
                new AgentSight(0, KnowledgeStatus.Observed, new AgentState(0, 3, 0), 1),
                new AgentSight(1, KnowledgeStatus.Observed, new AgentState(1, 5, 2), 1),
            },
            tick: 1));

        Assert.DoesNotContain(0, belief.Enemies.Keys);
        Assert.Equal(5, belief.Enemies[1].ZoneId);
        Assert.Equal(1, belief.Enemies[1].LastSeenTick);
        Assert.Equal(3, belief.MyZone);
    }

    [Fact]
    public void EdgesAccumulate_AndAreNeverPruned()
    {
        var belief = new AgentBeliefMap(0);

        belief.Update(Obs(new[] { Zone(0, KnowledgeStatus.Observed, 1, 1, 3) }, tick: 1));
        belief.Update(Obs(new[] { Zone(0, KnowledgeStatus.Stale, 1) }, tick: 2));

        Assert.Equal(new HashSet<int> { 1, 3 }, belief.Zones[0].KnownEdges);
    }
}