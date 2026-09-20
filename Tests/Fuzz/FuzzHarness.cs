using System.Diagnostics;

namespace Lattice.Tests.Fuzz;

/// <summary>
/// Seeded fuzz harness: every case derives a deterministic per-case seed from
/// a base seed and the case index, exactly like <see cref="Lattice.Tests.Property.PropertyHarness"/>
/// does for the property suite. A failing case always carries both the base
/// seed and the case seed, so the exact adversarial input re-materializes by
/// re-running the case body with the reported seed. The harness also measures
/// each case against a per-case budget so a pathological input that burns CPU
/// (accidental backtracking, degenerate rule schedules) is reported as a seed-
/// attributable finding instead of silently inflating suite runtime.
/// </summary>
internal static class FuzzHarness
{
    /// <summary>The number of cases each fuzz target exercises per base seed.</summary>
    public const int DefaultIterations = 800;

    /// <summary>
    /// Ceiling for a single case. Parser and CLI targets parse bounded inputs,
    /// so any case that trips this is itself a finding to isolate by seed.
    /// </summary>
    public static readonly TimeSpan PerCaseBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Mixes the case index and base seed into a per-case seed using the same
    /// splitmix64-style finalizer shape as the property suite, so nearby cases
    /// explore disjoint inputs rather than advancing one in-flight sequence.
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
    /// independently seeded cases. Any exception escaping the case body that is
    /// not already a <see cref="FuzzCheckException"/> is reported as an
    /// unhandled crash carrying the base seed, case index, and case seed.
    /// </summary>
    public static void Run(string target, int baseSeed, int iterations, Action<int> caseBody)
    {
        for (var index = 0; index < iterations; index++)
        {
            var caseSeed = CaseSeed(baseSeed, index);
            var watch = Stopwatch.StartNew();
            try
            {
                caseBody(caseSeed);
            }
            catch (FuzzCheckException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new FuzzCheckException(
                    $"{target} crashed unhandled on seeded case #{index} " +
                    $"(baseSeed={baseSeed}, caseSeed={caseSeed}).\n" +
                    $"{e.GetType().Name}: {e.Message}",
                    e);
            }
            finally
            {
                watch.Stop();
                if (watch.Elapsed > PerCaseBudget)
                {
                    throw new FuzzCheckException(
                        $"{target} exceeded the per-case budget " +
                        $"({PerCaseBudget.TotalSeconds:0.0}s) on seeded case #{index} " +
                        $"(baseSeed={baseSeed}, caseSeed={caseSeed}).");
                }
            }
        }
    }

    /// <summary>
    /// Executes <paramref name="body"/> and treats any exception as expected
    /// only when it is a typed, domain-level rejection
    /// (<see cref="ExceptionContract.IsGraceful"/>). A malformed/adversarial
    /// input that escapes with any other exception (e.g.
    /// <see cref="NullReferenceException"/>, <see cref="IndexOutOfRangeException"/>)
    /// is an unhandled crash and fails the case with <paramref name="detail"/>
    /// in the message.
    /// </summary>
    public static void ExpectGraceful(string detail, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e) when (ExceptionContract.IsGraceful(e))
        {
        }
        catch (Exception e)
        {
            throw new FuzzCheckException($"{detail} escaped with unhandled {e.GetType().Name}: {e.Message}.", e);
        }
    }
}

/// <summary>
/// Raised by <see cref="FuzzHarness"/> when a seeded fuzz case fails. The
/// message always embeds the base seed and case seed so the adversarial input
/// reproduces exactly by re-running the case body with the reported seed.
/// </summary>
public sealed class FuzzCheckException : Exception
{
    public FuzzCheckException(string message)
        : base(message)
    {
    }

    public FuzzCheckException(string message, Exception inner)
        : base(message, inner)
    {
    }
}