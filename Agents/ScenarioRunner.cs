using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Drives a fixed set of agents through one episode on a given map and
/// reports competitive metrics. Pure and deterministic: the runner owns
/// no mutable state, agents decide from post-tick Observations exactly as the
/// logged trajectory exposes them, and the Environment step contract resolves
/// every race — so the same (map, config, ordered agents, budget) always
/// yields serially equivalent turns, results, and metrics. This is the engine the
/// batch evaluation harness builds its statistics on.
/// </summary>
/// <remarks>
/// The whole episode is a loop over <see cref="ScenarioStepper.Step"/>: the
/// per-tick body lives in the stepper, so a caller that wants to advance one tick
/// at a time — a live viewer — runs the identical computation and cannot produce
/// a different episode. The runner adds the loop and nothing else.
/// </remarks>
public static class ScenarioRunner
{
    /// <summary>
    /// Runs <paramref name="agents"/> against each other on
    /// <paramref name="map"/> for at most <paramref name="maxSteps"/> ticks,
    /// stopping early on the first terminal tick. The <paramref name="agents"/>
    /// array must hold exactly <see cref="SimulationConfig.AgentCount"/> agents
    /// whose <see cref="IAgent.AgentId"/>s cover the slots 0..AgentCount-1
    /// exactly once; agents are polled in ascending id so the decision order
    /// (and thus any seeded-RNG draw order) is defined regardless of the array
    /// order the caller passed. Throws <see cref="ArgumentException"/> on
    /// malformed agent sets and non-positive budgets.
    /// <paramref name="rules"/> is the episode's optional dynamic topology
    /// policy (timed portcullises, event locks); when non-null it seeds the
    /// initial state's dynamics exactly as a recorded dynamic episode would.
    /// </summary>
    /// <param name="recordPerceptions">
    /// When non-null, records the decision-time perception of every agent at
    /// every tick, read from each agent's own filter
    /// (<see cref="IDecidesFromPerception.LastPerception"/>) immediately after it
    /// decided. Defaults to null, which records no perceptions at all — a
    /// roster whose agents do not carry a filter has no decision-time fog to
    /// record, and a partial roster is not recorded rather than recorded
    /// half-blind, because a per-agent array with a hole in it cannot be read
    /// back as "this agent saw nothing".</param>
    public static ScenarioResult Run(
        MapGraph map,
        SimulationConfig config,
        IAgent[] agents,
        int maxSteps,
        DynamicMapRuleSet? rules = null,
        bool recordPerceptions = false)
    {
        var stepper = new ScenarioStepper(map, config, agents, maxSteps, rules, recordPerceptions);

        while (stepper.CanStep)
        {
            stepper.Step();
        }

        return stepper.ToResult();
    }
}