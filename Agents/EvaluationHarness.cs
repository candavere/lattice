using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The result classification for one head-to-head match. A match whose episode
/// reached a terminal tick is a win for whichever side scored strictly more,
/// or a Draw when both pooled the same score; a match that burned the caller's
/// step budget without reaching a terminal tick is a Timeout regardless of
/// who holds a provisional lead.
/// </summary>
public enum MatchOutcome
{
    TeamAWin,
    TeamBWin,
    Draw,
    Timeout,
}

/// <summary>
/// One played match: which two teams were pitted against which, the run seed
/// they saw, the raw scores, and how the episode ended. Kept as a flat
/// per-match row so consumers can inspect (or verify) the exact inputs behind
/// any aggregated statistic.
/// </summary>
public sealed record MatchResult(
    ulong Seed,
    int TeamAIndex,
    int TeamBIndex,
    string TeamA,
    string TeamB,
    MatchOutcome Outcome,
    int ScoreA,
    int ScoreB,
    int TotalSteps,
    string? TerminationReason,
    double ContentionRate);

/// <summary>
/// Aggregated statistics over every seed in one (TeamA, TeamB) pairing:
/// match counts per outcome, the four rates (which sum to 1.0), and mean
/// reward (final score) plus mean contention per side.
/// </summary>
public sealed record PairingSummary(
    int TeamAIndex,
    int TeamBIndex,
    string TeamA,
    string TeamB,
    int Matches,
    int WinsA,
    int WinsB,
    int Draws,
    int Timeouts,
    double WinRateA,
    double WinRateB,
    double DrawRate,
    double TimeoutRate,
    double MeanRewardA,
    double MeanRewardB,
    double MeanContentionRate);

/// <summary>
/// The full result of one batch evaluation: the per-match rows (in seed-then
/// pairing order) and one <see cref="PairingSummary"/> per configured pairing.
/// </summary>
public sealed record BatchEvaluation(MatchResult[] Matches, PairingSummary[] Pairings);

/// <summary>
/// Configuration for a batch evaluation. Validates in the constructor
/// so a defective batch fails loudly: at least one seed, at least one team,
/// at least one pairing, pairings within team bounds, a positive step budget,
/// and a 2-agent simulation config (pairings are head-to-head by design).
/// Maps are produced per-seed by an injected <c>Func&lt;ulong, MapGraph&gt;</c>
/// so this project never depends on the generator — determinism of the maps is
/// the seed-propagation contract of whatever the caller supplies.
/// </summary>
public sealed class EvaluationSpec
{
    public EvaluationSpec(
        ulong[] seeds,
        Func<ulong, MapGraph> mapFactory,
        SimulationConfig simulationConfig,
        IReadOnlyList<IAgentFactory> teams,
        IReadOnlyList<(int TeamA, int TeamB)> pairings,
        int maxSteps)
    {
        if (seeds.Length == 0)
        {
            throw new ArgumentException("At least one seed is required.", nameof(seeds));
        }

        if (teams.Count == 0)
        {
            throw new ArgumentException("At least one team is required.", nameof(teams));
        }

        if (pairings.Count == 0)
        {
            throw new ArgumentException("At least one pairing is required.", nameof(pairings));
        }

        foreach (var (teamA, teamB) in pairings)
        {
            if (teamA < 0 || teamA >= teams.Count || teamB < 0 || teamB >= teams.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pairings),
                    $"Pairing ({teamA}, {teamB}) references a team outside 0..{teams.Count - 1}.");
            }
        }

        if (simulationConfig.AgentCount != 2)
        {
            throw new ArgumentException(
                "Evaluation pairings are head-to-head; SimulationConfig.AgentCount must be 2.",
                nameof(simulationConfig));
        }

        if (maxSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps), maxSteps, "maxSteps must be >= 1.");
        }

        Seeds = seeds;
        MapFactory = mapFactory;
        SimulationConfig = simulationConfig;
        Teams = teams;
        Pairings = pairings;
        MaxSteps = maxSteps;
    }

    /// <summary>The run seeds, in evaluation order.</summary>
    public ulong[] Seeds { get; }

    /// <summary>Per-seed map provider; must be deterministic in the seed.</summary>
    public Func<ulong, MapGraph> MapFactory { get; }

    /// <summary>Shared simulation parameters for every match (AgentCount == 2).</summary>
    public SimulationConfig SimulationConfig { get; }

    /// <summary>Team families, referenced by index from <see cref="Pairings"/>.</summary>
    public IReadOnlyList<IAgentFactory> Teams { get; }

    /// <summary>The ordered (TeamA, TeamB) pairings to evaluate.</summary>
    public IReadOnlyList<(int TeamA, int TeamB)> Pairings { get; }

    /// <summary>Per-match tick budget passed to <see cref="ScenarioRunner"/>.</summary>
    public int MaxSteps { get; }
}

