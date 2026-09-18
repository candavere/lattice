using Lattice.Analytics;
using Xunit;

namespace Lattice.Tests.Analytics;

public class IncidentDetectorTests
{
    [Fact]
    public void Fixture5_RecordsTheContentionEvent()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture5());
        var contention = Assert.Single(incidents.Contentions);
        Assert.Equal(3, contention.Tick);
        Assert.Equal(0, contention.ResourceId);
        Assert.Equal(new[] { 0, 1 }, contention.Participants);
        Assert.Equal(0, contention.WinnerAgentId);
        Assert.Equal(new[] { 1 }, contention.Losers);
    }

    [Fact]
    public void Fixture6_ThreeAgentScramble_RecordsOneWinner()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture6());
        var contention = Assert.Single(incidents.Contentions);
        Assert.Equal(new[] { 0, 1, 2 }, contention.Participants);
        Assert.Equal(2, contention.WinnerAgentId);
        Assert.Equal(new[] { 0, 1 }, contention.Losers);
    }

    [Fact]
    public void Fixture1_HasNoContention()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture1());
        Assert.Empty(incidents.Contentions);
    }

    [Fact]
    public void Fixture4_FindsInsurmountableLeadAtTick5AndEveryTickAfter()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture4());
        Assert.Equal(
            new[] { 5, 6, 7, 8 },
            incidents.TurningPoints.Select(point => point.Tick));
        var first = incidents.TurningPoints[0];
        Assert.Equal(0, first.LeaderAgentId);
        Assert.Equal(3, first.Lead);
        Assert.Equal(2, first.RemainingResources);
    }

    [Fact]
    public void Fixture2_HasNoTurningPoint()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture2());
        Assert.Empty(incidents.TurningPoints);
    }

    [Fact]
    public void Fixture5_HasTurningPointOnlyAtTheComeback()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture5());
        var point = Assert.Single(incidents.TurningPoints);
        Assert.Equal(6, point.Tick);
        Assert.Equal(1, point.LeaderAgentId);
        Assert.Equal(1, point.Lead);
        Assert.Equal(0, point.RemainingResources);
    }

    [Fact]
    public void Fixture1_CountsAgentOnesIdleTurns()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture1());
        var profile = incidents.Inefficiency.Single(entry => entry.AgentId == 1);
        Assert.Equal(4, profile.IdleSteps);
        Assert.Equal(0, profile.RejectedMoves);
        Assert.Equal(0, profile.SuboptimalMoves);
    }

    [Fact]
    public void Fixture2_FlagsTheHeadAwayMoveAsSuboptimal()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.Fixture2());
        var profile = incidents.Inefficiency.Single(entry => entry.AgentId == 1);
        Assert.Equal(1, profile.SuboptimalMoves);
        Assert.Equal(0, profile.IdleSteps);
    }

    [Fact]
    public void RejectedMove_FixtureCountsTheIllegalMoveAsRejected()
    {
        var incidents = IncidentDetector.Detect(AnalyticsFixtures.RejectedMove());
        var profile = incidents.Inefficiency.Single(entry => entry.AgentId == 1);
        Assert.Equal(1, profile.RejectedMoves);
        Assert.Equal(0, profile.IdleSteps);
    }

    [Fact]
    public void SameRecording_DetectsByteIdenticalIncidents()
    {
        var first = IncidentDetector.Detect(AnalyticsFixtures.Fixture5());
        var second = IncidentDetector.Detect(AnalyticsFixtures.Fixture5());
        Assert.Equivalent(first, second, strict: true);
    }
}