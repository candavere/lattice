using System.Globalization;
using System.Text;
using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Analytics;

/// <summary>
/// Renders a recorded trajectory into a human report (T6.3). Two surfaces,
/// same aggregates: <see cref="Report"/> produces a full Markdown document
/// (invariant culture, deterministic bytes) suitable for writing to a file,
/// and <see cref="TerminalReport"/> produces a compact ASCII rendering for
/// the terminal. Everything is derived from <see cref="TrajectoryAnalyzer"/>
/// and <see cref="IncidentDetector"/> — the report never re-simulates.
/// </summary>
public static class ReportGenerator
{
    private const char MoveChar = 'M';
    private const char CollectChar = 'R';
    private const char IdleChar = '.';

    /// <summary>
    /// Full Markdown report: summary table, contention events, turning points,
    /// per-agent resource timelines, zone occupancy and edge traversal bars,
    /// and a per-character steps timeline.
    /// </summary>
    public static string Report(TrajectoryRecording recording)
    {
        return Build(recording, markdown: true);
    }

    /// <summary>
    /// Compact ASCII report for interactive terminals: same sections with
    /// '#'-based bars instead of Unicode blocks.
    /// </summary>
    public static string TerminalReport(TrajectoryRecording recording)
    {
        return Build(recording, markdown: false);
    }

