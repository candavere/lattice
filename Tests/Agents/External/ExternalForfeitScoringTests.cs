using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// The forfeit rule of spec §9.3: a match that ends in an agent failure is
/// scored from <b>0 for the external side</b>, whatever the scoreboard said when
/// the plumbing broke.
/// </summary>
/// <remarks>
/// <para>
/// <b>The incentive this removes.</b> The paired delta is computed from scores,
/// so before the rule a failed match carried the scores it had reached. An agent
/// that led 5-1 and then stopped answering therefore banked a
/// <c>+4</c> contribution to the mean paired delta <em>and</em> took the loss in
/// the outcome rates. Leading and then stalling was strictly better than not
/// leading at all: the crash was free. The delta measures how well the policy
/// played, and a policy that stopped playing did not play well, so a number that
/// rewarded the stall was measuring the wrong thing.
/// </para>
/// <para>
/// <b>Why the partial scores are still recorded.</b> Zeroing the row loses real
/// information — how far the match actually got — and losing it would make a
/// failure indistinguishable from a match that never started. So the raw
/// partials are kept as <see cref="ExternalMatchResult.PartialScoreA"/> and
/// <see cref="ExternalMatchResult.PartialScoreB"/>, on the result rather than on
/// the row. That placement is the guarantee, not a convention: the analyzer reads
/// <see cref="MatchResult"/> and never sees these fields, so they cannot enter a
/// statistic even if a future change tried to.
/// </para>
/// <para>
/// The rows here are constructed rather than played, for the same reason
/// <c>ExternalPairedStudyTests</c> constructs them: the rule under test is a
/// function of the seat and the two scores, and a test that could only fail by
/// also getting a process, a map, and a policy right would not say which broke.
/// The scores that reach the row are computed by the production function under
/// test, not by this file, so the assertions are about the rule rather than
/// about a restatement of it. A real spawned match carrying the same rule is
/// covered at the end of the file.
/// </para>
/// </remarks>
public class ExternalForfeitScoringTests
{
    private const ulong Seed = 2001;

    private const string External = "external";
    private const string Baseline = "Scout";

