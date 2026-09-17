using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Operational regression coverage for the five hardening work items:
/// deterministic floating-point tie-breaking in the MCTS (seed-independent
/// decision streams), the generator fast-fail budget, capacity-1 choke
/// contention that must resolve in bounded ticks with a deterministic winner,
/// and Greedy/Random agent stall recovery after consecutive denied moves.
/// </summary>
public class OperationalRegressionTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    // ---- MCTS: decision streams identical regardless of the seed handed in ----

    /// <summary>
    /// The "JamLane" provenance map: agent 1 sits one choke from a single-lane
    /// corridor 2-3 leading to the only stash, behind a capacity-1 gate it must
    /// win from opponent slot 0. See MctsAgentTests.JamLaneFixture.
    /// </summary>
    private static MapGraph JamLaneMap => new(
        new[]
        {
            new Zone(0, new GridPoint(16, 0)),
            new Zone(1, new GridPoint(0, 0)),
            new Zone(2, new GridPoint(8, 16)),
            new Zone(3, new GridPoint(24, 16)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(16, 0)),
            new ResourceNode(1, 1, new GridPoint(0, 0)),
            new ResourceNode(2, 3, new GridPoint(24, 16)),
            new ResourceNode(3, 3, new GridPoint(24, 17)),
            new ResourceNode(4, 3, new GridPoint(24, 18)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 0, 2, MaxOccupancy: 1),
            new ChokePoint(2, 1, 2, MaxOccupancy: 1),
            new ChokePoint(3, 2, 3, MaxOccupancy: 1),
        });

    [Fact]
    public void MctsDecisionStream_IsByteIdentical_AcrossSixteenDifferentSeeds()
    {
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 200, TransitSpeed: 8);
        var search = new MctsSearchConfig(rolloutsPerAction: 4, maxDepth: 12);
        var opponent = new GreedyCollectorAgent(0);

        var reference = ScenarioRunner.Run(
            JamLaneMap, config, new IAgent[] { opponent, new MctsAgent(1, config, seed: 0, search) }, maxSteps: 200);

        for (ulong seed = 1; seed <= 16; seed++)
        {
            var run = ScenarioRunner.Run(
                JamLaneMap, config, new IAgent[] { new GreedyCollectorAgent(0), new MctsAgent(1, config, seed, search) }, maxSteps: 200);

            Assert.Equal(Json(reference.Turns), Json(run.Turns));
            Assert.Equal(Json(reference.Results), Json(run.Results));
        }
    }

    // ---- Adversarial capacity-1 choke: opposing crossings cannot livelock ----

    [Fact]
    public void OpposingAgentsOnSingleLaneChoke_ResolveInBoundedTicks_DeterministicWinner()
    {
        // The only stash (two resources at zone 2) sits beyond a single-lane
        // choke 1-2 (capacity 1, a seven-tick crossing). Both identical greedies
        // must cross that lane to score; agent 1 starts at the lanehead (zone 1,
        // spawn is zone i for agent i) so it steps onto the lane first and holds
        // the choke for the full crossing, denying agent 0 until it drains. The
        // episode must end in a finite number of ticks with a deterministic
        // winner (tie -> lowest id) — never a livelock into the tick limit.
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 5)),
                new Zone(2, new GridPoint(0, 55)),
            },
            new[]
            {
                new ResourceNode(0, 2, new GridPoint(0, 56)),
                new ResourceNode(1, 2, new GridPoint(0, 57)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2, MaxOccupancy: 1),
            });
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 60, TransitSpeed: 8);

        var first = ScenarioRunner.Run(map, config, new IAgent[] { new GreedyCollectorAgent(0), new GreedyCollectorAgent(1) }, maxSteps: 60);
        var second = ScenarioRunner.Run(map, config, new IAgent[] { new GreedyCollectorAgent(0), new GreedyCollectorAgent(1) }, maxSteps: 60);

        Assert.Equal(Json(first.Turns), Json(second.Turns));
        Assert.True(first.Metrics.Terminated, "opposing crossings must resolve, not livelock to the tick limit");
        Assert.Equal("resources-exhausted", first.Results[^1].Info.Reason);
        Assert.Equal(1, first.Results[^1].Info.WinnerAgentId);
        Assert.Equal(0, first.Results[^1].Observations[0].AgentStates[0].Score);
        Assert.Equal(2, first.Results[^1].Observations[0].AgentStates[1].Score);

        var terminalStep = first.Results[^1].Info.StepNumber;
        Assert.True(terminalStep < config.MaxTicks, $"bounded resolution expected, got {terminalStep} ticks");

        // The capacity-1 lane must never carry both agents mid-crossing, and
        // agent 0 must actually be held at the lanehead while agent 1 transits
        // (the capacity gate, not luck, sequences the two crossings).
        var holdObserved = false;
        foreach (var step in first.Results)
        {
            var states = step.Observations[0].AgentStates;
            Assert.False(
                states[0].Transit is not null && states[1].Transit is not null,
                $"both agents mid-crossing the single-lane choke at step {step.Info.StepNumber}");
            holdObserved |= states[0].Transit is null && states[1].Transit is not null;
        }

        Assert.True(holdObserved, "capacity gate must hold agent 0 at the lanehead while agent 1 transits");
    }

    // ---- Greedy stall recovery: three denied Moves route around the block ----

    [Fact]
    public void GreedyDeniedMoves_ThreeInARow_RoutesAroundBlockedZone()
    {
        // Two resources behind different first hops from the start: R0 is only
        // reachable through a capacity-1 zone permanently occupied by a waiting
        // agent (the direct route 0 -> 1 -> 2), while R1 is reachable via the
        // open detour branch (0 -> 3 -> 4). The greedy must hammer R0's blocked
        // first hop exactly three times, then pivot to R1's detour instead of
        // hammering the contested edge forever.
        //
        // Agents round-robin to zone i at spawn (agent 0 -> zone 0, agent 1 ->
        // zone 1), so WaitAgent slot 1 pins the capacity-1 blocker exactly where
        // R0's route begins.
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 5), MaxOccupancy: 1),
                new Zone(2, new GridPoint(0, 10)),
                new Zone(3, new GridPoint(5, 0)),
                new Zone(4, new GridPoint(5, 5)),
            },
            new[]
            {
                new ResourceNode(0, 2, new GridPoint(0, 11)),
                new ResourceNode(1, 4, new GridPoint(5, 6)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2),
                new ChokePoint(2, 0, 3),
                new ChokePoint(3, 3, 4),
            });
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 8);

        var (turns, _) = AgentEpisode.Run(
            map, config, new IAgent[] { new GreedyCollectorAgent(0), new WaitAgent(1) }, maxSteps: 8);

        // R0 is nearest (hop count 2 ties R1; resource 0 wins the tie), so the
        // first three turns probe its blocked first hop.
        for (var t = 0; t < 3; t++)
        {
            Assert.Equal(ActionKind.Move, turns[t][0].Kind);
            Assert.Equal(1, turns[t][0].ZoneId);
        }

        // On the third consecutive denial the latch engages and the very next
        // turn pivots to the open detour branch (R1 in zone 4 via hop 3).
        Assert.Equal(ActionKind.Move, turns[3][0].Kind);
        Assert.Equal(3, turns[3][0].ZoneId);

        var replay = AgentEpisode.Run(
            map, config, new IAgent[] { new GreedyCollectorAgent(0), new WaitAgent(1) }, maxSteps: 8);
        Assert.Equal(Json(turns), Json(replay.Turns));
    }

    // ---- Random stall recovery: three denied Moves pause for exactly one Wait ----

    [Fact]
    public void RandomDeniedMoves_ThreeInARow_EmitExactlyOneWaitPause()
    {
        // Feed a genuinely stuck stream: agent pinned at zone 0 while trying to
        // Move. Whatever seed the RNG lands on, once three consecutive Moves
        // have all been denied (agent still in zone 0, target untouched) the
        // fourth decision must be the deterministic single-tick pause — not a
        // fourth hammer of the contested edge.
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 5)),
                new Zone(2, new GridPoint(0, 10)),
            },
            new[]
            {
                new ResourceNode(0, 2, new GridPoint(0, 11)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2),
            });

        var observation = new Observation(
            0, map,
            new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 0) },
            Array.Empty<int>());

        for (var seed = 0UL; seed < 2000; seed++)
        {
            var agent = new RandomAgent(0, new Rng(seed));

            var first = agent.Decide(observation);
            var second = agent.Decide(observation);
            var third = agent.Decide(observation);
            if (first.Kind != ActionKind.Move || second.Kind != ActionKind.Move || third.Kind != ActionKind.Move)
            {
                continue;
            }

            var fourth = agent.Decide(observation);
            Assert.Equal(ActionKind.Wait, fourth.Kind);
            return;
        }

        Assert.Fail("No seed produced three consecutive denied Moves; the stuck stream must eventually trigger the pause.");
    }
}