using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Search-budget configuration for <see cref="MctsAgent"/>. Both knobs are
/// validated at construction so a defective configuration fails loudly instead
/// of degrading silently to a weak search.
/// </summary>
public sealed record MctsSearchConfig
{
    /// <summary>
    /// Validates the search budget: at least one rollout per candidate and at
    /// least one continuation tick per rollout.
    /// </summary>
    public MctsSearchConfig(int rolloutsPerAction = 16, int maxDepth = 12)
    {
        if (rolloutsPerAction < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rolloutsPerAction), rolloutsPerAction, "rolloutsPerAction must be >= 1.");
        }

        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "maxDepth must be >= 1.");
        }

        RolloutsPerAction = rolloutsPerAction;
        MaxDepth = maxDepth;
    }

    /// <summary>Number of independent rollouts averaged per candidate action.</summary>
    public int RolloutsPerAction { get; }

    /// <summary>Maximum continuation ticks examined inside a single rollout.</summary>
    public int MaxDepth { get; }
}

/// <summary>
/// A rollout-based tactical agent: it prices the future instead of
/// greedily grabbing the nearest resource. For each legal action it runs
/// fast, in-memory rollouts of the whole episode from the exact position the
/// observation describes — the decisive move, then the rival agents and its
/// own continuation played by the deterministic nearest-resource policy — and
/// reports the total score that action yields across its rollouts. Candidates
/// are ranked by that integer (<see cref="long"/>) total, never by a
/// floating-point average, so the win/lose verdict between two actions can
/// never flip across platforms or via FP rounding. When totals tie, the
/// tie-break is the canonical candidate order — Wait, then Moves in ascending
/// zone id, then Collects in ascending resource id — so the decision is a
/// total function of the observation alone. All search runs on detached
/// <see cref="SimulationFork"/>s built solely from the public
/// <see cref="Observation"/> data, so the live simulation and any recorded
/// trajectory stay untouched.
/// </summary>
public sealed class MctsAgent : IAgent
{
    private readonly SimulationConfig _config;
    private readonly MctsSearchConfig _search;
    private readonly GreedyCollectorAgent[] _rolloutPolicy;

    /// <summary>
    /// Creates the agent pinned to the given slot. <paramref name="config"/> is
    /// the episode's own simulation parameters (transit and capacity included),
    /// so rollouts reproduce exactly the physics the live episode obeys.
    /// <paramref name="seed"/> is accepted for construction parity with the
    /// other agent families; decisions are fully determined by the observation
    /// and never vary with the seed.
    /// </summary>
    public MctsAgent(int agentId, SimulationConfig config, ulong seed, MctsSearchConfig search)
    {
        if (agentId < 0 || agentId >= config.AgentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(agentId), agentId, $"AgentId must be within 0..{config.AgentCount - 1}.");
        }

        AgentId = agentId;
        _config = config;
        _search = search;
        _rolloutPolicy = new GreedyCollectorAgent[config.AgentCount];
        for (var i = 0; i < config.AgentCount; i++)
        {
            // Rollout policies stay strictly stateless: every rollout is an
            // independent continuation from the same position, so a policy
            // that carried blocked-tick history across rollouts would bias the
            // value estimates toward action enumeration order.
            _rolloutPolicy[i] = new GreedyCollectorAgent(i, stallRecovery: false);
        }
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        var state = new SimulationState(
            observation.Map,
            observation.AgentStates,
            observation.Claims,
            StepCount: 0); // tick-limit is irrelevant: rollouts clamp at MaxDepth

        var candidates = CandidateActions(observation);
        var turn = new AgentAction[_config.AgentCount];
        var observations = new Dictionary<int, Observation>(_config.AgentCount);

        var bestTotal = long.MinValue;
        var bestIndexes = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var total = 0L;
            for (var rollout = 0; rollout < _search.RolloutsPerAction; rollout++)
            {
                total += Rollout(state, candidates[i], turn, observations);
            }

