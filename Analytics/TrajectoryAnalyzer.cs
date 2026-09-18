using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Analytics;

/// <summary>
/// One resource acquisition by an agent in a recorded episode: the tick the
/// claim landed, the claiming agent, and the resource id claimed.
/// </summary>
public sealed record ClaimEvent(int Tick, int AgentId, int ResourceId);

/// <summary>
/// The ordered resource acquisitions of a single agent (by tick, then by
/// recorded claim order), for acquisition-timeline reporting.
/// </summary>
public sealed record AgentResourceTimeline(int AgentId, ClaimEvent[] Claims);

/// <summary>
/// How many recorded agent-step observations placed any agent in a zone —
/// the "where did agents actually spend their turns" heatmap bucket.
/// </summary>
public sealed record ZoneOccupancy(int ZoneId, int OccupiedSteps);

/// <summary>
/// How many recorded zone transitions crossed a choke point, i.e. molten-edge
/// traversal volume for the choke edge (from/to as recorded on the choke).
/// </summary>
public sealed record EdgeTraversal(int ChokePointId, int FromZoneId, int ToZoneId, int Traversals);

/// <summary>
/// Pathing economy for one agent: <see cref="TotalMoves"/> successful zone
/// transitions actually executed vs <see cref="OptimalMoves"/>, the minimal
/// number of such transitions that would have secured the same resources
/// (BFS-shortest tour through the claimed resources in claim order, from the
/// initial placement). <see cref="Ratio"/> is Optimal/Actual clamped to
/// [0,1]; agents with no moves and no claims score 1.0.
/// </summary>
public sealed record PathingEfficiency(int AgentId, int TotalMoves, int OptimalMoves, double Ratio);

/// <summary>
/// The full spatial/pathing/acquisition analysis of one recorded trajectory.
    /// Purely downstream: every number is a pure function of the
/// <see cref="TrajectoryRecording"/> (header, steps, final) and nothing else.
/// </summary>
public sealed record TrajectoryAnalytics(
    ZoneOccupancy[] ZoneHeatmap,
    EdgeTraversal[] EdgeHeatmap,
    PathingEfficiency[] Pathing,
    AgentResourceTimeline[] ResourceTimelines);

/// <summary>
/// Computes spatial heatmaps, pathing efficiency, and resource acquisition
/// timelines from a recorded trajectory. The analyzer is strictly
/// read-only over <see cref="TrajectoryRecording"/> — it never re-runs the
/// simulation and never mutates any input.
/// </summary>
public static class TrajectoryAnalyzer
{
    /// <summary>
    /// Analyzes <paramref name="recording"/>. Zone occupancy counts one
    /// agent-step per recorded observation per agent (initial placement is
    /// NOT counted — the heatmap reflects occupied *turns*). Edge traversals
    /// count ground-truth zone transitions between consecutive recorded
    /// observations (or from initial placement for step 1). A successful move
    /// is a Move action whose target zone differs from the agent's previous
    /// recorded zone; only successful moves contribute to
    /// <see cref="PathingEfficiency.TotalMoves"/>.
    /// </summary>
    public static TrajectoryAnalytics Analyze(TrajectoryRecording recording)
    {
        var map = recording.Header.Map;
        var steps = recording.Steps;
        var agentCount = recording.Header.SimulationConfig.AgentCount;
        var initial = Simulation.CreateInitial(map, recording.Header.SimulationConfig);
        var initialZones = initial.Agents.ToDictionary(agent => agent.AgentId, agent => agent.ZoneId);

        var previousZones = new Dictionary<int, int>(initialZones);
        var claimed = new HashSet<int>();
        var zoneCounts = new Dictionary<int, int>();
        var edgeCounts = new Dictionary<int, int>();
        var claimsByAgent = new Dictionary<int, List<ClaimEvent>>();
        var totalMoves = new int[agentCount];

        foreach (var step in steps)
        {
            var observation = step.Result.Observations[0];
            var zones = observation.AgentStates.ToDictionary(agent => agent.AgentId, agent => agent.ZoneId);

            foreach (var agent in observation.AgentStates)
            {
                zoneCounts[agent.ZoneId] = zoneCounts.GetValueOrDefault(agent.ZoneId) + 1;
            }

            foreach (var resourceId in observation.Claims)
            {
                if (claimed.Contains(resourceId))
                {
                    continue;
                }

                claimed.Add(resourceId);
                var claimant = NewClaimant(step, zones, map, resourceId);
                if (!claimsByAgent.TryGetValue(claimant, out var list))
                {
                    list = new List<ClaimEvent>();
                    claimsByAgent[claimant] = list;
                }

                list.Add(new ClaimEvent(step.StepNumber, claimant, resourceId));
            }

            for (var agentId = 0; agentId < agentCount; agentId++)
            {
                var action = step.Actions[agentId];
                var postZone = zones[agentId];
                if (action.Kind == ActionKind.Move && action.ZoneId == postZone && previousZones[agentId] != postZone)
                {
                    var choke = MapTraversal.ChokeForHop(map, previousZones[agentId], postZone);
                    if (choke.HasValue)
                    {
                        edgeCounts[choke.Value] = edgeCounts.GetValueOrDefault(choke.Value) + 1;
                    }

                    totalMoves[agentId]++;
                }

                previousZones[agentId] = postZone;
            }
        }

        var pathing = new PathingEfficiency[agentCount];
        for (var agentId = 0; agentId < agentCount; agentId++)
        {
            var claims = claimsByAgent.GetValueOrDefault(agentId) ?? new List<ClaimEvent>();
            var optimalMoves = ComputeOptimalMoves(map, initialZones[agentId], claims);
            var actualMoves = totalMoves[agentId];
            var ratio = actualMoves == 0
                ? optimalMoves == 0 ? 1.0 : 0.0
                : Math.Min(1.0, optimalMoves / (double)actualMoves);
            pathing[agentId] = new PathingEfficiency(agentId, actualMoves, optimalMoves, ratio);
        }

        return new TrajectoryAnalytics(
            BuildZoneHeatmap(map, zoneCounts),
            BuildEdgeHeatmap(map, edgeCounts),
            pathing,
            claimsByAgent.OrderBy(pair => pair.Key)
                .Select(pair => new AgentResourceTimeline(pair.Key, pair.Value.OrderBy(claim => claim.Tick).ToArray()))
                .ToArray());
    }

