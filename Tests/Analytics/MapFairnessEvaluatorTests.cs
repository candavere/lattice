using System.Text.Json;
using Lattice.Agents;
using Lattice.Analytics;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Analytics;

/// <summary>
/// Deterministic fixtures for the map fairness tests. Both maps are
/// hand-built 3-zone boards so their mirrored outcomes are hand-computable:
/// the symmetric board must collapse the spawn-bias index to zero and the
/// asymmetric board must report a pinned nonzero divergence.
/// </summary>
internal static class FairnessFixtures
{
    public const ulong ProbeSeed = 9;

    /// <summary>
    /// The simulation parameters fairness measurements run under: two seats,
    /// real transit timing so positional advantage is priced (nothing here
    /// depends on a 1-tick edge, so every capacity gate is live), and a
    /// 200-tick budget that any small fixture exhausts.
    /// </summary>
    public static SimulationConfig EvalConfig => new(AgentCount: 2, MaxTicks: 200, TransitSpeed: 8);

    /// <summary>
    /// Y-mirror-symmetric board across x=8: two identical spawn territories at
    /// (0,0) and (16,0) plus a shared center stash at (8,8). Both spawns are
    /// equidistant from the stash, so whichever seat holds either territory
    /// collects the same amount; the tick-interleaved resolution priority
    /// yields the same outcome in both mirrored seats, which the
    /// territory-averaged metric must cancel out. Expect the SpawnBiasIndex to
    /// be exactly 0.
    /// </summary>
    public static MapGraph SymmetricBoard() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(16, 0)),
            new Zone(2, new GridPoint(8, 8)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(0, 0)),
            new ResourceNode(1, 1, new GridPoint(16, 0)),
            new ResourceNode(2, 2, new GridPoint(8, 8)),
            new ResourceNode(3, 2, new GridPoint(8, 9)),
            new ResourceNode(4, 2, new GridPoint(9, 8)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 0, 2),
            new ChokePoint(2, 1, 2),
        });

    /// <summary>
    /// Asymmetrically placed board: spawn A's territory sits three transit
    /// ticks from the two-resource stash at (8,16) while spawn B's sits five
    /// ticks away, so the near spawn's seat captures the stash in both mirrored
    /// assignments (3 vs 1 over four resources). Expect bias 0.5.
    /// </summary>
    public static MapGraph AsymmetricBoard() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(32, 0)),
            new Zone(2, new GridPoint(8, 16)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(0, 0)),
            new ResourceNode(1, 1, new GridPoint(32, 0)),
            new ResourceNode(2, 2, new GridPoint(8, 16)),
            new ResourceNode(3, 2, new GridPoint(8, 17)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 0, 2),
            new ChokePoint(2, 1, 2),
        });

    public static GeneratorConfig GeneratorConfig() => new(
        MinZones: 3,
        MaxZones: 5,
        MinChokePointsPerZone: 1,
        MinResourcesPerZone: 1,
        MaxResourcesPerZone: 3,
        RetryCap: 50);
}

public class MapFairnessEvaluatorTests
{
    private static readonly MapFairnessEvaluator Evaluator =
        new(FairnessFixtures.EvalConfig);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void SymmetricMap_YieldsZeroSpawnBias()
    {
        var report = Evaluator.Evaluate(FairnessFixtures.SymmetricBoard(), seed: 1);

        Assert.Equal(5, report.TotalResources);
        Assert.Equal(2.5, report.SpawnAMeanScore);
        Assert.Equal(2.5, report.SpawnBMeanScore);
        Assert.Equal(0.0, report.SpawnBiasIndex);
    }

    [Fact]
    public void AsymmetricMap_QuantifiesSpawnBias()
    {
        var report = Evaluator.Evaluate(FairnessFixtures.AsymmetricBoard(), seed: 1);

        // Near spawn's seat collects the stash in both mirrored assignments.
        Assert.Equal(3, report.AssignmentAB.ScoreFromSpawnA);
        Assert.Equal(1, report.AssignmentAB.ScoreFromSpawnB);
        Assert.Equal(3, report.AssignmentBA.ScoreFromSpawnA);
        Assert.Equal(1, report.AssignmentBA.ScoreFromSpawnB);

        Assert.Equal(4, report.TotalResources);
        Assert.Equal(3.0, report.SpawnAMeanScore);
        Assert.Equal(1.0, report.SpawnBMeanScore);
        Assert.Equal(0.5, report.SpawnBiasIndex);
    }

    [Fact]
    public void Evaluate_IsDeterministicForIdenticalInputs()
    {
        var map = FairnessFixtures.AsymmetricBoard();

        Assert.Equal(Json(Evaluator.Evaluate(map, seed: 7)), Json(Evaluator.Evaluate(map, seed: 7)));
    }

    [Fact]
    public void MctsPolicy_EvaluatesDeterministicallyAndReportsBias()
    {
        var mcts = new MapFairnessEvaluator(
            FairnessFixtures.EvalConfig,
            FairnessPolicy.Mcts,
            new MctsSearchConfig(rolloutsPerAction: 2, maxDepth: 6));
        var map = FairnessFixtures.AsymmetricBoard();
        var first = mcts.Evaluate(map, seed: 1);
        var second = mcts.Evaluate(map, seed: 1);

        Assert.Equal(FairnessPolicy.Mcts, first.Policy);
        Assert.Equal(Json(first), Json(second));
        Assert.Equal(0.5, first.SpawnBiasIndex);
    }

    [Fact]
    public void Evaluator_RejectsNonTwoAgentConfig()
    {
        var threeAgents = new SimulationConfig(AgentCount: 3, MaxTicks: 100);

        Assert.Throws<ArgumentException>(() => new MapFairnessEvaluator(threeAgents));
    }

    [Fact]
    public void Evaluator_RejectsSingleZoneMap()
    {
        var oneZone = new MapGraph(
            new[] { new Zone(0, new GridPoint(0, 0)) },
            Array.Empty<ResourceNode>(),
            Array.Empty<ChokePoint>());

        Assert.Throws<ArgumentException>(() => Evaluator.Evaluate(oneZone, seed: 1));
    }

    [Fact]
    public void GeneratorGate_StrictFairnessThreshold_RejectsBiasedCandidates()
    {
        var exception = Assert.Throws<MapGenerationException>(() =>
            MapGenerator.Generate(
                FairnessFixtures.ProbeSeed,
                FairnessFixtures.GeneratorConfig(),
                candidate => Evaluator.Evaluate(candidate, FairnessFixtures.ProbeSeed).SpawnBiasIndex <= 0.0));

        Assert.Contains("acceptance-gate", exception.Message);
    }

    [Fact]
    public void GeneratorGate_PermissiveThreshold_ReturnsByteIdenticalMap()
    {
        var plain = Json(MapGenerator.Generate(FairnessFixtures.ProbeSeed, FairnessFixtures.GeneratorConfig()));
        var gatedFor = FairnessFixtures.ProbeSeed;

        var gated = Json(MapGenerator.Generate(
            gatedFor,
            FairnessFixtures.GeneratorConfig(),
            candidate => Evaluator.Evaluate(candidate, gatedFor).SpawnBiasIndex <= 1.0));

        Assert.Equal(plain, gated);
    }
}