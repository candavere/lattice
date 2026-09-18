using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;

namespace Lattice.Analytics.Benchmarking;

/// <summary>
/// The topological footprint of a benchmark map: small, rich, and dense maps
/// exercise the stepping core under very different congestion profiles.
/// </summary>
public enum MapScale
{
    /// <summary>A 3-zone, 2-5 choke map: the micro stepping baseline.</summary>
    Micro,

    /// <summary>A 10-zone facility map with a realistic choke layout.</summary>
    Medium,

    /// <summary>A 30-zone, 29+ choke map for heavy congestion work.</summary>
    Large,
}

/// <summary>
/// Which quadrant of the workload matrix a benchmark case belongs to. The
/// matrix covers raw engine stepping, a static facility with mixed agents,
/// a dynamic-topology episode (portcullises plus event locks), a large-map
/// contention stress, and a rollout-search policy workload.
/// </summary>
public enum WorkloadKind
{
    /// <summary>Workload A: 2 seeded random agents on the micro map.</summary>
    MicroRaw,

    /// <summary>Workload B: 4 mixed agents on a static 10-zone facility.</summary>
    FacilityStatic,

    /// <summary>Workload C: 4 mixed agents under a dynamic topology rule set.</summary>
    DynamicContention,

    /// <summary>
    /// Workload D: 4 agents (the contract maximum) contending on a 30-zone
    /// stress map. The task's nominal "16 agents / 40 chokes" exceeds the
    /// environment contract (agents are validated inclusive 2..4), so the
    /// workload runs at the contract ceiling on the large map and is reported
    /// honestly as such.
    /// </summary>
    StressTopology,

    /// <summary>Workload E: two 32-rollout MCTS search agents.</summary>
    PolicyLookahead,
}

/// <summary>
/// Describes one case of the workload matrix: what map scale, agent count,
/// and dynamic topology it exercises, plus the default stepping budget per
/// measured iteration. Pure configuration — the concrete artifacts of a run
/// (the generated map, simulation config, and per-iteration agent roster) are
/// produced by <see cref="WorkloadCatalog.Build"/>.
/// </summary>
public sealed record WorkloadDescriptor(
    WorkloadKind Kind,
    string Name,
    MapScale MapScale,
    int AgentCount,
    bool DynamicTopology,
    int DefaultStepsPerIteration,
    string ThroughputMetric)
{
    /// <summary>
    /// Whether the workload's ticks are agent-decided continuation (true) or
    /// raw engine stepping (false). Policy workloads report decisions/sec;
    /// raw workloads report steps/sec.
    /// </summary>
    public bool IsPolicy => ThroughputMetric == WorkloadCatalog.ThroughputDecisionsPerSecond;
}

/// <summary>
/// A fully materialized workload: the generated map, the simulation config it
/// runs under, the rules its episodes obey, and a factory that builds a fresh
/// deterministic agent roster per measured iteration. The roster factory must
/// return new agent instances on every call so each iteration and every replay
/// starts from identical (agent state, seed) conditions.
/// </summary>
public sealed record WorkloadRuntime(
    WorkloadDescriptor Descriptor,
    MapGraph Map,
    SimulationConfig Config,
    DynamicMapRuleSet Rules,
    int StepsPerIteration,
    Func<IAgent[]> RosterFactory)
{
    /// <summary>A compact shape string like <c>3z/2c/5r</c> for artifacts.</summary>
    public string MapShape => $"{Map.Zones.Length}z/{Map.ChokePoints.Length}c/{Map.Resources.Length}r";
}

/// <summary>
/// The canonical five-case benchmark matrix. Seeds and generation configs are
/// fixed so the maps are byte-identical across revisions, hosts, and replay —
/// a workload's map never changes, only its measured timings do.
/// </summary>
public static class WorkloadCatalog
{
    /// <summary>The default number of measured iterations per workload.</summary>
    public const int DefaultRuns = 10;

    /// <summary>The default warm-up budget in ticks across all workloads.</summary>
    public const int DefaultWarmupSteps = 50_000;

    /// <summary>Default steps per measured iteration for raw (non-policy) workloads.</summary>
    public const int DefaultStepsPerIteration = 100_000;

    /// <summary>
    /// Steps per iteration for the rollout-search policy workload. Each MCTS
    /// decision runs rolloutsPerAction independent depth-bounded continuations,
    /// so one decision costs thousands of fork steps; the budget stays small so
    /// the full matrix stays bounded while the workload still dominates its
    /// per-tick window with real search work.
    /// </summary>
    public const int PolicyStepsPerIteration = 100;

    /// <summary>Label used for the throughput metric of engine-stepping workloads.</summary>
    public const string ThroughputStepsPerSecond = "steps";

    /// <summary>Label used for the throughput metric of search/policy workloads.</summary>
    public const string ThroughputDecisionsPerSecond = "decisions";

    private const ulong MicroSeed = 0xCA11UL;
    private const ulong FacilitySeed = 0xFA01CEUL;
    private const ulong StressSeed = 0xC0FFEEUL;

    /// <summary>
    /// The five canonical workload descriptors, in stable order.
    /// </summary>
    public static IReadOnlyList<WorkloadDescriptor> Descriptors { get; } = new[]
    {
        new WorkloadDescriptor(WorkloadKind.MicroRaw, "micro_raw_2agent", MapScale.Micro, 2, false, DefaultStepsPerIteration, ThroughputStepsPerSecond),
        new WorkloadDescriptor(WorkloadKind.FacilityStatic, "facility_static_4agent", MapScale.Medium, 4, false, DefaultStepsPerIteration, ThroughputStepsPerSecond),
        new WorkloadDescriptor(WorkloadKind.DynamicContention, "dynamic_contention_4agent", MapScale.Medium, 4, true, DefaultStepsPerIteration, ThroughputStepsPerSecond),
        new WorkloadDescriptor(WorkloadKind.StressTopology, "stress_topology_4agent", MapScale.Large, 4, false, DefaultStepsPerIteration, ThroughputStepsPerSecond),
        new WorkloadDescriptor(WorkloadKind.PolicyLookahead, "policy_lookahead_mcts_32", MapScale.Micro, 2, false, PolicyStepsPerIteration, ThroughputDecisionsPerSecond),
    };

