using Lattice.Agents;
using Lattice.Environment;

namespace Lattice.Tests.Agents;

/// <summary>
/// Thin adapter over the public <see cref="ScenarioRunner"/> preserving the
/// tuple shape the earlier agent tests were written against: the raw turns and
/// results of an episode. Kept only so T3.1-era tests don't need to care about
/// scene-level metrics — the scenario machinery itself lives one directory up.
/// </summary>
internal static class AgentEpisode
{
    public static (List<AgentAction[]> Turns, List<StepResult> Results) Run(
        MapGraph map,
        SimulationConfig config,
        IAgent[] agents,
        int maxSteps)
    {
        var result = ScenarioRunner.Run(map, config, agents, maxSteps);
        return (result.Turns.ToList(), result.Results.ToList());
    }
}