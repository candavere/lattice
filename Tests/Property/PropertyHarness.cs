using Lattice.Environment;

namespace Lattice.Tests.Property;

/// <summary>
/// Seeded property harness: every case derives a deterministic per-case seed
/// from a base seed and the case index, and the property body is handed that
/// seed to reconstruct its whole scenario. A failing case therefore always
/// carries both the base seed and the exact case seed, and the case reproduces
/// byte-for-byte by re-running <see cref="Arbitrary.Scenario(int)"/> with the
/// reported case seed. This is the custom, self-contained counterpart to an
/// external property-testing library: it needs no package, is seed-explicit
/// end to end (matching the engine's <see cref="Rng"/> contract), and prints
/// failing seeds for exact reproduction.
/// </summary>
internal static class PropertyHarness
{
    /// <summary>
    /// The default number of generated cases a property exercises per base
    /// seed. Kept modest so the whole suite stays well under a second per
    /// seed while still sampling thousands of independently seeded scenarios.
    /// </summary>
    public const int DefaultIterations = 400;

    /// <summary>
    /// Mixes the case index and base seed into a case seed that is unique per
    /// (base seed, index) pair. Splitmix64-style finalizer keeps nearby cases
    /// far apart instead of merely advancing an in-flight sequence, so the
    /// first few dozen cases of a run already explore disjoint space.
    /// </summary>
    public static int CaseSeed(int baseSeed, int caseIndex)
    {
        unchecked
        {
            var z = (uint)caseIndex + 0x9E3779B9U + (uint)baseSeed * 0x85EBCA6BU;
            z = (z ^ (z >> 16)) * 0x85EBCA6BU;
            z = (z ^ (z >> 13)) * 0xC2B2AE35U;
            return (int)(z ^ (z >> 16));
        }
    }

    /// <summary>
    /// Runs <paramref name="caseBody"/> for <paramref name="iterations"/>
    /// independently seeded cases. On the first failure raises a
    /// <see cref="PropertyCheckException"/> naming the property, the base
    /// seed, the case index, and the reproducing case seed.
    /// </summary>
    public static void Run(string property, int baseSeed, int iterations, Action<int> caseBody)
    {
        for (var index = 0; index < iterations; index++)
        {
            var caseSeed = CaseSeed(baseSeed, index);
            try
            {
                caseBody(caseSeed);
            }
            catch (Exception e)
            {
                throw new PropertyCheckException(
                    $"{property} violated on seeded case #{index} " +
                    $"(baseSeed={baseSeed}, caseSeed={caseSeed}).\n" +
                    $"Reproduce in isolation: re-run this property with baseSeed {baseSeed}, " +
                    $"or replay the exact scenario via Arbitrary.Scenario({caseSeed}).\n" +
                    e.Message,
                    e);
            }
        }
    }
}

/// <summary>
/// Raised by <see cref="PropertyHarness"/> when a seeded case fails. The
/// message always embeds both seeds so the counterexample can be reproduced
/// exactly and, because every generated artifact is a pure function of the
/// case seed, reduced by hand to a minimal seed that still fails.
/// </summary>
public sealed class PropertyCheckException : Exception
{
    public PropertyCheckException(string message, Exception inner)
        : base(message, inner)
    {
    }
}