using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Analytics;

/// <summary>
/// One high-friction contention event: at <see cref="Tick"/>, at least two
/// agents tried to collect <see cref="ResourceId"/> in the same turn.
/// <see cref="WinnerAgentId"/> is the agent who actually secured the claim —
/// read from the recorded claim transition (ascending-id tie-breaker by
/// construction) — and <see cref="Losers"/> are the participating agents who
/// did not get it.
/// </summary>
public sealed record ContentionEvent(
    int Tick,
    int ResourceId,
    int[] Participants,
    int WinnerAgentId,
    int[] Losers);

/// <summary>
/// A tick after which the current score leader's lead could not be erased by
/// the remaining unclaimed resources: <see cref="Lead"/> (leader minus next
/// best) strictly exceeds <see cref="RemainingResources"/>. Its first
/// occurrence is the moment the lead became insurmountable.
/// </summary>
public sealed record TurningPoint(int Tick, int LeaderAgentId, int Lead, int RemainingResources);

/// <summary>
/// Per-agent inefficiency counters (T6.2): <see cref="IdleSteps"/> Wait
/// actions, <see cref="RejectedMoves"/> Move actions that did not change the
/// agent's zone (illegal or self-targeting — the environment's no-op rule),
/// and <see cref="SuboptimalMoves"/> legal moves that increased the BFS
/// distance to the nearest unclaimed resource.
/// </summary>
public sealed record InefficiencyProfile(int AgentId, int IdleSteps, int RejectedMoves, int SuboptimalMoves);

/// <summary>
/// Everything <see cref="IncidentDetector"/> found in one recorded
/// trajectory: contention events, turning points, and per-agent
/// inefficiency. Like the analyzer, this is a pure function of the recording.
/// </summary>
public sealed record TrajectoryIncidents(
    ContentionEvent[] Contentions,
    TurningPoint[] TurningPoints,
    InefficiencyProfile[] Inefficiency);

/// <summary>
/// Detects contention events, insurmountable-lead turning points, and
/// per-agent inefficiency across a recorded trajectory (T6.2). Read-only over
/// <see cref="TrajectoryRecording"/>; never mutates the simulation core or
/// any input.
/// </summary>
public static class IncidentDetector
{
    /// <summary>
    /// Scans every recorded step. Contention is computed from the recorded
    /// turn actions (per-resource collect groups of size &ge; 2): the winner
    /// is the participant who appears in the tick's recorded claim transition
    /// (first new appearance of the resource in <see cref="Observation.Claims"/>,
    /// matched to the agent occupying the resource's zone), falling back to the
    /// lowest participant id when the claim never materialized. Moves are
    /// judged against the claims as they stood BEFORE the tick resolved (moves
    /// resolve before collects), so suboptimal-move detection uses the state
    /// the agent actually saw. Turning points scan scores after every tick.
    /// </summary>
    public static TrajectoryIncidents Detect(TrajectoryRecording recording)
    {
        var map = recording.Header.Map;
        var steps = recording.Steps;
        var agentCount = recording.Header.SimulationConfig.AgentCount;
        var initial = Simulation.CreateInitial(map, recording.Header.SimulationConfig);
        var previousZones = initial.Agents.ToDictionary(agent => agent.AgentId, agent => agent.ZoneId);

        var idle = new int[agentCount];
        var rejected = new int[agentCount];
        var suboptimal = new int[agentCount];
        var contentions = new List<ContentionEvent>();
        var turningPoints = new List<TurningPoint>();
        var totalResources = map.Resources.Length;
        var claimsBefore = new HashSet<int>();

        foreach (var step in steps)
        {
            var observation = step.Result.Observations[0];
            var zones = observation.AgentStates.ToDictionary(agent => agent.AgentId, agent => agent.ZoneId);
            var scores = observation.AgentStates.ToDictionary(agent => agent.AgentId, agent => agent.Score);
            var claimsNow = new HashSet<int>(observation.Claims);

            DetectContentions(step, map, claimsNow, claimsBefore, zones, contentions);

            for (var agentId = 0; agentId < agentCount; agentId++)
            {
                var action = step.Actions[agentId];
                var preZone = previousZones[agentId];
                var postZone = zones[agentId];
                switch (action.Kind)
                {
                    case ActionKind.Wait:
                        idle[agentId]++;
                        break;
                    case ActionKind.Move:
                        if (preZone == postZone)
                        {
                            rejected[agentId]++;
                        }
                        else
                        {
                            var preDistance = NearestUnclaimedDistance(map, claimsBefore, preZone);
                            var postDistance = NearestUnclaimedDistance(map, claimsBefore, postZone);
                            if (preDistance < int.MaxValue && postDistance > preDistance)
                            {
                                suboptimal[agentId]++;
                            }
                        }

                        break;
                }

                previousZones[agentId] = postZone;
            }

            RecordTurningPoint(step.StepNumber, scores, claimsNow.Count, totalResources, turningPoints);
            claimsBefore = claimsNow;
        }

        return new TrajectoryIncidents(
            contentions.ToArray(),
            turningPoints.ToArray(),
            Enumerable.Range(0, agentCount)
                .Select(agentId => new InefficiencyProfile(agentId, idle[agentId], rejected[agentId], suboptimal[agentId]))
                .ToArray());
    }

