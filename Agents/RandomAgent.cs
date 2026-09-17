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
///
/// Stall recovery: a Move the agent issued that is denied three ticks in a
/// row (the environment keeps the agent at its zone) is a blocked tick. On
/// the third consecutive blocked tick the agent pauses for exactly one Wait —
/// clearing its target and giving the contested choke a tick to drain —
/// instead of spinning against it forever. The pause is fully determined by
/// the observation stream, so determinism still holds.
/// </summary>
public sealed class RandomAgent : IAgent
{
    /// <summary>How many consecutive denied Move attempts trigger a pause.</summary>
    public const int StallRecoveryThreshold = 3;

    private readonly Rng _rng;
    private bool _hasHistory;
    private int _lastZone;
    private int? _lastMoveTarget;
    private int _blockedStreak;

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

        if (_hasHistory && _lastMoveTarget is { } target)
        {
            if (myZone == target || myZone != _lastZone)
            {
                _blockedStreak = 0;
            }
            else
            {
                _blockedStreak++;
            }
        }

        if (_blockedStreak >= StallRecoveryThreshold)
        {
            // Re-evaluation pause: three consecutive Moves were denied, so
            // wait one tick instead of hammering the contested edge. The
            // choke drains and the draw stream resumes fresh next tick.
            _blockedStreak = 0;
            RecordDecision(myZone, null);
            return new AgentAction(ActionKind.Wait);
        }

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
        var action = distinct[_rng.Next(0, distinct.Length)];
        RecordDecision(myZone, action.Kind == ActionKind.Move ? action.ZoneId : null);
        return action;
    }

    private void RecordDecision(int myZone, int? moveTarget)
    {
        _lastZone = myZone;
        _lastMoveTarget = moveTarget;
        _hasHistory = true;
    }
}