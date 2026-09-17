namespace Lattice.Environment;

/// <summary>
/// Immutable simulation parameters. Validates ranges in the constructor so a
/// defective config fails loudly instead of producing an unreachable loop or a
/// nonsense environment.
/// </summary>
public sealed class SimulationConfig
{
    /// <summary>
    /// Sentinel meaning no masking: with this vision the perception filter
    /// exposes the full map, preserving pre-fog-of-war behavior for callers
    /// that never opt into partial observability.
    /// </summary>
    public const int UnboundedVision = -1;

    /// <summary>
    /// Sentinel meaning edge traversal is instantaneous (the default, matching
    /// pre-Phase-8 behavior): a Move arrives in the same tick and no
    /// <see cref="InTransit"/> state is ever produced.
    /// </summary>
    public const int InstantTransit = 0;

    /// <summary>Number of agents in the simulation, inclusive 2..4.</summary>
    public int AgentCount { get; }

    /// <summary>Maximum ticks before the simulation ends by time limit.</summary>
    public int MaxTicks { get; }

    /// <summary>
    /// Perception horizon in graph-hop distance (T7.1). A positive integer
    /// bounds every observer to zones within this many choke edges; the
    /// default <see cref="UnboundedVision"/> keeps the classic full
    /// observation model. The simulation core itself is vision-agnostic —
    /// masking happens in <see cref="PerceptionFilter"/>, never in
    /// <see cref="Simulation"/>.
    /// </summary>
    public int Vision { get; }

    /// <summary>
    /// Movement speed in integer distance-units per tick (T8.1). Edges take
    /// <c>max(1, ceil(ManhattanLength / TransitSpeed))</c> ticks to cross; the
    /// default <see cref="InstantTransit"/> preserves instantaneous moves for
    /// callers that never opt into kinematic traversal.
    /// </summary>
    public int TransitSpeed { get; }

    /// <summary>
    /// Validates and stores the parameters; throws
    /// <see cref="ArgumentOutOfRangeException"/> for out-of-range values.
    /// AgentCount is bounded to 2–4 per the environment ticket; MaxTicks must
    /// be positive so a tick limit always exists; Vision must be unbounded or
    /// at least 1 (a zero-hop cone would blind an agent to its own zone);
    /// TransitSpeed must be <see cref="InstantTransit"/> or at least 1.
    /// </summary>
    public SimulationConfig(int AgentCount, int MaxTicks, int Vision = UnboundedVision, int TransitSpeed = InstantTransit)
    {
        if (AgentCount is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentCount), AgentCount, "AgentCount must be between 2 and 4.");
        }

        if (MaxTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTicks), MaxTicks, "MaxTicks must be >= 1.");
        }

        if (Vision != UnboundedVision && Vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Vision), Vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        if (TransitSpeed != InstantTransit && TransitSpeed < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TransitSpeed), TransitSpeed, "TransitSpeed must be InstantTransit (0) or >= 1.");
        }

        this.AgentCount = AgentCount;
        this.MaxTicks = MaxTicks;
        this.Vision = Vision;
        this.TransitSpeed = TransitSpeed;
    }
}

/// <summary>
/// The immutable state the step function reads and rewrites: the map, each
/// agent's zone and score, which resource ids are claimed, and the tick
/// counter. No hidden mutable state — this record plus the next tick's actions
/// fully determine the following state (see docs/adr-002.md).
/// </summary>
public sealed record SimulationState(
    MapGraph Map,
    AgentState[] Agents,
    int[] Claims,
    int StepCount);

/// <summary>
/// The pure step function's output: the next immutable state together with
/// the <see cref="StepResult"/> that describes this tick.
/// </summary>
public sealed record StepOutcome(SimulationState NextState, StepResult Result);

