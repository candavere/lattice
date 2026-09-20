using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Perception reachability: the vision-bounded projection must expose exactly
/// and only what is within the observer's cone, as independently derived by a
/// from-scratch BFS over the map's choke edges. For a fresh
/// <see cref="PerceptionFilter"/> (no historical memory) the contract collapses
/// to: a zone is Observed if and only if it is within <c>Vision</c> graph hops
/// of the observer's (post-step) zone; zones, resources, and agents inside the
/// cone are observed in real time; and the visible-claims set is precisely the
/// claimed resources whose zones are in the cone. This pins cone radius,
/// springboard anchoring (transiting observers anchor at their departure
/// zone, matching <see cref="Simulation.Step"/>'s observation), and neighbor
/// disclosure (<see cref="ZoneSight.ObservedNeighbors"/>) against an
/// independent oracle.
/// </summary>
public sealed class PerceptionReachabilityPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void ObservedCone_MatchesIndependentBFS_WithinVisionHops(int baseSeed)
    {
        PropertyHarness.Run("perception-reachability", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var scenario = Arbitrary.Scenario(caseSeed);
            var map = scenario.Map;
            var vision = scenario.Config.Vision;
            var neighborLists = PropertyEvidence.NeighborLists(map);
            var state = scenario.CreateInitial();

            foreach (var turn in scenario.Actions)
            {
                var outcome = Simulation.Step(state, turn, scenario.Config);
                var next = outcome.NextState;

                for (var observer = 0; observer < scenario.AgentCount; observer++)
                {
                    var observation = outcome.Result.Observations[observer];
                    var observerZone = observation.AgentStates[observer].ZoneId;
                    var distances = PropertyEvidence.HopDistances(map, observerZone);
                    var filter = new PerceptionFilter(map, observer, vision);
                    var partial = filter.Project(observation.StepNumber, observation);

                    Assert.Equal(vision, partial.Vision);

                    var unbounded = vision == SimulationConfig.UnboundedVision;

                    // Zones: observed if and only if within the cone; a fresh
                    // filter carries no stale memory, so anything outside the
                    // cone is Unknown this tick.
                    for (var z = 0; z < map.Zones.Length; z++)
                    {
                        var sight = partial.Zones[z];
                        var inCone = distances[z] >= 0 && (unbounded || distances[z] <= vision);
                        if (inCone)
                        {
                            Assert.Equal(KnowledgeStatus.Observed, sight.Status);
                            Assert.Equal(neighborLists[z], sight.ObservedNeighbors);
                            Assert.NotNull(sight.LastKnownPosition);
                            Assert.Equal(observation.StepNumber, sight.LastSeenTick);
                        }
                        else
                        {
                            Assert.Equal(KnowledgeStatus.Unknown, sight.Status);
                            Assert.Empty(sight.ObservedNeighbors);
                            Assert.Null(sight.LastKnownPosition);
                            Assert.Equal(-1, sight.LastSeenTick);
                        }
                    }

                    // Resources: observed if and only if their zone is in the cone.
                    foreach (var resource in map.Resources)
                    {
                        var sight = partial.Resources[resource.Id];
                        var inCone = unbounded || (distances[resource.ZoneId] >= 0 && distances[resource.ZoneId] <= vision);
                        Assert.Equal(inCone ? KnowledgeStatus.Observed : KnowledgeStatus.Unknown, sight.Status);
                    }

                    // Agents: observed if and only if their (end-state) zone is in the cone.
                    for (var other = 0; other < scenario.AgentCount; other++)
                    {
                        var sight = partial.Agents[other];
                        var otherZone = observation.AgentStates[other].ZoneId;
                        var inCone = unbounded || (distances[otherZone] >= 0 && distances[otherZone] <= vision);
                        Assert.Equal(inCone ? KnowledgeStatus.Observed : KnowledgeStatus.Unknown, sight.Status);
                        if (inCone)
                        {
                            Assert.NotNull(sight.LastKnownState);
                        }
                        else
                        {
                            Assert.Null(sight.LastKnownState);
                        }
                    }

                    // Visible claims: exactly the claimed resources whose zones
                    // are in the cone, in claim order.
                    var expectedVisible = observation.Claims
                        .Where(resourceId => unbounded
                            || (distances[map.Resources[resourceId].ZoneId] >= 0
                                && distances[map.Resources[resourceId].ZoneId] <= vision))
                        .ToArray();
                    Assert.Equal(expectedVisible, partial.VisibleClaims);
                }

                state = next;
            }
        });
    }
}