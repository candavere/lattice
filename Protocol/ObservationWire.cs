using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// <c>observation</c> — Lattice to agent; a flattened projection of
/// <c>Lattice.Environment.Observation</c> (<c>Environment/StepContracts.cs:62-67</c>).
/// </summary>
/// <remarks>
/// The projection is mechanical and specified field by field in spec §5.3: every
/// record member becomes a wire field, <c>PascalCase</c> becomes
/// <c>snake_case</c>, nested records become nested objects, and a null
/// <c>string?</c> is omitted rather than emitted as <c>null</c>. The sub-record
/// names below deliberately mirror the source records so the provenance is
/// readable; the types themselves are independent, which is the point of ADR
/// decision 3 — the wire format is a specified contract, not a by-product of the
/// internal type layout.
/// </remarks>
public sealed record ObservationMessage : ProtocolMessage
{
    /// <summary>Initializes the message with its <c>observation</c> discriminator.</summary>
    public ObservationMessage()
        : base(ProtocolMessageTypes.Observation)
    {
    }

    /// <summary>
    /// The step this observation decides, 0-based (spec §5.1). The value an
    /// action MUST echo; distinct from <see cref="StepNumber"/>, and the
    /// trajectory's 1-based step <c>n</c> came from wire step <c>n - 1</c>
    /// (spec §5.4).
    /// </summary>
    [JsonPropertyName("step")]
    [JsonPropertyOrder(10)]
    public required int Step { get; init; }

    /// <summary>The observing agent's own id; selects its entry in <see cref="AgentStates"/>.</summary>
    [JsonPropertyName("agent_id")]
    [JsonPropertyOrder(20)]
    public required int AgentId { get; init; }

    /// <summary>The whole map, re-sent in full on every observation (spec §5.4).</summary>
    [JsonPropertyName("map")]
    [JsonPropertyOrder(30)]
    public required MapGraphWire Map { get; init; }

    /// <summary>
    /// <b>Every</b> agent in the episode, not just the observer — full
    /// observability, the same body for every slot (spec §5.4).
    /// </summary>
    [JsonPropertyName("agent_states")]
    [JsonPropertyOrder(40)]
    public required AgentStateWire[] AgentStates { get; init; }

    /// <summary>
    /// The list of claimed resource ids, not a per-resource mask. Always present;
    /// an empty episode state is <c>[]</c>, never absent (spec §5.4).
    /// </summary>
    [JsonPropertyName("claims")]
    [JsonPropertyOrder(50)]
    public required int[] Claims { get; init; }

    /// <summary>
    /// <c>SimulationState.StepCount</c>, which advances in lockstep with the
    /// runner's loop index, so this always equals <see cref="Step"/> (spec §5.4).
    /// </summary>
    [JsonPropertyName("step_number")]
    [JsonPropertyOrder(60)]
    public required int StepNumber { get; init; }

    /// <summary>
    /// Structural equality. The compiler's generated record equality compares
    /// array members by reference, which would make two observations carrying
    /// identical state unequal — and would quietly break the round-trip identity
    /// <c>parse(write(x)) == x</c>, since a re-read message never shares array
    /// instances with the original. These are plain data types whose whole purpose
    /// is to be compared, so the comparison is by value throughout.
    /// </summary>
    public bool Equals(ObservationMessage? other) =>
        other is not null
        && Type == other.Type
        && Step == other.Step
        && AgentId == other.AgentId
        && Map == other.Map
        && AgentStates.AsSpan().SequenceEqual(other.AgentStates)
        && Claims.AsSpan().SequenceEqual(other.Claims)
        && StepNumber == other.StepNumber;

    /// <inheritdoc cref="Equals(ObservationMessage?)"/>
    public override int GetHashCode() => HashCode.Combine(Type, Step, AgentId, Map, StepNumber);
}

/// <summary>
/// <c>observation.map</c> — projection of <c>MapGraph</c>
/// (<c>Environment/MapData.cs:85-88</c>).
/// </summary>
public sealed record MapGraphWire
{
    /// <summary>All zones.</summary>
    [JsonPropertyName("zones")]
    [JsonPropertyOrder(10)]
    public required ZoneWire[] Zones { get; init; }

    /// <summary>All resource nodes.</summary>
    [JsonPropertyName("resources")]
    [JsonPropertyOrder(20)]
    public required ResourceNodeWire[] Resources { get; init; }

    /// <summary>All choke points.</summary>
    [JsonPropertyName("choke_points")]
    [JsonPropertyOrder(30)]
    public required ChokePointWire[] ChokePoints { get; init; }

    /// <summary>
    /// Structural equality; see <see cref="ObservationMessage.Equals"/>.
    /// </summary>
    public bool Equals(MapGraphWire? other) =>
        other is not null
        && Zones.AsSpan().SequenceEqual(other.Zones)
        && Resources.AsSpan().SequenceEqual(other.Resources)
        && ChokePoints.AsSpan().SequenceEqual(other.ChokePoints);

    /// <inheritdoc cref="Equals(MapGraphWire?)"/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var zone in Zones)
        {
            hash.Add(zone);
        }

        foreach (var resource in Resources)
        {
            hash.Add(resource);
        }

