using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Tests.Analytics;

/// <summary>
/// Deterministic fixtures for the analytics, incident detection, and report
/// generation tests. Every fixture is a real simulation replay over a
/// hand-built ring map (zones 0-3, chokes C0(0-1) C1(1-2) C2(2-3) C3(3-0),
/// agents placed at zone = agentId % zoneCount), so the recordings encode the
/// ground truth the analyses must reproduce — no mock recorded state.
/// </summary>
internal static class AnalyticsFixtures
{
    public const int FixedSeed = 42;

    /// <summary>
    /// Ring map with one resource per id at <paramref name="resourceZones"/>[id].
    /// Each resource sits at its zone's position.
    /// </summary>
    public static MapGraph RingMap(int[] resourceZones)
    {
        var zones = new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(2, 0)),
            new Zone(2, new GridPoint(2, 2)),
            new Zone(3, new GridPoint(0, 2)),
        };

        var resources = resourceZones
            .Select((zoneId, index) => new ResourceNode(index, zoneId, MapPosition(zones, zoneId)))
            .ToArray();

        var chokes = new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
            new ChokePoint(2, 2, 3),
            new ChokePoint(3, 3, 0),
        };

        return new MapGraph(zones, resources, chokes);

        static GridPoint MapPosition(Zone[] zones, int zoneId) =>
            zones.First(zone => zone.Id == zoneId).Position;
    }

    /// <summary>
    /// Replays <paramref name="turns"/> through the pure simulation and wraps
    /// the terminal trajectory into a <see cref="TrajectoryRecording"/>. The
    /// recorded steps, claims, scores, and final metrics are exactly what the
    /// deterministic core produced.
    /// </summary>
    public static TrajectoryRecording Record(
        MapGraph map,
        int agentCount,
        params AgentAction[][] turns)
    {
        var config = new SimulationConfig(agentCount, MaxTicks: 100);
        var results = SimulationDriver.Play(map, config, turns);
        var steps = results
            .Select((result, index) => new TrajectoryStep(index + 1, turns[index], result))
            .ToArray();
        var finalObservation = results[^1].Observations[0];
        return new TrajectoryRecording(
            new TrajectoryHeader(FixedSeed, map, config),
            steps,
            new TrajectoryFinal(
                results[^1].Info.Reason,
                results[^1].Info.WinnerAgentId,
                results.Count,
                finalObservation.AgentStates.Select(agent => agent.Score).ToArray(),
                finalObservation.Claims.Length,
                map.Resources.Length));
    }

    /// <summary>
    /// Fixture-1: 2 agents, 4 resources, clean efficient run. A0 sweeps
    /// Z1→Z2→Z3 collecting one resource each; A1 takes the near one. No
    /// contention. Terminal at tick 6.
    /// </summary>
    public static TrajectoryRecording Fixture1() =>
        Record(
            RingMap([1, 2, 2, 3]),
            agentCount: 2,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Move, ZoneId: 2) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Collect, ResourceId: 1) },
            new AgentAction[] { new(ActionKind.Move, ZoneId: 2), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 2), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Move, ZoneId: 3), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 3), new(ActionKind.Wait) });

    /// <summary>
    /// Fixture-2: 2 agents, 2 resources, exhibiting an inefficient wanderer.
    /// A1's first move heads away from the nearest unclaimed resource
    /// (suboptimal), and it needs 3 moves for what 1 would have done.
    /// Terminal at tick 4.
    /// </summary>
    public static TrajectoryRecording Fixture2() =>
        Record(
            RingMap([1, 2]),
            agentCount: 2,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Move, ZoneId: 0) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Move, ZoneId: 1) },
            new AgentAction[] { new(ActionKind.Wait), new(ActionKind.Move, ZoneId: 2) },
            new AgentAction[] { new(ActionKind.Wait), new(ActionKind.Collect, ResourceId: 1) });

    /// <summary>
    /// Fixture-4: 2 agents, 5 resources. A0 steamrolls the board; A1 mostly
    /// idles. A0's lead first becomes insurmountable at tick 5 (lead 3 vs 2
    /// resources left), so turning points are recorded at ticks 5-8.
    /// </summary>
    public static TrajectoryRecording Fixture4() =>
        Record(
            RingMap([1, 1, 2, 2, 3]),
            agentCount: 2,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 1), new(ActionKind.Move, ZoneId: 2) },
            new AgentAction[] { new(ActionKind.Move, ZoneId: 2), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 2), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 3), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Move, ZoneId: 3), new(ActionKind.Move, ZoneId: 1) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 4), new(ActionKind.Wait) });

    /// <summary>
    /// Fixture-5: 2 agents, 3 resources; one tick of head-to-head contention on
    /// resource 0 at tick 3 (two collectors, winner agent 0), then A1 comes from
    /// behind to win 2-1 at tick 6 — the only turning point.
    /// </summary>
    public static TrajectoryRecording Fixture5() =>
        Record(
            RingMap([1, 2, 2]),
            agentCount: 2,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Wait), new(ActionKind.Wait) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Collect, ResourceId: 0) },
            new AgentAction[] { new(ActionKind.Wait), new(ActionKind.Move, ZoneId: 2) },
            new AgentAction[] { new(ActionKind.Wait), new(ActionKind.Collect, ResourceId: 1) },
            new AgentAction[] { new(ActionKind.Move, ZoneId: 2), new(ActionKind.Collect, ResourceId: 2) });

    /// <summary>
    /// Fixture-6: 3 agents fighting over a single resource R0@Z1. All three
    /// collect in the same tick; exactly one can win — at tick 2 the
    /// tick-interleaved priority seats agent 2 first, so agent 2 takes it.
    /// Terminal at tick 2.
    /// </summary>
    public static TrajectoryRecording Fixture6() =>
        Record(
            RingMap([1]),
            agentCount: 3,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait), new(ActionKind.Move, ZoneId: 1) },
            new AgentAction[] { new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Collect, ResourceId: 0), new(ActionKind.Collect, ResourceId: 0) });

    /// <summary>
    /// Suboptimal/illegal move fixture: A1 attempts a Move to a non-adjacent
    /// zone (Z1 → Z3) and stays put; the environment's no-op rule turns it
    /// into a rejected move. One step, no claims.
    /// </summary>
    public static TrajectoryRecording RejectedMove() =>
        Record(
            RingMap([1]),
            agentCount: 2,
            new AgentAction[] { new(ActionKind.Move, ZoneId: 1), new(ActionKind.Move, ZoneId: 3) });
}