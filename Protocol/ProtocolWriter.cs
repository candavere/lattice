using System.Text.Json;

namespace Lattice.Protocol;

/// <summary>
/// The writer of spec §1: one compact single-line UTF-8 JSON value per
/// message, LF-terminated, no BOM, no CR anywhere.
/// </summary>
/// <remarks>
/// <b>Byte-identical across operating systems</b> is a requirement here, not a
/// nicety: Lattice and its agent exchange bytes, and a recording that replayed
/// differently per platform would be unreproducible. Three things buy that, and
/// all three are pinned by tests rather than assumed:
/// <list type="bullet">
/// <item>Member order comes from explicit <c>JsonPropertyOrder</c> attributes, not
/// from declaration order, reflection order, or a hash table's iteration order —
/// and it matches the field tables of spec §5.3, so the output is the same text
/// the specification shows.</item>
/// <item>The encoder is <c>JavaScriptEncoder.Default</c>, which escapes every
/// non-ASCII code point as <c>\uXXXX</c>. Output is therefore pure ASCII on
/// every platform, and a raw LF or CR can never appear inside a string value,
/// which §1 forbids.</item>
/// <item>The terminating LF is appended here, by hand, rather than left to a
/// platform newline convention. A line-ending constant is precisely the kind of
/// implicit dependency that makes output differ between Windows and Unix.</item>
/// </list>
/// </remarks>
public static class ProtocolWriter
{
    private const byte LineFeed = 0x0A;
    private const byte CarriageReturn = 0x0D;

    /// <summary>
    /// Encodes <paramref name="message"/> as the message's payload: compact
    /// single-line JSON, UTF-8 without a BOM, <b>without</b> a trailing LF. This
    /// is the form the length limit of §7 is measured against.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If the encoded payload contains a CR or an LF, which would break the
    /// one-value-per-line framing. The schema makes this unreachable — no wire
    /// value can carry a raw control character — so the check is a tripwire, and
    /// it is a host-side assertion rather than something an agent can provoke.
    /// </exception>
    public static byte[] Encode(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The runtime type is passed explicitly. The declared parameter type is
        // the abstract base, and serializing against it would emit the
        // discriminator and nothing else — every message would collapse to
        // {"type":"..."} on the wire.
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ProtocolJson.Options);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] is LineFeed or CarriageReturn)
            {
                throw new InvalidOperationException(
                    $"A {message.Type} payload contains a raw 0x{payload[i]:X2} byte, which would break " +
                    "newline-delimited framing.");
            }
        }

        return payload;
    }

    /// <summary>
    /// Encodes <paramref name="message"/> and appends the single terminating LF,
    /// giving the exact bytes that go on the wire.
    /// </summary>
    public static byte[] WriteLine(ProtocolMessage message)
    {
        var payload = Encode(message);
        var line = new byte[payload.Length + 1];
        payload.CopyTo(line, 0);
        line[^1] = LineFeed;
        return line;
    }

    /// <summary>
    /// Writes <paramref name="message"/> to <paramref name="stream"/> as one
    /// LF-terminated line, flushing so the peer sees it before Lattice blocks
    /// reading the reply.
    /// </summary>
    public static void WriteLine(ProtocolMessage message, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(WriteLine(message));
        stream.Flush();
    }

    /// <summary>
    /// The §7 host-side gate: encodes <paramref name="message"/> and refuses it
    /// with <see cref="ProtocolReason.HostLimit"/> if it would breach
    /// <see cref="ProtocolLimits.MaxLineBytes"/> or
    /// <see cref="ProtocolLimits.MaxJsonDepth"/>.
    /// </summary>
    /// <remarks>
    /// This is the outbound mirror of the enforcement
    /// <see cref="ProtocolParser"/> applies to the agent's lines, and it exists
    /// because the whole map is re-sent every step while the generator's zone and
    /// resource bounds have no upper ceiling — so a large enough map produces an
    /// <c>observation</c> that cannot be sent legally. Spec §7 requires Lattice
    /// to refuse such a match <em>before</em> writing anything, rather than emit
    /// a line it has itself violated. The refusal is a <b>void</b> run and is not
    /// a loss for the external agent (§9.1, §9.3), because the agent had no
    /// influence on Lattice's own map size.
    /// </remarks>
    /// <returns>The payload, guaranteed to be within both limits.</returns>
    /// <exception cref="ProtocolViolation">
    /// <see cref="ProtocolReason.HostLimit"/> only.
    /// </exception>
    public static byte[] EncodeChecked(ProtocolMessage message)
    {
        var payload = Encode(message);

        if (payload.Length > ProtocolLimits.MaxLineBytes)
        {
            throw new ProtocolViolation(
                ProtocolReason.HostLimit,
                $"outbound {message.Type} line is {payload.Length} bytes, over max_line_bytes " +
                $"{ProtocolLimits.MaxLineBytes}; the match is refused before sending.");
        }

        var depth = ProtocolFraming.MaxContainerDepth(payload);
        if (depth > ProtocolLimits.MaxJsonDepth)
        {
            throw new ProtocolViolation(
                ProtocolReason.HostLimit,
                $"outbound {message.Type} line nests {depth} containers, over max_json_depth " +
                $"{ProtocolLimits.MaxJsonDepth}; the match is refused before sending.");
        }

        return payload;
    }
}
