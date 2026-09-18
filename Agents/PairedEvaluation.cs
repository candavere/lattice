namespace Lattice.Agents;

/// <summary>
/// One seed's mirrored-seat pair of matches: the target policy at seat 0
/// opposite the baseline at seat 1 (Match 0), and the baseline moved to seat 0
/// opposite the target at seat 1 (Match 1). Keeping both raw score pairs lets
/// consumers recompute the per-seat asymmetry that the mean delta then
/// cancels out.
/// </summary>
public sealed record SeedMatch(
    ulong Seed,
    int PolicyScoreAtSeat0,
    int PolicyScoreAtSeat1,
    int BaselineScoreAtSeat0,
    int BaselineScoreAtSeat1,
    double MeanDelta,
    MatchOutcome Match0Outcome,
    MatchOutcome Match1Outcome);

/// <summary>
/// Aggregated statistics over one paired study: paired-delta dispersion
/// (mean, median, sample standard deviation, interquartile range), the 95%
/// confidence interval for the mean on the t-distribution, match-level
/// failure accounting (win/draw/loss/timeout rates against the baseline), and
/// mean choke-contention saturation across every match.
/// </summary>
public sealed record PairedStudyStatistics(
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
/// The full outcome of one mirrored-seat paired study: the per-seed rows (in
/// ascending seed order), the aggregate statistics, and the decision-rule
/// verdict. The decision rule is explicit — the study passes when the mean
/// paired delta is strictly positive AND the lower bound of the 95% confidence
/// interval is strictly positive — so a "passes" claim is always backed by
/// the exact interval that produced it.
/// </summary>
public sealed record PairedStudyReport(
    string Suite,
    string TargetPolicy,
    string BaselinePolicy,
    int RolloutsPerAction,
    int MaxStepsPerMatch,
    SeedMatch[] PerSeed,
    PairedStudyStatistics Statistics,
    bool Passed,
    string Decision);

/// <summary>
/// Computes mirrored-seat paired statistics over a batch evaluation. For
/// every seed the batch must contain exactly one match pairing the target
/// policy at seat 0 against the baseline at seat 1, and one match with the
/// seats swapped — the harness's <c>(0,1)</c>/<c>(1,0)</c> convention — so
/// positional spawn bias cancels out of the per-seed delta.
/// </summary>
public static class PairedStudy
{
    private const double ConfidenceLevel = 0.95;

    /// <summary>
    /// Analyzes <paramref name="batch"/> as a paired study between
    /// <paramref name="targetPolicy"/> (seat role <c>MCTS</c>-style target) and
    /// <paramref name="baselinePolicy"/>. Teams are matched by their
    /// <see cref="MatchResult"/> family names, so the policy can be seated
    /// either first or second in the pairing list and the analyzer still
    /// reconstructs the mirrored pair per seed.
    /// </summary>
    public static PairedStudyReport Analyze(
        string suite,
        string targetPolicy,
        string baselinePolicy,
        int rolloutsPerAction,
        int maxStepsPerMatch,
        BatchEvaluation batch)
    {
        var rows = new List<SeedMatch>();
        foreach (var seed in batch.Matches.Select(match => match.Seed).Distinct().OrderBy(seed => seed))
        {
            var seedMatches = batch.Matches.Where(match => match.Seed == seed).ToArray();
            var forward = seedMatches.FirstOrDefault(
                match => match.TeamA == targetPolicy && match.TeamB == baselinePolicy);
            var mirrored = seedMatches.FirstOrDefault(
                match => match.TeamA == baselinePolicy && match.TeamB == targetPolicy);
            if (forward is null || mirrored is null)
            {
                throw new ArgumentException(
                    $"Seed {seed} is missing one side of the mirrored seat pair " +
                    $"({targetPolicy} vs {baselinePolicy}).", nameof(batch));
            }

            var deltaForward = forward.ScoreA - forward.ScoreB;
            var deltaMirrored = mirrored.ScoreB - mirrored.ScoreA;
            rows.Add(new SeedMatch(
                seed,
                forward.ScoreA,
                mirrored.ScoreB,
                forward.ScoreB,
                mirrored.ScoreA,
                (deltaForward + deltaMirrored) / 2.0,
                forward.Outcome,
                mirrored.Outcome));
        }

        var deltas = rows.Select(row => row.MeanDelta).ToArray();
        var sorted = deltas.OrderBy(value => value).ToArray();

        var mean = deltas.Average();
        var median = Quantile(sorted, 0.5);
        var stdDev = SampleStandardDeviation(deltas, mean);
        var iqr = Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
        (var ciLower, var ciUpper) = TConfidenceInterval(mean, stdDev, deltas.Length, ConfidenceLevel);

        var outcomes = rows.SelectMany(row => new[]
        {
            PolicyOutcome(row.Match0Outcome, policyAtSeat: 0),
            PolicyOutcome(row.Match1Outcome, policyAtSeat: 1),
        }).ToArray();
        var wins = outcomes.Count(outcome => outcome == MatchOutcome.TeamAWin);
        var losses = outcomes.Count(outcome => outcome == MatchOutcome.TeamBWin);
        var draws = outcomes.Count(outcome => outcome == MatchOutcome.Draw);
        var timeouts = outcomes.Count(outcome => outcome == MatchOutcome.Timeout);
        var totalMatches = outcomes.Length;
        var contention = batch.Matches.Where(match =>
                (match.TeamA == targetPolicy && match.TeamB == baselinePolicy)
                || (match.TeamA == baselinePolicy && match.TeamB == targetPolicy))
            .Average(match => match.ContentionRate);

        var graded = deltas.Length >= 30;
        var passed = graded && mean > 0.0 && ciLower > 0.0;
        var decision = !graded
            ? $"Not graded: {deltas.Length} seeds is below the 30-seed floor of the decision rule (the canonical suites run 50)."
            : passed
                ? $"Pass: mean paired delta {mean:0.###} > 0 and the 95% CI lower bound {ciLower:0.###} > 0 on {deltas.Length} seeds."
                : $"Fail: mean paired delta {mean:0.###} and/or the 95% CI lower bound {ciLower:0.###} did not clear 0 on {deltas.Length} seeds.";

        return new PairedStudyReport(
            suite,
            targetPolicy,
            baselinePolicy,
            rolloutsPerAction,
            maxStepsPerMatch,
            rows.ToArray(),
            new PairedStudyStatistics(
                deltas.Length,
                totalMatches,
                mean,
                median,
                stdDev,
                iqr,
                ciLower,
                ciUpper,
                wins,
                draws,
                losses,
                timeouts,
                wins / (double)totalMatches,
                draws / (double)totalMatches,
                losses / (double)totalMatches,
                timeouts / (double)totalMatches,
                contention),
            passed,
            decision);
    }

    private static MatchOutcome PolicyOutcome(MatchOutcome match, int policyAtSeat)
    {
        if (match == MatchOutcome.Timeout)
        {
            return MatchOutcome.Timeout;
        }

        if (match == MatchOutcome.Draw)
        {
            return MatchOutcome.Draw;
        }

        var policyWon = policyAtSeat == 0 ? match == MatchOutcome.TeamAWin : match == MatchOutcome.TeamBWin;
        return policyWon ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;
    }

    private static double Quantile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static double SampleStandardDeviation(double[] values, double mean)
    {
        if (values.Length < 2)
        {
            return 0.0;
        }

        var variance = values.Sum(value => (value - mean) * (value - mean)) / (values.Length - 1);
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// The two-sided (1 - <paramref name="level"/>) confidence interval for
    /// the mean: mean ± t_{1 - level/2, n-1} · s / √n. The t-critical value is
    /// Hill's rational approximation of the Student-t quantile from the
    /// standard-normal quantile — an approximation that is very accurate for
    /// the degrees of freedom of the canonical 50-seed suites and degrades
    /// only for tiny samples, which the decision rule refuses to grade anyway.
    /// </summary>
    private static (double Lower, double Upper) TConfidenceInterval(
        double mean,
        double standardDeviation,
        int sampleCount,
        double level)
    {
        if (sampleCount < 2 || standardDeviation == 0.0)
        {
            return (mean, mean);
        }

        var degreesOfFreedom = sampleCount - 1;
        var standardNormalQuantile = NormalQuantile(1.0 - (1.0 - level) / 2.0);
        var z = standardNormalQuantile;
        var v = degreesOfFreedom;
        var t = z
            + (z * z * z + z) / (4.0 * v)
            + (5.0 * z * z * z * z * z + 16.0 * z * z * z + 3.0 * z) / (96.0 * v * v);
        var margin = t * standardDeviation / Math.Sqrt(sampleCount);
        return (mean - margin, mean + margin);
    }

    /// <summary>
    /// The standard-normal quantile for a probability in (0, 1) using the
    /// well-known rational approximation (Acklam/Boris): a low-tail and
    /// high-tail rational form plus a central rational form over r². The
    /// whole lattice evaluation is integer-quantized on scores; this helper
    /// only shapes the reported interval, never an agent decision.
    /// </summary>
    private static double NormalQuantile(double probability)
    {
        Span<double> a = stackalloc[] {
            -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
            1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
        };
        Span<double> b = stackalloc[] {
            -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
            6.680131188771972e+01, -1.328068155288572e+01,
        };
        Span<double> c = stackalloc[] {
            -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
            -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
        };
        Span<double> d = stackalloc[] {
            7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
            3.754408661907416e+00,
        };

        const double plow = 0.02425;
        const double phigh = 1.0 - plow;

        if (probability < plow)
        {
            var q = Math.Sqrt(-2.0 * Math.Log(probability));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        if (probability > phigh)
        {
            var q = Math.Sqrt(-2.0 * Math.Log(1.0 - probability));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5])
                / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        var r = probability - 0.5;
        var r2 = r * r;
        return (((((a[0] * r2 + a[1]) * r2 + a[2]) * r2 + a[3]) * r2 + a[4]) * r2 + a[5]) * r
            / (((((b[0] * r2 + b[1]) * r2 + b[2]) * r2 + b[3]) * r2 + b[4]) * r2 + 1.0);
    }
}