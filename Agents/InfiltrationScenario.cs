using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The victory conditions of one infiltration episode, detected from the
/// recorded step results. <see cref="Intercepted"/> is true when the sentry
/// and infiltrator ever co-locate at a zone at a tick boundary (a physical
/// capture — the guard finally confronts the rogue); <see cref="Exfiltrated"/>
/// is true when the infiltrator claimed every treasure chest and the
/// extraction objective. The two are independent booleans: a run may intercept
/// but still technically collect everything, and vice versa.
/// </summary>
public sealed record InfiltrationOutcome(bool Intercepted, bool Exfiltrated)
{
    /// <summary>One of the four outcome labels for reporting and tests.</summary>
    public string Status =>
        Intercepted && Exfiltrated ? "intercepted-after-exfil"
        : Intercepted ? "intercepted"
        : Exfiltrated ? "exfiltrated"
        : "timeout";
}

/// <summary>
/// The full result of an infiltration episode: the tactical verdict plus the
/// underlying <see cref="ScenarioResult"/> (metrics, turns, raw step results)
/// so callers can record, replay, and re-render the exact run that produced
/// the outcome.
/// </summary>
public sealed record InfiltrationScenarioResult(
    InfiltrationOutcome Outcome,
    MapGraph Map,
    SimulationConfig Config,
    ulong Seed,
    ScenarioResult Base)
{
    /// <summary>The infiltrator's aggregate metrics.</summary>
    public AgentMetrics Infiltrator => Base.Metrics.Agents[InfiltrationScenario.InfiltratorAgentId];

    /// <summary>The sentry's aggregate metrics.</summary>
    public AgentMetrics Sentry => Base.Metrics.Agents[InfiltrationScenario.SentryAgentId];
}

/// <summary>
/// The "Dungeon Infiltration &amp; Sentry Patrol" demonstration scenario: a
/// fixed capacity-gated dungeon (<see cref="DungeonMapBuilder"/>) played by a
/// <see cref="SentryPatrolAgent"/> against an <see cref="InfiltratorAgent"/>
/// under partial (fog-of-war) observation. Pure and deterministic — the same
/// seed, vision bounds, and tick budget produce serially equivalent turns and
/// the same verdict under the runtime contract. This is the orchestrator the CLI
/// <c>simulate --scenario infiltration</c> subcommand and the web replay demo
/// are built on.
/// </summary>
public static class InfiltrationScenario
{
    /// <summary>The CLI scenario token ("infiltration").</summary>
    public const string ScenarioName = "infiltration";

    /// <summary>Semantic roster label for the guard.</summary>
    public const string SentryRole = "Sentry";

    /// <summary>Semantic roster label for the rogue.</summary>
    public const string InfiltratorRole = "Infiltrator";

    /// <summary>Agent slot of the guard.</summary>
    public const int SentryAgentId = 0;

    /// <summary>Agent slot of the rogue.</summary>
    public const int InfiltratorAgentId = 1;

    /// <summary>
    /// The default simulation profile for the scenario: two agents, kinematic
    /// transit at speed 4 so a capacity-1 portcullis crossing actually occupies
    /// the gate for several ticks (the contention the scenario is about), and
    /// no core-side vision masking — the core stays full-observation per
    /// adr-002, and fog-of-war is applied by the agents' own perception
    /// filters.
    /// </summary>
    public static SimulationConfig DefaultConfig(int maxSteps) =>
        new(
            AgentCount: 2,
            MaxTicks: maxSteps,
            Vision: SimulationConfig.UnboundedVision,
            TransitSpeed: 4);

    /// <summary>
    /// Runs the scenario on <see cref="DungeonMapBuilder.Build(ulong)"/> for
    /// <paramref name="seed"/> over at most <paramref name="maxSteps"/> ticks.
    /// </summary>
    public static InfiltrationScenarioResult Run(
        ulong seed,
        int maxSteps,
        int sentryVision = SentryPatrolAgent.DefaultVision,
        int infiltratorVision = InfiltratorAgent.DefaultVision)
    {
        if (maxSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps), maxSteps, "maxSteps must be >= 1.");
        }

        var map = DungeonMapBuilder.Build(seed);
        var config = DefaultConfig(maxSteps);
        var sentry = new SentryPatrolAgent(SentryAgentId, InfiltratorAgentId, vision: sentryVision);
        var infiltrator = new InfiltratorAgent(InfiltratorAgentId, SentryAgentId, vision: infiltratorVision);

        var result = ScenarioRunner.Run(
            map,
            config,
            new IAgent[] { sentry, infiltrator },
            maxSteps: maxSteps);

        return new InfiltrationScenarioResult(
            DetectOutcome(map, result),
            map,
            config,
            seed,
            result);
    }

    /// <summary>
    /// Scans the recorded step results for the two victory conditions:
    /// interception when the guard and rogue share a zone at any tick boundary,
    /// exfiltration when every resource (both chests and the extraction) is
    /// claimed. The sentry never collects, so total claims implies the
    /// infiltrator completed the full run.
    /// </summary>
    private static InfiltrationOutcome DetectOutcome(MapGraph map, ScenarioResult result)
    {
        var intercepted = false;
        foreach (var step in result.Results)
        {
            var observation = step.Observations[0];
            var sentry = observation.AgentStates[SentryAgentId];
            var infiltrator = observation.AgentStates[InfiltratorAgentId];
            if (sentry.Transit is null && infiltrator.Transit is null && sentry.ZoneId == infiltrator.ZoneId)
            {
                intercepted = true;
                break;
            }
        }

        var exfiltrated = result.Results.Length > 0
            && result.Results[^1].Observations[0].Claims.Length >= map.Resources.Length;

        return new InfiltrationOutcome(intercepted, exfiltrated);
    }
}