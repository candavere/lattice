using Lattice.Environment;

namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// Incremental FNV-1a (64-bit) digest of everything a step's outcome depends
/// on: each agent's zone/score/transit, the claimed resource ids in order,
/// the tick counter, the step's info (terminal flags, reason, winner), the
/// rewards, and the per-tick dynamic choke capacities sorted by choke id.
/// Iterations that produce a byte-identical state-and-result stream collapse
/// to the same digest, so the harness uses it as a cheap determinism gate:
/// the first warm-up iteration anchors the reference digest and every later
/// iteration must reproduce it exactly, or the measurement would be sampling
/// an off-script episode.
/// </summary>
internal static class StepHasher
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>The FNV-1a offset basis; the starting digest of a fresh episode.</summary>
    public const ulong Initial = OffsetBasis;

    /// <summary>
    /// Mixes the outcome of one step into <paramref name="hash"/>. The fields
    /// are mixed with a steady iteration so every digest is order-sensitive and
    /// a divergence anywhere in the episode shows up in one comparison.
    /// </summary>
    public static ulong Mix(ulong hash, StepResult result, SimulationState state)
    {
        foreach (var agent in state.Agents)
        {
            hash = MixValue(hash, agent.AgentId);
            hash = MixValue(hash, agent.ZoneId);
            hash = MixValue(hash, agent.Score);
            if (agent.Transit is { } transit)
            {
                hash = MixValue(hash, transit.FromZoneId);
                hash = MixValue(hash, transit.ToZoneId);
                hash = MixValue(hash, transit.RemainingTicks);
            }
            else
            {
                hash = MixValue(hash, -1);
            }
        }

        hash = MixValue(hash, state.StepCount);
        hash = MixValue(hash, state.Claims.Length);
        for (var i = 0; i < state.Claims.Length; i++)
        {
            hash = MixValue(hash, state.Claims[i]);
        }

        hash = MixValue(hash, result.Info.StepNumber);
        hash = MixValue(hash, result.Info.IsTerminal ? 1 : 0);
        hash = MixValue(hash, result.Info.WinnerAgentId ?? -1);

        var reason = result.Info.Reason;
        if (reason is null)
        {
            hash = MixValue(hash, 0);
        }
        else
        {
            for (var i = 0; i < reason.Length; i++)
            {
                hash = MixValue(hash, reason[i]);
            }
        }

        foreach (var reward in result.Rewards)
        {
            hash = MixValue(hash, reward.AgentId);
            hash = MixValue(hash, (int)Math.Round(reward.Value));
        }

        if (state.Dynamics.ChokeCapacities.Count > 0)
        {
            foreach (var pair in state.Dynamics.ChokeCapacities.OrderBy(pair => pair.Key))
            {
                hash = MixValue(hash, pair.Key);
                hash = MixValue(hash, pair.Value);
            }
        }

        return hash;
    }

    private static ulong MixValue(ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            return hash * Prime;
        }
    }
}