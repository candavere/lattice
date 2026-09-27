using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// Turns external match results into the rows the existing paired evaluation
/// already understands, and scores them with the existing analyzer (spec §9.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a bridge and not a second implementation.</b> §9.4 requires an
/// external agent to be scored with identical statistics, identical floors, and
/// an identical decision rule. The only durable way to have that is for the
/// external path to produce the same <see cref="MatchResult"/> rows the
/// in-process harness produces and hand them to the same
/// <see cref="PairedStudy.Analyze"/> — so the delta, the dispersion, the
/// confidence interval, the outcome rates, the 30-seed floor and the verdict all
/// come from one piece of code. Re-deriving any of them here would be a second
/// place for the two paths to disagree, and the disagreement would be invisible
/// until two published studies differed for no reason anyone could name.
/// </para>
/// <para>
/// <b>Seat attribution, which is the whole subtlety.</b>
/// <see cref="PairedStudy.Analyze"/> finds a seed's mirrored pair by team
/// <em>name</em>, and every row must therefore name the external agent as Team A
/// with the baseline as Team B, whichever seat the process actually played. The
/// seat comes from <see cref="ExternalMatchResult.ExternalSlot"/> and from
/// nothing else — never from <see cref="MatchResult.TeamAIndex"/>, which is a
/// constant on every external row and says nothing about where the agent was.
/// Reading the index would treat every match as a forward one, leave the analyzer
/// with no mirrored pair to find for any seed, and abort the study.
/// </para>
/// <para>
/// Void runs contribute no row at all (§9.3): they are reported as a count by
/// <see cref="ExternalMatchRunner.Report"/> and excluded from the statistics here,
/// so a void seed is simply absent from the analyzer's seed list. An
/// agent-attributable failure <em>does</em> contribute a row, carrying its reason
/// code, and is scored as a loss (§9.1) — so both mirrored seatings of a failing
/// seed are present and the analyzer's mirror requirement still holds.
/// </para>
/// </remarks>
public static class ExternalPairedStudy
{
    /// <summary>
    /// Converts played external matches into the per-match rows a paired study
    /// analyzes, with the mirrored seating expressed in the team labels.
    /// </summary>
    /// <param name="results">Every match of the study, in the order they were played.</param>
    /// <param name="externalName">The label the external agent carries in every row.</param>
    /// <param name="baselineName">The label the in-process baseline carries in every row.</param>
    /// <returns>
    /// One row per non-void match, in the order given. Void runs are excluded
    /// (§9.3); agent failures are included, with their reason code on the row.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The two labels are equal, which would make the mirrored pair
    /// indistinguishable to the analyzer.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A seed has a match from one seating and a void from the other. The paired
    /// analyzer needs both, and there is no honest way to invent the missing one:
    /// §9.1 forbids filling a mirror with a loss, and dropping the surviving row
    /// would publish a study that reads as complete while silently omitting a
    /// match. The run is refused instead, naming the seed and the void's reason.
    /// </exception>
    public static MatchResult[] Rows(
        IEnumerable<ExternalMatchResult> results,
        string externalName,
        string baselineName)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineName);

        if (string.Equals(externalName, baselineName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The external agent and the baseline must carry different team labels: both were " +
                $"'{externalName}', and the paired analyzer matches rows by name.",
                nameof(externalName));
        }

        var rows = new List<MatchResult>();
        foreach (var result in results)
        {
            if (result.Match is not { } match)
            {
                // A void run: no outcome, no delta, no denominator (§9.3).
                continue;
            }

            // The external agent is Team A by definition of this row set, so the
            // seat it played decides which side of the row is its score. The
            // runner's row is indexed by SLOT — ScoreA is always slot 0 — so at
            // seat 1 the labels swap to follow the seats and the scores stay
            // exactly where they are. Only the outcome has to move with them,
            // because it was classified from slot 0's point of view.
            //
            // The seat comes from ExternalSlot. Reading TeamAIndex instead would
            // find 0 on every external row, treat every match as a forward one,
            // and leave the analyzer with no mirrored pair to find at all.
            var externalAtSeatZero = result.ExternalSlot == 0;
            rows.Add(externalAtSeatZero
                ? match with
                {
                    TeamA = externalName,
                    TeamB = baselineName,
                    TeamAIndex = 0,
                    TeamBIndex = 1,
                }
                : match with
                {
                    TeamA = baselineName,
                    TeamB = externalName,
                    TeamAIndex = 0,
                    TeamBIndex = 1,
                    Outcome = SwapOutcome(match.Outcome),
                });
        }

        RequireCompleteMirrors(results);
        return [.. rows];
    }

    /// <summary>
    /// Refuses a study in which a seed contributed a match from one seating and a
    /// void from the other.
    /// </summary>
    /// <remarks>
    /// A void run is symmetric by construction — the two seatings of a seed differ
    /// only in the <c>agent_id</c> on the wire, which is the same number of bytes
    /// either way — so this is not reachable with a v3.0 map. It is checked anyway
    /// because the alternative is worse than a crash: <see cref="PairedStudy"/>
    /// would throw a message about a missing mirror that says nothing about the
    /// void that caused it, and a future change to the outbound gate would turn a
    /// confusing failure into an explanation.
    /// </remarks>
    private static void RequireCompleteMirrors(IEnumerable<ExternalMatchResult> results)
    {
        var bySeed = results
            .GroupBy(result => result.Seed)
            .Where(group => group.Any(result => result.Match is null) && group.Any(result => result.Match is not null));

        foreach (var group in bySeed)
        {
            var voids = group
                .Where(result => result.Match is null)
                .Select(result => result.Fault?.Reason.ToWireString() ?? "an unrecorded fault")
                .Distinct()
                .Order(StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Seed {group.Key} produced a match from one mirrored seating and a void from the other " +
                $"({string.Join(", ", voids)}), so the pair cannot be scored: the paired analyzer needs both " +
                "seatings, and a void contributes no row to stand in for the missing one. Re-run with maps small " +
                "enough for the outbound observation to fit (spec §7).");
        }
    }

    /// <summary>
    /// Scores external matches as a mirrored-seat paired study, through the same
    /// analyzer the in-process path uses.
    /// </summary>
    /// <param name="suite">The suite label, e.g. <c>dev</c>.</param>
    /// <param name="externalName">The label the external agent carries in every row.</param>
    /// <param name="baselineName">The label the in-process baseline carries in every row.</param>
    /// <param name="rolloutsPerAction">Recorded as provenance, exactly as for an in-process run.</param>
    /// <param name="maxStepsPerMatch">The per-match tick budget, as for an in-process run.</param>
    /// <param name="results">Every match of the study, in the order they were played.</param>
    /// <returns>
    /// The same <see cref="PairedStudyReport"/> an in-process run of the same
    /// rows produces, including the "Not graded" verdict below 30 valid seeds.
    /// </returns>
    public static PairedStudyReport Analyze(
        string suite,
        string externalName,
        string baselineName,
        int rolloutsPerAction,
        int maxStepsPerMatch,
        IEnumerable<ExternalMatchResult> results)
    {
        var rows = Rows(results, externalName, baselineName);

        // No pairings: the external path is one study, and a summary per pairing
        // would say nothing the per-seed rows and the statistics do not already
        // say. The analyzer reads neither the pairings nor the summaries.
        return PairedStudy.Analyze(
            suite,
            externalName,
            baselineName,
            rolloutsPerAction,
            maxStepsPerMatch,
            new BatchEvaluation(rows, []));
    }

    /// <summary>
    /// The same outcome seen from the other seat. Only the two wins differ: a
    /// draw and a timeout are the same event whoever was sitting where, which is
    /// why they pass through unchanged.
    /// </summary>
    private static MatchOutcome SwapOutcome(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.TeamAWin => MatchOutcome.TeamBWin,
        MatchOutcome.TeamBWin => MatchOutcome.TeamAWin,
        _ => outcome,
    };
}
