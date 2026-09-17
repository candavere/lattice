using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="ScoutCollectorAgent"/> (T7.2): decisions derive only
/// from what the scout has actually seen (its projected partial view plus
/// retained memory) — never from hidden simulation state; it collects lazily
/// under partial knowledge, routes around remembered enemy positions, explores
/// unknown territory, and is fully deterministic.
/// </summary>
public class ScoutCollectorAgentTests
{
    private static readonly SimulationConfig TwoAgents = new(AgentCount: 2, MaxTicks: 40);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static MapGraph RingMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 5)),
            new Zone(2, new GridPoint(5, 5)),
            new Zone(3, new GridPoint(5, 0)),
        },
        new[]
        {
            new ResourceNode(0, 1, new GridPoint(0, 6)),
            new ResourceNode(1, 2, new GridPoint(5, 6)),
            new ResourceNode(2, 3, new GridPoint(6, 0)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
            new ChokePoint(2, 2, 3),
            new ChokePoint(3, 3, 0),
        });

    private static MapGraph LineMap(bool withResource) => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 5)),
            new Zone(2, new GridPoint(0, 10)),
        },
        withResource
            ? new[] { new ResourceNode(0, 2, new GridPoint(0, 11)) }
            : Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    private static Observation Obs(MapGraph map, params AgentState[] states) =>
        new(0, map, states, Array.Empty<int>());

    // ---- Partial-knowledge decisions (the T7.2 guardrails) ----

    [Fact]
    public void DoesNotCollectResourcesItCannotCurrentlySee()
    {
        var scout = new ScoutCollectorAgent(0, vision: 1);
        var map = LineMap(withResource: true);

        var first = scout.Decide(Obs(map, new AgentState(0, 0, 0)));
        Assert.Equal(ActionKind.Move, first.Kind);
        Assert.Equal(1, first.ZoneId);

        var second = scout.Decide(Obs(map, new AgentState(0, 1, 0)));
        Assert.Equal(ActionKind.Move, second.Kind);
        Assert.Equal(2, second.ZoneId);

        var third = scout.Decide(Obs(map, new AgentState(0, 2, 0)));
        Assert.Equal(ActionKind.Collect, third.Kind);
        Assert.Equal(0, third.ResourceId);
    }

    [Fact]
    public void DoesNotEnterLastKnownEnemyZone()
    {
        var scout = new ScoutCollectorAgent(0, vision: 2);
        var map = LineMap(withResource: true);

        var action = scout.Decide(
            Obs(map, new AgentState(0, 0, 0), new AgentState(1, 1, 5)));

        Assert.Equal(ActionKind.Wait, action.Kind);
    }

    [Fact]
    public void RoutesAroundEnemyZone_ToReachFarResource()
    {
        var scout = new ScoutCollectorAgent(0, vision: 1);
        var map = RingMap();

        var action = scout.Decide(
            Obs(map, new AgentState(0, 0, 0), new AgentState(1, 1, 4)));

        Assert.Equal(ActionKind.Move, action.Kind);
        Assert.Equal(3, action.ZoneId);
        Assert.NotEqual(1, action.ZoneId);
    }

    // ---- Episode-level integration ----

    [Fact]
    public void ScoutEpisodes_EveryActionValid_CollectsOnlyOwnZoneResources()
    {
        const ulong seed = 0xBEEFUL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(seed, generatorConfig);
        var agents = new IAgent[] { new ScoutCollectorAgent(0, vision: 1), new GreedyCollectorAgent(1) };
        var (turns, results) = AgentEpisode.Run(map, TwoAgents, agents, 40);

        var state = Simulation.CreateInitial(map, TwoAgents);
        foreach (var (turnIndex, turn) in turns.Select((turn, index) => (index, turn)))
        {
            for (var agentId = 0; agentId < turn.Length; agentId++)
            {
                var action = turn[agentId];
                var myZone = state.Agents.First(agent => agent.AgentId == agentId).ZoneId;

                if (action.Kind == ActionKind.Collect)
                {
                    Assert.Equal(
                        map.Resources[action.ResourceId].ZoneId,
                        myZone);
                }
                else if (action.Kind == ActionKind.Move)
                {
                    var neighbors = map.ChokePoints
                        .Where(choke => choke.FromZoneId == myZone || choke.ToZoneId == myZone)
                        .Select(choke => choke.FromZoneId == myZone ? choke.ToZoneId : choke.FromZoneId);
                    Assert.Contains(action.ZoneId, neighbors);
                }
            }

            var outcome = Simulation.Step(state, turn, TwoAgents);
            state = outcome.NextState;
        }

        foreach (var turn in turns)
        {
            foreach (var action in turn)
            {
                Assert.True(ActionSpace.IsValid(action, map));
            }
        }

        Assert.NotEmpty(results);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(999UL)]
    public void EveryActionIsValidAcrossSeeds(ulong generatorSeed)
    {
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(generatorSeed, generatorConfig);
        var agents = new IAgent[] { new ScoutCollectorAgent(0, vision: 1), new GreedyCollectorAgent(1) };

        var (turns, _) = AgentEpisode.Run(map, TwoAgents, agents, 40);

        foreach (var turn in turns)
        {
            foreach (var action in turn)
            {
                Assert.True(ActionSpace.IsValid(action, map));
            }
        }
    }

    [Fact]
    public void SameSeed_ScoutEpisode_DeterministicAcrossRuns()
    {
        const ulong seed = 0xF00DUL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(seed, generatorConfig);

        var first = AgentEpisode.Run(map, TwoAgents, new IAgent[] { new ScoutCollectorAgent(0, vision: 1), new GreedyCollectorAgent(1) }, 40);
        var second = AgentEpisode.Run(map, TwoAgents, new IAgent[] { new ScoutCollectorAgent(0, vision: 1), new GreedyCollectorAgent(1) }, 40);

        Assert.Equal(Json(first.Turns), Json(second.Turns));
        Assert.Equal(Json(first.Results), Json(second.Results));
    }

    [Fact]
    public void ScoutCollectsAllReachableResources_WhileAvoidingEnemyZone()
    {
        // Enemy (WaitAgent) occupies zone 1, which holds no resource here.
        // The scout must route the long way round (0 -> 3 -> 2) and can still
        // clear both resources while never entering the enemy's zone.
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 5)),
                new Zone(2, new GridPoint(5, 5)),
                new Zone(3, new GridPoint(5, 0)),
            },
            new[]
            {
                new ResourceNode(0, 2, new GridPoint(5, 6)),
                new ResourceNode(1, 3, new GridPoint(6, 0)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2),
                new ChokePoint(2, 2, 3),
                new ChokePoint(3, 3, 0),
            });
        var config = new SimulationConfig(AgentCount: 2, MaxTicks: 30, Vision: 2);

        var (turns, results) = AgentEpisode.Run(
            map, config,
            new IAgent[] { new ScoutCollectorAgent(0, vision: 2), new WaitAgent(1) },
            30);

        Assert.True(results[^1].Info.IsTerminal);
        Assert.Equal("resources-exhausted", results[^1].Info.Reason);
        Assert.Equal(2, results[^1].Observations[0].AgentStates[0].Score);
        Assert.Equal(0, results[^1].Observations[0].AgentStates[1].Score);

        // The scout never stepped into the enemy's last-known zone.
        foreach (var turn in turns)
        {
            if (turn[0].Kind == ActionKind.Move)
            {
                Assert.NotEqual(1, turn[0].ZoneId);
            }
        }
    }
}