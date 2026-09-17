using Lattice.Agents;
using Lattice.Environment;

namespace Lattice.Analytics;

/// <summary>
/// Which identical-policy family a map fairness evaluation plays on both spawn
/// slots. Greedy is the cheap structural default (zero randomness, one seeded
/// episode per assignment); Mcts prices real transit timing and choke
/// contention in its rollouts, so it can surface spawn bias that a greedy race
/// would wash out — at a higher per-candidate cost.
/// </summary>
public enum FairnessPolicy
{
    Greedy,
    Mcts,
}

/// <summary>
/// The outcome of one mirrored, head-to-head assignment on a <see cref="MapGraph"/>:
/// scores captured per spawn territory rather than per playing slot, because
/// the whole point of the metric is that the board position — not the agent —
/// decides how much is collectable from a territory.
/// </summary>
public sealed record SpawnFairnessRun(
    ulong Seed,
    int ScoreFromSpawnA,
    int ScoreFromSpawnB,
    int TotalSteps,
    string? TerminationReason);

/// <summary>
/// The full fairness verdict for a map: the two mirrored assignment rows, the
/// aggregate per-territory mean scores, and the normalized
/// <see cref="SpawnBiasIndex"/>. The index is 0 when both spawn territories
/// yield the same expected score and 1 when one territory captures every
/// resource over the two assignments — a caller compares it to its own
/// tolerance (e.g. <c>&lt;= 0.25</c> for a forgiving map, <c>0.0</c> for a
/// strictly balanced one).
/// </summary>
public sealed record MapFairnessReport(
    MapGraph Map,
    FairnessPolicy Policy,
    SpawnFairnessRun AssignmentAB,
    SpawnFairnessRun AssignmentBA,
    int TotalResources,
    double SpawnAMeanScore,
    double SpawnBMeanScore,
    double SpawnBiasIndex);

/// <summary>
/// Measures structural spawn bias of a map by playing the same
/// identical policy twice in mirrored seating and comparing how much each
/// spawn territory could collect, averaged over both seats.
///
/// The environment hardwires the starting zone to the agent slot — agent i
/// spawns at zone i (see <see cref="Simulation.CreateInitial"/>) — so the
/// "second assignment has Agent 0 at Spawn B / Agent 1 at Spawn A" inversion
/// is spelled by swapping the two spawn territories in the map graph (zone 0
/// and zone 1 trade positions, resources, and choke endpoints) and replaying
/// the same two-slot roster. On a structurally symmetric map the swapped run is
/// the original run with seat roles exchanged and the bias index collapses to
/// 0; on a map whose spawns are unbalanced, whichever seat ends up holding the
/// favored territory dominates and the divergence is reported.
///
/// Score from Spawn A is the score a contestant collected while seated in
/// spawn A's territory across both runs, so the index aggregates seat order
/// and reveals territory quality alone. Deterministic: identical
/// (map, seed, policy, config) always yields an identical report.
/// </summary>
public sealed class MapFairnessEvaluator
{
    private readonly SimulationConfig _simulationConfig;
    private readonly FairnessPolicy _policy;
    private readonly MctsSearchConfig _search;

    public MapFairnessEvaluator(
        SimulationConfig simulationConfig,
        FairnessPolicy policy = FairnessPolicy.Greedy,
        MctsSearchConfig? search = null)
    {
        if (simulationConfig.AgentCount != 2)
        {
            throw new ArgumentException(
                "Map fairness is a head-to-head measurement; SimulationConfig.AgentCount must be 2.",
                nameof(simulationConfig));
        }

        _simulationConfig = simulationConfig;
        _policy = policy;
        _search = search ?? new MctsSearchConfig();
    }

