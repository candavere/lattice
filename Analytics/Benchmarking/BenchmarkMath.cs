namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// Pure descriptive statistics over benchmark samples. Every value is computed
/// the same way everywhere so an artifact, a README, and a regression gate all
/// quote identical numbers. Percentile uses nearest-rank interpolation over a
/// pre-sorted sample (the conventional latency metric), cost O(1) after sort.
/// </summary>
public static class BenchmarkMath
{
    /// <summary>
    /// The arithmetic mean of <paramref name="samples"/>, or 0 for an empty
    /// set.
    /// </summary>
    public static double Mean(double[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        return sum / samples.Length;
    }

    /// <summary>
    /// The sample standard deviation (n-1 denominator) of
    /// <paramref name="samples"/>, or 0 for fewer than two samples.
    /// </summary>
    public static double SampleStandardDeviation(double[] samples)
    {
        if (samples.Length < 2)
        {
            return 0;
        }

        var mean = Mean(samples);
        var sumOfSquares = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var diff = samples[i] - mean;
            sumOfSquares += diff * diff;
        }

        return Math.Sqrt(sumOfSquares / (samples.Length - 1));
    }

    /// <summary>
    /// The nearest-rank <paramref name="percentile"/> (0..1 inclusive) over
    /// the pre-sorted ascending sample <paramref name="sortedAscending"/>.
    /// Median is percentile 0.5; p95 is 0.95.
    /// </summary>
    public static double Percentile(long[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0)
        {
            throw new ArgumentException("Cannot compute a percentile over an empty sample.", nameof(sortedAscending));
        }

        if (percentile is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be within [0, 1].");
        }

        var rank = (int)Math.Ceiling(percentile * sortedAscending.Length);
        return sortedAscending[Math.Max(0, rank - 1)];
    }
}