/// <summary>
/// Runs a batch of head-to-head matches: for every (pairing, seed), it
/// generates the map, builds one fresh agent per side, plays the episode via
/// <see cref="ScenarioRunner"/>, and accumulates per-pairing statistics. The
/// harness itself is pure — same spec, same aggregated numbers on a given host —
/// which is what makes win/draw/timeout rates meaningful comparison signals
/// rather than noisy draws.
/// </summary>
public static class EvaluationHarness
{
    /// <summary>
    /// Evaluates every configured pairing against every seed and returns the
    /// raw matches plus aggregated summaries, in pairing order.
    /// </summary>
    public static BatchEvaluation Evaluate(EvaluationSpec spec)
    {
        var matches = new List<MatchResult>();
        foreach (var (teamA, teamB) in spec.Pairings)
        {
            var familyA = spec.Teams[teamA];
            var familyB = spec.Teams[teamB];
            foreach (var seed in spec.Seeds)
            {
                var map = spec.MapFactory(seed);
                var agents = new IAgent[] { familyA.Create(0, seed), familyB.Create(1, seed) };
                var result = ScenarioRunner.Run(map, spec.SimulationConfig, agents, spec.MaxSteps);
                var scoreA = result.Metrics.Agents[0].Score;
                var scoreB = result.Metrics.Agents[1].Score;
                matches.Add(new MatchResult(
                    seed,
                    teamA,
                    teamB,
                    familyA.Name,
                    familyB.Name,
                    Classify(result.Metrics, scoreA, scoreB),
                    scoreA,
                    scoreB,
                    result.Metrics.TotalSteps,
                    result.Metrics.TerminationReason,
                    result.Metrics.ContentionRate));
            }
        }

        var summaries = spec.Pairings
            .Select(pairing => Summarize(pairing, matches))
            .ToArray();
        return new BatchEvaluation(matches.ToArray(), summaries);
    }

    private static MatchOutcome Classify(ScenarioMetrics metrics, int scoreA, int scoreB)
    {
        if (!metrics.Terminated)
        {
            return MatchOutcome.Timeout;
        }

        if (scoreA == scoreB)
        {
            return MatchOutcome.Draw;
        }

        return scoreA > scoreB ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;
    }

    private static PairingSummary Summarize((int TeamA, int TeamB) pairing, List<MatchResult> matches)
    {
        var rows = matches
            .Where(m => m.TeamAIndex == pairing.TeamA && m.TeamBIndex == pairing.TeamB)
            .ToArray();
        var count = rows.Length;
        var winsA = rows.Count(m => m.Outcome == MatchOutcome.TeamAWin);
        var winsB = rows.Count(m => m.Outcome == MatchOutcome.TeamBWin);
        var draws = rows.Count(m => m.Outcome == MatchOutcome.Draw);
        var timeouts = rows.Count(m => m.Outcome == MatchOutcome.Timeout);

        return new PairingSummary(
            pairing.TeamA,
            pairing.TeamB,
            rows[0].TeamA,
            rows[0].TeamB,
            count,
            winsA,
            winsB,
            draws,
            timeouts,
            winsA / (double)count,
            winsB / (double)count,
            draws / (double)count,
            timeouts / (double)count,
            rows.Average(m => m.ScoreA),
            rows.Average(m => m.ScoreB),
            rows.Average(m => m.ContentionRate));
    }
}