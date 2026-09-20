using Lattice.Environment;

namespace Lattice.Tests.Property;

/// <summary>
/// Seeded, in-range generators for the domain primitives under property test:
/// connected topologies, valid and boundary <see cref="SimulationConfig"/>s,
/// optional dynamic rule sets, and in-space action episodes. Every artifact is
/// a pure function of a seed: <see cref="Scenario(int)"/> reconstructs the
/// exact same (map, config, rules, actions) tuple from the same seed on any
/// run, which is what makes seeded case reproduction possible.
/// </summary>
internal static class Arbitrary
{
    /// <summary>Agent count in the engine's supported 2..4 range.</summary>
    public static int AgentCount(Rng rng) => rng.Next(2, 5);

    /// <summary>
    /// A connected, gameplay-regular map. Guarantees used by the invariant
    /// suite: at least as many zones as agents (so initial round-robin
    /// placement is one agent per zone), zone capacity at least 1 (an agent
    /// always starts somewhere), a passable spanning tree for connectivity
    /// (tree edges carry capacity 1..2 or unlimited), optional extra edges
    /// that reuse existing ids and may be bottleneck-capable (capacity 0..2),
    /// and choke ids equal to their array index so dynamic rules reference the
    /// same identity the step core uses. Positions are arbitrary integers; edge
    /// length is their Manhattan distance.
    /// </summary>
    public static MapGraph Topology(Rng rng, int agentCount)
    {
        var zoneCount = rng.Next(agentCount, 10);
        var zones = new Zone[zoneCount];
        for (var i = 0; i < zoneCount; i++)
        {
            zones[i] = new Zone(i, new GridPoint(rng.Next(0, 1000), rng.Next(0, 1000)), MaxOccupancy: rng.Next(1, 4));
        }

        var chokes = new List<ChokePoint>();

        // Passable spanning tree: every zone i connects to an earlier zone.
        for (var i = 1; i < zoneCount; i++)
        {
            var parent = rng.Next(0, i);
            chokes.Add(new ChokePoint(chokes.Count, parent, i, MaxOccupancy: PickTreeEdgeCapacity(rng)));
        }

        // Optional extra edges (may create loops and bottleneck edge cases).
        var extraEdges = rng.Next(0, zoneCount);
        for (var e = 0; e < extraEdges; e++)
        {
            var from = rng.Next(0, zoneCount);
            var to = rng.Next(0, zoneCount);
            if (from != to && !chokes.Any(c => SameEdge(c, from, to)))
            {
                chokes.Add(new ChokePoint(chokes.Count, from, to, MaxOccupancy: PickExtraEdgeCapacity(rng)));
            }
        }

        // Scatter a modest number of resources over the zones.
        var resources = new List<ResourceNode>();
        var resourceId = 0;
        var perZoneMax = rng.Next(0, 3);
        for (var i = 0; i < zoneCount; i++)
        {
            var count = rng.Next(0, perZoneMax + 1);
            for (var r = 0; r < count; r++)
            {
                resources.Add(new ResourceNode(resourceId++, i, new GridPoint(rng.Next(0, 1000), rng.Next(0, 1000))));
            }
        }

        return new MapGraph(zones, resources.ToArray(), chokes.ToArray());
    }

    /// <summary>
    /// A valid <see cref="SimulationConfig"/>: 2..4 agents, a real tick limit,
    /// vision unbounded or 1..2, transit speed instant or 1..5. Named
    /// <c>Config</c> to avoid shadowing the <see cref="SimulationConfig"/> type
    /// at call sites inside this class.
    /// </summary>
    public static SimulationConfig Config(Rng rng, int agentCount)
    {
        var maxTicks = rng.Next(1, 42);
        var vision = rng.Next(0, 3) switch
        {
            0 => SimulationConfig.UnboundedVision,
            var v => v,
        };
        var transitSpeed = rng.Next(0, 5) switch
        {
            0 => SimulationConfig.InstantTransit,
            var v => v,
        };
        return new SimulationConfig(agentCount, maxTicks, vision, transitSpeed);
    }

