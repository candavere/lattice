namespace Lattice.Generator;

/// <summary>
/// Thrown when <see cref="MapGenerator.Generate"/> exhausts its bounded
/// rejection budget without producing a map that satisfies every constraint
/// (or the caller's acceptance gate). The generator never returns a
/// "probably fine" map and never patches a rejected one — it raises this typed
/// diagnostic so a caller can surface a precise message (the CLI prints it to
/// stderr and exits non-zero). Carries the seed, the number of attempts
/// consumed, and the checks that rejected the final candidate.
/// </summary>
public sealed class MapGenerationException : Exception
{
    /// <summary>
    /// Builds the diagnostic for an exhausted budget. The message names the
    /// seed, the consumed attempt budget, and the failing checks from the final
    /// candidate, so a field report identifies both the cause and the input.
    /// </summary>
    public MapGenerationException(ulong seed, int attempts, int zoneCount, IReadOnlyList<string> failedChecks)
        : base(
            $"MapGenerator exhausted {attempts} attempt(s) for seed {seed} " +
            $"(zone count {zoneCount}); final attempt failed checks: {string.Join(", ", failedChecks)}.")
    {
        Seed = seed;
        Attempts = attempts;
        FailedChecks = failedChecks;
    }

    /// <summary>The seed the exhausted generation ran under.</summary>
    public ulong Seed { get; }

    /// <summary>How many generation attempts the budget allowed before failing.</summary>
    public int Attempts { get; }

    /// <summary>
    /// The constraint labels (e.g. "connectivity", "acceptance-gate") that the
    /// final candidate failed, in evaluation order.
    /// </summary>
    public IReadOnlyList<string> FailedChecks { get; }
}