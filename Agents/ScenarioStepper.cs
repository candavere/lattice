using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// One episode on one map, advanced one tick at a time: the per-tick body that
/// <see cref="ScenarioRunner"/> runs in a loop, held as an object so a caller can
/// decide when the next tick happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own type.</b> The runner answers "run the whole episode";
/// a live viewer needs "advance one tick, and tell me when you have". Both are the
/// same computation — poll the agents in ascending id order, record the turn,
/// count the tick's contention, step the pure
/// <see cref="Simulation.Step(SimulationState, AgentAction[], SimulationConfig)"/>,
/// rebuild the observations — and there is exactly one copy of it here.
/// <see cref="ScenarioRunner.Run"/> is a loop over <see cref="Step"/>, so the two
/// cannot produce different episodes.
/// </para>
/// <para>
/// <b>Pure and deterministic, exactly as the runner is.</b> The stepper owns no
/// mutable state beyond its own immutable <see cref="SimulationState"/> and its
/// recorded turns; agents are read-only consumers of their observations, and the
/// environment step contract resolves every race. The same (map, config, ordered
/// agents, budget) therefore yields the same turns, results and metrics, which is
/// what lets a test compare a stepper-driven episode with committed bytes rather
/// than with the runner.
/// </para>
/// <para>
/// <b>No clock, no cancellation.</b> Nothing here reads the time and nothing here
/// stops an agent: <see cref="IAgent.Decide"/> is synchronous and takes no token,
/// so a tick either completes or the caller never started it. A caller that wants
/// to stop between ticks checks <see cref="CanStep"/> between them. That is the
/// only safe stopping point, and it is stated here so a caller does not have to
/// rediscover it.
/// </para>
/// </remarks>
public sealed class ScenarioStepper
{
    private readonly MapGraph _map;
    private readonly SimulationConfig _config;
    private readonly IAgent[] _agents;
    private readonly bool _recordPerceptions;
    private readonly List<AgentAction[]> _turns = new();
    private readonly List<StepResult> _results = new();
    private readonly List<PartialObservation[]>? _perceptions;
    private readonly int _maxSteps;
    private Dictionary<int, Observation> _observations;
    private SimulationState _state;
    private int _contendedTicks;