/// <summary>
/// The pure simulation core. Every rule is embodied in one total function,
/// <see cref="Step"/>, so there is exactly one place for conflict resolution,
/// rewards, and termination to be tested and audited.
/// </summary>
public static class Simulation
{
    /// <summary>
    /// Builds the initial state for <paramref name="config"/> on
    /// <paramref name="map"/>: agent i round-robins to zone i % zoneCount with
    /// score 0, nothing is claimed, the tick counter is 0. Throws
    /// <see cref="ArgumentException"/> for an empty map.
    /// </summary>
    public static SimulationState CreateInitial(MapGraph map, SimulationConfig config)
    {
        if (map.Zones.Length == 0)
        {
            throw new ArgumentException("Cannot initialize a simulation on an empty map.", nameof(map));
        }

        var agents = new AgentState[config.AgentCount];
        for (var i = 0; i < agents.Length; i++)
        {
            agents[i] = new AgentState(i, i % map.Zones.Length, 0);
        }

        return new SimulationState(map, agents, Array.Empty<int>(), 0);
    }

    /// <summary>
    /// Advances <paramref name="state"/> by one tick under the agents'
    /// actions. Pure and total: the input state is never mutated and any
    /// action array yields the next state (invalid/missing actions degrade to
    /// Wait). Resolution is still two-phase (docs/adr-002.md): phase 1 moves
    /// (now respecting edge transit and zone/choke capacity, Phase 8 — see
    /// ticket T8.1/T8.2), then phase 2 collects in ascending agent id against
    /// post-move zones, one claim per resource per tick (lowest id wins).
    /// Returns the next state plus the tick's <see cref="StepResult"/>.
    /// </summary>
    public static StepOutcome Step(SimulationState state, AgentAction[] actions, SimulationConfig config)
    {
        var agentCount = state.Agents.Length;
        var claimsSet = new HashSet<int>(state.Claims);
        var zoneCount = state.Map.Zones.Length;
        var chokeCount = state.Map.ChokePoints.Length;

        // Seed occupancy: who is at each node now, who is transiting each choke.
        // Every array is recomputed from the immutable state each tick, so there
        // is no cross-tick hidden accounting.
        var nodeLoad = new int[zoneCount];
        var edgeLoad = new int[chokeCount];
        for (var i = 0; i < agentCount; i++)
        {
            var agentState = state.Agents[i];
            if (agentState.Transit is not { } transit)
            {
                nodeLoad[agentState.ZoneId]++;
            }
            else
            {
                edgeLoad[EdgeChoke(state.Map, transit.FromZoneId, transit.ToZoneId)]++;
            }
        }

        // Phase 1 — movement & transit (T8.1) with capacity gating (T8.2).
        // Agents resolve in ascending id so the outcome is a total function of
        // the state. Transiting agents never act: their countdown advances one
        // tick this tick and they arrive when it reaches zero (the transit
        // consumes exactly one tick per unit of RemainingTicks).
        var nextZones = new int[agentCount];
        var nextTransit = new InTransit?[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            var agentState = state.Agents[i];
            if (agentState.Transit is { } transit)
            {
                var remaining = transit.RemainingTicks - 1;
                if (remaining == 0)
                {
                    nextZones[i] = transit.ToZoneId;
                    nextTransit[i] = null;
                    nodeLoad[transit.ToZoneId]++; // occupies the destination now
                }
                else
                {
                    nextZones[i] = transit.FromZoneId;
                    nextTransit[i] = transit with { RemainingTicks = remaining };
                }

                // The edge slot is intentionally NOT freed while an agent is
                // mid-crossing, so the choke stays at capacity for the full tick.
                continue;
            }

            var current = agentState.ZoneId;
            var action = At(actions, i);
            var grantsMove = false;

            if (action is { Kind: ActionKind.Move })
            {
                var destination = action.ZoneId;
                if (destination != current && IsAdjacent(state.Map, current, destination))
                {
                    var zoneCapacity = state.Map.Zones[destination].MaxOccupancy;
                    var nodeOpen = zoneCapacity > 0 && nodeLoad[destination] < zoneCapacity;

                    var travelOpen = true;
                    var transitTicks = 0;
                    if (config.TransitSpeed != SimulationConfig.InstantTransit)
                    {
                        transitTicks = TransitTicks(state.Map, current, destination, config.TransitSpeed);
                        if (transitTicks > 1) // one-tick edges arrive instantly; only real crossings occupy the choke
                        {
                            var chokeIndex = EdgeChoke(state.Map, current, destination);
                            var chokeCapacity = chokeIndex >= 0
                                ? state.Map.ChokePoints[chokeIndex].MaxOccupancy
                                : MapLimits.Unlimited;
                            travelOpen = chokeCapacity > 0 && edgeLoad[chokeIndex] < chokeCapacity;
                        }
                    }

                    grantsMove = nodeOpen && travelOpen;
                    if (grantsMove)
                    {
                        nodeLoad[destination]++; // reserves destination capacity for this tick
                        if (config.TransitSpeed == SimulationConfig.InstantTransit || transitTicks <= 1)
                        {
                            nextZones[i] = destination; // arrives the same tick
                            nextTransit[i] = null;
                        }
                        else
                        {
                            edgeLoad[EdgeChoke(state.Map, current, destination)]++; // choke is occupied for the (multi-tick) crossing
                            nextZones[i] = current; // stays at the departure node while transiting
                            nextTransit[i] = new InTransit(current, destination, transitTicks - 1);
                        }
                    }
                }
            }

            if (!grantsMove)
            {
                nextZones[i] = current;
                nextTransit[i] = null;
                // The agent occupies its departure node for this whole tick:
                // capacity slots are only freed by the next tick's reseed.
            }
        }

        // Phase 2 — collects (unchanged semantics: ascending id, post-move
        // zones, one claim per resource per tick). Transiting agents are on an
        // edge and cannot collect, even from their departure node.
        var nextScores = state.Agents.Select(agent => agent.Score).ToArray();
        var rewards = new double[agentCount];
        var nextClaims = new List<int>(state.Claims);
        for (var i = 0; i < agentCount; i++)
        {
            if (nextTransit[i] is not null)
            {
                continue;
            }

            var action = At(actions, i);
            if (action is { Kind: ActionKind.Collect })
            {
                var resource = FindResource(state.Map, action.ResourceId);
                if (resource is not null
                    && resource.ZoneId == nextZones[i]
                    && !claimsSet.Contains(resource.Id))
                {
                    nextScores[i] += 1;
                    nextClaims.Add(resource.Id);
                    claimsSet.Add(resource.Id);
                    rewards[i] = 1;
                }
            }
        }

        var stepCount = state.StepCount + 1;
        var resourcesRemain = state.Map.Resources.Length > 0;
        var resourcesExhausted = resourcesRemain && nextClaims.Count >= state.Map.Resources.Length;
        var tickLimitHit = stepCount >= config.MaxTicks;
        var isTerminal = resourcesExhausted || tickLimitHit;
        string? reason = resourcesExhausted ? "resources-exhausted" : tickLimitHit ? "tick-limit" : null;
        int? winner = isTerminal ? Winner(nextScores) : null;

        var agents = new AgentState[agentCount];
        var observations = new Observation[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            agents[i] = new AgentState(i, nextZones[i], nextScores[i], nextTransit[i]);
        }

        var claims = nextClaims.ToArray();
        for (var i = 0; i < agentCount; i++)
        {
            observations[i] = new Observation(i, state.Map, agents, claims);
        }

        var result = new StepResult(
            observations,
            BuildRewards(rewards, agentCount),
            new Info(stepCount, isTerminal, reason, winner));

        return new StepOutcome(
            new SimulationState(state.Map, agents, claims, stepCount),
            result);
    }

