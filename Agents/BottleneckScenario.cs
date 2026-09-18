using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A fixed, contention-bearing evaluation topology: a vault holding every
/// resource, reached by both agents only through a single shared capacity-1
/// choke. Two spawns sit outside the choke room, each connected by its own
/// capacity-1 gate; both agents must then pass through the SAME
/// capacity-1 <c>(2, 3)</c> choke to enter the vault and claim resources. That
/// is the point of the scenario: it is a map built to make transit denials on
/// the shared single-lane gate and claim races in the vault occur, so a paired
/// evaluation on it yields non-zero contention saturation instead of the ~0 the
/// default generated maps produce.
/// </summary>
public static class BottleneckScenario
{
    private const int VaultZone = 3;

    /// <summary>
    /// The bottleneck map itself. Deliberately hand-authored and seed-fixed
    /// (like the dungeon builder) so every host and revision evaluates the same
    /// topology; the run seed still drives any agent RNG and mirrored seating.
    /// </summary>
    public static MapGraph Build() => new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(12, 12)),
            new Zone(2, new GridPoint(0, 12)),
            new Zone(VaultZone, new GridPoint(0, 24)),
        },
        new[]
        {
            new ResourceNode(0, VaultZone, new GridPoint(0, 22)),
            new ResourceNode(1, VaultZone, new GridPoint(0, 23)),
            new ResourceNode(2, VaultZone, new GridPoint(0, 24)),
            new ResourceNode(3, VaultZone, new GridPoint(1, 24)),
        },
        new[]
        {
            new ChokePoint(0, 0, 2, MaxOccupancy: 1, Role: "Portcullis"),
            new ChokePoint(1, 1, 2, MaxOccupancy: 1, Role: "Portcullis"),
            new ChokePoint(2, 2, VaultZone, MaxOccupancy: 1, Role: "Portcullis"),
        });

    /// <summary>
    /// An <see cref="EvaluationSpec"/>-compatible map factory that ignores the
    /// seed and returns the fixed bottleneck topology.
    /// </summary>
    public static MapGraph ForSeed(ulong seed) => Build();
}