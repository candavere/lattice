namespace Lattice.Protocol;

/// <summary>
/// The five members of the closed message-type set of spec §4.
/// </summary>
public static class ProtocolMessageTypes
{
    /// <summary>Opens the session; declares protocol, scenario, seed, slot, budget, and limits.</summary>
    public const string Hello = "hello";

    /// <summary>Confirms the exact protocol version.</summary>
    public const string HelloAck = "hello_ack";

    /// <summary>Everything the agent may see at one tick.</summary>
    public const string Observation = "observation";

    /// <summary>The agent's request for that tick.</summary>
    public const string Action = "action";

    /// <summary>Optional; announces a named failure before termination.</summary>
    public const string Error = "error";

    /// <summary>
    /// The closed set, in spec §4 catalogue order. The parser's discriminator
    /// dispatch is built from this list, so a <c>type</c> outside it is a
    /// <see cref="ProtocolReason.SchemaViolation"/> (spec §8.3) rather than a
    /// parse failure.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Hello, HelloAck, Observation, Action, Error];

    /// <summary>True when <paramref name="type"/> is one of the five.</summary>
    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);
}
