using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Procedurally generates the bottleneck evaluation topology family from a
/// trial seed. The generator varies three seed-driven dimensions — the number
/// of corridor zones per spawn arm (choke placement / zone layout), the zone
/// spacing and vault depth (transit geometry), and the vault resource count
/// and distribution across one or two capacity-gated vault zones — while
/// preserving the single invariant that makes contention non-zero: the two
/// spawn arms are geometric mirror images, so both agents always reach the
/// shared antechamber on the same tick and request the same capacity-1 vault
/// gate in the same tick (a transit denial).
///
/// Every choke is authored with <c>MaxOccupancy = 1</c>, so the map is a
/// strict chain of single-lane gates between distinct subgraphs: the left
/// spawn subgraph, the right spawn subgraph, and the vault subgraph(s). All
/// resources sit behind capacity-1 chokes, so any claim requires crossing a
/// contested single lane. Generation is a pure function of the seed — the
/// same seed yields a byte-identical graph every call, and the retry-free
/// construction always returns a connected, valid map.
/// </summary>
public static class ProceduralBottleneckGenerator
{
    private const int SpawnLeft = 0;
    private const int SpawnRight = 1;
    private const int Antechamber = 2;

    /// <summary>
    /// Builds a deterministic bottleneck topology for <paramref name="seed"/>.
    /// Zone ids are assigned contiguously 0..N-1; the first three are the two
    /// spawns and the shared antechamber, then the left/right corridor zones,
    /// then the vault zone(s). Choke ids are contiguous in construction order.
    /// </summary>
    public static MapGraph Generate(ulong seed)
    {
        var rng = new Rng(seed);

        var armLength = rng.Next(0, 3);       // corridor zones per spawn arm (0..2)
        var spacing = rng.Next(3, 7);         // Manhattan hop distance between adjacent zones
        var vaultDepth = rng.Next(3, 9);      // shared-gate -> vault transit distance
        var deepVault = rng.Next(0, 2) == 1;  // whether a second, deeper vault zone exists
        var resourceCount = rng.Next(4, 9);   // total resources in the vault subgraph (4..8)

        var zones = new List<Zone>
        {
            new(SpawnLeft, new GridPoint(-(armLength + 1) * spacing, 0), Role: BottleneckScenario.SpawnLeftRole),
            new(SpawnRight, new GridPoint((armLength + 1) * spacing, 0), Role: BottleneckScenario.SpawnRightRole),
            new(Antechamber, new GridPoint(0, 0), Role: BottleneckScenario.AntechamberRole),
        };

        var leftArm = new List<int> { SpawnLeft };
        for (var i = 1; i <= armLength; i++)
        {
            var id = zones.Count;
            zones.Add(new Zone(id, new GridPoint(-i * spacing, 0)));
            leftArm.Add(id);
        }

        var rightArm = new List<int> { SpawnRight };
        for (var i = 1; i <= armLength; i++)
        {
            var id = zones.Count;
            zones.Add(new Zone(id, new GridPoint(i * spacing, 0)));
            rightArm.Add(id);
        }

        var vault0 = zones.Count;
        zones.Add(new Zone(vault0, new GridPoint(0, -vaultDepth), Role: BottleneckScenario.VaultRole));
        int? vault1 = null;
        if (deepVault)
        {
            vault1 = zones.Count;
            zones.Add(new Zone(vault1.Value, new GridPoint(0, -vaultDepth - 3), Role: BottleneckScenario.VaultRole));
        }

        var chokes = new List<ChokePoint>();
        var chokeId = 0;
        AddArm(chokes, ref chokeId, leftArm, Antechamber);
        AddArm(chokes, ref chokeId, rightArm, Antechamber);
        chokes.Add(Gate(chokeId++, Antechamber, vault0));
        if (vault1 is { } deep)
        {
            chokes.Add(Gate(chokeId++, vault0, deep));
        }

        var resources = new List<ResourceNode>();
        for (var i = 0; i < resourceCount; i++)
        {
            var target = deepVault && i % 3 == 2 ? vault1!.Value : vault0;
            var baseY = target == vault0 ? -vaultDepth : -vaultDepth - 3;
            resources.Add(new ResourceNode(
                i,
                target,
                new GridPoint(-1 + (i % 2), baseY - (i / 2)),
                Role: null));
        }

        return new MapGraph(zones.ToArray(), resources.ToArray(), chokes.ToArray());
    }

    /// <summary>
    /// Wires a spawn-arm path (spawn or corridor zone chain) into the shared
    /// antechamber as a chain of capacity-1 single-lane gates.
    /// </summary>
    private static void AddArm(List<ChokePoint> chokes, ref int chokeId, List<int> arm, int antechamber)
    {
        for (var i = 0; i + 1 < arm.Count; i++)
        {
            chokes.Add(Gate(chokeId++, arm[i], arm[i + 1]));
        }

        chokes.Add(Gate(chokeId++, arm[^1], antechamber));
    }

    /// <summary>A capacity-1 single-lane gate between two zones.</summary>
    private static ChokePoint Gate(int id, int from, int to) =>
        new(id, from, to, MaxOccupancy: 1, Role: BottleneckScenario.PortcullisRole);
}