using Lattice.Analytics;
using Xunit;

namespace Lattice.Tests.Analytics;

public class TrajectoryAnalyzerTests
{
    [Fact]
    public void Fixture1_ZoneHeatmap_MatchesHandComputedTurnCounts()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        Assert.Equal(
            new[] { (0, 0), (1, 2), (2, 8), (3, 2) },
            analytics.ZoneHeatmap
                .OrderBy(bucket => bucket.ZoneId)
                .Select(bucket => (bucket.ZoneId, bucket.OccupiedSteps)));
    }

    [Fact]
    public void Fixture1_EdgeHeatmap_MatchesGroundTruthTransitions()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        Assert.Equal(
            new[] { (0, 1), (1, 2), (2, 1), (3, 0) },
            analytics.EdgeHeatmap
                .OrderBy(edge => edge.ChokePointId)
                .Select(edge => (edge.ChokePointId, edge.Traversals)));
    }

    [Fact]
    public void Fixture1_Pathing_WasOptimalForBothAgents()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        var byAgent = analytics.Pathing.ToDictionary(entry => entry.AgentId);
        Assert.Equal(3, byAgent[0].TotalMoves);
        Assert.Equal(3, byAgent[0].OptimalMoves);
        Assert.Equal(1.0, byAgent[0].Ratio, 3);
        Assert.Equal(1, byAgent[1].TotalMoves);
        Assert.Equal(1, byAgent[1].OptimalMoves);
        Assert.Equal(1.0, byAgent[1].Ratio, 3);
    }

    [Fact]
    public void Fixture1_ResourceTimeline_RecordsClaimsInTickOrder()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        Assert.Equal(
            new[] { (2, 0), (4, 2), (6, 3) },
            analytics.ResourceTimelines.Single(timeline => timeline.AgentId == 0)
                .Claims.Select(claim => (claim.Tick, claim.ResourceId)));
        Assert.Equal(
            new[] { (2, 1) },
            analytics.ResourceTimelines.Single(timeline => timeline.AgentId == 1)
                .Claims.Select(claim => (claim.Tick, claim.ResourceId)));
    }

    [Fact]
    public void Fixture2_CatchesTheWanderingAgent()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture2());
        var byAgent = analytics.Pathing.ToDictionary(entry => entry.AgentId);
        Assert.Equal(3, byAgent[1].TotalMoves);
        Assert.Equal(1, byAgent[1].OptimalMoves);
        Assert.Equal(1.0 / 3.0, byAgent[1].Ratio, 3);
        Assert.Equal(1.0, byAgent[0].Ratio, 3);
    }

    [Fact]
    public void Fixture2_ZoneHeatmap_MatchesHandComputedTurnCounts()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture2());
        Assert.Equal(
            new[] { (0, 1), (1, 5), (2, 2), (3, 0) },
            analytics.ZoneHeatmap
                .OrderBy(bucket => bucket.ZoneId)
                .Select(bucket => (bucket.ZoneId, bucket.OccupiedSteps)));
    }

    [Fact]
    public void Fixture2_EdgeHeatmap_MatchesGroundTruthTransitions()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture2());
        Assert.Equal(
            new[] { (0, 3), (1, 1), (2, 0), (3, 0) },
            analytics.EdgeHeatmap
                .OrderBy(edge => edge.ChokePointId)
                .Select(edge => (edge.ChokePointId, edge.Traversals)));
    }

    [Fact]
    public void RejectedMove_IsNotCountedAsAMove_AndNoClaimScoresZero()
    {
        var recording = AnalyticsFixtures.RejectedMove();
        var analytics = TrajectoryAnalyzer.Analyze(recording);
        var byAgent = analytics.Pathing.ToDictionary(entry => entry.AgentId);
        Assert.Equal(1, byAgent[0].TotalMoves);
        Assert.Equal(0, byAgent[0].OptimalMoves);
        Assert.Equal(0.0, byAgent[0].Ratio, 3);
    }

    [Fact]
    public void NoMovesAndNoClaims_ScorePerfectEfficiency()
    {
        var recording = AnalyticsFixtures.RejectedMove();
        var analytics = TrajectoryAnalyzer.Analyze(recording);
        var byAgent = analytics.Pathing.ToDictionary(entry => entry.AgentId);
        Assert.Equal(0, byAgent[1].TotalMoves);
        Assert.Equal(0, byAgent[1].OptimalMoves);
        Assert.Equal(1.0, byAgent[1].Ratio, 3);
    }

    [Fact]
    public void Fixture5_AttributionInSharedZones_GoesToActualClaimant()
    {
        var analytics = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture5());
        Assert.Equal(
            new[] { (3, 0) },
            analytics.ResourceTimelines.Single(timeline => timeline.AgentId == 0)
                .Claims.Select(claim => (claim.Tick, claim.ResourceId)));
        Assert.Equal(
            new[] { (5, 1), (6, 2) },
            analytics.ResourceTimelines.Single(timeline => timeline.AgentId == 1)
                .Claims.Select(claim => (claim.Tick, claim.ResourceId)));
    }

    [Fact]
    public void SameRecording_AnalyzesToByteIdenticalAnalytics()
    {
        var first = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        var second = TrajectoryAnalyzer.Analyze(AnalyticsFixtures.Fixture1());
        Assert.False(ReferenceEquals(first, second));
        Assert.Equivalent(first, second, strict: true);
    }
}