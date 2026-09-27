using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Agents;

/// <summary>
/// Pins the "Dungeon Infiltration &amp; Sentry Patrol" demonstration scenario:
/// the hand-authored dungeon's tactical roles and capacity-1 gates, the
/// seed-parameterized outcome taxonomy, and the determinism guarantee that a
/// (seed, budget) pair replays byte-for-byte. Outcomes are asserted as stable
/// regression anchors for the shipped agent policies, not as a re-derivation of
/// the win conditions.
/// </summary>
public class InfiltrationScenarioTests
{
    /// <summary>The (seed, budget) anchor whose full heist is the demo trajectory.</summary>
    private const ulong DemoSeed = 42;

    /// <summary>A budget large enough to always run the episode to completion.</summary>
    private const int AmpleBudget = 400;

    [Fact]
    public void Dungeon_CarriesTacticalRolesAndCapacityGates()
    {
        var map = DungeonMapBuilder.BuildCore();

        Assert.Equal(6, map.Zones.Length);
        Assert.All(map.Zones, zone => Assert.False(string.IsNullOrEmpty(zone.Role)));
        Assert.Contains(map.Zones, zone => zone.Id == DungeonMapBuilder.TreasureVaultZone && zone.Role == DungeonRoles.TreasureVault);
        Assert.Contains(map.Zones, zone => zone.Id == DungeonMapBuilder.EntryHallZone && zone.Role == DungeonRoles.EntryHall);

        Assert.Equal(7, map.ChokePoints.Length);
        Assert.All(map.ChokePoints, choke =>
        {
            Assert.Equal(1, choke.MaxOccupancy);
            Assert.True(choke.Role is DungeonRoles.Portcullis or DungeonRoles.AirLockDoorway);
        });

        var extraction = Assert.Single(map.Resources, r => r.Role == DungeonRoles.ObjectiveExtraction);
        Assert.Equal(DungeonMapBuilder.EntryHallZone, extraction.ZoneId);
        Assert.Equal(0, extraction.Id);

        var chests = map.Resources.Where(r => r.Role == DungeonRoles.TreasureChest).ToList();
        Assert.Equal(2, chests.Count);
        Assert.All(chests, chest => Assert.Equal(DungeonMapBuilder.TreasureVaultZone, chest.ZoneId));
    }

    [Fact]
    public void Build_IsSeedStable()
    {
        static string Serialize(ulong seed) => JsonSerializer.Serialize(DungeonMapBuilder.Build(seed));

        Assert.Equal(Serialize(7), Serialize(7));
    }

    [Fact]
    public void Build_ScalesTheTreasuryWithTheSeed()
    {
        var chestCounts = Enumerable.Range(0, 24)
            .Select(seed => DungeonMapBuilder.Build((ulong)seed).Resources.Length)
            .Distinct()
            .OrderBy(count => count)
            .ToArray();

        Assert.Equal(new[] { 3, 4 }, chestCounts);
    }

    [Fact]
    public void Run_IsDeterministicAcrossRuns()
    {
        var first = InfiltrationScenario.Run(DemoSeed, AmpleBudget);
        var second = InfiltrationScenario.Run(DemoSeed, AmpleBudget);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Base.Metrics.TotalSteps, second.Base.Metrics.TotalSteps);
        Assert.Equal(first.Base.Turns, second.Base.Turns);
        Assert.Equal(first.Infiltrator.Score, second.Infiltrator.Score);
    }

    [Fact]
    public void Run_WithDifferentSeeds_ProducesDistinctEpisodes()
    {
        var seedOne = InfiltrationScenario.Run(1, AmpleBudget);
        var seedTwo = InfiltrationScenario.Run(2, AmpleBudget);

        Assert.NotEqual(
            seedOne.Base.Metrics.TotalSteps,
            seedTwo.Base.Metrics.TotalSteps);
    }

    [Fact]
    public void Run_FullBudget_CompletesTheHeistButIsCaughtAtTheExit()
    {
        var run = InfiltrationScenario.Run(DemoSeed, AmpleBudget);

        Assert.True(run.Outcome.Exfiltrated);
        Assert.True(run.Outcome.Intercepted);
        Assert.Equal("intercepted-after-exfil", run.Outcome.Status);

        var final = run.Base.Results[^1].Observations[0];
        Assert.Equal(run.Map.Resources.Length, final.Claims.Length);
        Assert.Equal(run.Map.Resources.Length, run.Infiltrator.Score);
        Assert.Equal(0, run.Sentry.Score);
    }

    [Fact]
    public void Run_TightBudget_InterceptsBeforeTheHaulFinishes()
    {
        // Under the gated engine a same-tick swap through a capacity-1
        // portcullis is denied for the second crosser, so the tight-budget
        // run ends with the sentry catching the rogue at a tick boundary
        // before the haul finishes (the pre-gate engine granted the swap).
        var run = InfiltrationScenario.Run(DemoSeed, maxSteps: 6);

        Assert.True(run.Outcome.Intercepted);
        Assert.False(run.Outcome.Exfiltrated);
        Assert.Equal("intercepted", run.Outcome.Status);
        Assert.True(run.Base.Results[^1].Observations[0].Claims.Length < run.Map.Resources.Length);
    }

    [Fact]
    public void Run_AcrossSeeds_SentryNeverCollectsForTheRogue()
    {
        foreach (var seed in Enumerable.Range(0, 8).Select(s => (ulong)s))
        {
            var run = InfiltrationScenario.Run(seed, AmpleBudget);
            Assert.Equal(0, run.Sentry.Score);
        }
    }

    [Fact]
    public void Run_InterceptionImpliesATickBoundaryColocation()
    {
        var run = InfiltrationScenario.Run(DemoSeed, AmpleBudget);
        Assert.True(run.Outcome.Intercepted);

        var colocated = run.Base.Results.Any(step =>
        {
            var observation = step.Observations[0];
            var sentry = observation.AgentStates[InfiltrationScenario.SentryAgentId];
            var infiltrator = observation.AgentStates[InfiltrationScenario.InfiltratorAgentId];
            return sentry.Transit is null && infiltrator.Transit is null && sentry.ZoneId == infiltrator.ZoneId;
        });

        Assert.True(colocated);
    }

    [Fact]
    public void Run_NonPositiveBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InfiltrationScenario.Run(DemoSeed, maxSteps: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => InfiltrationScenario.Run(DemoSeed, maxSteps: -3));
    }

    [Theory]
    [InlineData(false, false, "timeout")]
    [InlineData(false, true, "exfiltrated")]
    [InlineData(true, false, "intercepted")]
    [InlineData(true, true, "intercepted-after-exfil")]
    public void OutcomeStatus_MapsBothFlags(bool intercepted, bool exfiltrated, string expected)
    {
        Assert.Equal(expected, new InfiltrationOutcome(intercepted, exfiltrated).Status);
    }
}