    /// <summary>
    /// Measures the fairness of <paramref name="map"/> under <paramref name="seed"/>:
    /// the as-shipped assignment (id 0 in spawn A, id 1 in spawn B) plus the
    /// territory-swapped mirror. Throws <see cref="ArgumentException"/> for a
    /// map with fewer than two zones (no two spawn slots) or a config that is
    /// not a 2-agent head-to-head.
    /// </summary>
    public MapFairnessReport Evaluate(MapGraph map, ulong seed)
    {
        if (map.Zones.Length < 2)
        {
            throw new ArgumentException(
                "A fairness evaluation needs at least two zones to seat one contestant per spawn.",
                nameof(map));
        }

        var assignmentAB = Run(map, seed);
        var mirroredRun = Run(SwapSpawnTerritories(map), seed);

        // In the mirrored run agent 0 is seated at spawn B's node and agent 1
        // at spawn A's node (the swap relabels territories, not seats), so the
        // per-territory scores are the oppositely-labeled half of the raw run.
        var assignmentBA = new SpawnFairnessRun(
            Seed: mirroredRun.Seed,
            ScoreFromSpawnA: mirroredRun.ScoreFromSpawnB,
            ScoreFromSpawnB: mirroredRun.ScoreFromSpawnA,
            TotalSteps: mirroredRun.TotalSteps,
            TerminationReason: mirroredRun.TerminationReason);

        var totalResources = map.Resources.Length;

        // Integer-quantized fairness math: the per-territory aggregates are
        // exact long sums of the four integer run scores, and the divergence is
        // |sumA - sumB| over the exact denominator 2 * totalResources. Only the
        // final normalization to the reported scalar is a (single, IEEE-exact)
        // division, so the underlying rank/verdict between territories can
        // never flip through FP rounding or platform variation.
        var sumA = (long)assignmentAB.ScoreFromSpawnA + assignmentBA.ScoreFromSpawnA;
        var sumB = (long)assignmentAB.ScoreFromSpawnB + assignmentBA.ScoreFromSpawnB;
        var divergence = Math.Abs(sumA - sumB);
        var spawnAMean = sumA / 2.0;
        var spawnBMean = sumB / 2.0;
        var biasDenominator = 2L * totalResources;
        var bias = biasDenominator == 0
            ? 0.0
            : Math.Min(1.0, (double)divergence / biasDenominator);

        return new MapFairnessReport(
            Map: map,
            Policy: _policy,
            AssignmentAB: assignmentAB,
            AssignmentBA: assignmentBA,
            TotalResources: totalResources,
            SpawnAMeanScore: spawnAMean,
            SpawnBMeanScore: spawnBMean,
            SpawnBiasIndex: bias);
    }

    private SpawnFairnessRun Run(MapGraph map, ulong seed)
    {
        var result = ScenarioRunner.Run(
            map,
            _simulationConfig,
            new IAgent[]
            {
                Create(_policy, agentId: 0, seed),
                Create(_policy, agentId: 1, seed),
            },
            maxSteps: _simulationConfig.MaxTicks);

        var metrics = result.Metrics;
        return new SpawnFairnessRun(
            Seed: seed,
            ScoreFromSpawnA: metrics.Agents[0].Score,
            ScoreFromSpawnB: metrics.Agents[1].Score,
            TotalSteps: metrics.TotalSteps,
            TerminationReason: metrics.TerminationReason);
    }

    private IAgent Create(FairnessPolicy policy, int agentId, ulong seed) =>
        policy == FairnessPolicy.Greedy
            ? new GreedyCollectorAgent(agentId)
            : new MctsAgent(agentId, _simulationConfig, seed, _search);

    /// <summary>
    /// Swaps the two spawn territories (zone ids 0 and 1): the zones trade
    /// positions and occupancy bounds, resources recorded against one zone flip
    /// to the other, and every choke endpoint referencing them is exchanged.
    /// Zones beyond the spawn pair are untouched, so the rest of the board
    /// stays fixed — the swapped map is exactly "the same map with the starting
    /// territories inverted", which is what a mirrored assignment must play.
    /// </summary>
    private static MapGraph SwapSpawnTerritories(MapGraph map)
    {
        var zone0 = map.Zones.First(zone => zone.Id == 0);
        var zone1 = map.Zones.First(zone => zone.Id == 1);

        var zones = map.Zones
            .Select(zone => zone.Id switch
            {
                0 => zone1 with { Id = 0 },
                1 => zone0 with { Id = 1 },
                _ => zone,
            })
            .ToArray();

        var resources = map.Resources
            .Select(resource => resource with { ZoneId = Swap(resource.ZoneId) })
            .ToArray();

        var chokes = map.ChokePoints
            .Select(choke => choke with
            {
                FromZoneId = Swap(choke.FromZoneId),
                ToZoneId = Swap(choke.ToZoneId),
            })
            .ToArray();

        return new MapGraph(zones, resources, chokes);

        int Swap(int zoneId) => zoneId switch
        {
            0 => 1,
            1 => 0,
            _ => zoneId,
        };
    }
}