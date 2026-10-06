namespace Lattice.Tui;

/// <summary>
/// The values one <c>evaluate --out</c> artifact contributes to the Ledger screen,
/// in plain data with no dependency on the CLI, the evaluation harness or the
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The model lives here rather than beside the reader because the reader is in
/// <c>Lattice.Cli.Presentation</c> and this library cannot reference that assembly —
/// the same constraint that put the setup screen's own field vocabulary here. It is
/// the seam between the artifact's format and the drawing: a reader projects the
/// writer's own records into these, and everything downstream works against values.
/// </para>
/// <para>
/// <b>Nothing here is a judgement.</b> A statistics row is the artifact's own, the
/// per-seed outcomes are decoded from the enum the analyzer wrote, and
/// <see cref="LedgerStudy.Passed"/> and <see cref="LedgerStudy.Decision"/> are
/// carried as the artifact's own verdict. No type in this file compares two
/// artifacts or ranks anything, and there is deliberately nowhere for a ranking to
/// live.
/// </para>
/// </remarks>
/// <param name="Label">
/// The artifact's own file name, which is how a reader told several apart. The full
/// path is not kept: a screen shows the shortest name that identifies the artifact,
/// and a path is machine-specific text that would differ between two machines
/// reading the same file.
/// </param>
/// <param name="CommitSha">The revision the artifact claims, or <c>null</c> when it claims none.</param>
/// <param name="CreatedAtUtc">When the artifact was written, as it was written.</param>
/// <param name="Runtime">The runtime the run was made on, as the artifact states it.</param>
/// <param name="Os">The operating system the run was made on, as the artifact states it.</param>
/// <param name="Cores">The core count the run was made on, as the artifact states it.</param>
/// <param name="Architecture">The process architecture the run was made on, as the artifact states it.</param>
/// <param name="Studies">The studies, in the order the artifact lists them.</param>
/// <param name="Agent">
/// The external-agent block, or <c>null</c> when the artifact carries none — which
/// means the run scored no external agent, not that nothing happened to one.
/// </param>
public sealed record LedgerArtifact(
    string Label,
    string? CommitSha,
    DateTime CreatedAtUtc,
    string Runtime,
    string Os,
    int Cores,
    string Architecture,
    IReadOnlyList<LedgerStudy> Studies,
    LedgerAgent? Agent);

/// <summary>
/// One suite's study: the protocol it ran under, its per-seed rows, its own
/// statistics, and the verdict the artifact recorded for it.
/// </summary>
/// <param name="Suite">The seed suite's own name, as the artifact spells it.</param>
/// <param name="TargetPolicy">The policy under test, as the artifact names it.</param>
/// <param name="BaselinePolicy">The policy it was compared against.</param>
/// <param name="RolloutsPerAction">The search budget per action the study ran under.</param>
/// <param name="MaxStepsPerMatch">The step budget per match the study ran under.</param>
/// <param name="PerSeed">The per-seed rows, in the order the artifact lists them.</param>
/// <param name="Statistics">The artifact's own aggregate statistics for this study.</param>
/// <param name="Passed">The artifact's own verdict for this study, carried as the artifact's.</param>
/// <param name="Decision">The sentence the artifact recorded behind that verdict.</param>
public sealed record LedgerStudy(
    string Suite,
    string TargetPolicy,
    string BaselinePolicy,
    int RolloutsPerAction,
    int MaxStepsPerMatch,
    IReadOnlyList<LedgerSeed> PerSeed,
    LedgerStatistics Statistics,
    bool Passed,
    string Decision);

/// <summary>One seed's mirrored pair of matches, as the artifact recorded it.</summary>
/// <param name="Seed">The seed both matches were played from.</param>
/// <param name="PolicyScoreAtSeat0">The target policy's score when it was seated first.</param>
/// <param name="PolicyScoreAtSeat1">The target policy's score when it was seated second.</param>
/// <param name="BaselineScoreAtSeat0">The baseline's score when the target was seated first.</param>
/// <param name="BaselineScoreAtSeat1">The baseline's score when the target was seated second.</param>
/// <param name="MeanDelta">
/// The paired delta for this seed: the mean of the two seat-differenced scores, which
/// is what cancels positional spawn bias.
/// </param>
/// <param name="Match0">The outcome of the first match, decoded from the target policy's seat.</param>
/// <param name="Match1">The outcome of the mirrored match, decoded from the target policy's seat.</param>
public sealed record LedgerSeed(
    ulong Seed,
    int PolicyScoreAtSeat0,
    int PolicyScoreAtSeat1,
    int BaselineScoreAtSeat0,
    int BaselineScoreAtSeat1,
    double MeanDelta,
    LedgerOutcome Match0,
    LedgerOutcome Match1);

