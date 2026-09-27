using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// The canonical full-state digest: a fixed-field-order, invariant-culture
/// serialization of everything that determines the next tick, hashed with
/// SHA-256. These tests pin the canonical text exactly, so a change to field
/// order, to the derived occupancy, or to the number formatting is a visible
/// diff rather than a silent hash change.
/// </summary>
public class SimulationStateHashTests
{
    private const ulong Seed = 7UL;

    /// <summary>
    /// Two zones joined by one capacity-1 choke — small enough that the whole
    /// canonical text can be asserted by hand.
    /// </summary>
    private static MapGraph SingleChokeMap(int chokeCapacity = 1) => new(
        new[] { new Zone(0, new GridPoint(0, 0)), new Zone(1, new GridPoint(0, 10)) },
        Array.Empty<ResourceNode>(),
        new[] { new ChokePoint(0, 0, 1, chokeCapacity) });

    /// <summary>
    /// Agent 0 parked in zone 1 with 2 points; agent 1 departed zone 0 and is
    /// mid-crossing of the choke with 5 ticks to run. Claims arrive unsorted
    /// so the sort is exercised.
    /// </summary>
    private static SimulationState TwoAgentState(MapGraph? map = null, DynamicMapRuleSet? rules = null)
    {
        var topology = map ?? SingleChokeMap();
        return new SimulationState(
            topology,
            new[]
            {
                new AgentState(0, 1, 2),
                new AgentState(1, 0, 0, new InTransit(0, 1, 5)),
            },
            new[] { 4, 1 },
            3)
        {
            Dynamics = DynamicMapOverrides.ForInitialTick(rules ?? DynamicMapRuleSet.None, topology),
        };
    }

    [Fact]
    public void CanonicalText_IsFixedFieldOrderInvariantAndComplete()
    {
        var text = SimulationStateHash.CanonicalText(TwoAgentState(), Seed);

        Assert.Equal(
            "lattice-state-hash/2\n" +
            "seed=7\n" +
            "tick=3\n" +
            "agents=2\n" +
            "agent.0=0;1;2;-\n" +
            "agent.1=1;0;0;0>1>5\n" +
            "chokes=1\n" +
            "choke.0=0;0;1;1;-;1;1\n" +
            "zones=2\n" +
            "zone.0=0;0;0;2147483647;0\n" +
            "zone.1=1;0;10;2147483647;1\n" +
            "resources=0\n" +
            "claims=1;4\n",
            text);
    }

    [Fact]
    public void Compute_IsDeterministicAcrossCalls()
    {
        var state = TwoAgentState();

        Assert.Equal(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(state, Seed));
    }

    [Fact]
    public void Compute_IsLowercaseHexSha256()
    {
        var hash = SimulationStateHash.Compute(TwoAgentState(), Seed);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void DifferentSeed_ChangesTheHash()
    {
        Assert.NotEqual(
            SimulationStateHash.Compute(TwoAgentState(), 7UL),
            SimulationStateHash.Compute(TwoAgentState(), 8UL));
    }

    [Fact]
    public void DifferentTick_ChangesTheHash()
    {
        var state = TwoAgentState();

        Assert.NotEqual(
            SimulationStateHash.Compute(state, Seed),
            SimulationStateHash.Compute(state with { StepCount = 4 }, Seed));
    }

    [Fact]
    public void DifferentScore_ChangesTheHash()
    {
        var state = TwoAgentState();
        var rescored = state with
        {
            Agents = new[] { state.Agents[0] with { Score = 3 }, state.Agents[1] },
        };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(rescored, Seed));
    }

