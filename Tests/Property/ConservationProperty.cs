using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Resource conservation and reward integrity, stated as one property per tick:
/// every claimed resource is a real, distinct resource of the map; claims only
/// grow (never silently drop); and the sum of all agent scores equals the
/// number of claims at every tick — the engine's answer to "is any material
/// created or destroyed across an episode?" (each collect is exactly +1).
/// Monotonicity is asserted per agent: a score never regresses between ticks.
/// </summary>
public sealed class ConservationPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void ClaimedResources_And_ScoreSum_AreConserved_EveryTick(int baseSeed)
    {
        PropertyHarness.Run("resource-conservation", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var scenario = Arbitrary.Scenario(caseSeed);
            var map = scenario.Map;
            var resourceCount = map.Resources.Length;
            var previousScores = new int[scenario.AgentCount];
            var state = scenario.CreateInitial();

            Assert.Empty(state.Claims);
            Assert.All(state.Agents, agent => Assert.Equal(0, agent.Score));

            foreach (var turn in scenario.Actions)
            {
                var outcome = Simulation.Step(state, turn, scenario.Config);
                var next = outcome.NextState;

                var claims = next.Claims;
                Assert.True(claims.Length <= resourceCount, "Claims exceed the number of resources on the map.");
                Assert.Equal(claims.Length, claims.Distinct().Count());
                Assert.All(claims, resourceId => Assert.InRange(resourceId, 0, resourceCount - 1));
                Assert.True(claims.Length >= state.Claims.Length, "Claims shrank between ticks.");
                foreach (var resourceId in state.Claims)
                {
                    Assert.Contains(resourceId, claims);
                }

                var sumScores = next.Agents.Sum(agent => agent.Score);
                Assert.Equal(claims.Length, sumScores);

                if (resourceCount > 0)
                {
                    Assert.Equal(resourceCount, sumScores + (resourceCount - claims.Length));
                }

                for (var i = 0; i < next.Agents.Length; i++)
                {
                    Assert.True(
                        next.Agents[i].Score >= previousScores[i],
                        $"Agent {i} score regressed from {previousScores[i]} to {next.Agents[i].Score}.");
                }

                previousScores = next.Agents.Select(agent => agent.Score).ToArray();
                state = next;
            }

            // Terminal bookkeeping: the final line reports exactly what the
            // simulation holds — no phantom resources claimed during recording.
            Assert.Equal(state.Claims.Length, state.Agents.Sum(agent => agent.Score));
        });
    }
}