    private static AgentAction? At(AgentAction[] actions, int index)
    {
        return index < actions.Length ? actions[index] : null;
    }

    private static bool IsAdjacent(MapGraph map, int from, int to)
    {
        foreach (var choke in map.ChokePoints)
        {
            if ((choke.FromZoneId == from && choke.ToZoneId == to)
                || (choke.FromZoneId == to && choke.ToZoneId == from))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Phase 8 (T8.1) edge cost: how many ticks a traversal of the choke
    /// between <paramref name="fromZoneId"/> and <paramref name="toZoneId"/>
    /// takes at the given <paramref name="transitSpeed"/>. Length is the
    /// Manhattan distance between the zones' positions, and the division is
    /// pure integer arithmetic (ceil without floating point). A speed of
    /// <see cref="SimulationConfig.InstantTransit"/> is instantaneous (0); any
    /// real speed yields at least 1 tick, so the countdown always terminates.
    /// </summary>
    public static int TransitTicks(MapGraph map, int fromZoneId, int toZoneId, int transitSpeed)
    {
        if (transitSpeed == SimulationConfig.InstantTransit)
        {
            return 0;
        }

        var from = map.Zones[fromZoneId].Position;
        var to = map.Zones[toZoneId].Position;
        var distance = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);
        return Math.Max(1, (distance + transitSpeed - 1) / transitSpeed);
    }

    /// <summary>
    /// Phase 8 (T8.2): the index of the choke backing the undirected edge
    /// between two zones, or -1 if no choke connects them. Choke capacity is
    /// attributed to the edge (in either direction), so a single-lane choke
    /// gates both crossings.
    /// </summary>
    private static int EdgeChoke(MapGraph map, int fromZoneId, int toZoneId)
    {
        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            var choke = map.ChokePoints[i];
            if ((choke.FromZoneId == fromZoneId && choke.ToZoneId == toZoneId)
                || (choke.FromZoneId == toZoneId && choke.ToZoneId == fromZoneId))
            {
                return i;
            }
        }

        return -1;
    }

