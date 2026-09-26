using System.Text;

namespace Lattice.Protocol;

/// <summary>
/// The §1 framing layer: it decides whether a byte sequence is a well-formed
/// line <em>before</em> anything tries to understand it, and it reads lines off
/// a stream one LF at a time.
/// </summary>
/// <remarks>
/// Nothing here parses JSON. The separation is the point of spec §8.2's framing
/// category — "the bytes were wrong before any meaning could be read" — and it is
/// also what makes the limits enforceable before parsing rather than after.
/// <para>
/// The four rules, in the order they are applied:
/// <list type="number">
/// <item>Length: a payload of more than <see cref="ProtocolLimits.MaxLineBytes"/>
/// bytes is <c>line_too_long</c>. Lattice counts bytes, not characters, and
/// never truncates (§7).</item>
/// <item>Byte-level framing: a CR anywhere, a leading byte-order mark, and
/// invalid UTF-8 are all <c>malformed_json</c>, because each means the line is
/// not a single well-formed JSON value (§1).</item>
/// <item>Depth: a value nesting more than
/// <see cref="ProtocolLimits.MaxJsonDepth"/> containers is
/// <c>depth_exceeded</c> (§7).</item>
/// <item>Parse, in <see cref="ProtocolParser"/>.</item>
/// </list>
/// </para>
/// </remarks>
public static class ProtocolFraming
{
    private const byte CarriageReturn = 0x0D;
    private const byte LineFeed = 0x0A;

    /// <summary>
    /// Strict UTF-8: no throw-on-invalid by default is exactly the bug this
    /// avoids, because the lenient decoder substitutes U+FFFD and turns a
    /// corrupt line into a parse error reported as the wrong reason code.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Applies every framing rule to <paramref name="line"/>, which is one
    /// message's payload <b>without</b> its terminating LF. Throws
    /// <see cref="ProtocolViolation"/> on the first rule that fails.
    /// </summary>
    /// <exception cref="ProtocolViolation">
    /// <see cref="ProtocolReason.LineTooLong"/>,
    /// <see cref="ProtocolReason.MalformedJson"/>, or
    /// <see cref="ProtocolReason.DepthExceeded"/>.
    /// </exception>
    public static void Validate(ReadOnlySpan<byte> line)
    {
        if (line.Length > ProtocolLimits.MaxLineBytes)
        {
            throw new ProtocolViolation(
                ProtocolReason.LineTooLong,
                $"{line.Length} bytes exceeds max_line_bytes {ProtocolLimits.MaxLineBytes} (LF excluded).");
        }

        if (line.IndexOf(CarriageReturn) >= 0)
        {
            throw new ProtocolViolation(
                ProtocolReason.MalformedJson,
                "CR (0x0D) is not a terminator and must not appear in a line.");
        }

        if (StartsWithBom(line))
        {
            throw new ProtocolViolation(
                ProtocolReason.MalformedJson,
                "a byte-order mark is not permitted at the start of a line.");
        }

        try
        {
            _ = StrictUtf8.GetString(line);
        }
        catch (DecoderFallbackException e)
        {
            throw new ProtocolViolation(
                ProtocolReason.MalformedJson,
                $"line is not valid UTF-8: {e.Message}");
        }

        // Measured before the document is parsed, and non-recursively, so that a
        // hostile payload cannot exhaust the stack on the way to being measured.
        // The one tie-break this creates: a payload that is BOTH over the depth
        // cap AND malformed reports depth_exceeded, because the depth cap is
        // evaluated first. Both are framing codes in spec §8.4, and the order
        // here is the "first detected" rule applied literally.
        var depth = MaxContainerDepth(line);
        if (depth > ProtocolLimits.MaxJsonDepth)
        {
            throw new ProtocolViolation(
                ProtocolReason.DepthExceeded,
                $"nesting {depth} containers exceeds max_json_depth {ProtocolLimits.MaxJsonDepth}.");
        }
    }

