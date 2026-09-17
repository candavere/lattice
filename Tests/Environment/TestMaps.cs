using Lattice.Environment;

namespace Lattice.Tests.Environment;

internal static class TestMaps
{
    public static MapGraph TriangleWithResources() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(0, 10)),
            new Zone(2, new GridPoint(10, 10)),
        },
        new[]
        {
            new ResourceNode(0, 1, new GridPoint(1, 9)),
            new ResourceNode(1, 1, new GridPoint(1, 11)),
            new ResourceNode(2, 2, new GridPoint(9, 11)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
            new ChokePoint(2, 0, 2),
        });
}