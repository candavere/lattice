using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Validates <see cref="GreedyCollectorAgent"/>: nearest-by-hop decisions,
/// immediate Collect when standing on an unclaimed resource, claimed-resource
/// skipping, observability-only usage, and deterministic replay across runs.
/// </summary>
public class GreedyCollectorAgentTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();

    private static readonly SimulationConfig Config = new(AgentCount: 2, MaxTicks: 40);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static Observation Obs(MapGraph map, int agentId, int zone, int[]? claims = null) =>
        new(agentId, map, new[] { new AgentState(agentId, zone, 0) }, claims ?? Array.Empty<int>());

    private static IAgent[] MakeAgents(int agentCount = 2)
    {
        var agents = new IAgent[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            agents[i] = new GreedyCollectorAgent(i);
        }

        return agents;
    }

    [Fact]
    public void CollectsImmediatelyWhenStandingOnResourceZone()
    {
        var action = new GreedyCollectorAgent(0).Decide(
            Obs(TriangleMap, 0, 1, claims: Array.Empty<int>()));

        Assert.Equal(ActionKind.Collect, action.Kind);
        Assert.Equal(0, action.ResourceId);
    }

    [Fact]
    public void PicksLowestIdResourceInZone()
    {
        var action = new GreedyCollectorAgent(0).Decide(
            Obs(TriangleMap, 0, 1, claims: new[] { 0 }));

        Assert.Equal(ActionKind.Collect, action.Kind);
        Assert.Equal(1, action.ResourceId);
    }

    [Fact]
    public void MovesTowardNearestUnclaimedByHopCount()
    {
        var lineMap = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 5)),
                new Zone(2, new GridPoint(0, 10)),
            },
            new[]
            {
                new ResourceNode(0, 2, new GridPoint(0, 11)),
                new ResourceNode(1, 2, new GridPoint(0, 12)),
                new ResourceNode(2, 1, new GridPoint(0, 6)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2),
            });

        var action = new GreedyCollectorAgent(0).Decide(
            Obs(lineMap, 0, 0, claims: Array.Empty<int>()));

        Assert.Equal(ActionKind.Move, action.Kind);
        Assert.Equal(1, action.ZoneId);
    }

    [Fact]
    public void SkipsAlreadyClaimedResources()
    {
        var action = new GreedyCollectorAgent(0).Decide(
            Obs(TriangleMap, 0, 1, claims: new[] { 0 }));

        Assert.Equal(ActionKind.Collect, action.Kind);
        Assert.Equal(1, action.ResourceId);
    }

    [Fact]
    public void AllClaimed_ReturnsWait()
    {
        var action = new GreedyCollectorAgent(0).Decide(
            Obs(TriangleMap, 0, 0, claims: new[] { 0, 1, 2 }));

        Assert.Equal(ActionKind.Wait, action.Kind);
    }

    [Fact]
    public void UnreachableResource_ReturnsWait()
    {
        var disconnectedMap = new MapGraph(
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
            });

        var action = new GreedyCollectorAgent(0).Decide(
            Obs(disconnectedMap, 0, 0, claims: Array.Empty<int>()));

        Assert.Equal(ActionKind.Wait, action.Kind);
    }

    [Fact]
    public void DoesNotMutateObservation()
    {
        var agent = new GreedyCollectorAgent(0);
        var observation = Obs(TriangleMap, 0, 0);

        var before = Json(observation);
        agent.Decide(observation);
        Assert.Equal(before, Json(observation));
    }

    [Fact]
    public void SameObservation_AnyCall_SameAction()
    {
        var agent = new GreedyCollectorAgent(0);
        var observation = Obs(TriangleMap, 0, 0);
        var expected = agent.Decide(observation);
        var second = agent.Decide(observation);
        Assert.Equal(Json(expected), Json(second));
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
        var agents = MakeAgents();

        var (turns, _) = AgentEpisode.Run(map, Config, agents, 40);

        foreach (var turn in turns)
        {
            foreach (var action in turn)
            {
                Assert.True(ActionSpace.IsValid(action, map));
            }
        }
    }

    [Fact]
    public void SameSeed_GreedyEpisode_DeterministicAcrossRuns()
    {
        const ulong seed = 0xC0FFEEUL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var map = MapGenerator.Generate(seed, generatorConfig);
        var agents = MakeAgents();

        var first = AgentEpisode.Run(map, Config, agents, 40);
        var second = AgentEpisode.Run(map, Config, agents, 40);

        Assert.Equal(Json(first.Turns), Json(second.Turns));
        Assert.Equal(Json(first.Results), Json(second.Results));
    }
}