            if (total > bestTotal)
            {
                bestTotal = total;
                bestIndexes.Clear();
                bestIndexes.Add(i);
            }
            else if (total == bestTotal)
            {
                bestIndexes.Add(i);
            }
        }

        // Ties resolve to the first index: candidates are emitted in the
        // canonical order (Wait, Moves ascending, Collects ascending), so an
        // equal total breaks by that explicit ordering, deterministically.
        return candidates[bestIndexes[0]];
    }

    /// <summary>
    /// Every legal action in a fixed order: Wait, then Moves to adjacent zones
    /// (ascending zone id), then Collects of unclaimed resources in the agent's
    /// own zone (ascending resource id). Built from the observation only, so
    /// every candidate is in the action space by construction. This order is
    /// the documented tie-break for equal rollout totals.
    /// </summary>
    private List<AgentAction> CandidateActions(Observation observation)
    {
        var myZone = ObservationView.MyZone(observation);
        var candidates = new List<AgentAction> { new(ActionKind.Wait) };

        var targets = new SortedSet<int>();
        foreach (var choke in observation.Map.ChokePoints)
        {
            if (choke.FromZoneId == myZone)
            {
                targets.Add(choke.ToZoneId);
            }
            else if (choke.ToZoneId == myZone)
            {
                targets.Add(choke.FromZoneId);
            }
        }

        foreach (var target in targets)
        {
            candidates.Add(new AgentAction(ActionKind.Move, ZoneId: target));
        }

        foreach (var resource in ObservationView.Unclaimed(observation))
        {
            if (resource.ZoneId == myZone)
            {
                candidates.Add(new AgentAction(ActionKind.Collect, ResourceId: resource.Id));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Plays one full episode from <paramref name="state"/> with
    /// <paramref name="firstMove"/> as this agent's first action; from the
    /// second tick on, every agent (including this one) follows the seeded
    /// nearest-resource policy until the episode ends or the depth budget runs
    /// out. Falls back to a Wait turn when a policy agent is asked to act on a
    /// state it is no longer valid for. Returns the agent's final score as an
    /// integer, so the caller's rollup is exact integer arithmetic.
    /// <paramref name="turn"/> and <paramref name="observations"/> are caller
    /// owned scratch buffers, reused across ticks and rollouts so the search
    /// allocates a fixed amount per decision rather than per tick.
    /// </summary>
    private int Rollout(
        SimulationState state,
        AgentAction firstMove,
        AgentAction[] turn,
        Dictionary<int, Observation> observations)
    {
        var fork = SimulationFork.Create(state, _config);
        PopulateObservations(observations, fork.Snapshot);

        turn[AgentId] = firstMove;
        for (var i = 0; i < _config.AgentCount; i++)
        {
            if (i == AgentId)
            {
                continue;
            }

            turn[i] = _rolloutPolicy[i].Decide(observations[i]);
        }

        fork.Step(turn);

        var ticks = 1;
        while (!fork.IsTerminal && ticks < _search.MaxDepth)
        {
            PopulateObservations(observations, fork.Snapshot);
            for (var i = 0; i < _config.AgentCount; i++)
            {
                turn[i] = _rolloutPolicy[i].Decide(observations[i]);
            }

            fork.Step(turn);
            ticks++;
        }

        return fork.Snapshot.Agents[AgentId].Score;
    }

    private static void PopulateObservations(
        Dictionary<int, Observation> observations,
        SimulationState state)
    {
        observations.Clear();
        foreach (var agent in state.Agents)
        {
            observations.Add(
                agent.AgentId,
                new Observation(agent.AgentId, state.Map, state.Agents, state.Claims));
        }
    }
}

/// <summary>
/// The MCTS family label for the batch harness. Each agent slot is
/// seeded deterministically from (familySeed, runSeed, slot) like the other
/// families, and the episode's own simulation config is carried through so
/// rollouts reproduce the live physics.
/// </summary>
public sealed class MctsAgentFactory : IAgentFactory
{
    private readonly SimulationConfig _config;
    private readonly MctsSearchConfig _search;
    private readonly ulong _familySeed;

    public MctsAgentFactory(SimulationConfig config, MctsSearchConfig search, string name = "MCTS", ulong familySeed = 0)
    {
        _config = config;
        _search = search;
        Name = name;
        _familySeed = familySeed;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IAgent Create(int agentId, ulong runSeed) =>
        new MctsAgent(agentId, _config, _familySeed ^ (runSeed * 1_000_003UL) ^ (ulong)agentId, _search);
}