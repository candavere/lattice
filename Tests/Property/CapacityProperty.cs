using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Property;

/// <summary>
/// Spatial capacity, stated as the two invariants the step function provably
/// honors on every tick (docs/adr-002.md, Simulation.Step):
///
/// Chokes — the move gateway only grants a crossing onto a choke while the
/// choke's running load stays below its effective capacity at that tick. Since
/// the edge slot is reserved for the entire crossing and never freed mid-tick,
/// the number of NEW crossings granted onto a choke in one tick is bounded by
/// <c>max(0, effectiveCapacity - loadAtTickStart)</c>. The precise, noisy peak
/// is deliberately not asserted: a crossing already underway keeps its slot
/// even if a timed portcullis later drops the effective capacity below the
/// in-flight load, so only the permissioning is a real invariant. For static
/// maps the capacity never changes, so the end-state in-flight load also stays
/// within the base capacity (induced from the same gate).
///
/// Zones — at most <c>MaxOccupancy</c> agents may be GRANTED ENTRY into a zone
/// in a single tick: the gateway checks the running per-tick load before each
/// grant. End-state occupancy is deliberately NOT asserted: in-flight
/// crossings converging on a full zone may legitimately overfill it at arrival
/// (arrivals are not re-gated), so the engine's real, testable promise is the
/// entry gate, not a steady-state headcount.
/// </summary>
public sealed class CapacityPropertyTests
{
    [Theory]
    [InlineData(1337)]
    [InlineData(8675309)]
    [InlineData(42424242)]
    public void EntryGrants_And_TransitGrants_NeverExceedCapacity_AnyTick(int baseSeed)
    {
        PropertyHarness.Run("capacity", baseSeed, PropertyHarness.DefaultIterations, caseSeed =>
        {
            var scenario = Arbitrary.Scenario(caseSeed);
            var map = scenario.Map;
            var state = scenario.CreateInitial();

            AssertInitialPlacementWithinCapacity(state, map);

            foreach (var turn in scenario.Actions)
            {
                var input = state;
                var outcome = Simulation.Step(input, turn, scenario.Config);
                var next = outcome.NextState;

                AssertZoneEntryGate(input, next, map);
                AssertChokeTransitGate(input, next, map, staticMap: scenario.Rules is null);

                state = next;
            }
        });
    }

    private static void AssertInitialPlacementWithinCapacity(SimulationState state, MapGraph map)
    {
        var occupancy = new int[map.Zones.Length];
        foreach (var agent in state.Agents)
        {
            Assert.Null(agent.Transit);
            Assert.InRange(agent.ZoneId, 0, map.Zones.Length - 1);
            occupancy[agent.ZoneId]++;
        }

        for (var z = 0; z < map.Zones.Length; z++)
        {
            Assert.True(occupancy[z] <= map.Zones[z].MaxOccupancy, $"initial occupancy of zone {z} exceeds its capacity");
        }
    }

    /// <summary>
    /// Counts, per zone, the agents whose ENTRY was granted this tick — anyone
    /// starting at a node whose move was granted toward that zone (transiting
    /// toward it, or arriving into it this same tick) — and asserts the count
    /// stays within the zone's <c>MaxOccupancy</c>, the per-tick entry gate.
    /// </summary>
    private static void AssertZoneEntryGate(SimulationState input, SimulationState next, MapGraph map)
    {
        var grantedEntries = new int[map.Zones.Length];
        for (var i = 0; i < input.Agents.Length; i++)
        {
            if (input.Agents[i].Transit is not null)
            {
                continue; // transiting agents cannot act this tick
            }

            var inputZone = input.Agents[i].ZoneId;
            var outputAgent = next.Agents[i];
            if (outputAgent.Transit is { } transit)
            {
                if (transit.ToZoneId != inputZone)
                {
                    grantedEntries[transit.ToZoneId]++;
                }
            }
            else if (outputAgent.ZoneId != inputZone)
            {
                grantedEntries[outputAgent.ZoneId]++;
            }
        }

        for (var z = 0; z < map.Zones.Length; z++)
        {
            Assert.True(
                grantedEntries[z] <= map.Zones[z].MaxOccupancy,
                $"zone {z} granted entry to {grantedEntries[z]} agent(s) in one tick (capacity {map.Zones[z].MaxOccupancy})");
        }
    }

    /// <summary>
    /// Asserts the choke movement gate: newly granted crossings onto a choke in
    /// one tick never exceed <c>max(0, effectiveCapacity - inFlightLoad)</c>,
    /// where capacities are read from the INPUT state's dynamic snapshot (the
    /// one the step consulted). On static maps the end-state in-flight load is
    /// additionally bounded by the base choke capacity.
    /// </summary>
    private static void AssertChokeTransitGate(
        SimulationState input,
        SimulationState next,
        MapGraph map,
        bool staticMap)
    {
        foreach (var choke in map.ChokePoints)
        {
            var effectiveCapacity = Effective(input.Dynamics.EffectiveChokeCapacity(map, choke.Id));

            var inFlight = 0; // B: crossings already underway at the input tick
            var newlyEntered = 0; // E: crossings granted onto this choke this tick
            var endLoad = 0; // crossings still underway at the output tick

            for (var i = 0; i < input.Agents.Length; i++)
            {
                var wasTransiting = input.Agents[i].Transit is { } before
                    && IsSameEdge(choke, before.FromZoneId, before.ToZoneId);
                var isTransiting = next.Agents[i].Transit is { } after
                    && IsSameEdge(choke, after.FromZoneId, after.ToZoneId);

                if (wasTransiting)
                {
                    inFlight++;
                }

                if (isTransiting)
                {
                    endLoad++;
                }

                if (!wasTransiting && isTransiting)
                {
                    newlyEntered++;
                }
            }

            Assert.True(
                newlyEntered <= Math.Max(0, effectiveCapacity - inFlight),
                $"choke {choke.Id} ({choke.FromZoneId}-{choke.ToZoneId}) granted {newlyEntered} new crossing(s) " +
                $"while {inFlight} were in flight (effective capacity {effectiveCapacity})");

            if (staticMap)
            {
                Assert.True(
                    endLoad <= choke.MaxOccupancy,
                    $"choke {choke.Id} ({choke.FromZoneId}-{choke.ToZoneId}) ended with {endLoad} crossing(s), " +
                    $"base capacity {choke.MaxOccupancy}");
            }
        }
    }

    private static bool IsSameEdge(ChokePoint choke, int from, int to) =>
        PropertyEvidence.IsSameEdge(choke, from, to);

    /// <summary><see cref="MapLimits.Unlimited"/> is left untouched as a large bound for the arithmetic comparison.</summary>
    private static int Effective(int capacity) => Math.Max(0, capacity);
}