    /// <summary>
    /// Opens a stepper on <paramref name="map"/> for <paramref name="agents"/>,
    /// with at most <paramref name="maxSteps"/> ticks and not one tick run yet.
    /// </summary>
    /// <param name="recordPerceptions">
    /// When non-null, records the decision-time perception of every agent at every
    /// tick, read from each agent's own filter
    /// (<see cref="IDecidesFromPerception.LastPerception"/>) immediately after it
    /// decided. Defaults to false, which records no perceptions at all — a roster
    /// whose agents do not carry a filter has no decision-time fog to record, and
    /// a partial roster is not recorded rather than recorded half-blind, because a
    /// per-agent array with a hole in it cannot be read back as "this agent saw
    /// nothing".</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxSteps"/> is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="agents"/> is not exactly <see cref="SimulationConfig.AgentCount"/>
    /// agents whose <see cref="IAgent.AgentId"/>s cover the slots 0..AgentCount-1
    /// exactly once, or perceptions were asked for from a roster that cannot carry
    /// them.
    /// </exception>
    public ScenarioStepper(
        MapGraph map,
        SimulationConfig config,
        IAgent[] agents,
        int maxSteps,
        DynamicMapRuleSet? rules = null,
        bool recordPerceptions = false)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agents);

        if (maxSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSteps), maxSteps, "maxSteps must be >= 1.");
        }

        ValidateAgents(config, agents);
        if (recordPerceptions && agents.Any(agent => agent is not IDecidesFromPerception))
        {
            throw new ArgumentException(
                "Every agent must implement IDecidesFromPerception to record decision-time perceptions; " +
                $"agent {agents.First(a => a is not IDecidesFromPerception).AgentId} does not.",
                nameof(agents));
        }

        _map = map;
        _config = config;
        _agents = agents;
        _maxSteps = maxSteps;
        _recordPerceptions = recordPerceptions;
        _perceptions = recordPerceptions ? new List<PartialObservation[]>() : null;
        _state = Simulation.CreateInitial(map, config, rules ?? DynamicMapRuleSet.None);
        _observations = BuildObservations(_state);
    }

    /// <summary>The episode's tick budget: the most ticks this stepper may run.</summary>
    public int MaxSteps => _maxSteps;

    /// <summary>The map every tick is played on.</summary>
    public MapGraph Map => _map;

    /// <summary>The config every tick is stepped under.</summary>
    public SimulationConfig Config => _config;

    /// <summary>The agents, in the array order the caller passed them.</summary>
    public IReadOnlyList<IAgent> Agents => _agents;

    /// <summary>Whether this stepper records decision-time perceptions.</summary>
    public bool RecordsPerceptions => _recordPerceptions;

    /// <summary>
    /// The state after every tick run so far: the initial state before the first
    /// one. An immutable snapshot, so a caller holding it cannot move the episode.
    /// </summary>
    public SimulationState State => _state;

    /// <summary>The actions submitted at each tick so far, in tick order.</summary>
    public IReadOnlyList<AgentAction[]> Turns => _turns;

    /// <summary>The simulation's own results, one per tick so far.</summary>
    public IReadOnlyList<StepResult> Results => _results;

    /// <summary>
    /// The decision-time perceptions, one array per tick in agent-slot order, or
    /// null when this stepper records none.
    /// </summary>
    public IReadOnlyList<PartialObservation[]>? Perceptions => _perceptions;

    /// <summary>How many of the ticks so far were contended.</summary>
    public int ContendedTicks => _contendedTicks;

    /// <summary>How many ticks have been run.</summary>
    public int StepCount => _results.Count;

    /// <summary>
    /// Whether the last tick run was terminal. False before any tick has run: a
    /// stepper that has not started has not finished.
    /// </summary>
    public bool IsTerminal => _results.Count > 0 && _results[^1].Info.IsTerminal;

    /// <summary>Whether the tick budget still has a tick in it.</summary>
    public bool HasBudget => StepCount < _maxSteps;

    /// <summary>
    /// Whether another tick may be run: the budget has not been spent and the last
    /// tick was not terminal. The runner's own loop condition, read as a property
    /// so the two cannot disagree about when an episode is over.
    /// </summary>
    public bool CanStep => HasBudget && !IsTerminal;

    /// <summary>
    /// Runs one tick: every agent decides from the observation built after the
    /// previous tick, the turn is recorded, the tick's contention is counted, and
    /// the pure step function advances the state.
    /// </summary>
    /// <remarks>
    /// <paramref name="stopRequested"/> is consulted before every agent's
    /// <see cref="IAgent.Decide"/> and again before the step is applied, and a tick
    /// that was asked to stop simply does not happen: nothing is recorded, the
    /// observations are not rebound, and the episode is left exactly as it was.
    /// That is the only safe stopping point in this type, because
    /// <see cref="IAgent.Decide"/> is synchronous and takes no token — a call
    /// already running cannot be interrupted, and this contract does not pretend
    /// otherwise. Null, the default, never stops.
    /// </remarks>
    /// <returns>
    /// The tick's result, or null when <paramref name="stopRequested"/> asked to
    /// stop before the tick could run. A null result is the caller's signal that
    /// the episode ended where it stands rather than where it was going.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CanStep"/> is false — the budget is spent or the last tick was
    /// terminal.
    /// </exception>
    public StepResult? Step(Func<bool>? stopRequested = null)
    {
        if (!CanStep)
        {
            throw new InvalidOperationException(
                $"This episode cannot step: {StepCount} tick(s) run against a budget of {_maxSteps}" +
                (IsTerminal ? " and the last tick was terminal." : "."));
        }

        var turn = new AgentAction[_config.AgentCount];
        foreach (var agent in _agents.OrderBy(a => a.AgentId))
        {
            if (stopRequested?.Invoke() == true)
            {
                return null;
            }

            turn[agent.AgentId] = agent.Decide(_observations[agent.AgentId]);
        }

        // Checked again before anything is recorded, so a stopped tick leaves no
        // half-episode behind: no perception, no turn, no contention count, no step.
        if (stopRequested?.Invoke() == true)
        {
            return null;
        }

        if (_perceptions is not null)
        {
            _perceptions.Add(CapturePerceptions(_agents, _config.AgentCount));
        }

        _turns.Add(turn);
        if (HasContention(_state, turn))
        {
            _contendedTicks++;
        }

        var outcome = Simulation.Step(_state, turn, _config);
        _results.Add(outcome.Result);
        _state = outcome.NextState;
        _observations = BuildObservations(_state);

        return outcome.Result;
    }

    /// <summary>
    /// The episode so far as the value the rest of the system consumes: the
    /// aggregates plus the turns, results and perceptions behind them. All three
    /// views describe the same events, so they cannot drift apart.
    /// </summary>
    public ScenarioResult ToResult()
    {
        var lastInfo = _results.Count > 0 ? _results[^1].Info : null;
        var metrics = new ScenarioMetrics(
            TerminationReason: lastInfo?.Reason,
            Terminated: lastInfo?.IsTerminal ?? false,
            TotalSteps: _results.Count,
            MaxSteps: _maxSteps,
            ContendedTicks: _contendedTicks,
            ContentionRate: _results.Count == 0 ? 0.0 : _contendedTicks / (double)_results.Count,
            Agents: BuildAgentMetrics(_state, _turns));

        return new ScenarioResult(
            metrics,
            _turns.ToArray(),
            _results.ToArray(),
            _perceptions?.ToArray());
    }

    /// <summary>
    /// Reads every agent's <see cref="IDecidesFromPerception.LastPerception"/>
    /// right after the turn was decided, in agent-slot order. The agents are
    /// read in the same ascending-id order they were polled in, and each value
    /// is the one its own filter produced for this tick — the stepper never
    /// projects anything itself, because a second projection would be a second
    /// opinion about what the agent saw rather than a record of it.
    /// </summary>
    private static PartialObservation[] CapturePerceptions(IAgent[] agents, int agentCount)
    {
        var captured = new PartialObservation[agentCount];
        foreach (var agent in agents)
        {
            captured[agent.AgentId] = ((IDecidesFromPerception)agent).LastPerception
                ?? throw new InvalidOperationException(
                    $"Agent {agent.AgentId} decided without projecting a perception.");
        }

        return captured;
    }

    private static void ValidateAgents(SimulationConfig config, IAgent[] agents)
    {
        if (agents.Length != config.AgentCount)
        {
            throw new ArgumentException(
                $"Expected {config.AgentCount} agents (per SimulationConfig.AgentCount) but received {agents.Length}.",
                nameof(agents));
        }

        var ids = new HashSet<int>();
        foreach (var agent in agents)
        {
            if (agent.AgentId is < 0 or > 3 || agent.AgentId >= config.AgentCount || !ids.Add(agent.AgentId))
            {
                throw new ArgumentException(
                    $"Agent ids must cover the slots 0..{config.AgentCount - 1} exactly once; saw {agent.AgentId}.",
                    nameof(agents));
            }
        }
    }

    /// <summary>
    /// True when this tick is contended. A tick is contended by a claim race
    /// when at least two distinct agents target the same resource id for
    /// Collect, OR by a transit denial when at least two distinct agents
    /// request a Move across the same capacity-1 choke edge in the same tick
    /// (a single-lane gate only one may hold at a time — the later resolvers
    /// are denied passage). Counting attempts, not outcomes, is deliberate: it
    /// measures how often agents compete for the same prize or gate, which is
    /// exactly the pressure a competitive match is supposed to surface.
    /// </summary>
    private static bool HasContention(SimulationState state, AgentAction[] turn)
    {
        if (HasClaimRace(turn))
        {
            return true;
        }

        return HasTransitDenial(state, turn);
    }

    /// <summary>
    /// True when at least one resource id is the Collect target of two or more
    /// distinct agents in this turn. Duplicates within a single agent's action
    /// are impossible by construction (one action per agent).
    /// </summary>
    private static bool HasClaimRace(AgentAction[] turn)
    {
        foreach (var action in turn)
        {
            if (action.Kind != ActionKind.Collect)
            {
                continue;
            }

            var rivals = 0;
            foreach (var other in turn)
            {
                if (other.Kind == ActionKind.Collect && other.ResourceId == action.ResourceId)
                {
                    rivals++;
                }
            }

            if (rivals > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when two or more agents request a Move across the same capacity-1
    /// choke edge in this turn. Only single-lane (capacity-1) gates are priced:
    /// a wider choke can absorb multiple simultaneous crossings, so it cannot
    /// deny transit. The agent's current zone is read from the pre-step state.
    /// </summary>
    private static bool HasTransitDenial(SimulationState state, AgentAction[] turn)
    {
        var gateRequests = new Dictionary<int, int>();
        for (var i = 0; i < turn.Length; i++)
        {
            var action = turn[i];
            if (action.Kind != ActionKind.Move)
            {
                continue;
            }

            var current = state.Agents[i].ZoneId;
            if (action.ZoneId == current)
            {
                continue;
            }

            var chokeIndex = EdgeChoke(state.Map, current, action.ZoneId);
            if (chokeIndex < 0 || state.Map.ChokePoints[chokeIndex].MaxOccupancy != 1)
            {
                continue;
            }

            gateRequests.TryGetValue(chokeIndex, out var count);
            gateRequests[chokeIndex] = count + 1;
        }

        foreach (var count in gateRequests.Values)
        {
            if (count > 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The index of the choke backing the undirected edge between two zones,
    /// or -1 if no choke connects them. Choke capacity is attributed to the
    /// edge in either direction, so a single-lane choke gates both crossings.
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

    private static AgentMetrics[] BuildAgentMetrics(SimulationState state, List<AgentAction[]> turns)
    {
        return state.Agents
            .Select(agent =>
            {
                var moves = 0;
                foreach (var turn in turns)
                {
                    if (turn[agent.AgentId].Kind == ActionKind.Move)
                    {
                        moves++;
                    }
                }

                var efficiency = agent.Score / (double)Math.Max(1, moves);
                return new AgentMetrics(agent.AgentId, agent.Score, moves, efficiency);
            })
            .OrderBy(metrics => metrics.AgentId)
            .ToArray();
    }

    private static Dictionary<int, Observation> BuildObservations(SimulationState state) =>
        state.Agents.ToDictionary(
            a => a.AgentId,
            a => new Observation(a.AgentId, state.Map, state.Agents, state.Claims, state.StepCount));
}