    /// <summary>
    /// A dynamic topology policy, or null for a static map. When present, a
    /// timed portcullis governs a randomly chosen choke of the map with a
    /// valid open/closed cycle so the capacity schedule is always well-formed.
    /// </summary>
    public static DynamicMapRuleSet? DynamicRules(Rng rng, MapGraph map)
    {
        if (rng.Next(0, 3) == 0)
        {
            return null;
        }

        var choke = map.ChokePoints[rng.Next(0, map.ChokePoints.Length)];
        var openTicks = rng.Next(1, 4);
        var closedTicks = rng.Next(1, 4);
        var openCapacity = rng.Next(0, 3);
        return new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(choke.Id, openTicks, closedTicks, OpenCapacity: openCapacity, ClosedCapacity: 0),
        });
    }

    /// <summary>
    /// An in-space episode: every move targets a real zone, every collect
    /// targets a real resource, waits are waits — so the recording gate
    /// (<see cref="TrajectoryReplay"/>'s action-space check) always passes.
    /// Moves are not required to target an adjacent zone: the step function
    /// downgrades a non-adjacent target to a no-op, which itself is part of the
    /// replay contract under test.
    /// </summary>
    public static AgentAction[][] Episode(Rng rng, MapGraph map, int agentCount, int maxTicks)
    {
        var turnCount = rng.Next(1, Math.Min(maxTicks, 26) + 1);
        var zoneCount = map.Zones.Length;
        var resourceCount = map.Resources.Length;
        var episode = new AgentAction[turnCount][];

        for (var t = 0; t < turnCount; t++)
        {
            var turn = new AgentAction[agentCount];
            for (var a = 0; a < agentCount; a++)
            {
                turn[a] = rng.Next(0, 3) switch
                {
                    0 => new AgentAction(ActionKind.Wait),
                    1 => new AgentAction(ActionKind.Move, ZoneId: rng.Next(0, zoneCount)),
                    _ when resourceCount > 0 => new AgentAction(ActionKind.Collect, ResourceId: rng.Next(0, resourceCount)),
                    _ => new AgentAction(ActionKind.Wait),
                };
            }

            episode[t] = turn;
        }

        return episode;
    }

    /// <summary>
    /// A complete scenario reconstructed from one seed — the primitive every
    /// property exercises and the unit a failing case reports for reproduction.
    /// </summary>
    public static Scenario Scenario(int seed)
    {
        var rng = new Rng(seed);
        var agentCount = AgentCount(rng);
        var map = Topology(rng, agentCount);
        var config = Config(rng, agentCount);
        var rules = DynamicRules(rng, map);
        var actions = Episode(rng, map, agentCount, config.MaxTicks);
        return new Scenario(map, config, rules, actions, agentCount);
    }

    private static int PickTreeEdgeCapacity(Rng rng) =>
        rng.Next(0, 4) switch
        {
            0 => MapLimits.Unlimited,
            _ => rng.Next(1, 3),
        };

    private static int PickExtraEdgeCapacity(Rng rng) =>
        rng.Next(0, 3) switch
        {
            0 => 0,
            1 => 1,
            _ => MapLimits.Unlimited,
        };

    private static bool SameEdge(ChokePoint choke, int from, int to) =>
        (choke.FromZoneId == from && choke.ToZoneId == to) || (choke.FromZoneId == to && choke.ToZoneId == from);
}

/// <summary>
/// One generated scenario: the map, config, optional rules, the in-space
/// action episode, and the agent count. <see cref="CreateInitial"/> builds the
/// simulation's initial state exactly as a real caller would.
/// </summary>
internal sealed record Scenario(
    MapGraph Map,
    SimulationConfig Config,
    DynamicMapRuleSet? Rules,
    AgentAction[][] Actions,
    int AgentCount)
{
    public SimulationState CreateInitial() =>
        Simulation.CreateInitial(Map, Config, Rules ?? DynamicMapRuleSet.None);
}