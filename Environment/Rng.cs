namespace Lattice.Environment;

/// <summary>
/// The canonical deterministic random source for the simulation core. Wraps
/// <see cref="System.Random"/> behind explicit-seed-only constructors so every
/// consumer is forced to state its seed: there is no ambient, unsourced
/// randomness anywhere in Lattice. Two instances built from the same seed
/// produce identical sequences, which underlies the deterministic-transition
/// guarantee.
/// </summary>
public sealed class Rng
{
    private readonly Random _random;

    /// <summary>
    /// Constructs an Rng from an <see cref="int"/> seed. The seed is forwarded
    /// to <see cref="System.Random"/> unchanged, so the contract is identical
    /// to seeding System.Random directly.
    /// </summary>
    public Rng(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>
    /// Constructs an Rng from a <see cref="ulong"/> seed. System.Random can
    /// only be seeded by an int, so the 64-bit seed is folded to 32 bits by a
    /// deterministic, avalanching bit mix (the splitmix64 finalizer) before
    /// forwarding — this keeps distinct and nearby ulong seeds (the common
    /// generator 0..N case) from collapsing onto one inner sequence, while
    /// introducing no randomness of its own.
    /// </summary>
    public Rng(ulong seed)
    {
        _random = new Random(FoldSeed(seed));
    }

    /// <summary>
    /// Returns a non-negative 32-bit integer, forwarded from the wrapped
    /// <see cref="System.Random"/>. Deterministic for a given seed.
    /// </summary>
    public int Next() => _random.Next();

    /// <summary>
    /// Returns a random 32-bit integer in [minValue, maxValue), forwarded from
    /// the wrapped <see cref="System.Random"/>. Identical min and max bounds
    /// are allowed and return <paramref name="minValue"/>. Deterministic for a
    /// given seed; needed by the generator for bounded draws.
    /// </summary>
    public int Next(int minValue, int maxValue) => minValue == maxValue ? minValue : _random.Next(minValue, maxValue);

    /// <summary>
    /// Returns a random double in [0,1), forwarded from the wrapped
    /// <see cref="System.Random"/>. Deterministic for a given seed.
    /// </summary>
    public double NextDouble() => _random.NextDouble();

    private static int FoldSeed(ulong value)
    {
        unchecked
        {
            ulong z = value + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (int)(z ^ (z >> 31));
        }
    }
}