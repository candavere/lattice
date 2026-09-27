using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// The bridge from external match results to the existing paired statistics, and
/// the seat attribution it exists to get right.
/// </summary>
/// <remarks>
/// <para>
/// Spec §9.4 requires an external agent to be scored with <em>identical</em>
/// statistics to an in-process one. The way that is guaranteed here is structural
/// rather than promised: this class builds the same
/// <see cref="MatchResult"/> rows the in-process harness builds and hands them to
/// the same <see cref="PairedStudy.Analyze"/>. There is no second implementation
/// of the delta, the confidence interval, the outcome rates, the 30-seed floor,
/// or the decision rule to drift out of step — a change to any of those moves
/// both paths at once, or neither.
/// </para>
/// <para>
/// The rows are constructed directly rather than by playing matches, because the
/// thing under test here is the <em>attribution</em>: which side of a mirrored
/// pair a score and an outcome belong to. A match would bury that decision inside
/// a process launch and a policy, and a test that could only fail for both reasons
/// at once would not say which one broke.
/// </para>
/// </remarks>
public class ExternalPairedStudyTests
{
    private const ulong Seed = 1001;

    /// <summary>A second seed, for the cases that must not collide with the first.</summary>
    private const ulong OtherSeed = 1002;

    private const string External = "external";
    private const string Baseline = "Scout";

    [Fact]
    public void A_Mirrored_Seating_Attributes_Scores_To_The_Seat_The_External_Agent_Played()
    {
        // The external agent wins 7-3 from both seats. The runner records a row
        // indexed by SLOT — ScoreA is always slot 0's score — so the seat-1 row
        // carries the baseline's 3 in ScoreA and the external agent's 7 in
        // ScoreB. A mirrored pairing follows the seat through the labels, not
        // through the field position, and this test is where that shows up.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3),
            Completed(externalSlot: 1, slot0Score: 3, slot1Score: 7),
        };

        var rows = ExternalPairedStudy.Rows(results, External, Baseline);

        Assert.Equal(2, rows.Length);

        // Seat 0: the external agent is Team A, so its score is the one in A.
        Assert.Equal(External, rows[0].TeamA);
        Assert.Equal(Baseline, rows[0].TeamB);
        Assert.Equal(7, rows[0].ScoreA);
        Assert.Equal(3, rows[0].ScoreB);