    /// <summary>
    /// Who claimed <paramref name="resourceId"/> on <paramref name="step"/>:
    /// the collect participant the step actually rewarded (+1 collection
    /// reward), falling back to the lowest participant id and then the lowest
    /// zone occupant for recordings where no reward was recorded. Rewards are
    /// the authoritative, resolution-order-independent signal of the claim, so
    /// shared-zone contention never misattributes the claim under any priority
    /// scheme.
    /// </summary>
    private static int NewClaimant(TrajectoryStep step, Dictionary<int, int> zones, MapGraph map, int resourceId)
    {
        var resourceZone = map.Resources[resourceId].ZoneId;
        var participants = Enumerable.Range(0, step.Actions.Length)
            .Where(agentId =>
                step.Actions[agentId].Kind == ActionKind.Collect
                && step.Actions[agentId].ResourceId == resourceId
                && zones[agentId] == resourceZone)
            .OrderBy(agentId => agentId)
            .ToArray();
        if (participants.Length > 0)
        {
            var rewarded = participants.FirstOrDefault(agentId => step.Result.Rewards[agentId].Value > 0, -1);
            return rewarded >= 0 ? rewarded : participants[0];
        }

        return zones.First(pair => pair.Value == resourceZone).Key;
    }

    /// <summary>
    /// Minimal edge count to visit each claimed resource's zone in claim
    /// order starting from <paramref name="startZone"/>. Unreachable hops
    /// (int.MaxValue distances) contribute nothing and leave the tour
    /// position unchanged — the sum only rewards what is actually reachable.
    /// </summary>
    private static int ComputeOptimalMoves(
        MapGraph map,
        int startZone,
        IReadOnlyList<ClaimEvent> claims)
    {
        var currentZone = startZone;
        var optimal = 0;
        foreach (var claim in claims)
        {
            var resourceZone = map.Resources[claim.ResourceId].ZoneId;
            if (currentZone == resourceZone)
            {
                continue;
            }

            var distance = MapTraversal.Distance(map, currentZone, resourceZone);
            if (distance < int.MaxValue)
            {
                optimal += distance;
                currentZone = resourceZone;
            }
        }

        return optimal;
    }

    private static ZoneOccupancy[] BuildZoneHeatmap(MapGraph map, Dictionary<int, int> zoneCounts)
    {
        return map.Zones
            .OrderBy(zone => zone.Id)
            .Select(zone => new ZoneOccupancy(zone.Id, zoneCounts.GetValueOrDefault(zone.Id)))
            .ToArray();
    }

    private static EdgeTraversal[] BuildEdgeHeatmap(MapGraph map, Dictionary<int, int> edgeCounts)
    {
        return map.ChokePoints
            .OrderBy(choke => choke.Id)
            .Select(choke => new EdgeTraversal(
                choke.Id,
                choke.FromZoneId,
                choke.ToZoneId,
                edgeCounts.GetValueOrDefault(choke.Id)))
            .ToArray();
    }
}