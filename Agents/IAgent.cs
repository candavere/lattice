using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The thin contract every rule-based agent implements: return exactly one
/// <see cref="AgentAction"/> from an <see cref="Observation"/>, purely.
/// Agents are read-only consumers of the observation — they must never
/// mutate it — and the returned action is a plain data value the environment
/// interprets. No planning or search is permitted here (the "environment
/// over agents" principle);
/// <see cref="GreedyCollectorAgent"/> is the complexity ceiling.
/// </summary>
public interface IAgent
{
    /// <summary>Which agent slot this instance plays in the episode.</summary>
    int AgentId { get; }

    /// <summary>
    /// Produces the agent's action for the current tick from its
    /// <paramref name="observation"/>. Must be deterministic for a given
    /// (agent state, observation) pair — any randomness must come from an
    /// explicitly seeded instance retained by the agent (e.g.
    /// <see cref="Rng"/>), never ambient.
    /// </summary>
    AgentAction Decide(Observation observation);
}