    private static void DetectContentions(
        TrajectoryStep step,
        MapGraph map,
        HashSet<int> claimsNow,
        HashSet<int> claimsBefore,
        Dictionary<int, int> zones,
        List<ContentionEvent> contentions)
    {
        var collectorsByResource = new Dictionary<int, List<int>>();
        for (var agentId = 0; agentId < step.Actions.Length; agentId++)
        {
            var action = step.Actions[agentId];
            if (action.Kind != ActionKind.Collect)
            {
                continue;
            }

            if (!collectorsByResource.TryGetValue(action.ResourceId, out var collectors))
            {
                collectors = new List<int>();
                collectorsByResource[action.ResourceId] = collectors;
            }

            collectors.Add(agentId);
        }

        foreach (var pair in collectorsByResource)
        {
            var participants = pair.Value;
            if (participants.Count < 2)
            {
                continue;
            }

            var winner = Claimant(map, pair.Key, claimsNow, claimsBefore, zones) ?? participants.Min();
            contentions.Add(new ContentionEvent(
                step.StepNumber,
                pair.Key,
                participants.OrderBy(id => id).ToArray(),
                winner,
                participants.Where(id => id != winner).OrderBy(id => id).ToArray()));
        }
    }

    /// <summary>
    /// Who actually secured <paramref name="resource"/> this tick: non-null
    /// only when the resource first appears in this tick's claims, mapped to
    /// the agent occupying its zone after the tick. Null when the claim did
    /// not land.
    /// </summary>
    private static int? Claimant(
        MapGraph map,
        int resource,
        HashSet<int> claimsNow,
        HashSet<int> claimsBefore,
        Dictionary<int, int> zones)
    {
        if (claimsBefore.Contains(resource) || !claimsNow.Contains(resource))
        {
            return null;
        }

        var zone = map.Resources[resource].ZoneId;
        var candidates = zones.Where(pair => pair.Value == zone).Select(pair => pair.Key).OrderBy(id => id);
        return candidates.FirstOrDefault() is { } agentId ? agentId : (int?)null;
    }

    private static int NearestUnclaimedDistance(MapGraph map, HashSet<int> claimedResources, int fromZone)
    {
        var best = int.MaxValue;
        foreach (var resource in map.Resources)
        {
            if (claimedResources.Contains(resource.Id))
            {
                continue;
            }

            var distance = MapTraversal.Distance(map, fromZone, resource.ZoneId);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    private static void RecordTurningPoint(
        int tick,
        Dictionary<int, int> scores,
        int claimedCount,
        int totalResources,
        List<TurningPoint> turningPoints)
    {
        var ordered = scores.OrderByDescending(pair => pair.Value).ToArray();
        if (ordered.Length < 2 || ordered[0].Value == ordered[1].Value)
        {
            return;
        }

        var lead = ordered[0].Value - ordered[1].Value;
        var remaining = totalResources - claimedCount;
        if (lead > remaining)
        {
            turningPoints.Add(new TurningPoint(tick, ordered[0].Key, lead, remaining));
        }
    }
}