using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// Reads and writes <see cref="ProtocolReason"/> as its exact spec §8 wire
/// string.
/// </summary>
/// <remarks>
/// A bespoke converter rather than <c>JsonStringEnumConverter</c> for two
/// reasons. First, exactness: the framework's converter matches member names
/// case-insensitively, so <c>"TIMEOUT_STEP"</c> would silently parse — and
/// spec §8.3 refuses exactly that class of tolerance. Second, closure: an
/// unrecognised string raises <see cref="JsonException"/>, which the parser maps
/// to <see cref="ProtocolReason.SchemaViolation"/>, so a protocol-2 agent's
/// reason is refused rather than guessed at.
/// </remarks>
internal sealed class ProtocolReasonConverter : JsonConverter<ProtocolReason>
{
    public override ProtocolReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"reason must be a string, not {reader.TokenType}.");
        }

        var wire = reader.GetString();
        if (ProtocolReasons.TryParse(wire, out var reason))
        {
            return reason;
        }

        throw new JsonException($"'{wire}' is not a protocol v1 reason code.");
    }

    public override void Write(Utf8JsonWriter writer, ProtocolReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

/// <summary>
/// Reads and writes <see cref="ProtocolActionKind"/> as its exact
/// <c>ActionKind</c> member name (spec §6.1).
/// </summary>
/// <remarks>
/// The exactness is the requirement: §6.1 says <c>"move"</c>, <c>"MOVE"</c>,
/// <c>"Idle"</c>, and the integer <c>1</c> are "all schema violations", which is
/// what keeps the <c>ActionKind</c> unknown-value branch in
/// <c>ActionSpace.Validate</c> unreachable from the wire. A non-string token is
/// rejected here rather than coerced, so <c>1</c> cannot slip through as
/// <c>Move</c>.
/// </remarks>
internal sealed class ProtocolActionKindConverter : JsonConverter<ProtocolActionKind>
{
    public override ProtocolActionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"kind must be a string, not {reader.TokenType}.");
        }

        return reader.GetString() switch
        {
            "Wait" => ProtocolActionKind.Wait,
            "Move" => ProtocolActionKind.Move,
            "Collect" => ProtocolActionKind.Collect,
            var other => throw new JsonException($"'{other}' is not an ActionKind member name."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ProtocolActionKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