    [Fact]
    public void DifferentZone_ChangesTheHash()
    {
        var state = TwoAgentState();
        var moved = state with
        {
            Agents = new[] { state.Agents[0] with { ZoneId = 0 }, state.Agents[1] },
        };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(moved, Seed));
    }

    [Fact]
    public void DifferentTransitRemaining_ChangesTheHash()
    {
        var state = TwoAgentState();
        var later = state with
        {
            Agents = new[] { state.Agents[0], state.Agents[1] with { Transit = new InTransit(0, 1, 4) } },
        };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(later, Seed));
    }

    [Fact]
    public void ClaimOrder_DoesNotChangeTheHash()
    {
        var state = TwoAgentState();
        var reordered = state with { Claims = new[] { 1, 4 } };

        Assert.Equal(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(reordered, Seed));
    }

    [Fact]
    public void DifferentClaimSet_ChangesTheHash()
    {
        var state = TwoAgentState();
        var other = state with { Claims = new[] { 1, 5 } };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(other, Seed));
    }

    [Fact]
    public void ChokeCapacityOverride_ChangesTheHash()
    {
        var open = TwoAgentState(rules: new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(0, OpenTicks: 2, ClosedTicks: 2),
        }));

        // A 2-open/2-closed cycle: ticks 0 and 1 are open (effective capacity
        // 1, no override stored because it equals the base), tick 2 is closed
        // (override 0).
        var closed = open with
        {
            StepCount = 2,
            Dynamics = open.Dynamics.Advance(2, open.Claims, open.Map),
        };

        // ...; base 1, override 0, effective 0, derived edge load 1 (agent 1
        // is mid-crossing this very choke).
        Assert.Contains("choke.0=0;0;1;1;0;0;1", SimulationStateHash.CanonicalText(closed, Seed), StringComparison.Ordinal);
        Assert.NotEqual(SimulationStateHash.Compute(open, Seed), SimulationStateHash.Compute(closed, Seed));
    }

    [Fact]
    public void DerivedEdgeLoad_CountsAgentsInTransitOnTheirChoke()
    {
        // Agent 1 is mid-crossing of the only choke, so that choke's derived
        // load is 1 while the zone it departed reports 0 occupancy.
        var text = SimulationStateHash.CanonicalText(TwoAgentState(), Seed);

        Assert.Contains("choke.0=0;0;1;1;-;1;1\n", text, StringComparison.Ordinal);
        Assert.Contains("zone.0=0;0;0;2147483647;0\n", text, StringComparison.Ordinal);
        Assert.Contains("zone.1=1;0;10;2147483647;1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyClaims_SerializeAsAnEmptyValue()
    {
        var state = TwoAgentState() with { Claims = Array.Empty<int>() };

        Assert.Contains("claims=\n", SimulationStateHash.CanonicalText(state, Seed), StringComparison.Ordinal);
    }

    [Fact]
    public void MapWithNoChokes_SerializesZeroChokes()
    {
        var map = new MapGraph(
            new[] { new Zone(0, new GridPoint(0, 0)) },
            Array.Empty<ResourceNode>(),
            Array.Empty<ChokePoint>());
        var state = new SimulationState(map, new[] { new AgentState(0, 0, 0) }, Array.Empty<int>(), 1);

        Assert.Contains("chokes=0\n", SimulationStateHash.CanonicalText(state, Seed), StringComparison.Ordinal);
    }

    /// <summary>
    /// A position is not rendering data: <see cref="Simulation.TransitTicks"/>
    /// reads <c>Zone.Position</c> to compute a crossing's duration, and
    /// <c>PerceptionFilter</c> reads both zone and resource positions to build
    /// the observations a step line records. So a position change is a change
    /// to the state the digest must attest to, and the digest covers it.
    /// </summary>
    [Fact]
    public void AMovedZone_ChangesTheHash()
    {
        var state = TwoAgentState();
        var moved = state with
        {
            Map = state.Map with
            {
                Zones = new[]
                {
                    state.Map.Zones[0],
                    state.Map.Zones[1] with { Position = new GridPoint(0, 11) },
                },
            },
        };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(moved, Seed));
    }

    [Fact]
    public void AMovedResource_ChangesTheHash()
    {
        var map = new MapGraph(
            new[] { new Zone(0, new GridPoint(0, 0)) },
            new[] { new ResourceNode(0, 0, new GridPoint(0, 1)) },
            Array.Empty<ChokePoint>());
        var state = new SimulationState(map, new[] { new AgentState(0, 0, 0) }, Array.Empty<int>(), 1);
        var moved = state with
        {
            Map = map with { Resources = new[] { map.Resources[0] with { Position = new GridPoint(0, 2) } } },
        };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(moved, Seed));
    }

    [Fact]
    public void ReorderedResources_ChangeTheHash()
    {
        // Resource array order is part of the canonical text, so it is hashed in
        // array order like every other collection — a reordering is a different
        // serialization, not a silent collision.
        var first = new ResourceNode(0, 0, new GridPoint(0, 1));
        var second = new ResourceNode(1, 0, new GridPoint(0, 2));
        var map = new MapGraph(
            new[] { new Zone(0, new GridPoint(0, 0)) },
            new[] { first, second },
            Array.Empty<ChokePoint>());
        var state = new SimulationState(map, new[] { new AgentState(0, 0, 0) }, Array.Empty<int>(), 1);
        var swapped = state with { Map = map with { Resources = new[] { second, first } } };

        Assert.NotEqual(SimulationStateHash.Compute(state, Seed), SimulationStateHash.Compute(swapped, Seed));
    }

    /// <summary>
    /// The property the hash exists for: stepping the same episode twice
    /// produces the same digest at every tick, and consecutive ticks differ
    /// (so the digest is not accidentally tick-invariant).
    /// </summary>
    [Fact]
    public void TwoIdenticalRuns_ProduceTheSameDigestAtEveryTick()
    {
        var first = DigestEveryTick();
        var second = DigestEveryTick();

        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct().Count());
    }

    private static List<string> DigestEveryTick()
    {
        var map = new MapGraph(
            new[] { new Zone(0, new GridPoint(0, 0)), new Zone(1, new GridPoint(0, 10)) },
            new[] { new ResourceNode(0, 0, new GridPoint(0, 1)) },
            new[] { new ChokePoint(0, 0, 1, 1) });
        var config = new SimulationConfig(2, 6);
        var state = Simulation.CreateInitial(map, config);
        var digests = new List<string>();

        for (var tick = 0; tick < 4; tick++)
        {
            var actions = new[]
            {
                new AgentAction(ActionKind.Move, ZoneId: tick % 2),
                new AgentAction(ActionKind.Collect, ResourceId: 0),
            };
            var outcome = Simulation.Step(state, actions, config);
            state = outcome.NextState;
            digests.Add(SimulationStateHash.Compute(state, Seed));
        }

        return digests;
    }
}