/// <summary>
/// One study's aggregate statistics, exactly as the artifact recorded them. Every
/// number is the artifact's own; none is recomputed here, so a screen showing them
/// cannot disagree with the file it read.
/// </summary>
/// <param name="Seeds">How many seeds the study ran.</param>
/// <param name="Matches">How many matches those seeds produced.</param>
/// <param name="MeanDelta">The mean paired delta.</param>
/// <param name="MedianDelta">The median paired delta.</param>
/// <param name="StdDevDelta">The sample standard deviation of the paired deltas.</param>
/// <param name="IqrDelta">The interquartile range of the paired deltas.</param>
/// <param name="CiLower95">The lower bound of the 95% confidence interval on the mean.</param>
/// <param name="CiUpper95">The upper bound of the 95% confidence interval on the mean.</param>
/// <param name="Wins">Matches the target policy won.</param>
/// <param name="Draws">Matches that ended level.</param>
/// <param name="Losses">Matches the target policy lost.</param>
/// <param name="Timeouts">Matches that ran out of budget.</param>
/// <param name="WinRate">The win rate, as the artifact computed it over <paramref name="Matches"/>.</param>
/// <param name="DrawRate">The draw rate.</param>
/// <param name="LossRate">The loss rate.</param>
/// <param name="TimeoutRate">The timeout rate.</param>
/// <param name="MeanContentionSaturation">The mean choke-contention saturation across the study's matches.</param>
public sealed record LedgerStatistics(
    int Seeds,
    int Matches,
    double MeanDelta,
    double MedianDelta,
    double StdDevDelta,
    double IqrDelta,
    double CiLower95,
    double CiUpper95,
    int Wins,
    int Draws,
    int Losses,
    int Timeouts,
    double WinRate,
    double DrawRate,
    double LossRate,
    double TimeoutRate,
    double MeanContentionSaturation);

/// <summary>
/// The external-agent block, present only when the run scored an external agent.
/// </summary>
/// <param name="Failures">
/// How many matches failed for each protocol reason code, ordered by reason so the
/// same artifact always draws the same rows.
/// </param>
/// <param name="VoidRuns">
/// Matches the run itself refused on its own limits. Not a loss, and excluded from
/// every statistic — reported here because the artifact reports it.
/// </param>
/// <param name="Command">The argument vector that was launched, program first.</param>
/// <param name="Limits">The two limits the matches were actually played under.</param>
/// <param name="Forfeits">
/// The matches whose agent plumbing broke, with the scoreboard as it stood at the
/// point of failure beside the scores the study was scored from. An artifact written
/// by a run in which nothing was forfeited carries no array at all.
/// </param>
public sealed record LedgerAgent(
    IReadOnlyList<LedgerAgentFailure> Failures,
    int VoidRuns,
    IReadOnlyList<string> Command,
    LedgerAgentLimits Limits,
    IReadOnlyList<LedgerForfeit> Forfeits);

/// <summary>How many matches failed for one reason code.</summary>
/// <param name="Reason">The protocol reason code, spelled as the artifact spells it.</param>
/// <param name="Count">How many matches failed that way.</param>
public readonly record struct LedgerAgentFailure(string Reason, int Count);

/// <summary>The two limits an external run was actually played under.</summary>
/// <param name="StepTimeoutMs">The per-step budget, in milliseconds.</param>
/// <param name="MatchTimeoutMs">
/// The per-match budget in milliseconds, computed from the step budget rather than
/// chosen separately.
/// </param>
public readonly record struct LedgerAgentLimits(int StepTimeoutMs, int MatchTimeoutMs);

