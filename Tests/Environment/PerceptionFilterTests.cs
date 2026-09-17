using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Environment;

/// <summary>
/// Coverage for the vision-bounded <see cref="PerceptionFilter"/> and
/// <see cref="PartialObservation"/> contract. Fixtures replay the real
/// simulation over a ring map (zones 0-3, chokes C0(0-1) C1(1-2) C2(2-3)
/// C3(3-0)) so masking and stale-memory behavior are asserted against ground
/// truth produced by the environment itself.
/// </summary>
public class PerceptionFilterTests
{
    private static readonly MapGraph Ring = new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(2, 0)),
            new Zone(2, new GridPoint(2, 2)),
            new Zone(3, new GridPoint(0, 2)),
        },
        new[]
        {
            new ResourceNode(0, 1, new GridPoint(2, 1)),
            new ResourceNode(1, 2, new GridPoint(3, 2)),
            new ResourceNode(2, 3, new GridPoint(1, 2)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
            new ChokePoint(2, 2, 3),
            new ChokePoint(3, 3, 0),
        });

    [Fact]
    public void VisionTwo_MasksZoneAndContentsBeyondTwoHops()
    {
        // Observer at zone 0. Distances: z0=0 (observed), z1=1, z3=1 (observed),
        // z2=2. With Vision=2 zone 2 is observed; with Vision=1 it is masked.
        // Build a full observation for the observer sitting in zone 0 with one
        // rival agent and a far resource. R1@z2, R2@z3.
        var observation = ObservationFor(
            observerZone: 0,
            rivalZone: 2,
            claims: new[] { 1 });

        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);
        var partial = filter.Project(tick: 1, observation);

        Assert.Equal(KnowledgeStatus.Observed, partial.Zones[0].Status);
        Assert.Equal(KnowledgeStatus.Observed, partial.Zones[1].Status);
        Assert.Equal(KnowledgeStatus.Observed, partial.Zones[3].Status);
        Assert.Equal(KnowledgeStatus.Unknown, partial.Zones[2].Status);
        Assert.Null(partial.Zones[2].LastKnownPosition);
        Assert.Equal(-1, partial.Zones[2].LastSeenTick);

        // Observed zones expose their choke exits; masked zones expose none.
        Assert.Equal(new[] { 1, 3 }, partial.Zones[0].ObservedNeighbors);
        Assert.Empty(partial.Zones[2].ObservedNeighbors);

        // Resource 0 in zone 1: observed. Resource 1 in zone 2: masked as it
        // sits beyond the cone. Resource 2 in zone 3 (observed) but unclaimed:
        // still observed (resources are visible, claim status is separate).
        Assert.Equal(KnowledgeStatus.Observed, partial.Resources[0].Status);
        Assert.Equal(1, partial.Resources[0].ZoneId);
        Assert.Equal(KnowledgeStatus.Unknown, partial.Resources[1].Status);
        Assert.Null(partial.Resources[1].ZoneId);
        Assert.Equal(-1, partial.Resources[1].LastSeenTick);
        Assert.Equal(KnowledgeStatus.Observed, partial.Resources[2].Status);

        // Rival in zone 2 is masked; the observer sees itself (zone 0) real-time.
        Assert.Equal(KnowledgeStatus.Observed, partial.Agents[0].Status);
        Assert.Equal(0, partial.Agents[0].LastKnownState!.ZoneId);
        Assert.Equal(KnowledgeStatus.Unknown, partial.Agents[1].Status);
        Assert.Null(partial.Agents[1].LastKnownState);

        // Claimed resource 1 sits in masked zone 2 -> excluded from visible claims.
        Assert.Empty(partial.VisibleClaims);
    }

    [Fact]
    public void VisionTwo_ExpandsTheConeToTwoHops()
    {
        var observation = ObservationFor(observerZone: 0, rivalZone: 2, claims: new[] { 1 });
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 2);
        var partial = filter.Project(tick: 1, observation);

        Assert.Equal(KnowledgeStatus.Observed, partial.Zones[2].Status);
        Assert.Equal(KnowledgeStatus.Observed, partial.Resources[1].Status);
        Assert.Equal(KnowledgeStatus.Observed, partial.Agents[1].Status);
        Assert.Equal(new[] { 1 }, partial.VisibleClaims);
    }

    [Fact]
    public void UnboundedVision_ExposesTheFullMapEveryTick()
    {
        var observation = ObservationFor(observerZone: 0, rivalZone: 2, claims: new[] { 1 });
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: SimulationConfig.UnboundedVision);
        var partial = filter.Project(tick: 1, observation);

        Assert.All(partial.Zones, zone => Assert.Equal(KnowledgeStatus.Observed, zone.Status));
        Assert.All(partial.Resources, resource => Assert.Equal(KnowledgeStatus.Observed, resource.Status));
        Assert.All(partial.Agents, agent => Assert.Equal(KnowledgeStatus.Observed, agent.Status));
        Assert.Equal(new[] { 1 }, partial.VisibleClaims);
    }

    [Fact]
    public void MemoryRecordsUpdateOnRevisit_ThenGoStalePreservingTick()
    {
        // Episode: observer in zone 0 (tick 1), moves to zone 1 (tick 2,
        // bringing zone 2 into the cone), moves back to zone 0 (tick 3,
        // throwing zone 2 back out of the cone).
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, Vision: 1);
        var state = Simulation.CreateInitial(Ring, config);
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);

        var tick1 = filter.Project(1, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Unknown, tick1.Zones[2].Status);

        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) }, config).NextState;
        var tick2 = filter.Project(2, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Observed, tick2.Zones[2].Status);
        Assert.Equal(2, tick2.Zones[2].LastSeenTick);

        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 0), new AgentAction(ActionKind.Wait) }, config).NextState;
        var tick3 = filter.Project(3, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Stale, tick3.Zones[2].Status);
        Assert.Equal(2, tick3.Zones[2].LastSeenTick);
        Assert.Equal(new GridPoint(2, 2), tick3.Zones[2].LastKnownPosition);
    }

    [Fact]
    public void StaleRivalSight_CarriesLastKnownAgentState()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, Vision: 1);
        var state = Simulation.CreateInitial(Ring, config); // agent 1 starts in zone 1 (observed from zone 0, dist 1)
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);

        var tick1 = filter.Project(1, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Observed, tick1.Agents[1].Status);

        // Agent 1 departs for zone 2 (dist 2 from the observer) — out of the cone.
        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Move, ZoneId: 2) }, config).NextState;
        var tick2 = filter.Project(2, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Stale, tick2.Agents[1].Status);
        Assert.Equal(1, tick2.Agents[1].LastKnownState!.ZoneId); // last known position: zone 1
        Assert.Equal(1, tick2.Agents[1].LastSeenTick);
    }

    [Fact]
    public void ResourceMemory_SurvivesDisappearingBeyondTheCone()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, Vision: 1);
        var state = Simulation.CreateInitial(Ring, config);
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);

        var tick1 = filter.Project(1, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Observed, tick1.Resources[0].Status); // R0@z1 within cone from z0

        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) }, config).NextState;
        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 3), new AgentAction(ActionKind.Wait) }, config).NextState; // observer parked in z3, cone = {z3,z0,z2}, R0@z1 out of cone
        var tick3 = filter.Project(3, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Stale, tick3.Resources[0].Status);
        Assert.Equal(1, tick3.Resources[0].ZoneId);
        Assert.Equal(1, tick3.Resources[0].LastSeenTick);
    }

    [Fact]
    public void VisibleClaims_OnlyIncludeClaimsInsideTheCone()
    {
        // Observer parks in z3 (cone V=1 = {z3,z0,z2}). R2@z3 is inside the
        // cone; R0@z1 is inside zone 1, which is out of it. So a claim on R2
        // is observable while a simultaneous claim on R0 is not.
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, Vision: 1);
        var state = Simulation.CreateInitial(Ring, config);
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);

        // Tick 1: agent 0 reaches z3, agent 1 stays on its start zone z1.
        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Move, ZoneId: 3), new AgentAction(ActionKind.Wait) }, config).NextState;

        // Tick 2: both collect from the zones they stand on. R2@z3 -> in cone, R0@z1 -> out.
        state = Simulation.Step(state, new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Collect, ResourceId: 0) }, config).NextState;
        var tick2 = filter.Project(2, BuildObservation(state));

        Assert.Equal(new[] { 2 }, tick2.VisibleClaims);
    }

    [Fact]
    public void Projection_IsDeterministicAcrossRepeatedIdenticalInputs()
    {
        var observation = ObservationFor(observerZone: 0, rivalZone: 2, claims: new[] { 1 });

        var first = new PerceptionFilter(Ring, agentId: 0, vision: 1).Project(1, observation);
        var second = new PerceptionFilter(Ring, agentId: 0, vision: 1).Project(1, observation);
        Assert.Equivalent(first, second, strict: true);
        Assert.False(ReferenceEquals(first, second));
    }

    [Fact]
    public void Reset_ClearsStaleMemory()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 10, Vision: 1);
        var state = Simulation.CreateInitial(Ring, config);
        var filter = new PerceptionFilter(Ring, agentId: 0, vision: 1);
        _ = filter.Project(1, BuildObservation(state));

        filter.Reset();
        var reborn = filter.Project(2, BuildObservation(state));
        Assert.Equal(KnowledgeStatus.Unknown, reborn.Zones[2].Status);
    }

    [Fact]
    public void PartialObservation_RoundTripsThroughJson()
    {
        var observation = ObservationFor(observerZone: 0, rivalZone: 2, claims: new[] { 1 });
        var partial = new PerceptionFilter(Ring, agentId: 0, vision: 1).Project(1, observation);

        var json = JsonSerializer.Serialize(partial);
        var roundTripped = JsonSerializer.Deserialize<PartialObservation>(json);

        Assert.Equivalent(partial, roundTripped, strict: true);
    }

    [Fact]
    public void ZeroVision_ThrowsNeitherZeroNorNegativeConfig()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfig(2, 10, Vision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerceptionFilter(Ring, 0, vision: 0));
    }

    private static Observation ObservationFor(int observerZone, int rivalZone, int[] claims)
    {
        var agents = new[]
        {
            new AgentState(0, observerZone, 0),
            new AgentState(1, rivalZone, 0),
        };
        return new Observation(0, Ring, agents, claims);
    }

    private static Observation BuildObservation(SimulationState state)
    {
        return new Observation(0, Ring, state.Agents, state.Claims);
    }
}