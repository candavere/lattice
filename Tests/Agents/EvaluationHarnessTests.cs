using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="EvaluationHarness"/>: spec validation, outcome
/// classification (win/loss/draw/timeout), the rate invariant that a sum to
/// 1.0, hand-crafted scores, and — the core guarantee — byte-identical batch
/// statistics for byte-identical spec inputs across repeated runs.
/// </summary>
public class EvaluationHarnessTests
{
    private static readonly SimulationConfig TwoPlayer = new(AgentCount: 2, MaxTicks: 40);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static MapGraph TriangleMap() => TestMaps.TriangleWithResources();

    private static MapGraph GeneratedMap(ulong seed) =>
        MapGenerator.Generate(seed, new GeneratorConfig(3, 5, 1, 1, 3, 50));

    private static EvaluationSpec WaitVsWait(int maxSteps = 40, SimulationConfig? config = null, int seedCount = 3)
    {
        var seeds = new ulong[seedCount];
        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = (ulong)i + 1;
        }

        var teams = new IAgentFactory[] { new WaitAgentFactory(), new WaitAgentFactory() };
        return new EvaluationSpec(
            seeds,
            _ => TriangleMap(),
            config ?? TwoPlayer,
            teams,
            new[] { (TeamA: 0, TeamB: 1) },
            maxSteps);
    }

    [Fact]
    public void RejectsNonTwoPlayerConfig()
    {
        var threePlayer = new SimulationConfig(3, 40);
        Assert.Throws<ArgumentException>(() =>
            WaitVsWait(config: threePlayer));
    }

    [Fact]
    public void RejectsPairingOutsideTeamBounds()
    {
        var teams = new IAgentFactory[] { new WaitAgentFactory() };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvaluationSpec(
                new[] { 1UL },
                _ => TriangleMap(),
                TwoPlayer,
                teams,
                new[] { (TeamA: 0, TeamB: 1) },
                40));
    }

    [Fact]
    public void RejectsEmptySeedsAndPairings()
    {
        var teams = new IAgentFactory[] { new WaitAgentFactory(), new WaitAgentFactory() };
        Assert.Throws<ArgumentException>(() =>
            new EvaluationSpec(Array.Empty<ulong>(), _ => TriangleMap(), TwoPlayer, teams,
                new[] { (TeamA: 0, TeamB: 1) }, 40));
        Assert.Throws<ArgumentException>(() =>
            new EvaluationSpec(new[] { 1UL }, _ => TriangleMap(), TwoPlayer, teams,
                Array.Empty<(int, int)>(), 40));
    }

    [Fact]
    public void BudgetCut_ClassifiesEveryMatchAsTimeout()
    {
        // A one-step budget can never reach a terminal tick on MaxTicks 40, so
        // every match must be a Timeout regardless of who leads provisionally.
        var spec = WaitVsWait(maxSteps: 1, seedCount: 5);
        var batch = EvaluationHarness.Evaluate(spec);

        Assert.All(batch.Matches, m => Assert.Equal(MatchOutcome.Timeout, m.Outcome));
        Assert.All(batch.Pairings, p =>
        {
            Assert.Equal(1.0, p.TimeoutRate, precision: 6);
            Assert.Equal(0.0, p.DrawRate, precision: 6);
        });
    }

    [Fact]
    public void TickLimitWithEqualScores_ClassifiesAsDraw()
    {
        // MaxTicks 1 forces a terminal tick-limit after one step; both Wait
        // agents score 0, so every match is a Draw.
        var spec = WaitVsWait(config: new SimulationConfig(2, 1), seedCount: 5);
        var batch = EvaluationHarness.Evaluate(spec);

        Assert.Equal(batch.Matches.Length, batch.Pairings[0].Draws);
        Assert.Equal(1.0, batch.Pairings[0].DrawRate, precision: 6);
        Assert.All(batch.Matches, m => Assert.Equal(MatchOutcome.Draw, m.Outcome));
    }

    [Fact]
    public void GreedyVersusWait_WinsInBothRoles()
    {
        var seeds = new[] { 1UL, 2UL };
        var teams = new IAgentFactory[]
        {
            new GreedyCollectorAgentFactory(),
            new WaitAgentFactory(),
        };

        var spec = new EvaluationSpec(
            seeds,
            _ => TriangleMap(),
            TwoPlayer,
            teams,
            new[] { (TeamA: 0, TeamB: 1), (TeamA: 1, TeamB: 0) },
            40);

        var batch = EvaluationHarness.Evaluate(spec);

        var greedyAsA = batch.Pairings[0];
        Assert.Equal(seeds.Length, greedyAsA.Matches);
        Assert.Equal(seeds.Length, greedyAsA.WinsA);
        Assert.Equal(0, greedyAsA.WinsB);
        Assert.Equal(0.0, greedyAsA.MeanRewardB, precision: 6);
        Assert.Equal(3.0, greedyAsA.MeanRewardA, precision: 6);

        var greedyAsB = batch.Pairings[1];
        Assert.Equal(seeds.Length, greedyAsB.WinsB);
        Assert.Equal(0, greedyAsB.WinsA);
        Assert.Equal(3.0, greedyAsB.MeanRewardB, precision: 6);
    }

    [Fact]
    public void GreedyBeatsRandom_AcrossSeeds()
    {
        // Greedy deterministically sweeps all three resources far faster than
        // uniform randomness, so across a 25-seed batch the win counts must
        // strictly favor Greedy — a behavioral shakeout, not a flaky assertion.
        var seeds = new ulong[25];
        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = (ulong)100 + (ulong)i;
        }

        var teams = new IAgentFactory[]
        {
            new GreedyCollectorAgentFactory(),
            new RandomAgentFactory(),
        };

        var spec = new EvaluationSpec(
            seeds,
            GeneratedMap,
            new SimulationConfig(2, 40),
            teams,
            new[] { (TeamA: 0, TeamB: 1) },
            40);

        var summary = EvaluationHarness.Evaluate(spec).Pairings[0];

        Assert.Equal(seeds.Length, summary.Matches);
        Assert.True(summary.WinsA > summary.WinsB, $"Expected Greedy to win more than Random: {summary.WinsA} vs {summary.WinsB}.");
        Assert.True(summary.WinsA > 0);
    }

    [Fact]
    public void MatchesSumToHalfWithRatesRespectingLimits()
    {
        var seeds = new ulong[] { 7, 11, 13 };
        var teams = new IAgentFactory[]
        {
            new GreedyCollectorAgentFactory(),
            new RandomAgentFactory(),
            new WaitAgentFactory(),
        };

        var spec = new EvaluationSpec(
            seeds,
            GeneratedMap,
            new SimulationConfig(2, 40),
            teams,
            new[] { (TeamA: 0, TeamB: 1), (TeamA: 2, TeamB: 1), (TeamA: 1, TeamB: 0) },
            40);

        var batch = EvaluationHarness.Evaluate(spec);

        Assert.Equal(seeds.Length * spec.Pairings.Count, batch.Matches.Length);
        foreach (var summary in batch.Pairings)
        {
            Assert.Equal(summary.WinsA + summary.WinsB + summary.Draws + summary.Timeouts, summary.Matches);
            var rateSum = summary.WinRateA + summary.WinRateB + summary.DrawRate + summary.TimeoutRate;
            Assert.Equal(1.0, rateSum, precision: 9);
        }
    }

    [Fact]
    public void SameSpec_TwoEvaluations_ByteIdenticalStatistics()
    {
        var seeds = new ulong[12];
        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = 1000 + (ulong)i;
        }

        var spec = new EvaluationSpec(
            seeds,
            GeneratedMap,
            new SimulationConfig(2, 40),
            new IAgentFactory[]
            {
                new GreedyCollectorAgentFactory(),
                new RandomAgentFactory(familySeed: 42),
            },
            new[] { (TeamA: 0, TeamB: 1), (TeamA: 1, TeamB: 0) },
            40);

        var first = EvaluationHarness.Evaluate(spec);
        var second = EvaluationHarness.Evaluate(spec);

        Assert.Equal(Json(first), Json(second));
    }
}