/// <summary>
/// One forfeited match: the scoreboard as it stood when the agent's plumbing broke,
/// beside the scores the study was scored from.
/// </summary>
/// <param name="Seed">The seed the match was played from.</param>
/// <param name="ExternalSeat">Which slot the external agent was seated in, never inferred.</param>
/// <param name="Reason">The protocol reason code the failure was recorded under.</param>
/// <param name="PartialScoreAtSlot0">Slot 0's score at the point of failure.</param>
/// <param name="PartialScoreAtSlot1">Slot 1's score at the point of failure.</param>
/// <param name="ScoredExternalScore">The external agent's score the study was scored from.</param>
/// <param name="ScoredOpponentScore">The opponent's score the study was scored from.</param>
public sealed record LedgerForfeit(
    ulong Seed,
    int ExternalSeat,
    string Reason,
    int PartialScoreAtSlot0,
    int PartialScoreAtSlot1,
    int ScoredExternalScore,
    int ScoredOpponentScore);

/// <summary>How one match ended, seen from the target policy's own seat.</summary>
/// <remarks>
/// The artifact stores a match outcome from Team A's side, and the two mirrored
/// matches seat the target policy in opposite columns, so the same stored value
/// means opposite things in the two. This enum is the decoded, seat-corrected form,
/// so a screen cannot accidentally draw a stored value as a result.
/// </remarks>
public enum LedgerOutcome
{
    /// <summary>The target policy won that match.</summary>
    PolicyWin,

    /// <summary>The target policy lost that match.</summary>
    PolicyLoss,

    /// <summary>The match ended level.</summary>
    Draw,

    /// <summary>The match ran out of budget without reaching a terminal tick.</summary>
    Timeout,
}

/// <summary>
/// Decoding a stored match outcome into the target policy's own point of view.
/// </summary>
/// <remarks>
/// The stored value is <c>MatchOutcome</c>'s, which counts from Team A. In the first
/// mirrored match the target policy <em>is</em> Team A; in the second it is Team B.
/// So a stored Team A win is a policy win in the first match and a policy loss in the
/// second, and a stored Team B win is the reverse. Draw and timeout name no side.
/// This is the mapping the study's own analyzer applies, and it is reproduced rather
/// than re-derived so the screen and the artifact cannot disagree about a result.
/// </remarks>
public static class LedgerOutcomes
{
    /// <summary>The stored <c>TeamAWin</c>.</summary>
    private const int TeamAWin = 0;

    /// <summary>The stored <c>TeamBWin</c>.</summary>
    private const int TeamBWin = 1;

    /// <summary>The stored <c>Draw</c>.</summary>
    private const int Draw = 2;

    /// <summary>The stored <c>Timeout</c>.</summary>
    private const int Timeout = 3;

    /// <summary>How many values the stored outcome can take, and so the range of a legal one.</summary>
    public const int KnownValues = 4;

    /// <summary>
    /// Whether a stored value is one the outcome enum has. Checked before a stored
    /// value is decoded, so a value no analyzer ever produced is refused by name
    /// rather than given a meaning.
    /// </summary>
    public static bool IsKnown(int stored) => stored is >= TeamAWin and < KnownValues;

    /// <summary>
    /// The outcome as the target policy saw it, given the stored value and the seat
    /// the policy occupied in that match: 0 for the first mirrored match, 1 for the
    /// second.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The stored value is not one the outcome enum has. A reader is expected to
    /// have checked with <see cref="IsKnown"/> first so it can name the field.
    /// </exception>
    public static LedgerOutcome ForPolicySeat(int stored, int policyAtSeat) => stored switch
    {
        TeamAWin => policyAtSeat == 0 ? LedgerOutcome.PolicyWin : LedgerOutcome.PolicyLoss,
        TeamBWin => policyAtSeat == 0 ? LedgerOutcome.PolicyLoss : LedgerOutcome.PolicyWin,
        Draw => LedgerOutcome.Draw,
        Timeout => LedgerOutcome.Timeout,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stored),
            stored,
            $"A match outcome of {stored} is not one this project's outcome enum has, so it cannot be decoded."),
    };
}
