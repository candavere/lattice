using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The family label for <see cref="ScoutCollectorAgent"/> used by the
/// evaluation harness. The scout is fully deterministic, so the run seed is
/// accepted and ignored — keeping the factory contract uniform across
/// families. <paramref name="vision"/> is the per-agent perception cone in
/// graph hops (or <see cref="SimulationConfig.UnboundedVision"/>).
/// </summary>
public sealed class ScoutCollectorAgentFactory : IAgentFactory
{
    private readonly int _vision;

    public ScoutCollectorAgentFactory(int vision = SimulationConfig.UnboundedVision, string name = "Scout")
    {
        if (vision != SimulationConfig.UnboundedVision && vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vision), vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        Name = name;
        _vision = vision;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IAgent Create(int agentId, ulong runSeed) => new ScoutCollectorAgent(agentId, _vision);
}