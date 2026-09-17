using System.Text.Json;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Generator;

public class MapGraphTests
{
    [Fact]
    public void MapGraph_RoundTripsThroughJson_WithEqualContent()
    {
        var original = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 10)),
                new Zone(2, new GridPoint(10, 10)),
            },
            new[]
            {
                new ResourceNode(0, 1, new GridPoint(1, 9)),
                new ResourceNode(1, 2, new GridPoint(9, 11)),
            },
            new[]
            {
                new ChokePoint(0, 0, 1),
                new ChokePoint(1, 1, 2),
                new ChokePoint(2, 0, 2),
            });

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<MapGraph>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Zones, roundTripped.Zones);
        Assert.Equal(original.Resources, roundTripped.Resources);
        Assert.Equal(original.ChokePoints, roundTripped.ChokePoints);
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
    }
}