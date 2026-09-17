using Lattice.Environment;
using Lattice.Generator;
using Xunit;

namespace Lattice.Tests.Generator;

public class ConstraintCheckerTests
{
    [Fact]
    public void Connectivity_Passes_ForLineOfThreeZones()
    {
        var map = ThreeZoneLineMap();
        Assert.True(ConnectivityChecker.IsSatisfied(map));
    }

    [Fact]
    public void Connectivity_Fails_WhenOneZoneIsDisconnected()
    {
        var map = ThreeZoneDisconnectedMap();
        Assert.False(ConnectivityChecker.IsSatisfied(map));
    }

    [Fact]
    public void NoIsolatedZone_Passes_ForLineOfThreeZones()
    {
        var map = ThreeZoneLineMap();
        Assert.True(NoIsolatedZoneChecker.IsSatisfied(map));
    }

    [Fact]
    public void NoIsolatedZone_Fails_WhenOneZoneHasNoChokePoints()
    {
        var map = ThreeZoneDisconnectedMap();
        Assert.False(NoIsolatedZoneChecker.IsSatisfied(map));
    }

    [Fact]
    public void MinimumChokePoint_Passes_WhenEveryZoneMeetsMinimum()
    {
        var map = ThreeZoneTriangleMap();
        Assert.True(MinimumChokePointChecker.IsSatisfied(map, minimumPerZone: 2));
    }

    [Fact]
    public void MinimumChokePoint_Fails_WhenAZoneIsBelowMinimum()
    {
        var map = ThreeZoneTriangleMap();
        Assert.False(MinimumChokePointChecker.IsSatisfied(map, minimumPerZone: 3));
    }

    private static MapGraph ThreeZoneLineMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 10)),
            new Zone(2, new GridPoint(0, 20)),
        },
        Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    private static MapGraph ThreeZoneDisconnectedMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 10)),
            new Zone(2, new GridPoint(0, 20)),
        },
        Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1),
        });

    private static MapGraph ThreeZoneTriangleMap() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 10)),
            new Zone(2, new GridPoint(10, 10)),
        },
        Array.Empty<ResourceNode>(),
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
            new ChokePoint(2, 0, 2),
        });
}