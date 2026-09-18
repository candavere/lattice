using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Tests.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="PairedStudy.Analyze"/>, the mirrored-seat paired
/// statistics behind the evaluation CLI: per-seed delta reconstruction, the
/// the exact mean/median/IQR/stddev numbers (verified against hand-crafted
/// deltas), the t-distribution 95% CI, the decision rule, the under-powered
/// refusal, and the missing-mirror failure mode.
/// </summary>
public class PairedEvaluationTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static MatchResult Match(
        ulong seed,
        string teamA,
        string teamB,
        MatchOutcome outcome,
        int scoreA,
        int scoreB,
        double contention = 0.5) =>
        new(seed,
            TeamAIndex: 0,
            TeamBIndex: 1,
            TeamA: teamA,
            TeamB: teamB,
            Outcome: outcome,
            ScoreA: scoreA,
            ScoreB: scoreB,
            TotalSteps: 10,
            TerminationReason: "resources-exhausted",
            ContentionRate: contention);

    /// <summary>Builds a full mirrored batch for seed d = [1,2,3,4,5].</summary>
    private static BatchEvaluation DeltasOneToFive()
    {
        var matchResults = new List<MatchResult>();
        var deltas = new[] { 1, 2, 3, 4, 5 };
        for (var i = 0; i < deltas.Length; i++)
        {
            var seed = (ulong)i + 1;
            matchResults.Add(Match(
                seed,
                teamA: "MCTS",
                teamB: "Scout",
                outcome: MatchOutcome.TeamAWin,
                scoreA: deltas[i],
                scoreB: 0));
            matchResults.Add(Match(
                seed,
                teamA: "Scout",
                teamB: "MCTS",
                outcome: MatchOutcome.TeamBWin,
                scoreA: 0,
                scoreB: deltas[i]));
        }

        return new BatchEvaluation(matchResults.ToArray(), Array.Empty<PairingSummary>());
    }

    private static BatchEvaluation MirroredEqualScores(int seedCount)
    {
        var matchResults = new List<MatchResult>();
        for (ulong seed = 1; seed <= (ulong)seedCount; seed++)
        {
            matchResults.Add(Match(
                seed, "Target", "Baseline", MatchOutcome.Draw, scoreA: 2, scoreB: 2));
            matchResults.Add(Match(
                seed, "Baseline", "Target", MatchOutcome.Draw, scoreA: 2, scoreB: 2));
        }

        return new BatchEvaluation(matchResults.ToArray(), Array.Empty<PairingSummary>());
    }

    private static BatchEvaluation UniformDelta(int seedCount, int delta)
    {
        var matchResults = new List<MatchResult>();
        for (ulong seed = 1; seed <= (ulong)seedCount; seed++)
        {
            matchResults.Add(Match(
                seed, "MCTS", "Scout", MatchOutcome.TeamAWin, scoreA: delta, scoreB: 0));
            matchResults.Add(Match(
                seed, "Scout", "MCTS", MatchOutcome.TeamBWin, scoreA: 0, scoreB: delta));
        }

        return new BatchEvaluation(matchResults.ToArray(), Array.Empty<PairingSummary>());
    }

    [Fact]
    public void AnnounceStats_MatchHandcraftedDeltas()
    {
        var report = PairedStudy.Analyze(
            suite: "dev",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: DeltasOneToFive());

        Assert.Equal(5, report.PerSeed.Length);
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, report.PerSeed.Select(row => row.Seed));

        var stats = report.Statistics;
        Assert.Equal(5, stats.Seeds);
        Assert.Equal(10, stats.Matches);
        Assert.Equal(3.0, stats.MeanDelta, precision: 9);
        Assert.Equal(3.0, stats.MedianDelta, precision: 9);
        Assert.Equal(2.0, stats.IqrDelta, precision: 9);
        Assert.Equal(Math.Sqrt(2.5), stats.StdDevDelta, precision: 9);
        Assert.Equal(10, stats.Wins);
        Assert.Equal(0, stats.Losses);
        Assert.Equal(0, stats.Draws);
        Assert.Equal(0, stats.Timeouts);
        Assert.Equal(1.0, stats.WinRate, precision: 9);
        Assert.Equal(0.0, stats.TimeoutRate, precision: 9);

        Assert.True(stats.CiLower95 < stats.MeanDelta);
        Assert.True(stats.MeanDelta < stats.CiUpper95);

        Assert.False(report.Passed, "Five seeds must not be graded.");
        Assert.Contains("Not graded", report.Decision);
    }

    [Fact]
    public void MirroredEqualScores_CancelToZeroDelta()
    {
        var report = PairedStudy.Analyze(
            suite: "heldout",
            targetPolicy: "Target",
            baselinePolicy: "Baseline",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: MirroredEqualScores(seedCount: 8));

        var stats = report.Statistics;
        Assert.Equal(0.0, stats.MeanDelta, precision: 9);
        Assert.Equal(0.0, stats.MedianDelta, precision: 9);
        Assert.Equal(0.0, stats.IqrDelta, precision: 9);
        Assert.Equal(0.0, stats.StdDevDelta, precision: 9);
        Assert.Equal(1.0, stats.DrawRate, precision: 9);
        Assert.Equal(0.0, stats.WinRate, precision: 9);
        Assert.All(report.PerSeed, row => Assert.Equal(2, row.PolicyScoreAtSeat0));
        Assert.All(report.PerSeed, row => Assert.Equal(2, row.BaselineScoreAtSeat1));
    }

    [Fact]
    public void UniformPositiveDelta_OverThirtySeeds_PassesDecisionRule()
    {
        var report = PairedStudy.Analyze(
            suite: "heldout",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: UniformDelta(seedCount: 30, delta: 1));

        Assert.True(report.Passed);
        Assert.StartsWith("Pass", report.Decision);
        Assert.Equal(1.0, report.Statistics.CiLower95, precision: 9);
        Assert.Equal(1.0, report.Statistics.CiUpper95, precision: 9);
    }

    [Fact]
    public void UniformNegativeDelta_OverThirtySeeds_FailsDecisionRule()
    {
        var report = PairedStudy.Analyze(
            suite: "heldout",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: UniformDelta(seedCount: 30, delta: -1));

        Assert.False(report.Passed);
        Assert.StartsWith("Fail", report.Decision);
    }

    [Fact]
    public void MissingMirrorSide_Throws()
    {
        var lopsided = new BatchEvaluation(new[]
        {
            Match(1, "MCTS", "Scout", MatchOutcome.TeamAWin, scoreA: 1, scoreB: 0),
        }, Array.Empty<PairingSummary>());

        Assert.Throws<ArgumentException>(() => PairedStudy.Analyze(
            suite: "dev",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: lopsided));
    }

    [Fact]
    public void MatchAndSeedCountsStayConsistent()
    {
        var batch = UniformDelta(seedCount: 40, delta: 2);
        var report = PairedStudy.Analyze(
            suite: "dev",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 32,
            maxStepsPerMatch: 200,
            batch: batch);

        Assert.Equal(40, report.Statistics.Seeds);
        Assert.Equal(40, report.PerSeed.Length);
        Assert.Equal(80, report.Statistics.Matches);
        Assert.Equal(2.0, report.Statistics.MeanDelta, precision: 9);
    }

    [Fact]
    public void ScoutVersusMcts_ThroughHarness_IsDeterministic()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 200, TransitSpeed: 4);
        var search = new MctsSearchConfig(rolloutsPerAction: 2, maxDepth: 12);
        var spec = new EvaluationSpec(
            seeds: new ulong[] { 1001, 1002 },
            mapFactory: _ => TestMaps.TriangleWithResources(),
            simulationConfig: config,
            teams: new IAgentFactory[]
            {
                new MctsAgentFactory(config, search, "MCTS"),
                new ScoutCollectorAgentFactory(name: "Scout"),
            },
            pairings: new[] { (TeamA: 0, TeamB: 1), (TeamA: 1, TeamB: 0) },
            maxSteps: 200);

        var first = PairedStudy.Analyze(
            suite: "dev",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 2,
            maxStepsPerMatch: 200,
            batch: EvaluationHarness.Evaluate(spec));
        var second = PairedStudy.Analyze(
            suite: "dev",
            targetPolicy: "MCTS",
            baselinePolicy: "Scout",
            rolloutsPerAction: 2,
            maxStepsPerMatch: 200,
            batch: EvaluationHarness.Evaluate(spec));

        Assert.Equal(Json(first), Json(second));
        Assert.Equal(2, first.PerSeed.Length);
        Assert.Equal(4, first.Statistics.Matches);
        Assert.Equal(0.0, first.Statistics.MeanDelta, precision: 9);

        // With a tiny rollout budget the mirror-cancelled delta should agree
        // with the per-seed rows: each seed's mean delta is re-derivable from
        // its two raw score pairs, so the aggregate is never a blind number.
        Assert.All(first.PerSeed, row =>
        {
            var delta = (row.PolicyScoreAtSeat0 - row.BaselineScoreAtSeat0
                         + row.PolicyScoreAtSeat1 - row.BaselineScoreAtSeat1) / 2.0;
            Assert.Equal(row.MeanDelta, delta, precision: 9);
        });
    }
}