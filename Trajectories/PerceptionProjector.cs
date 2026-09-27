using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// The one place decision-time perceptions are validated, declared, and rebuilt.
/// Three callers need the same rule — the writer that stamps a recording, the
/// reader that accepts one, and verification that recomputes one — and three
/// copies of the rule would be three places for a recording to disagree with
/// itself, so the rule lives here and is called, not restated.
/// <para>
/// The rule: a recording either carries a <see cref="PartialObservation"/> for
/// every agent slot on every step, or on none of them. A step's array is
/// indexed by agent slot and its entries must name the slot they sit in. When
/// the perceptions are present the header must declare the per-agent radius
/// they were produced with, so a reader can rebuild the filters that produced
/// them; that declared array is the one the projections below run under.
/// </para>
/// </summary>
internal static class PerceptionProjector
{
    /// <summary>
    /// The per-agent perception radius a block of recorded perceptions
    /// declares, in agent-slot order, or null when the block is absent. Throws
    /// <see cref="InvalidDataException"/> when the block contradicts itself:
    /// ragged arrays, a hole, or one agent's radius changing mid-episode. The
    /// radius is read from the perceptions themselves rather than passed in,
    /// so the header can never state a cone the step lines do not bear.
    /// </summary>
    internal static int[]? DeclaredVision(IReadOnlyList<PartialObservation?[]>? perceptions)
    {
        if (perceptions is null || perceptions.Count == 0)
        {
            return null;
        }

        var vision = new int[perceptions[0].Length];
        for (var agentId = 0; agentId < vision.Length; agentId++)
        {
            vision[agentId] = RequireEntry(perceptions[0], agentId, "first step").Vision;
        }

        for (var step = 1; step < perceptions.Count; step++)
        {
            if (perceptions[step].Length != vision.Length)
            {
                throw new InvalidDataException(
                    $"Step {step + 1} records {perceptions[step].Length} perception(s), " +
                    $"but the first step records {vision.Length}; every step must record one per agent slot.");
            }

            for (var agentId = 0; agentId < vision.Length; agentId++)
            {
                var entry = RequireEntry(perceptions[step], agentId, $"step {step + 1}");
                if (entry.Vision != vision[agentId])
                {
                    throw new InvalidDataException(
                        $"Step {step + 1} records a vision of {entry.Vision} for agent {agentId}, " +
                        $"but the first step records {vision[agentId]}; one agent's perception radius " +
                        "cannot change mid-episode.");
                }
            }
        }

        return vision;
    }

    /// <summary>
    /// The per-agent radius a header declares, checked against the roster it
    /// claims to describe; null when the header declares none. Shared by the
    /// reader (which rejects a bad declaration) and by verification (which
    /// reports one), so the rule is stated once.
    /// </summary>
    internal static int[]? ReadVision(int agentCount, int[]? agentVision, string where)
    {
        if (agentVision is null)
        {
            return null;
        }

        if (agentVision.Length != agentCount)
        {
            throw new InvalidDataException(
                $"The {where} declares 'AgentVision' with {agentVision.Length} entr(ies), " +
                $"but the simulation config declares {agentCount} agent(s).");
        }

        foreach (var radius in agentVision)
        {
            if (radius != SimulationConfig.UnboundedVision && radius < 1)
            {
                throw new InvalidDataException(
                    $"The {where} declares a vision of {radius} for an agent; a perception radius " +
                    $"must be {SimulationConfig.UnboundedVision} (unbounded) or at least 1.");
            }
        }

        return agentVision;
    }

    /// <summary>
    /// Checks one step line's <see cref="TrajectoryStep.Perceptions"/> against
    /// the roster: null is allowed (a recording with no perceptions at all), and
    /// when present the array must hold exactly one non-null entry per agent
    /// slot, each entry naming the slot it sits in. <paramref name="where"/>
    /// names the line for the error message.
    /// </summary>
    internal static void CheckStep(int agentCount, PartialObservation?[]? perceptions, string where)
    {
        if (perceptions is null)
        {
            return;
        }

        if (perceptions.Length != agentCount)
        {
            throw new InvalidDataException(
                $"{where} records {perceptions.Length} perception(s), but the simulation config " +
                $"declares {agentCount} agent(s).");
        }

        for (var agentId = 0; agentId < perceptions.Length; agentId++)
        {
            var entry = RequireEntry(perceptions, agentId, where);
            if (entry.AgentId != agentId)
            {
                throw new InvalidDataException(
                    $"{where} has a perception for agent {entry.AgentId} in slot {agentId}; " +
                    "perceptions are indexed by agent slot.");
            }
        }
    }

    /// <summary>
    /// Rebuilds every agent's decision-time perception from the world states the
    /// agent itself decided from: one <see cref="PerceptionFilter"/> per agent
    /// slot, held across the episode exactly as the agents hold theirs, each
    /// projecting the pre-step observation of the tick it was deciding. Stale
    /// memory therefore accumulates the same way it did at record time, which is
    /// what makes the comparison a check of the recorded fog rather than a
    /// re-derivation of it from omniscient positions.
    /// <para>
    /// This is the verification side only. The recording path never calls it —
    /// the recorded perceptions are the agents' own objects — which is why the
    /// two can be compared at all.
    /// </para>
    /// </summary>
    internal static PartialObservation[][] Project(
        MapGraph map,
        int[] vision,
        IReadOnlyList<SimulationState> preStepStates)
    {
        var filters = new PerceptionFilter[vision.Length];
        for (var agentId = 0; agentId < vision.Length; agentId++)
        {
            filters[agentId] = new PerceptionFilter(map, agentId, vision[agentId]);
        }

        var projected = new PartialObservation[preStepStates.Count - 1][];
        for (var tick = 0; tick < projected.Length; tick++)
        {
            var state = preStepStates[tick];
            var row = new PartialObservation[filters.Length];
            for (var agentId = 0; agentId < filters.Length; agentId++)
            {
                row[agentId] = filters[agentId].Project(
                    tick + 1,
                    new Observation(agentId, state.Map, state.Agents, state.Claims, state.StepCount));
            }

            projected[tick] = row;
        }

        return projected;
    }

    private static PartialObservation RequireEntry(PartialObservation?[] perceptions, int agentId, string where)
    {
        if (perceptions[agentId] is null)
        {
            throw new InvalidDataException($"{where} has no perception for agent slot {agentId}.");
        }

        return perceptions[agentId]!;
    }
}