    private static ResourceNode? FindResource(MapGraph map, int resourceId)
    {
        foreach (var resource in map.Resources)
        {
            if (resource.Id == resourceId)
            {
                return resource;
            }
        }

        return null;
    }

    private static int Winner(int[] scores)
    {
        var winnerId = 0;
        for (var i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[winnerId])
            {
                winnerId = i;
            }
        }

        return winnerId;
    }

    private static Reward[] BuildRewards(double[] rewards, int agentCount)
    {
        var result = new Reward[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            result[i] = new Reward(i, rewards[i]);
        }

        return result;
    }
}

/// <summary>
/// Drives the pure step contract to its terminal state: generates a trajectory
/// of <see cref="StepResult"/>s by applying each turn's actions in order and
/// stopping on the first terminal tick (tick limit or all resources claimed) or
/// when the action sequence runs out.
/// </summary>
public static class SimulationDriver
{
    /// <summary>
    /// Plays <paramref name="actions"/> turn-by-turn on a new simulation over
    /// <paramref name="map"/>, returning the trajectory of StepResults. Stops
    /// early if a turn's <see cref="Info.IsTerminal"/> is true. Deterministic:
    /// the same map and action sequence always yield the same trajectory.
    /// </summary>
    public static List<StepResult> Play(MapGraph map, SimulationConfig config, AgentAction[][] actions)
    {
        var state = Simulation.CreateInitial(map, config);
        var trajectory = new List<StepResult>();

        foreach (var turn in actions)
        {
            var outcome = Simulation.Step(state, turn, config);
            trajectory.Add(outcome.Result);
            state = outcome.NextState;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return trajectory;
    }
}

/// <summary>
/// The stateful wrapper around the pure <see cref="Simulation"/> core. Holds
/// the current immutable state internally, exposes only <see cref="StepResult"/>s
/// (plus a terminal latch), and never inspects agents beyond the
/// <see cref="AgentAction"/> values they submit.
/// </summary>
public sealed class LatticeEnvironment
{
    private readonly MapGraph _map;
    private readonly SimulationConfig _config;
    private SimulationState _state;
    private bool _terminal;

