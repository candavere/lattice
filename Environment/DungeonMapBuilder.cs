namespace Lattice.Environment;

/// <summary>
/// Builds the fixed, hand-authored dungeon behind the infiltration scenario.
/// Every room and gate is written once here so agents, renderers, tests, and
/// the CLI can rely on a stable layout. The layout is a ring of six rooms
/// (see <see cref="DungeonRoles"/> for the semantic vocabulary) connected by
/// capacity-1 choke points: the sentry patrols the outer loop
/// <c>SentryPost → EntryHall → Corridor → Armory → SentryPost</c> while the
/// infiltrator must slip through the interior gates
/// (<c>EntryHall → Corridor → ChokeDoorway → TreasureVault</c>) to claim the
/// chests and then return to the EntryHall extraction point.
///
/// The composition deliberately reuses the stock step contract untouched:
/// "portcullis" and "air-lock" semantics are just <see cref="ChokePoint.Role"/>
/// metadata on edges authored with <c>MaxOccupancy = 1</c>, and the objective
/// is ordinary <see cref="ResourceNode"/>s framed as treasure and extraction.
/// Nothing here mutates <see cref="Simulation"/>; the scenario is purely a
/// layer on top of the graph.
/// </summary>
public static class DungeonMapBuilder
{
    /// <summary>Zone id of the sentry's start room.</summary>
    public const int SentryPostZone = 0;

    /// <summary>Zone id of the infiltrator's spawn room and the extraction point.</summary>
    public const int EntryHallZone = 1;

    /// <summary>Zone id of the main corridor hub.</summary>
    public const int CorridorZone = 2;

    /// <summary>Zone id of the armory side chamber on the sentry's patrol loop.</summary>
    public const int ArmoryZone = 3;

    /// <summary>Zone id of the interior doorway room guarding the vault.</summary>
    public const int ChokeDoorwayZone = 4;

    /// <summary>Zone id of the objective room.</summary>
    public const int TreasureVaultZone = 5;

    /// <summary>
    /// The dungeon layout with a deterministic seed-dependent variation: the
    /// seed only scales the treasury (2–3 <see cref="DungeonRoles.TreasureChest"/>s
    /// on the vault), every other room and gate is fixed, so adjacent seeds
    /// produce the same tactical skeleton with a slightly richer or leaner
    /// objective. Returns a new, immutable <see cref="MapGraph"/> each call;
    /// the same seed yields a byte-identical graph every time.
    /// </summary>
    public static MapGraph Build(ulong seed)
    {
        var chestCount = 2 + new Rng(seed).Next(0, 2);
        return BuildCore(chestCount);
    }

    /// <summary>
    /// The canonical six-room dungeon with exactly two treasure chests, no
    /// seed dependence — the layout the documentation and tests refer to.
    /// </summary>
    public static MapGraph BuildCore() => BuildCore(chestCount: 2);

    private static MapGraph BuildCore(int chestCount)
    {
        var zones = new[]
        {
            new Zone(SentryPostZone, new GridPoint(1, 1), Role: DungeonRoles.SentryPost),
            new Zone(EntryHallZone, new GridPoint(4, 1), Role: DungeonRoles.EntryHall),
            new Zone(CorridorZone, new GridPoint(7, 1), Role: DungeonRoles.Corridor),
            new Zone(ArmoryZone, new GridPoint(7, 5), Role: DungeonRoles.Armory),
            new Zone(ChokeDoorwayZone, new GridPoint(4, 5), Role: DungeonRoles.ChokeDoorway),
            new Zone(TreasureVaultZone, new GridPoint(1, 5), Role: DungeonRoles.TreasureVault),
        };

        var chokes = new[]
        {
            new ChokePoint(0, SentryPostZone, EntryHallZone, MaxOccupancy: 1, Role: DungeonRoles.Portcullis),
            new ChokePoint(1, EntryHallZone, CorridorZone, MaxOccupancy: 1, Role: DungeonRoles.Portcullis),
            new ChokePoint(2, CorridorZone, ArmoryZone, MaxOccupancy: 1, Role: DungeonRoles.AirLockDoorway),
            new ChokePoint(3, ArmoryZone, ChokeDoorwayZone, MaxOccupancy: 1, Role: DungeonRoles.AirLockDoorway),
            new ChokePoint(4, ChokeDoorwayZone, TreasureVaultZone, MaxOccupancy: 1, Role: DungeonRoles.Portcullis),
            new ChokePoint(5, CorridorZone, ChokeDoorwayZone, MaxOccupancy: 1, Role: DungeonRoles.AirLockDoorway),
            new ChokePoint(6, SentryPostZone, ArmoryZone, MaxOccupancy: 1, Role: DungeonRoles.Portcullis),
        };

        var resources = new List<ResourceNode> { ExtractionResource() };
        for (var i = 0; i < chestCount; i++)
        {
            resources.Add(ChestResource(resources.Count));
        }

        return new MapGraph(zones, resources.ToArray(), chokes);
    }

    /// <summary>The extraction objective — the claim that completes the infiltration.</summary>
    public static ResourceNode ExtractionResource() => new(
        Id: 0,
        ZoneId: EntryHallZone,
        Position: new GridPoint(5, 1),
        Role: DungeonRoles.ObjectiveExtraction);

    /// <summary>A treasure chest in the vault, fanned out near the room so it renders distinctly.</summary>
    public static ResourceNode ChestResource(int id) => new(
        Id: id,
        ZoneId: TreasureVaultZone,
        Position: new GridPoint(2 + id % 2, 4 + id / 2),
        Role: DungeonRoles.TreasureChest);
}