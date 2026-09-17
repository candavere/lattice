using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A factory keyed by a <c>ulong runSeed</c> and an agent slot id, producing a
/// fresh <see cref="IAgent"/> whose internal RNG state (if any) is derived
/// deterministically from both inputs. The batch harness creates new
/// agent instances per run-seed × pairing combination so the draw-stream of
/// one match can never leak into another.
/// </summary>
public interface IAgentFactory
{
    /// <summary>
    /// Human-readable team label for result reports and summary statistics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates the agent for a given slot (0 or 1 in a head-to-head pairing)
    /// and run-seed. Identical inputs always yield identical decision streams.
    /// </summary>
    IAgent Create(int agentId, ulong runSeed);
}