    /// <summary>
    /// Wraps <paramref name="map"/> into a ready-to-play environment. The map
    /// is intentionally supplied (not generated from a seed here) so this
    /// class stays dependency-free; callers compose a generated map — see
    /// <see cref="SimulationDriver"/> for seed-driven playback.
    /// </summary>
    public LatticeEnvironment(MapGraph map, SimulationConfig config)
    {
        _map = map;
        _config = config;
        _state = Simulation.CreateInitial(map, config);
    }

    /// <summary>True once a step has produced a terminal tick.</summary>
    public bool IsTerminal => _terminal;

    /// <summary>
    /// Resets to the initial state (same map, all agents at their starting
    /// zones, score 0, empty claims). Deterministic.
    /// </summary>
    public void Reset()
    {
        _state = Simulation.CreateInitial(_map, _config);
        _terminal = false;
    }

    /// <summary>
    /// Submits the agents' actions for this tick, advances the internal state,
    /// and returns the pure <see cref="StepResult"/>. Subsequent calls continue
    /// from the updated state, so the caller needs no environment internals.
    /// </summary>
    public StepResult Step(AgentAction[] actions)
    {
        var outcome = Simulation.Step(_state, actions, _config);
        _state = outcome.NextState;
        _terminal = outcome.Result.Info.IsTerminal;
        return outcome.Result;
    }
}

/// <summary>
/// A detached, sandboxed copy of the simulation (T9.1): it snapshots an
/// immutable <see cref="SimulationState"/> as of any tick and lets the caller
/// advance that private copy turn-by-turn without any effect on the original
/// state, its owning trajectory, or any other fork. This is the primitive the
/// counterfactual rollouts and the MCTS agent's lookahead are built on.
/// Because <see cref="SimulationState"/> is an immutable record, the "snapshot"
/// is a shared reference — there is nothing to deep-copy and nothing to tear,
/// so forking is free and provably non-destructive.
/// </summary>
public sealed class SimulationFork
{
    private readonly SimulationConfig _config;
    private SimulationState _state;
    private bool _terminal;

    private SimulationFork(SimulationState state, SimulationConfig config)
    {
        _state = state;
        _config = config;
        _terminal = IsTerminalState(state);
    }

    /// <summary>Whether the forked episode has already reached a terminal tick.</summary>
    public bool IsTerminal => _terminal;

    /// <summary>
    /// The fork's current (immutable) state. Reading it never mutates the fork;
    /// only <see cref="Step"/> advances it.
    /// </summary>
    public SimulationState Snapshot => _state;

    /// <summary>
    /// Snapshots <paramref name="state"/> — captured as of any tick, including
    /// mid-trajectory states produced by <see cref="Simulation.Step"/> — into a
    /// fresh sandbox that steps under <paramref name="config"/>. The input state
    /// remains owned by its caller and is never modified.
    /// </summary>
    public static SimulationFork Create(SimulationState state, SimulationConfig config) => new(state, config);

    /// <summary>
    /// Advances the fork's private state by one tick via the pure
    /// <see cref="Simulation.Step"/> and returns the <see cref="StepResult"/>.
    /// Identical action sequences yield byte-identical results on every fork,
    /// so a re-rolled branch is deterministic by construction.
    /// </summary>
    public StepResult Step(AgentAction[] actions)
    {
        var outcome = Simulation.Step(_state, actions, _config);
        _state = outcome.NextState;
        _terminal = outcome.Result.Info.IsTerminal;
        return outcome.Result;
    }

    private bool IsTerminalState(SimulationState state) =>
        (state.Map.Resources.Length > 0 && state.Claims.Length >= state.Map.Resources.Length)
        || state.StepCount >= _config.MaxTicks;
}