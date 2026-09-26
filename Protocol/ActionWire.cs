using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// <c>action</c> — agent to Lattice; a 1:1 projection of
/// <c>Lattice.Environment.AgentAction</c>
/// (<c>Environment/StepContracts.cs:31</c>, spec §6.1).
/// </summary>
/// <remarks>
/// The record's shape is uniform across kinds while the wire is not:
/// <c>zone_id</c> and <c>resource_id</c> are nullable here and are present only
/// where §6.1's conditional rule makes them meaningful, because an omitted
/// conditional field maps to the record's <c>-1</c> default and the wire "carries
/// no field the record would ignore". A <c>Wait</c> therefore serializes with
/// neither field, a <c>Move</c> with <c>zone_id</c> only, and a <c>Collect</c>
/// with <c>resource_id</c> only. <see cref="ProtocolActionGuard"/> enforces that
/// rule in both directions, so a record that violates it cannot be built
/// unnoticed or written to the wire.
/// </remarks>
public sealed record ActionMessage : ProtocolMessage
{
    /// <summary>Initializes the message with its <c>action</c> discriminator.</summary>
    public ActionMessage()
        : base(ProtocolMessageTypes.Action)
    {
    }

    /// <summary>
    /// The step being answered. MUST equal the <c>step</c> of the
    /// <c>observation</c> it answers; Lattice MUST NOT renumber an action to make
    /// it fit (spec §6.2).
    /// </summary>
    [JsonPropertyName("step")]
    [JsonPropertyOrder(10)]
    public required int Step { get; init; }

    /// <summary>What the agent is doing this tick.</summary>
    [JsonPropertyName("kind")]
    [JsonPropertyOrder(20)]
    public required ProtocolActionKind Kind { get; init; }

    /// <summary>
    /// The destination zone for a <see cref="ProtocolActionKind.Move"/>, and
    /// forbidden for the other two kinds. <c>-1</c> is the record's sentinel, not
    /// an addressable zone, so sending it explicitly is a schema violation
    /// (spec §6.1).
    /// </summary>
    [JsonPropertyName("zone_id")]
    [JsonPropertyOrder(30)]
    public int? ZoneId { get; init; }

    /// <summary>
    /// The target resource for a <see cref="ProtocolActionKind.Collect"/>, and
    /// forbidden for the other two kinds. Subject to the same <c>-1</c> rule as
    /// <see cref="ZoneId"/>.
    /// </summary>
    [JsonPropertyName("resource_id")]
    [JsonPropertyOrder(40)]
    public int? ResourceId { get; init; }
}