    /// <summary>
    /// Materializes every descriptor into a runnable
    /// <see cref="WorkloadRuntime"/>. When <paramref name="stepsOverride"/> is
    /// supplied it replaces the default per-iteration budget of the raw
    /// stepping workloads (A-D); the rollout-search workload (E) keeps its own
    /// small budget so a full-matrix pass stays bounded (one MCTS decision runs
    /// rolloutsPerAction depth-bounded continuations, so a large override
    /// would turn the whole pass into a minutes-long search run).
    /// </summary>
    public static IReadOnlyList<WorkloadRuntime> BuildAll(int? stepsOverride = null)
    {
        var runtimes = new WorkloadRuntime[Descriptors.Count];
        for (var i = 0; i < runtimes.Length; i++)
        {
            runtimes[i] = Build(Descriptors[i], stepsOverride);
        }

        return runtimes;
    }

    /// <summary>
    /// Materializes one descriptor into a runnable workload. For
    /// <see cref="WorkloadKind.DynamicContention"/> the rules are derived from
    /// the generated map (a timed portcullis on the first choke and an event
    /// lock on the last, triggered by a far resource) so the rule set is
    /// always valid for the exact topology it governs.
    /// </summary>
    public static WorkloadRuntime Build(WorkloadDescriptor descriptor, int? stepsOverride = null)
    {
        var map = Generate(descriptor.MapScale);
        var steps = descriptor.IsPolicy
            ? descriptor.DefaultStepsPerIteration
            : stepsOverride ?? descriptor.DefaultStepsPerIteration;
        var config = new SimulationConfig(
            descriptor.AgentCount,
            MaxTicks: Math.Max(steps + 1, 250_000));

        var rules = DynamicMapRuleSet.None;
        if (descriptor.DynamicTopology)
        {
            rules = BuildContentionRules(map);
        }

        return new WorkloadRuntime(
            descriptor,
            map,
            config,
            rules,
            steps,
            () => BuildRoster(descriptor, config, rules));
    }

    private static MapGraph Generate(MapScale scale)
    {
        return scale switch
        {
            MapScale.Micro => MapGenerator.Generate(MicroSeed, new GeneratorConfig(3, 3, 1, 1, 3, GeneratorConfig.DefaultRetryCap)),
            MapScale.Medium => MapGenerator.Generate(FacilitySeed, new GeneratorConfig(10, 10, 1, 1, 3, GeneratorConfig.DefaultRetryCap)),
            _ => MapGenerator.Generate(StressSeed, new GeneratorConfig(30, 30, 1, 1, 3, GeneratorConfig.DefaultRetryCap)),
        };
    }

    private static DynamicMapRuleSet BuildContentionRules(MapGraph map)
    {
        var portcullisChoke = map.ChokePoints[0].Id;
        var lockedChoke = map.ChokePoints[map.ChokePoints.Length - 1].Id;
        var triggerResource = map.Resources[map.Resources.Length / 2].Id;
        return new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(portcullisChoke, OpenTicks: 30, ClosedTicks: 20),
            new EventLockedChokeRule(lockedChoke, triggerResource),
        });
    }

    private static IAgent[] BuildRoster(
        WorkloadDescriptor descriptor,
        SimulationConfig config,
        DynamicMapRuleSet rules)
    {
        return descriptor.Kind switch
        {
            WorkloadKind.MicroRaw =>
                new IAgent[]
                {
                    new RandomAgent(0, new Rng(0xA11CEUL)),
                    new RandomAgent(1, new Rng(0xBEEFUL)),
                },
            WorkloadKind.FacilityStatic =>
                new IAgent[]
                {
                    new GreedyCollectorAgent(0),
                    new GreedyCollectorAgent(1),
                    new RandomAgent(2, new Rng(0x2A11CEUL)),
                    new RandomAgent(3, new Rng(0x3BEEFUL)),
                },
            WorkloadKind.DynamicContention =>
                new IAgent[]
                {
                    new RandomAgent(0, new Rng(0x4A11CEUL)),
                    new RandomAgent(1, new Rng(0x5BEEFUL)),
                    new RandomAgent(2, new Rng(0x6A11CEUL)),
                    new RandomAgent(3, new Rng(0x7BEEFUL)),
                },
            WorkloadKind.StressTopology =>
                new IAgent[]
                {
                    new GreedyCollectorAgent(0),
                    new ScoutCollectorAgent(1, vision: 3),
                    new GreedyCollectorAgent(2),
                    new ScoutCollectorAgent(3, vision: 3),
                },
            WorkloadKind.PolicyLookahead =>
                new IAgent[]
                {
                    new MctsAgent(0, config, seed: 0x5EED, new MctsSearchConfig(rolloutsPerAction: 32, maxDepth: 12), rules),
                    new MctsAgent(1, config, seed: 0x5EEF, new MctsSearchConfig(rolloutsPerAction: 32, maxDepth: 12), rules),
                },
            _ => throw new ArgumentException($"Unhandled workload kind '{descriptor.Kind}'.", nameof(descriptor)),
        };
    }
}