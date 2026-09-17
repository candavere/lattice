namespace Lattice.Environment;

/// <summary>
/// Global occupancy sentinels for the Phase 8 capacity model. Every zone and
/// choke point carries a <c>MaxOccupancy</c>; <see cref="Unlimited"/> means no
/// bound at all (the pre-Phase-8 behavior), and 0 models an impassable
/// entry. Kept as plain integer configuration so capacity math is exact.
/// </summary>
public static class MapLimits
{
    /// <summary>The default capacity on zones and choke points: unbounded.</summary>
    public const int Unlimited = int.MaxValue;
}

/// <summary>
/// A 2D integer coordinate on the map plane. Immutable and value-comparable,
/// so graph nodes can hold one without imposing grid structure: the map is a
/// graph (see docs/adr-001.md), and positions are an embedding for the spatial
/// reasoning agents need (e.g. "nearest resource"), not a storage lattice.
/// This is also the Phase 8 measure of edge length: Manhattan distance here
/// feeds <see cref="Simulation.TransitTicks"/> with pure integer arithmetic.
/// </summary>
public sealed record GridPoint(int X, int Y);

/// <summary>
/// A contiguous region of play and one node of <see cref="MapGraph"/>. Identity
/// is the explicit <see cref="Id"/> so generator, environment, and trajectories
/// can reference zones deterministically without object identity or Guid work.
/// <see cref="MaxOccupancy"/> bounds how many agents may be triaged into the
/// zone in one tick (<see cref="MapLimits.Unlimited"/> by default; 0 = no
/// entry allowed).
/// </summary>
public sealed record Zone(int Id, GridPoint Position, int MaxOccupancy = MapLimits.Unlimited);

/// <summary>
/// A collectable that belongs to exactly one zone (<see cref="ZoneId"/>) and
/// sits at an absolute position on the map plane. It deliberately carries no
/// type/tier fields yet — nothing in the Phase 1 constraints needs them, and
/// adding speculative state now would be burned attention for the generator.
/// </summary>
public sealed record ResourceNode(int Id, int ZoneId, GridPoint Position);

/// <summary>
/// A direct, traversable connection between two zones — the undirected edge of
/// <see cref="MapGraph"/>. Choke points are where movement concentrates, so
/// they are the natural subject of connectivity and minimum-degree checks.
/// <see cref="MaxOccupancy"/> bounds how many agents can be transiting the
/// choke at once (e.g. a narrow choke allows only one; 0 = impassable).
/// </summary>
public sealed record ChokePoint(int Id, int FromZoneId, int ToZoneId, int MaxOccupancy = MapLimits.Unlimited);

/// <summary>
/// The whole generated map: zone nodes, resource nodes, and choke-point edges,
/// all plain immutable data. It holds no checker or generation logic so it can
/// be serialized to JSON and compared value-for-value in determinism tests.
/// </summary>
public sealed record MapGraph(
    Zone[] Zones,
    ResourceNode[] Resources,
    ChokePoint[] ChokePoints);