    /// <summary>
    /// Reads one LF-terminated line from <paramref name="stream"/> and returns
    /// its payload, with the terminating LF removed. Enforces
    /// <see cref="ProtocolLimits.MaxLineBytes"/> as it reads, so an over-long line
    /// is refused without ever being buffered whole.
    /// </summary>
    /// <returns>
    /// The payload bytes, or <see langword="null"/> at a clean end of stream —
    /// that is, when the stream ended before any byte of a further line. A clean
    /// end of stream is therefore distinguishable from a line, which matters
    /// because spec §1 forbids skipping a blank line rather than tolerating it.
    /// </returns>
    /// <exception cref="ProtocolViolation">
    /// <see cref="ProtocolReason.LineTooLong"/> for a payload past the cap, or
    /// <see cref="ProtocolReason.MalformedJson"/> for a blank line, which is
    /// reported here rather than parsed.
    /// </exception>
    public static byte[]? ReadLine(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new MemoryStream();
        try
        {
            while (true)
            {
                var next = stream.ReadByte();
                if (next < 0)
                {
                    if (buffer.Length == 0)
                    {
                        return null;
                    }

                    throw new ProtocolViolation(
                        ProtocolReason.MalformedJson,
                        "stream ended mid-line: the line is not LF-terminated.");
                }

                if (next == LineFeed)
                {
                    if (buffer.Length == 0)
                    {
                        throw new ProtocolViolation(
                            ProtocolReason.MalformedJson,
                            "blank line: a framing violation, not whitespace to skip.");
                    }

                    return buffer.ToArray();
                }

                if (buffer.Length >= ProtocolLimits.MaxLineBytes)
                {
                    throw new ProtocolViolation(
                        ProtocolReason.LineTooLong,
                        $"line exceeds max_line_bytes {ProtocolLimits.MaxLineBytes} (LF excluded).");
                }

                buffer.WriteByte((byte)next);
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Reads one line and applies every framing rule to it, so a caller gets
    /// back a payload that is known to be a legal line. Equivalent to
    /// <see cref="Validate"/> applied to <see cref="ReadLine"/>, in one call.
    /// </summary>
    public static byte[]? ReadValidatedLine(Stream stream)
    {
        var payload = ReadLine(stream);
        if (payload is not null)
        {
            Validate(payload);
        }

        return payload;
    }

    /// <summary>
    /// The maximum number of nested JSON containers (objects and arrays)
    /// enclosing any value in <paramref name="line"/>. A bare scalar is
    /// <c>0</c>.
    /// </summary>
    /// <remarks>
    /// This is the definition <see cref="ProtocolLimits.MaxJsonDepth"/> is stated
    /// against, and it counts containers, not scalars: the §4.1 <c>observation</c>
    /// example nests five (<c>observation → map → zones[] → position</c>) and
    /// therefore sits four levels below the cap, as spec §7 says it should.
    /// <para>
    /// The scan is a single non-recursive pass that tracks string literals and
    /// escapes, so a brace inside a string value is not counted. It stops early
    /// once the cap is passed, which is what makes it safe to run on a hostile
    /// input.
    /// </para>
    /// </remarks>
    public static int MaxContainerDepth(ReadOnlySpan<byte> line)
    {
        var depth = 0;
        var deepest = 0;
        var inString = false;
        var escaped = false;

        foreach (var b in line)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (b)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                case (byte)'[':
                    depth++;
                    if (depth > deepest)
                    {
                        deepest = depth;
                        if (deepest > ProtocolLimits.MaxJsonDepth)
                        {
                            return deepest;
                        }
                    }

                    break;
                case (byte)'}':
                case (byte)']':
                    depth--;
                    break;
                default:
                    break;
            }
        }

        return deepest;
    }

    private static bool StartsWithBom(ReadOnlySpan<byte> line) =>
        line.Length >= Bom.Length && line[..Bom.Length].SequenceEqual(Bom);
}
