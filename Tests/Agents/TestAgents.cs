using Lattice.Agents;
using Lattice.Environment;

namespace Lattice.Tests.Agents;

/// <summary>
/// Shared scripted/fixed agents for scenario and harness tests: a pure Wait
/// agent and a queue-driven scripted agent for hand-crafting exact episodes
/// (contention, draw, timeout). Scripts are consumed in Decide order, which is
/// deterministic under <see cref="ScenarioRunner"/>'s ascending-id polling.
/// </summary>
internal sealed class WaitAgent : IAgent
{
    public WaitAgent(int agentId)
    {
        AgentId = agentId;
    }

    public int AgentId { get; }

    public AgentAction Decide(Observation observation) => new(ActionKind.Wait);
}

internal sealed class WaitAgentFactory : IAgentFactory
{
    public string Name => "Wait";

    public IAgent Create(int agentId, ulong runSeed) => new WaitAgent(agentId);
}

internal sealed class ScriptedAgent : IAgent
{
    private readonly Queue<AgentAction> _script;

    public ScriptedAgent(int agentId, params AgentAction[] script)
    {
        AgentId = agentId;
        _script = new Queue<AgentAction>(script);
    }

    public int AgentId { get; }

    public AgentAction Decide(Observation observation) =>
        _script.Count > 0 ? _script.Dequeue() : new AgentAction(ActionKind.Wait);
}