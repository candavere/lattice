namespace Lattice.Protocol;

/// <summary>
/// The single typed rejection the protocol layer raises, carrying exactly one
/// reason from the closed §8 set and a human-readable detail.
/// </summary>
/// <remarks>
/// It derives from <see cref="FormatException"/> so that it lands inside the
/// repository's existing <c>ExceptionContract.IsGraceful</c> boundary without
/// that boundary needing to know this type exists. A protocol line is an
/// argument whose <em>format</em> is invalid, which is what
/// <see cref="FormatException"/> means, and the derivation buys the property
/// that matters for the fuzz targets: a refusal is an intentional, typed
/// rejection, exactly like a corrupt trajectory file, and stays
/// distinguishable from an unhandled runtime fault such as a
/// <see cref="NullReferenceException"/>. Every fuzz case feeds hostile bytes
/// through the parser, so that distinction is the difference between a covered
/// input class and an unexplained crash. (<see cref="InvalidDataException"/>
/// would read better but is sealed in .NET 8, and the boundary cannot be edited
/// to learn about a new type.)
/// <para>
/// A <see cref="ProtocolViolation"/> always carries <b>one</b> reason. When
/// several conditions hold at once, the reported one is the first detected
/// under the §8.4 precedence order, and the detail says what was actually seen.
/// </para>
/// </remarks>
public sealed class ProtocolViolation : FormatException
{
    /// <summary>Creates a violation for <paramref name="reason"/> with a diagnostic <paramref name="detail"/>.</summary>
    public ProtocolViolation(ProtocolReason reason, string detail)
        : base($"protocol {reason.ToWireString()}: {detail}")
    {
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The single reason code reported for this rejection (spec §8).</summary>
    public ProtocolReason Reason { get; }

    /// <summary>
    /// Human-readable text describing what was actually seen. Diagnostic only:
    /// spec §8.1 forbids an agent from parsing or branching on an
    /// <c>error.detail</c>, and this string carries the same status.
    /// </summary>
    public string Detail { get; }
}
