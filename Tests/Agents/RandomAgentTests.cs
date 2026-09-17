using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="RandomAgent"/>: every decision is in the action space,
/// observations are never mutated, and the same (seed, map, config) produces
/// an identical sequence of turns across independent runs.
/// </summary>
public class RandomAgentTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();

    private static readonly SimulationConfig Config = new(AgentCount: 2, MaxTicks: 30);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static IAgent[] MakeAgents(ulong rngSeed, int agentCount)
    {
        var agents = new IAgent[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            agents[i] = new RandomAgent(i, new Rng(rngSeed + (ulong)i));
        }

        return agents;
    }

    [Fact]
    public void SameSeed_SameEpisode_ProducesIdenticalActions()
    {
        const ulong rngSeed = 0xA11CEUL;
        var agents = MakeAgents(rngSeed, 2);
        var first = AgentEpisode.Run(TriangleMap, Config, agents, 20);
        var second = AgentEpisode.Run(TriangleMap, Config, MakeAgents(rngSeed, 2), 20);

        Assert.Equal(Json(first.Turns), Json(second.Turns));
        Assert.Equal(Json(first.Results), Json(second.Results));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(42UL)]
    [InlineData(999UL)]
    [InlineData(0xDEAD_BEEFUL)]
    public void EveryActionIsValidAcrossMaps(ulong generatorSeed)
    {
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(generatorSeed, generatorConfig);
        var agents = MakeAgents(generatorSeed, 2);

        var (turns, _) = AgentEpisode.Run(map, Config, agents, 30);

        foreach (var turn in turns)
        {
            foreach (var action in turn)
            {
                Assert.True(ActionSpace.IsValid(action, map), $"Out-of-space action: {action}");
            }
        }
    }

    [Fact]
    public void EveryActionIsValidAcrossManySeeds()
    {
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        for (var seed = 0UL; seed < 5; seed++)
        {
            var map = MapGenerator.Generate(seed, generatorConfig);
            var agents = MakeAgents(seed, 2);
            var (turns, _) = AgentEpisode.Run(map, Config, agents, 30);
            foreach (var turn in turns)
            {
                foreach (var action in turn)
                {
                    Assert.True(ActionSpace.IsValid(action, map));
                }
            }
        }
    }

    [Fact]
    public void DoesNotMutateObservation()
    {
        var rng = new Rng(7UL);
        var agent = new RandomAgent(0, rng);
        var observation = new Observation(
            0,
            TriangleMap,
            new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 0) },
            Array.Empty<int>());

        var before = Json(observation);
        agent.Decide(observation);
        Assert.Equal(before, Json(observation));
    }

    [Fact]
    public void DoesNotMutateMapOrClaims()
    {
        var rng = new Rng(3UL);
        var agent = new RandomAgent(0, rng);
        var observation = new Observation(
            0,
            TriangleMap,
            new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 0) },
            new[] { 0 });

        var mapBefore = Json(observation.Map);
        var claimsBefore = Json(observation.Claims);
        agent.Decide(observation);
        Assert.Equal(mapBefore, Json(observation.Map));
        Assert.Equal(claimsBefore, Json(observation.Claims));
    }
}