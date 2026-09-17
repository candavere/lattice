using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A uniformly random rule-based agent: each tick it picks equally among
/// Wait, every adjacent-zone Move, and every Collectable resource in its
/// current zone. Randomness comes only from the explicitly seeded
/// <see cref="Rng"/> handed in at construction, consuming exactly one draw
/// per decision, so the whole decision stream is deterministic for a given
/// (seed, observation sequence) — and every emitted action is in the action
/// space by construction.
/// </summary>
public sealed class RandomAgent : IAgent
{
    private readonly Rng _rng;

    /// <summary>
    /// Creates the agent. <paramref name="rng"/> must be a seeded instance;
    /// passing the same seed reproduces the same decision stream.
    /// </summary>
    public RandomAgent(int agentId, Rng rng)
    {
        AgentId = agentId;
        _rng = rng;
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        var myZone = ObservationView.MyZone(observation);
        var options = new List<AgentAction> { new(ActionKind.Wait) };

        foreach (var choke in observation.Map.ChokePoints)
        {
            if (choke.FromZoneId == myZone)
            {
                options.Add(new AgentAction(ActionKind.Move, choke.ToZoneId));
            }
            else if (choke.ToZoneId == myZone)
            {
                options.Add(new AgentAction(ActionKind.Move, choke.FromZoneId));
            }
        }

        foreach (var resource in ObservationView.Unclaimed(observation))
        {
            if (resource.ZoneId == myZone)
            {
                options.Add(new AgentAction(ActionKind.Collect, ResourceId: resource.Id));
            }
        }

        var distinct = options.Distinct().ToArray();
        return distinct[_rng.Next(0, distinct.Length)];
    }
}