    [Fact]
    public void An_Agent_That_Leads_Five_One_And_Then_Stalls_Is_Scored_From_Zero()
    {
        // The whole point in one case: 5-1 on the board at the moment the agent
        // stopped answering, and the row the analyzer sees is 0 for the external
        // side. The opponent keeps the 1 it had actually earned.
        var result = Stalled(externalSlot: 0, externalPartial: 5, opponentPartial: 1);

        var forfeited = ExternalMatchRunner.ScoreWithForfeit(0, (Slot0Score: 5, Slot1Score: 1));

        // Slot-indexed, like every row this runner writes: ScoreA is slot 0.
        Assert.Equal(0, forfeited.ScoreA);
        Assert.Equal(1, forfeited.ScoreB);

        // The raw partials survive, on the result, out of the row's reach.
        Assert.Equal(5, forfeited.PartialScoreA);
        Assert.Equal(1, forfeited.PartialScoreB);

        Assert.Equal(5, result.PartialScoreA);
        Assert.Equal(1, result.PartialScoreB);
        Assert.Equal(0, result.Match!.ScoreA);
        Assert.Equal(1, result.Match.ScoreB);

        // And the outcome is still a loss, because the forfeit moved a score and
        // not an outcome.
        Assert.Equal(MatchOutcome.TeamBWin, result.Match.Outcome);
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);
    }

    [Fact]
    public void The_Forfeit_Follows_The_Seat_Rather_Than_The_Field_Position()
    {
        // The same 5-1 from the mirrored seat. The row is indexed by SLOT, so at
        // seat 1 the external agent's zero is ScoreB and the opponent's kept 1 is
        // ScoreA. A rule written as "ScoreA = 0" would zero the baseline's score
        // here and hand the external agent a free point.
        var result = Stalled(externalSlot: 1, externalPartial: 5, opponentPartial: 1);

        Assert.Equal(1, result.Match!.ScoreA);
        Assert.Equal(0, result.Match.ScoreB);

        // The partials are indexed by SLOT like the row, so at seat 1 the
        // opponent's kept 1 is PartialScoreA and the external agent's abandoned
        // 5 is PartialScoreB. Reading them by team rather than by slot would
        // attribute the stall's lead to the wrong agent.
        Assert.Equal(1, result.PartialScoreA);
        Assert.Equal(5, result.PartialScoreB);

        // Through the bridge and the real analyzer, with both mirrored seatings
        // as the paired rule requires: the external agent's score is the zero on
        // both sides of the pair, so the delta is the opponent's kept score taken
        // away, never a credit. Before the rule this same pair would have
        // averaged (4 + 4) / 2 = +4 while recording two losses.
        var report = ExternalPairedStudy.Analyze(
            "dev", External, Baseline, 32, 200,
            [
                Stalled(externalSlot: 0, externalPartial: 5, opponentPartial: 1),
                Stalled(externalSlot: 1, externalPartial: 5, opponentPartial: 1),
            ]);

        Assert.Equal(-1.0, report.Statistics.MeanDelta, 10);
        Assert.Equal(2, report.Statistics.Losses);
        Assert.Equal(0, report.Statistics.Wins);
    }

    [Theory]
    // Leading by a lot and stalling: the case the rule exists for.
    [InlineData(9, 0)]
    [InlineData(5, 1)]
    [InlineData(2, 0)]
    [InlineData(1, 0)]
    // Level and stalling.
    [InlineData(3, 3)]
    // Behind and stalling: already negative, and must not get worse or better.
    [InlineData(0, 4)]
    [InlineData(1, 7)]
    public void Stalling_Can_Never_Produce_A_Positive_Delta_For_The_External_Side(
        int externalPartial,
        int opponentPartial)
    {
        // Every lead/stall pair, from both seats, through the real analyzer. The
        // bound is structural rather than incidental: the external side's score
        // enters the delta as 0 and the opponent's as a non-negative integer, so
        // each mirrored delta is -(opponent) and the mean cannot be positive. The
        // table is here so a future change to the rule has to break a case rather
        // than merely look plausible.
        var results = new[]
        {
            Stalled(externalSlot: 0, externalPartial, opponentPartial),
            Stalled(externalSlot: 1, externalPartial, opponentPartial),
        };

        var rows = ExternalPairedStudy.Rows(results, External, Baseline);
        var report = ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results);

        // The rows the analyzer is handed carry the forfeit: the external side's
        // score is 0 on both, and the opponent's is the score it kept.
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, rows[0].TeamA == External ? rows[0].ScoreA : rows[0].ScoreB);
        Assert.Equal(0, rows[1].TeamA == External ? rows[1].ScoreA : rows[1].ScoreB);

        Assert.True(
            report.Statistics.MeanDelta <= 0.0,
            $"a stalled external agent produced a positive delta of {report.Statistics.MeanDelta} " +
            $"(external {externalPartial}, opponent {opponentPartial}).");

        // A stall is a loss, never a win, whatever the board said.
        Assert.Equal(2, report.Statistics.Losses);
        Assert.Equal(0, report.Statistics.Wins);
    }

    [Fact]
    public void A_Clean_Match_Is_Untouched_By_The_Forfeit()
    {
        // The rule is scoped to failures. A match that played to a normal ending
        // keeps its real scores in the row, which is what keeps an ordinary
        // study reading exactly as it did before the rule existed.
        var result = Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3);

        Assert.Equal(7, result.Match!.ScoreA);
        Assert.Equal(3, result.Match.ScoreB);
        Assert.Equal(MatchOutcome.TeamAWin, result.ExternalOutcome);

        // No partials: there was no failure to report one for.
        Assert.Null(result.PartialScoreA);
        Assert.Null(result.PartialScoreB);
    }

    [Fact]
    public void A_Clean_Mirrored_Pair_Still_Scores_Its_Real_Delta()
    {
        // The before/after comparison in one assertion: a clean 7-3 pair from both
        // seats is a +4 mean delta, which is the number the in-process path
        // produces for the same rows. A forfeit applied too broadly would show up
        // here first.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3),
            Completed(externalSlot: 1, slot0Score: 3, slot1Score: 7),
        };

        var external = ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results);

        var inProcess = PairedStudy.Analyze(
            "dev",
            External,
            Baseline,
            32,
            200,
            new BatchEvaluation(ExternalPairedStudy.Rows(results, External, Baseline), []));

        Assert.Equal(4.0, external.Statistics.MeanDelta, 10);
        Assert.Equal(inProcess.Statistics, external.Statistics);
        Assert.Equal(inProcess.Decision, external.Decision);
        Assert.Equal(inProcess.Passed, external.Passed);
    }

    [Fact]
    public void A_Handshake_Failure_Stays_Zero_Zero_And_The_Opponent_Keeps_Zero()
    {
        // No step ever began, so there is no partial to report: the forfeit and
        // the honest 0-0 are the same row. The opponent keeps its score at the
        // moment of failure, which for a handshake is 0, so nothing is invented
        // in either direction.
        var forfeited = ExternalMatchRunner.ScoreWithForfeit(0, scoresAtFailure: null);

        Assert.Equal(0, forfeited.ScoreA);
        Assert.Equal(0, forfeited.ScoreB);
        Assert.Equal(0, forfeited.PartialScoreA);
        Assert.Equal(0, forfeited.PartialScoreB);
    }

    [Fact]
    public void A_Void_Run_Still_Contributes_No_Row_And_No_Statistics()
    {
        // host_limit is not an agent failure, so the forfeit never reaches it: a
        // void run is still no row, no delta, and no denominator. This is the
        // carve-out that makes "not a loss" true, and it must survive the rule.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3),
            Completed(externalSlot: 1, slot0Score: 3, slot1Score: 7),
            Void(seed: 2002, externalSlot: 0),
            Void(seed: 2002, externalSlot: 1),
        };

        Assert.Equal(2, ExternalPairedStudy.Rows(results, External, Baseline).Length);

        var report = ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results);
        Assert.Equal(1, report.Statistics.Seeds);
        Assert.Equal(4.0, report.Statistics.MeanDelta, 10);
        Assert.Equal(2, ExternalMatchRunner.Report(results).VoidRuns);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Real_Spawned_Agent_That_Stalls_Forfeits_Its_Score()
    {
        // The rule on a real process, not on a constructed row: the stub plays
        // legally, then stops answering, and the row the runner produces is
        // scored from 0 for the external side with the partials preserved. This is
        // what proves the forfeit is applied on the path a real agent takes, and
        // that the reason code still rides along on the row.
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            ExternalAgentTestHost.TwoZoneMap(),
            ExternalAgentTestHost.Config(maxTicks: 6),
            ExternalAgentTestHost.Launch("hang-at-step", "3"),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: 6,
            limits: ExternalAgentTestHost.StandardBudget(maxTicks: 6)));

        Assert.True(result.ChildExited, "the stalled child was not observed to have exited.");
        if (result.ChildProcessId is { } childId)
        {
            ExternalAgentTestHost.AssertNoProcessLeft(childId);
        }

        Assert.True(result.IsAgentFailure);
        Assert.Equal("timeout_step", result.Fault!.Reason.ToWireString());

        // The external side's score is 0 in the row that will be analyzed.
        Assert.Equal(0, result.Match!.ScoreA);

        // And the outcome is still a loss, still with the reason code on the row.
        Assert.Equal(MatchOutcome.TeamBWin, result.Match.Outcome);
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);
        Assert.Equal("timeout_step", result.Match.TerminationReason);

        // The failure count is unchanged by the forfeit: it is a count of
        // plumbing, and a stall still counts once.
        var report = ExternalMatchRunner.Report([result]);
        Assert.Equal(1, report.AgentFailureCount);
        Assert.Equal(1, report.AgentFailuresByCode[ProtocolReason.TimeoutStep]);
    }

    /// <summary>
    /// A match the external agent lost to its own plumbing, with the external
    /// side's partial score and the opponent's stated separately. The row is
    /// built from <c>ScoreWithForfeit</c> — the production rule — rather than from
    /// a score written out here, so a change to the rule moves these fixtures.
    /// </summary>
    private static ExternalMatchResult Stalled(int externalSlot, int externalPartial, int opponentPartial)
    {
        // LastKnownScores is indexed by slot, so the external side's partial goes
        // in the slot it actually played.
        var scoresAtFailure = externalSlot == 0
            ? (Slot0Score: externalPartial, Slot1Score: opponentPartial)
            : (Slot0Score: opponentPartial, Slot1Score: externalPartial);

        var forfeited = ExternalMatchRunner.ScoreWithForfeit(externalSlot, scoresAtFailure);
        var reason = ProtocolReason.TimeoutStep;
        var fault = new ExternalAgentFault(reason, "the agent stopped answering.", Step: 12, StderrTail: string.Empty);

        return new ExternalMatchResult(
            Seed,
            externalSlot,
            new MatchResult(
                Seed,
                TeamAIndex: 0,
                TeamBIndex: 1,
                TeamA: External,
                TeamB: Baseline,
                MatchOutcome.TeamBWin,
                ScoreA: forfeited.ScoreA,
                ScoreB: forfeited.ScoreB,
                TotalSteps: 12,
                TerminationReason: reason.ToWireString(),
                ContentionRate: 0.0),
            MatchOutcome.TeamBWin,
            fault,
            Episode: null,
            PartialScoreA: forfeited.PartialScoreA,
            PartialScoreB: forfeited.PartialScoreB);
    }

    /// <summary>A played match, in the shape the runner produces: a row indexed by slot.</summary>
    private static ExternalMatchResult Completed(int externalSlot, int slot0Score, int slot1Score)
    {
        var outcome = slot0Score == slot1Score
            ? MatchOutcome.Draw
            : slot0Score > slot1Score ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;

        return new ExternalMatchResult(
            Seed,
            externalSlot,
            new MatchResult(
                Seed,
                TeamAIndex: 0,
                TeamBIndex: 1,
                TeamA: External,
                TeamB: Baseline,
                outcome,
                ScoreA: slot0Score,
                ScoreB: slot1Score,
                TotalSteps: 200,
                TerminationReason: "resources-exhausted",
                ContentionRate: 0.25),
            outcome,
            Fault: null,
            Episode: null);
    }

    private static ExternalMatchResult Void(ulong seed, int externalSlot) =>
        new(
            seed,
            externalSlot,
            Match: null,
            ExternalOutcome: null,
            new ExternalAgentFault(
                ProtocolReason.HostLimit,
                "the observation would exceed max_line_bytes.",
                Step: 0,
                StderrTail: string.Empty),
            Episode: null);
}
