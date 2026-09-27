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
    /// <param name="perceptions">
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

        var state = Simulation.CreateInitial(map, config, rules ?? DynamicMapRuleSet.None);
        var observations = BuildObservations(state);
        var turns = new List<AgentAction[]>();
        var results = new List<StepResult>();
        var perceptions = recordPerceptions ? new List<PartialObservation[]>() : null;
        var contendedTicks = 0;

        for (var step = 0; step < maxSteps && (results.Count == 0 || !results[^1].Info.IsTerminal); step++)
        {
            var turn = new AgentAction[config.AgentCount];
            foreach (var agent in agents.OrderBy(a => a.AgentId))
            {
                turn[agent.AgentId] = agent.Decide(observations[agent.AgentId]);
            }

            if (perceptions is not null)
            {
                perceptions.Add(CapturePerceptions(agents, config.AgentCount));
            }

            turns.Add(turn);
            if (HasContention(state, turn))
            {
                contendedTicks++;
            }

            var outcome = Simulation.Step(state, turn, config);
            results.Add(outcome.Result);
            state = outcome.NextState;
            observations = BuildObservations(state);
        }

        var lastInfo = results.Count > 0 ? results[^1].Info : null;
        var metrics = new ScenarioMetrics(
            TerminationReason: lastInfo?.Reason,
            Terminated: lastInfo?.IsTerminal ?? false,
            TotalSteps: results.Count,
            MaxSteps: maxSteps,
            ContendedTicks: contendedTicks,
            ContentionRate: results.Count == 0 ? 0.0 : contendedTicks / (double)results.Count,
            Agents: BuildAgentMetrics(state, turns));

        return new ScenarioResult(metrics, turns.ToArray(), results.ToArray(), perceptions?.ToArray());
    }

    /// <summary>
    /// Reads every agent's <see cref="IDecidesFromPerception.LastPerception"/>
    /// right after the turn was decided, in agent-slot order. The agents are
    /// read in the same ascending-id order they were polled in, and each value
    /// is the one its own filter produced for this tick — the runner never
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