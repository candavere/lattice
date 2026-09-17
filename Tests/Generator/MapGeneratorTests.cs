using System.Text.Json;
using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Generator;

public class MapGeneratorTests
{
    [Fact]
    public void SameSeed_ProducesByteIdenticalMaps_Across100Runs()
    {
        const ulong seed = 0x1234_5678_9ABC_DEF0UL;
        var expected = JsonSerializer.Serialize(MapGenerator.Generate(seed, TestConfig()));

        for (var run = 1; run < 100; run++)
        {
            var actual = JsonSerializer.Serialize(MapGenerator.Generate(seed, TestConfig()));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void OneHundredDifferentSeeds_AllPassEveryChecker()
    {
        var config = TestConfig();

        for (ulong seed = 0; seed < 100; seed++)
        {
            var map = MapGenerator.Generate(seed, config);

            Assert.True(ConnectivityChecker.IsSatisfied(map), $"seed {seed}: connectivity");
            Assert.True(NoIsolatedZoneChecker.IsSatisfied(map), $"seed {seed}: no isolated zone");
            Assert.True(
                MinimumChokePointChecker.IsSatisfied(map, config.MinChokePointsPerZone),
                $"seed {seed}: minimum choke points");
        }
    }

    [Fact]
    public void ExhaustedRetryCap_ThrowsWithDiagnostic()
    {
        var impossible = new GeneratorConfig(
            MinZones: 3,
            MaxZones: 3,
            MinChokePointsPerZone: 500,
            MinResourcesPerZone: 0,
            MaxResourcesPerZone: 0,
            RetryCap: 5);

        var exception = Assert.Throws<MapGenerationException>(() => MapGenerator.Generate(1UL, impossible));

        Assert.Equal(5, exception.Attempts);
        Assert.Equal((ulong)1, exception.Seed);
        Assert.Contains("5 attempt(s)", exception.Message);
        Assert.Contains("1", exception.Message);
        Assert.NotEmpty(exception.FailedChecks);
    }

    [Fact]
    public void Generate_WithSatisfiedGate_ReturnsByteIdenticalMap()
    {
        const ulong seed = 0x1234_5678_9ABC_DEF0UL;
        var plain = JsonSerializer.Serialize(MapGenerator.Generate(seed, TestConfig()));

        var gated = JsonSerializer.Serialize(
            MapGenerator.Generate(seed, TestConfig(), acceptanceGate: _ => true));

        Assert.Equal(plain, gated);
    }

    [Fact]
    public void Generate_WithRejectingGate_ThrowsAfterRetryCap()
    {
        var smallCap = new GeneratorConfig(
            MinZones: 3,
            MaxZones: 3,
            MinChokePointsPerZone: 1,
            MinResourcesPerZone: 0,
            MaxResourcesPerZone: 1,
            RetryCap: 3);

        var exception = Assert.Throws<MapGenerationException>(() =>
            MapGenerator.Generate(1UL, smallCap, acceptanceGate: _ => false));

        Assert.Contains("acceptance-gate", exception.Message);
        Assert.Contains("acceptance-gate", exception.FailedChecks);
        Assert.Contains("3 attempt(s)", exception.Message);
    }

    private static GeneratorConfig TestConfig() => new(
        MinZones: 3,
        MaxZones: 6,
        MinChokePointsPerZone: 1,
        MinResourcesPerZone: 0,
        MaxResourcesPerZone: 3,
        RetryCap: 50);
}