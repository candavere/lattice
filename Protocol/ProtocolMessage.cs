using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// Base of the five message types. Carries the closed <c>type</c> discriminator
/// as a constructor-set, read-only property (spec §4).
/// </summary>
/// <remarks>
/// The discriminator is get-only on purpose. It is not something a peer
/// chooses: a line whose <c>type</c> does not equal the literal for the type
/// being parsed is refused with <see cref="ProtocolReason.SchemaViolation"/>
/// (spec §8.3) before deserialization, so letting the serializer populate it
/// would only create a second, weaker path to the same check. Because the
/// property is declared only here, it serializes exactly once and first, which
/// is the field order the §4.1 examples use.
/// <para>
/// Records are used throughout so that value equality covers every field — and
/// therefore also the discriminator — which is what makes the round-trip
/// identity <c>parse(write(x)) == x</c> checkable at all.
/// </para>
/// </remarks>
public abstract record ProtocolMessage
{
    /// <summary>Initializes the discriminator. Only a concrete message type may call this.</summary>
    protected ProtocolMessage(string type) => Type = type;

    /// <summary>The closed <c>type</c> discriminator. Exactly one of <see cref="ProtocolMessageTypes"/>.</summary>
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string Type { get; }

    /// <summary>
    /// The <c>type</c> literal that a line must carry to be deserialized as
    /// <typeparamref name="TMessage"/>. Kept as an explicit table rather than
    /// derived by instantiating the type, so that the parser can reject a
    /// mismatched discriminator before it does any other work.
    /// </summary>
    internal static string LiteralOf<TMessage>() where TMessage : ProtocolMessage => LiteralOf(typeof(TMessage));

    /// <inheritdoc cref="LiteralOf{TMessage}()"/>
    internal static string LiteralOf(Type messageType) => messageType.Name switch
    {
        nameof(HelloMessage) => ProtocolMessageTypes.Hello,
        nameof(HelloAckMessage) => ProtocolMessageTypes.HelloAck,
        nameof(ObservationMessage) => ProtocolMessageTypes.Observation,
        nameof(ActionMessage) => ProtocolMessageTypes.Action,
        nameof(ErrorMessage) => ProtocolMessageTypes.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Not a protocol message type."),
    };
}
