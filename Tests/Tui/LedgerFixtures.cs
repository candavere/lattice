using Lattice.Tui;

namespace Lattice.Tests.Tui;

/// <summary>
/// Synthetic artifacts for the Ledger's tests: built in memory, never written to
/// the repository, and carrying no host facts of any developer's machine.
/// </summary>
/// <remarks>
/// <para>
/// These builders exist so the layout's tests can name the exact numbers they expect
/// on screen. Every value is a literal chosen by the test, which is what lets the
/// assertions be composed from the model rather than read back out of the renderer —
/// a golden compared only against the renderer that produced it can agree with a
/// regression.
/// </para>
/// <para>
/// The provenance here is deliberately neutral (<c>TestOs</c>, a fixed date, a small
/// core count) so nothing about a developer's machine can reach a committed file.
/// </para>
/// </remarks>
internal static class LedgerFixtures
{
    /// <summary>The fixed instant every fixture's artifact claims to have been written.</summary>
    internal static readonly DateTime Written = new(2026, 9, 25, 19, 41, 20, DateTimeKind.Utc);

    /// <summary>A terminal at the size the layout is designed for.</summary>
    internal static readonly PaneSize Full = new(100, 30);

    /// <summary>The narrower terminal the layout falls back to.</summary>
    internal static readonly PaneSize Narrow = new(80, 25);

    /// <summary>The commit every fixture claims, so a test can say which it meant.</summary>
    internal const string Commit = "abc1234";

    /// <summary>
    /// One artifact with one study and the given per-seed rows. The statistics are
    /// derived from the rows so the fixture is internally consistent — a fixture whose
    /// own counts disagreed would make any layout assertion about it meaningless.
    /// </summary>
    internal static LedgerArtifact Artifact(
        string label = "results.json",
        string? commit = Commit,
        int rollouts = 32,
        int maxSteps = 200,
        IReadOnlyList<LedgerSeed>? seeds = null,
        bool? passed = null,
        string? decision = null,
        LedgerAgent? agent = null) =>
        new(
            label,
            commit,
            Written,
            "TestRuntime 1.0",
            "TestOs 1.0",
            4,
            "X64",
            [Study("heldout", rollouts, maxSteps, seeds, passed, decision)],
            agent);

    /// <summary>
    /// One artifact with two suites, so the study table has more than one row to
    /// scroll and the compare pane has two suites to align.
    /// </summary>
    internal static LedgerArtifact TwoSuiteArtifact(
        string label = "results.json",
        IReadOnlyList<LedgerSeed>? devSeeds = null,
        IReadOnlyList<LedgerSeed>? heldoutSeeds = null) =>
        new(
            label,
            Commit,
            Written,
            "TestRuntime 1.0",
            "TestOs 1.0",
            4,
            "X64",
            [
                Study("dev", 32, 200, devSeeds ?? Seeds(2), false, "Fail: not graded here."),
                Study("heldout", 32, 200, heldoutSeeds ?? Seeds(3), true, "Pass: cleared here."),
            ],
            null);

    /// <summary>
    /// A study whose statistics are counted from its own rows, so the wins, draws,
    /// losses and timeouts it reports are the ones a reader can recount.
    /// </summary>
    internal static LedgerStudy Study(
        string suite,
        int rollouts,
        int maxSteps,
        IReadOnlyList<LedgerSeed>? seeds = null,
        bool? passed = null,
        string? decision = null)
    {
        var rows = seeds ?? Seeds(3);
        var outcomes = rows.SelectMany(row => new[] { row.Match0, row.Match1 }).ToArray();
        var matches = outcomes.Length;
        var wins = outcomes.Count(outcome => outcome == LedgerOutcome.PolicyWin);
        var draws = outcomes.Count(outcome => outcome == LedgerOutcome.Draw);
        var losses = outcomes.Count(outcome => outcome == LedgerOutcome.PolicyLoss);
        var timeouts = outcomes.Count(outcome => outcome == LedgerOutcome.Timeout);
        var deltas = rows.Select(row => row.MeanDelta).ToArray();

        var statistics = new LedgerStatistics(
            rows.Count,
            matches,
            deltas.Length == 0 ? 0.0 : deltas.Average(),
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            wins,
            draws,
            losses,
            timeouts,
            Rate(wins, matches),
            Rate(draws, matches),
            Rate(losses, matches),
            Rate(timeouts, matches),
            0.158);

        var verdict = passed ?? statistics.MeanDelta > 0.0;

        return new LedgerStudy(
            suite,
            "MCTS",
            "Scout",
            rollouts,
            maxSteps,
            rows,
            statistics,
            verdict,
            decision ?? (verdict
                ? $"Pass: mean paired delta {statistics.MeanDelta:0.###} > 0 on {rows.Count} seeds."
                : $"Fail: mean paired delta {statistics.MeanDelta:0.###} did not clear 0 on {rows.Count} seeds."));
    }

    /// <summary>
    /// <paramref name="count"/> per-seed rows whose scores make each mirrored pair
    /// differ by one, so the mean delta is a whole number a test can write down.
    /// </summary>
    internal static IReadOnlyList<LedgerSeed> Seeds(int count, ulong first = 2001)
    {
        var rows = new List<LedgerSeed>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new LedgerSeed(
                first + (ulong)i,
                PolicyScoreAtSeat0: 1,
                PolicyScoreAtSeat1: 5,
                BaselineScoreAtSeat0: 4,
                BaselineScoreAtSeat1: 0,
                MeanDelta: 1.0,
                Match0: LedgerOutcome.PolicyLoss,
                Match1: LedgerOutcome.PolicyWin));
        }

        return rows;
    }

    /// <summary>
    /// The external-agent block, with two reasons and two forfeits, so the pane has
    /// every row it can draw.
    /// </summary>
    internal static LedgerAgent Agent(int forfeits = 2) => new(
        [new LedgerAgentFailure("timeout_step", 1), new LedgerAgentFailure("agent_crashed", 2)],
        VoidRuns: 3,
        ["python3", "agent.py"],
        new LedgerAgentLimits(5000, 20000),
        [.. Enumerable.Range(0, forfeits)
            .Select(index => new LedgerForfeit(1001, index % 2, "timeout_step", 4, 1, 0, 3))]);

    private static double Rate(int count, int total) => total == 0 ? 0.0 : count / (double)total;
}