        foreach (var choke in ChokePoints)
        {
            hash.Add(choke);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// <c>observation.map.zones[]</c> — projection of <c>Zone</c>
/// (<c>Environment/MapData.cs:39-43</c>).
/// </summary>
public sealed record ZoneWire
{
    /// <summary>Zone id; the addressable unit for a <c>Move</c> action.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(10)]
    public required int Id { get; init; }

    /// <summary>Grid coordinates of the zone.</summary>
    [JsonPropertyName("position")]
    [JsonPropertyOrder(20)]
    public required GridPointWire Position { get; init; }

    /// <summary>
    /// Explicit capacity, never omitted. The generator default is
    /// <c>MapLimits.Unlimited</c> = <see cref="int.MaxValue"/>; <c>0</c> means no
    /// entry is allowed (spec §5.3).
    /// </summary>
    [JsonPropertyName("max_occupancy")]
    [JsonPropertyOrder(30)]
    public required int MaxOccupancy { get; init; }

    /// <summary>
    /// The scenario-specific role. Omitted when absent — never emitted as
    /// <c>null</c> (spec §5.2, rule 5).
    /// </summary>
    [JsonPropertyName("role")]
    [JsonPropertyOrder(40)]
    public string? Role { get; init; }
}

/// <summary>
/// <c>observation.map.resources[]</c> — projection of <c>ResourceNode</c>
/// (<c>Environment/MapData.cs:55-59</c>).
/// </summary>
public sealed record ResourceNodeWire
{
    /// <summary>Resource id; the addressable unit for a <c>Collect</c> action.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(10)]
    public required int Id { get; init; }

    /// <summary>The zone this resource sits in. Required, never omitted (spec §5.3).</summary>
    [JsonPropertyName("zone_id")]
    [JsonPropertyOrder(20)]
    public required int ZoneId { get; init; }

    /// <summary>Grid coordinates of the resource.</summary>
    [JsonPropertyName("position")]
    [JsonPropertyOrder(30)]
    public required GridPointWire Position { get; init; }

    /// <summary>Scenario-specific role; omitted when absent (spec §5.2, rule 5).</summary>
    [JsonPropertyName("role")]
    [JsonPropertyOrder(40)]
    public string? Role { get; init; }
}

/// <summary>
/// <c>observation.map.choke_points[]</c> — projection of <c>ChokePoint</c>
/// (<c>Environment/MapData.cs:73-78</c>).
/// </summary>
public sealed record ChokePointWire
{
    /// <summary>Choke-point id.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(10)]
    public required int Id { get; init; }

    /// <summary>The zone the edge leaves from.</summary>
    [JsonPropertyName("from_zone_id")]
    [JsonPropertyOrder(20)]
    public required int FromZoneId { get; init; }

    /// <summary>The zone the edge enters.</summary>
    [JsonPropertyName("to_zone_id")]
    [JsonPropertyOrder(30)]
    public required int ToZoneId { get; init; }

    /// <summary>How many agents may cross the edge at once.</summary>
    [JsonPropertyName("max_occupancy")]
    [JsonPropertyOrder(40)]
    public required int MaxOccupancy { get; init; }

    /// <summary>Scenario-specific role; omitted when absent (spec §5.2, rule 5).</summary>
    [JsonPropertyName("role")]
    [JsonPropertyOrder(50)]
    public string? Role { get; init; }
}

/// <summary>
/// <c>position</c> — projection of <c>GridPoint</c>
/// (<c>Environment/MapData.cs:25</c>).
/// </summary>
public sealed record GridPointWire
{
    /// <summary>Row/column component.</summary>
    [JsonPropertyName("x")]
    [JsonPropertyOrder(10)]
    public required int X { get; init; }

    /// <summary>Perpendicular component.</summary>
    [JsonPropertyName("y")]
    [JsonPropertyOrder(20)]
    public required int Y { get; init; }
}

/// <summary>
/// <c>observation.agent_states[]</c> — projection of <c>AgentState</c>
/// (<c>Environment/StepContracts.cs:50</c>).
/// </summary>
public sealed record AgentStateWire
{
    /// <summary>The agent this state describes.</summary>
    [JsonPropertyName("agent_id")]
    [JsonPropertyOrder(10)]
    public required int AgentId { get; init; }

    /// <summary>
    /// The agent's current zone. While <see cref="Transit"/> is present this
    /// remains the <b>departure</b> node (spec §5.4).
    /// </summary>
    [JsonPropertyName("zone_id")]
    [JsonPropertyOrder(20)]
    public required int ZoneId { get; init; }

    /// <summary>The agent's running score.</summary>
    [JsonPropertyName("score")]
    [JsonPropertyOrder(30)]
    public required int Score { get; init; }

    /// <summary>
    /// The edge crossing in progress, or absent when the agent is at a node and
    /// may act (spec §5.4).
    /// </summary>
    [JsonPropertyName("transit")]
    [JsonPropertyOrder(40)]
    public InTransitWire? Transit { get; init; }
}

/// <summary>
/// <c>observation.agent_states[].transit</c> — projection of <c>InTransit</c>
/// (<c>Environment/StepContracts.cs:41</c>).
/// </summary>
public sealed record InTransitWire
{
    /// <summary>The zone the agent left.</summary>
    [JsonPropertyName("from_zone_id")]
    [JsonPropertyOrder(10)]
    public required int FromZoneId { get; init; }

    /// <summary>The zone the agent is heading for.</summary>
    [JsonPropertyName("to_zone_id")]
    [JsonPropertyOrder(20)]
    public required int ToZoneId { get; init; }

    /// <summary>
    /// Ticks left on the crossing; never <c>0</c>. A value of <c>1</c> means
    /// "arrives at the end of this tick" (spec §5.4).
    /// </summary>
    [JsonPropertyName("remaining_ticks")]
    [JsonPropertyOrder(30)]
    public required int RemainingTicks { get; init; }
}
