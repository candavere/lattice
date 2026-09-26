using System.Text.Json;

namespace Lattice.Protocol;

/// <summary>
/// The strict reader of spec §8.3: it turns one line of bytes into one of the
/// five message records, or refuses it with exactly one reason from the closed
/// §8 set.
/// </summary>
/// <remarks>
/// The order of the checks <em>is</em> the contract. Spec §8.4 says exactly one
/// reason is reported per failed match and that the first detected wins, so the
/// stages below run in the §8.4 precedence order and each one only runs if the
/// previous passed:
/// <list type="number">
/// <item><b>Framing</b> — delegated wholesale to
/// <see cref="ProtocolFraming.Validate"/>: <c>line_too_long</c>,
/// <c>malformed_json</c>, <c>depth_exceeded</c>.</item>
/// <item><b>Protocol version</b> — the <c>protocol</c> member of a <c>hello</c>
/// or <c>hello_ack</c> is compared before the body is deserialized, so a string,
/// a float, a different integer, and a missing field are all
/// <c>protocol_mismatch</c> rather than a generic schema error (spec §2).</item>
/// <item><b>Discriminator</b> — a root that is not an object, a missing
/// <c>type</c>, a <c>type</c> outside the closed five, or a <c>type</c> that does
/// not match the type being parsed is <c>schema_violation</c> (spec §8.3).</item>
/// <item><b>Unknown members</b> — any undeclared member anywhere in the document
/// is <c>unknown_field</c> (spec §8.3).</item>
/// <item><b>Schema</b> — deserialization under
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> and
/// <c>required</c>-member enforcement; a missing required field, a wrong JSON
/// type, or a bad enum spelling is <c>schema_violation</c>.</item>
/// <item><b>Conditional presence</b> — §6.1's rule for <c>zone_id</c> and
/// <c>resource_id</c>, also <c>schema_violation</c>.</item>
/// <item><b>Step echo</b> — <c>step_mismatch</c>, when the caller supplied the
/// step being answered (spec §6.2).</item>
/// </list>
/// <para>
/// <c>illegal_action</c> is not decided here. It is a claim about whether an
/// action is inside the action space <em>for this map</em>, so it belongs to
/// <see cref="ProtocolActionGuard"/>, which the caller applies once the map is in
/// hand. The parser reports what the line <em>said</em>; the guard reports what
/// the map <em>allows</em>.
/// </para>
/// </remarks>
public static class ProtocolParser
{
    /// <summary>
    /// Parses any of the five message types, dispatching on the <c>type</c>
    /// discriminator. Context-free: it applies the framing, discriminator,
    /// unknown-member, and schema checks, but cannot apply
    /// <c>protocol_mismatch</c> or <c>step_mismatch</c>, because neither the
    /// expected version nor the expected step is available. Use the
    /// type-specific overloads on the handshake and step paths.
    /// </summary>
    public static ProtocolMessage Parse(ReadOnlySpan<byte> line)
    {
        var document = ReadDocument(line);
        return ReadDiscriminator(document.RootElement) switch
        {
            // The dispatcher does know the protocol version — it is this
            // library's own — so it applies the same check the typed handshake
            // overloads do, with no caller-supplied override.
            ProtocolMessageTypes.Hello => Read<HelloMessage>(document, ProtocolLimits.Version, null),
            ProtocolMessageTypes.HelloAck => Read<HelloAckMessage>(document, ProtocolLimits.Version, null),
            ProtocolMessageTypes.Observation => Read<ObservationMessage>(document, null, null),
            ProtocolMessageTypes.Action => Read<ActionMessage>(document, null, null),
            ProtocolMessageTypes.Error => Read<ErrorMessage>(document, null, null),
            var other => throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"'{other}' is not one of the five message types."),
        };
    }

    /// <summary>Parses a <c>hello</c>, checking <c>protocol</c> against <see cref="ProtocolLimits.Version"/>.</summary>
    public static HelloMessage ParseHello(ReadOnlySpan<byte> line) => ParseHello(line, ProtocolLimits.Version);

    /// <inheritdoc cref="ParseHello(ReadOnlySpan{byte})"/>
    public static HelloMessage ParseHello(ReadOnlySpan<byte> line, int expectedProtocol) =>
        Read<HelloMessage>(ReadDocument(line), expectedProtocol, null);

    /// <summary>Parses a <c>hello_ack</c>, checking <c>protocol</c> against <see cref="ProtocolLimits.Version"/>.</summary>
    public static HelloAckMessage ParseHelloAck(ReadOnlySpan<byte> line) =>
        ParseHelloAck(line, ProtocolLimits.Version);

    /// <inheritdoc cref="ParseHelloAck(ReadOnlySpan{byte})"/>
    public static HelloAckMessage ParseHelloAck(ReadOnlySpan<byte> line, int expectedProtocol) =>
        Read<HelloAckMessage>(ReadDocument(line), expectedProtocol, null);

    /// <summary>Parses an <c>observation</c>. Carries no version or step context.</summary>
    public static ObservationMessage ParseObservation(ReadOnlySpan<byte> line) =>
        Read<ObservationMessage>(ReadDocument(line), null, null);

    /// <summary>Parses an <c>error</c>. Carries no version or step context.</summary>
    public static ErrorMessage ParseError(ReadOnlySpan<byte> line) =>
        Read<ErrorMessage>(ReadDocument(line), null, null);

    /// <summary>
    /// Parses an <c>action</c> and enforces the §6.2 step echo against
    /// <paramref name="expectedStep"/>, raising <c>step_mismatch</c> when the
    /// action answers a different step.
    /// </summary>
    public static ActionMessage ParseAction(ReadOnlySpan<byte> line, int expectedStep) =>
        Read<ActionMessage>(ReadDocument(line), null, expectedStep);

    private static TMessage Read<TMessage>(
        JsonDocument document,
        int? expectedProtocol,
        int? expectedStep)
        where TMessage : ProtocolMessage
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"a message must be a JSON object, not {root.ValueKind}.");
        }

        if (expectedProtocol is { } version)
        {
            RequireProtocolVersion(root, version);
        }

        RequireDiscriminator(root, typeof(TMessage));
        RequireKnownMembers(root, typeof(TMessage));

        TMessage message;
        try
        {
            message = root.Deserialize<TMessage>(ProtocolJson.Options)
                ?? throw new ProtocolViolation(
                    ProtocolReason.SchemaViolation,
                    $"the line deserialized to no {typeof(TMessage).Name} value.");
        }
        catch (JsonException e)
        {
            // Everything still unreported at this point is a schema failure: a
            // missing required member, a wrong JSON type, or a value outside a
            // closed enum. Undeclared members were already resolved above, so
            // this catch cannot swallow an unknown_field.
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"the line is not a conforming {ProtocolMessage.LiteralOf(typeof(TMessage))} message: {e.Message}");
        }

        if (message is ActionMessage action)
        {
            ProtocolActionGuard.RequireConditionalFields(action);
        }

        if (expectedStep is { } step && message is ActionMessage echo && echo.Step != step)
        {
            throw new ProtocolViolation(
                ProtocolReason.StepMismatch,
                $"expected step {step}, received step {echo.Step}");
        }

        return message;
    }

    /// <summary>
    /// Applies framing, then parses the document. Split out because both the
    /// dispatcher and every typed overload need it, and because the
    /// <see cref="JsonDocument"/> is what the discriminator and unknown-member
    /// checks read from.
    /// </summary>
    private static JsonDocument ReadDocument(ReadOnlySpan<byte> line)
    {
        ProtocolFraming.Validate(line);

        try
        {
            return JsonDocument.Parse(line.ToArray(), ProtocolJson.DocumentOptions);
        }
        catch (JsonException e)
        {
            throw new ProtocolViolation(
                ProtocolReason.MalformedJson,
                $"the line is not a single well-formed JSON value: {e.Message}");
        }
    }

    /// <summary>
    /// Spec §2: negotiation is an exact match, and <em>any</em> other value — a
    /// different integer, a string, a float, or a missing field — is
    /// <c>protocol_mismatch</c>. Checked against the raw JSON before
    /// deserialization so that a string version is reported as a version problem
    /// rather than as a type problem, which is what §8.4's precedence requires.
    /// </summary>
    private static void RequireProtocolVersion(JsonElement root, int expectedProtocol)
    {
        if (!root.TryGetProperty("protocol", out var protocol))
        {
            throw new ProtocolViolation(
                ProtocolReason.ProtocolMismatch,
                "the handshake carries no 'protocol' field.");
        }

        if (protocol.ValueKind != JsonValueKind.Number
            || !protocol.TryGetInt32(out var value)
            || value != expectedProtocol)
        {
            throw new ProtocolViolation(
                ProtocolReason.ProtocolMismatch,
                $"expected the integer protocol {expectedProtocol}, received {Describe(protocol)}.");
        }
    }

    private static void RequireDiscriminator(JsonElement root, Type messageType)
    {
        var expected = ProtocolMessage.LiteralOf(messageType);
        if (!root.TryGetProperty("type", out var discriminator))
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"the line carries no 'type' field; a {expected} requires it.");
        }

        if (discriminator.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"'type' must be a string, not {discriminator.ValueKind}.");
        }

        var found = discriminator.GetString();
        if (found == expected)
        {
            return;
        }

        throw new ProtocolViolation(
            ProtocolReason.SchemaViolation,
            ProtocolMessageTypes.IsKnown(found)
                ? $"expected a {expected} line, received {found}."
                : $"'{found}' is not one of the five message types.");
    }

    private static void RequireKnownMembers(JsonElement root, Type messageType)
    {
        var unknown = ProtocolSchemaCheck.FindUnknownMember(root, ProtocolSchemaNode.For(messageType));
        if (unknown is not null)
        {
            throw new ProtocolViolation(
                ProtocolReason.UnknownField,
                $"'{unknown}' is not a member of the {ProtocolMessage.LiteralOf(messageType)} schema.");
        }
    }

    /// <summary>
    /// The <c>type</c> of a line, already known to be one of the five by
    /// <see cref="ReadDiscriminator"/>'s contract with the caller.
    /// </summary>
    private static string ReadDiscriminator(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"a message must be a JSON object, not {root.ValueKind}.");
        }

        if (!root.TryGetProperty("type", out var discriminator) || discriminator.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                "the line carries no string 'type' field.");
        }

        return discriminator.GetString()!;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string \"{value.GetString()}\"",
        JsonValueKind.Null => "null",
        _ => $"{value.GetRawText()} ({value.ValueKind})",
    };
}