    private static string Build(TrajectoryRecording recording, bool markdown)
    {
        var analytics = TrajectoryAnalyzer.Analyze(recording);
        var incidents = IncidentDetector.Detect(recording);
        var agentCount = recording.Header.SimulationConfig.AgentCount;
        var totalTicks = recording.Final.TotalSteps;
        var culture = CultureInfo.InvariantCulture;
        var bar = markdown ? '█' : '#';
        var builder = new StringBuilder();

        AppendHeader(builder, recording, agentCount);
        AppendSummary(builder, recording, analytics, incidents, agentCount, totalTicks, culture);
        AppendContention(builder, incidents);
        AppendTurningPoints(builder, incidents);
        AppendResourceTimeline(builder, analytics);
        AppendOccupancy(builder, analytics, bar);
        AppendEdges(builder, analytics, bar);
        AppendStepsTimeline(builder, recording, agentCount);

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendHeader(StringBuilder builder, TrajectoryRecording recording, int agentCount)
    {
        var header = recording.Header;
        var final = recording.Final;
        var result = final.Reason switch
        {
            "resources-exhausted" => "completed",
            "tick-limit" => "stopped at tick limit",
            null => "action budget exhausted",
            var other => other ?? "unknown",
        };

        builder.Append("# Lattice Tactical Report").Append('\n').Append('\n');
        builder.Append($"seed={header.Seed} | zones={header.Map.Zones.Length} | " +
                       $"resources={header.Map.Resources.Length} | chokes={header.Map.ChokePoints.Length} | " +
                       $"agents={agentCount} | ticks={final.TotalSteps} | result={result} | " +
                       $"winner={(final.WinnerAgentId.HasValue ? $"agent {final.WinnerAgentId}" : "none")}")
            .Append('\n');
        builder.Append('\n');
    }

    private static void AppendSummary(
        StringBuilder builder,
        TrajectoryRecording recording,
        TrajectoryAnalytics analytics,
        TrajectoryIncidents incidents,
        int agentCount,
        int totalTicks,
        CultureInfo culture)
    {
        builder.Append("## Summary").Append('\n');
        builder.Append("| agent | score | moves | efficiency | rating | archetype | idle | contended |")
            .Append('\n');
        builder.Append("|-------|-------|-------|------------|--------|-----------|------|-----------|")
            .Append('\n');

        for (var agentId = 0; agentId < agentCount; agentId++)
        {
            var pathing = analytics.Pathing[agentId];
            var idle = incidents.Inefficiency[agentId].IdleSteps;
            var score = recording.Final.FinalScores[agentId];
            var (wins, losses) = ContentionRecord(incidents.Contentions, agentId);
builder.Append($"| {agentId} | {score} | {pathing.TotalMoves} | " +
                       $"{pathing.OptimalMoves}/{pathing.TotalMoves} " +
                       $"({pathing.Ratio.ToString("0.00", culture)})" +
                       $" | {Rating(pathing.Ratio)} | {Archetype(pathing, analytics, totalTicks)} | " +
                       $"{idle} | {wins}-{losses} |")
                .Append('\n');
        }

        builder.Append('\n');
    }

    private static (int Wins, int Losses) ContentionRecord(ContentionEvent[] contentions, int agentId)
    {
        var wins = contentions.Count(contention => contention.WinnerAgentId == agentId);
        var losses = contentions.Count(contention => contention.Losers.Contains(agentId));
        return (wins, losses);
    }

    private static char Rating(double ratio)
    {
        return ratio switch
        {
            >= 0.85 => 'A',
            >= 0.7 => 'B',
            >= 0.5 => 'C',
            >= 0.25 => 'D',
            > 0.0 => 'E',
            _ => 'F',
        };
    }

    private static string Archetype(PathingEfficiency pathing, TrajectoryAnalytics analytics, int totalTicks)
    {
        var claims = analytics.ResourceTimelines.FirstOrDefault(timeline => timeline.AgentId == pathing.AgentId);
        if (claims is null || claims.Claims.Length == 0)
        {
            return "Passive Wanderer";
        }

        var rusherBudget = (int)Math.Ceiling(totalTicks * 0.2);
        if (claims.Claims[0].Tick <= rusherBudget)
        {
            return "Rusher";
        }

        return pathing.Ratio >= 0.5 ? "Harvester" : "Passive Wanderer";
    }

    private static void AppendContention(StringBuilder builder, TrajectoryIncidents incidents)
    {
        builder.Append("## Contention Events").Append('\n');
        var contentions = incidents.Contentions
            .OrderBy(contention => contention.Tick)
            .ThenBy(contention => contention.ResourceId)
            .ToArray();
        if (contentions.Length == 0)
        {
            builder.Append("none").Append('\n').Append('\n');
            return;
        }

        builder.Append("| tick | resource | participants | winner | losers |").Append('\n');
        builder.Append("|------|----------|--------------|--------|--------|").Append('\n');
        foreach (var contention in contentions)
        {
            builder.Append($"| {contention.Tick} | {contention.ResourceId} | " +
                           $"[{string.Join(",", contention.Participants)}] | {contention.WinnerAgentId} | " +
                           $"[{string.Join(",", contention.Losers)}] |")
                .Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendTurningPoints(StringBuilder builder, TrajectoryIncidents incidents)
    {
        builder.Append("## Turning Points").Append('\n');
        var turningPoints = incidents.TurningPoints.OrderBy(point => point.Tick).ToArray();
        if (turningPoints.Length == 0)
        {
            builder.Append("none").Append('\n').Append('\n');
            return;
        }

        foreach (var point in turningPoints)
        {
            builder.Append($"- tick {point.Tick}: agent {point.LeaderAgentId} unassailable " +
                           $"(lead {point.Lead} > {point.RemainingResources} resources left)")
                .Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendResourceTimeline(StringBuilder builder, TrajectoryAnalytics analytics)
    {
        builder.Append("## Resource Timeline").Append('\n');
        foreach (var timeline in analytics.ResourceTimelines.OrderBy(timeline => timeline.AgentId))
        {
            builder.Append($"agent {timeline.AgentId}: ");
            if (timeline.Claims.Length == 0)
            {
                builder.Append("(none)");
            }
            else
            {
                builder.Append(string.Join(", ",
                    timeline.Claims.Select(claim => $"t{claim.Tick}:R{claim.ResourceId}")));
            }

            builder.Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendOccupancy(StringBuilder builder, TrajectoryAnalytics analytics, char bar)
    {
        builder.Append("## Zone Occupancy (agent-steps)").Append('\n');
        var max = Math.Max(1, analytics.ZoneHeatmap.Select(bucket => bucket.OccupiedSteps).DefaultIfEmpty(0).Max());
        foreach (var bucket in analytics.ZoneHeatmap.OrderBy(bucket => bucket.ZoneId))
        {
            builder.Append($"zone {bucket.ZoneId}: {bucket.OccupiedSteps,3}  " +
                           $"{Bar(bucket.OccupiedSteps, max, bar)}")
                .Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendEdges(StringBuilder builder, TrajectoryAnalytics analytics, char bar)
    {
        builder.Append("## Edge Traversals").Append('\n');
        if (analytics.EdgeHeatmap.Length == 0)
        {
            builder.Append("none").Append('\n').Append('\n');
            return;
        }

        var max = Math.Max(1, analytics.EdgeHeatmap.Select(edge => edge.Traversals).DefaultIfEmpty(0).Max());
        foreach (var edge in analytics.EdgeHeatmap.OrderBy(edge => edge.ChokePointId))
        {
            builder.Append($"choke {edge.ChokePointId} ({edge.FromZoneId} -> {edge.ToZoneId}): " +
                           $"{edge.Traversals,3}  {Bar(edge.Traversals, max, bar)}")
                .Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendStepsTimeline(StringBuilder builder, TrajectoryRecording recording, int agentCount)
    {
        builder.Append("## Steps Timeline").Append('\n');
        for (var agentId = 0; agentId < agentCount; agentId++)
        {
            builder.Append($"agent {agentId}: ");
            foreach (var step in recording.Steps)
            {
                builder.Append(step.Actions[agentId].Kind switch
                {
                    ActionKind.Wait => IdleChar,
                    ActionKind.Move => MoveChar,
                    ActionKind.Collect => CollectChar,
                    _ => '?',
                });
            }

            builder.Append('\n');
        }
    }

    /// <summary>
    /// Scaled to an 8-character bar, one char per eighth of the category max;
    /// non-zero counts always render at least one char so tiny traffic is
    /// visible.
    /// </summary>
    private static string Bar(int count, int max, char bar)
    {
        if (count == 0)
        {
            return string.Empty;
        }

        var length = Math.Max(1, (int)Math.Round(8.0 * count / max));
        return new string(bar, length);
    }
}