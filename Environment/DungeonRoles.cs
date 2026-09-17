namespace Lattice.Environment;

/// <summary>
/// Canonical semantic labels for the dungeon infiltration demonstration layer.
/// Each constant is pure metadata that may be attached to the matching graph
/// record (<see cref="Zone.Role"/>, <see cref="ResourceNode.Role"/>,
/// <see cref="ChokePoint.Role"/>), carrying tactical meaning the step contract
/// never reads. Kept as a single vocabulary so the builder, agents, renderers,
/// and tests agree on spelling with no string drift.
/// </summary>
public static class DungeonRoles
{
    /// <summary>The dungeon's main gate and the extraction room; where the infiltrator must return to exfiltrate.</summary>
    public const string EntryHall = "EntryHall";

    /// <summary>A neutral passage room linking the wings of the dungeon.</summary>
    public const string Corridor = "Corridor";

    /// <summary>A side chamber that can hold a sentry patrolling its beat.</summary>
    public const string Armory = "Armory";

    /// <summary>A narrow doorway room whose only egresses are bounded gates.</summary>
    public const string ChokeDoorway = "ChokeDoorway";

    /// <summary>The objective room holding the treasure chests the infiltrator must claim.</summary>
    public const string TreasureVault = "TreasureVault";

    /// <summary>The guard's own room; the null point of the patrol loop.</summary>
    public const string SentryPost = "SentryPost";

    /// <summary>A collectable framed as dungeon loot; the infiltrator's primary objective haul.</summary>
    public const string TreasureChest = "TreasureChest";

    /// <summary>A collectable framing mission extraction; claimed only after the haul when the infiltrator returns to the entry hall.</summary>
    public const string ObjectiveExtraction = "ObjectiveExtraction";

    /// <summary>A capacity-1 gate edge; exactly one agent may hold it at a time (authored with <c>MaxOccupancy = 1</c>).</summary>
    public const string Portcullis = "Portcullis";

    /// <summary>A capacity-1 doorway edge; same contention semantics as a portcullis, used for interior cortile gateways.</summary>
    public const string AirLockDoorway = "AirLockDoorway";
}