        // Seat 1: the labels follow the seats, so the baseline is Team A — and the
        // external agent's own 7 must still be the number the delta is built from.
        Assert.Equal(Baseline, rows[1].TeamA);
        Assert.Equal(External, rows[1].TeamB);
        Assert.Equal(3, rows[1].ScoreA);
        Assert.Equal(7, rows[1].ScoreB);
    }

    [Fact]
    public void A_Mirrored_Seating_Attributes_Outcomes_To_The_Correct_Seat()
    {
        // The same shape with the baseline ahead: both seatings are losses for the
        // external agent. Attribution is what is under test, and it is worth being
        // precise about how a wrong seat fails — reading the seat from
        // TeamAIndex makes every row a forward row, so the analyzer finds no
        // mirrored pair for any seed and throws. The failure is loud rather than a
        // plausible number, which is the better property and the reason the labels
        // are the first thing asserted.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 1, slot1Score: 4, slot0Won: false),
            Completed(externalSlot: 1, slot0Score: 4, slot1Score: 1, slot0Won: false),
        };

        var report = ExternalPairedStudy.Analyze("dev", External, Baseline, rolloutsPerAction: 32, maxStepsPerMatch: 200, results);

        Assert.Equal(1, report.Statistics.Seeds);
        Assert.Equal(2, report.Statistics.Matches);
        Assert.Equal(-3.0, report.Statistics.MeanDelta, 10);

        // Every loss, from both seatings. A policy that had swapped the two
        // would score one win and one loss here and still average -3.
        Assert.Equal(0, report.Statistics.Wins);
        Assert.Equal(2, report.Statistics.Losses);
        Assert.Equal(1.0, report.Statistics.LossRate, 10);
    }

    [Fact]
    public void The_Delta_And_The_Decision_Rule_Are_The_Ones_The_InProcess_Path_Uses()
    {
        // The same rows an in-process batch would produce for this pairing, fed
        // through the in-process analyzer. If this ever needed its own arithmetic
        // it would stop being the same statistic, which is the whole claim of §9.4.
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

        Assert.Equal(inProcess.Statistics, external.Statistics);
        Assert.Equal(inProcess.Decision, external.Decision);
        Assert.Equal(inProcess.Passed, external.Passed);
        Assert.Equal(inProcess.PerSeed, external.PerSeed);

        // And the delta is the paired average of the two seatings, positive here
        // because the external agent won both. The decision is still "not graded":
        // one seed is below the 30-seed floor, exactly as for an in-process run.
        Assert.Equal(4.0, external.Statistics.MeanDelta, 10);
        Assert.Contains("Not graded", external.Decision, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Failed_Match_Is_Scored_As_A_Loss_And_Enters_No_Other_Count()
    {
        var results = new[]
        {
            Failed(externalSlot: 0, ProtocolReason.TimeoutStep),
            Failed(externalSlot: 1, ProtocolReason.TimeoutStep),
        };

        var report = ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results);

        // The reason code is on the row, in the same field an environment
        // termination uses, and the outcome is a win for the baseline side.
        Assert.All(results, r => Assert.Equal("timeout_step", r.Match!.TerminationReason));

        Assert.Equal(2, report.Statistics.Losses);
        Assert.Equal(0, report.Statistics.Wins);
        Assert.Equal(0, report.Statistics.Timeouts);
        Assert.Equal(1.0, report.Statistics.LossRate, 10);

        // The failure count is the runner's, and it is a count of plumbing rather
        // than of play: it does not turn a loss into a timeout, and it does not
        // enter the rates above.
        var failures = ExternalMatchRunner.Report(results);
        Assert.Equal(2, failures.AgentFailureCount);
        Assert.Equal(2, failures.AgentFailuresByCode[ProtocolReason.TimeoutStep]);
    }

    [Fact]
    public void A_Void_Run_Contributes_No_Row_And_No_Seed()
    {
        // host_limit is void, not a loss: no row at all, so the seed contributes
        // no delta and no denominator. The seed count is what the grading floor
        // reads, which is how a void reduces a study rather than penalising it.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3),
            Completed(externalSlot: 1, slot0Score: 3, slot1Score: 7),
            Void(OtherSeed, externalSlot: 0),
            Void(OtherSeed, externalSlot: 1),
        };

        var rows = ExternalPairedStudy.Rows(results, External, Baseline);
        Assert.Equal(2, rows.Length);

        var report = ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results);
        Assert.Equal(1, report.Statistics.Seeds);
        Assert.Equal(2, ExternalMatchRunner.Report(results).VoidRuns);
    }

    [Fact]
    public void A_Void_On_Only_One_Seating_Is_Refused_Rather_Than_Half_Reported()
    {
        // The analyzer needs both mirrored seatings of a seed, and a void produces
        // no row, so one void and one match leaves a pair that cannot be scored.
        // Dropping the surviving row would publish a study that looks complete and
        // is not; the run is refused instead, naming the seed and saying which
        // seating is missing.
        var results = new[]
        {
            Completed(externalSlot: 0, slot0Score: 7, slot1Score: 3),
            Void(Seed, externalSlot: 1),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalPairedStudy.Analyze("dev", External, Baseline, 32, 200, results));

        Assert.Contains(Seed.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("host_limit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A played match, in the shape <see cref="ExternalMatchRunner"/> produces: a
    /// row indexed by slot, whatever the external agent's seat was.
    /// </summary>
    private static ExternalMatchResult Completed(int externalSlot, int slot0Score, int slot1Score, bool slot0Won = true)
    {
        var outcome = slot0Score == slot1Score
            ? MatchOutcome.Draw
            : slot0Won ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;

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

    private static ExternalMatchResult Failed(int externalSlot, ProtocolReason reason)
    {
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
                ScoreA: 0,
                ScoreB: 0,
                TotalSteps: 12,
                TerminationReason: reason.ToWireString(),
                ContentionRate: 0.0),
            MatchOutcome.TeamBWin,
            fault,
            Episode: null);
    }

    private static ExternalMatchResult Void(ulong seed, int externalSlot) =>
        new(
            seed,
            externalSlot,
            Match: null,
            ExternalOutcome: null,
            new ExternalAgentFault(ProtocolReason.HostLimit, "the observation would exceed max_line_bytes.", Step: 0, StderrTail: string.Empty),
            Episode: null);
}
