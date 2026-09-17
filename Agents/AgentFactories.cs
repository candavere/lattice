using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The family label for <see cref="RandomAgent"/> used by the evaluation
/// harness. The per-instance RNG is seeded from (familySeed, runSeed,
/// agentId) via an odd-constant mix, so within one batch evaluation every
/// (seed, slot) draws from its own never-reused stream while remaining fully
/// reproducible from the three seeds alone.
/// </summary>
public sealed class RandomAgentFactory : IAgentFactory
{
    private readonly ulong _familySeed;

    public RandomAgentFactory(string name = "Random", ulong familySeed = 0)
    {
        Name = name;
        _familySeed = familySeed;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IAgent Create(int agentId, ulong runSeed) =>
        new RandomAgent(agentId, new Rng(_familySeed ^ (runSeed * 1_000_003UL) ^ (ulong)agentId));
}

/// <summary>
/// The family label for <see cref="GreedyCollectorAgent"/> with stall recovery
/// enabled. Greedy is fully
/// deterministic, so the run seed is accepted and ignored —
/// keeping the factory contract uniform across families.
/// </summary>
public sealed class GreedyCollectorAgentFactory : IAgentFactory
{
    public GreedyCollectorAgentFactory(string name = "Greedy")
    {
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IAgent Create(int agentId, ulong runSeed) => new GreedyCollectorAgent(agentId);
}