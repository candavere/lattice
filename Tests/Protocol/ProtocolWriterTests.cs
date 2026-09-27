using System.Text;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// The writer's framing guarantees (spec §1) and its round-trip identity.
/// </summary>
/// <remarks>
/// Byte-identical output across operating systems is a requirement, not a
/// nicety: Lattice and its agent exchange bytes, and a recording that replayed
/// differently per platform would be unreproducible. None of these properties can
/// be observed on a single OS, so each one is stated as an invariant of the bytes
/// — no CR, exactly one trailing LF, no BOM, ASCII-only, round-trip equality —
/// and a platform that broke any of them would fail the same assertion.
/// </remarks>
public class ProtocolWriterTests
{
    public static TheoryData<string> ValidFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in ProtocolFixtures.ValidNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void WriteLine_Contains_No_Carriage_Return(string fixture)
    {
        var line = ProtocolWriter.WriteLine(ProtocolParser.Parse(ProtocolFixtures.Line(fixture)));

        Assert.DoesNotContain((byte)0x0D, line);
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void WriteLine_Ends_With_Exactly_One_LF(string fixture)
    {
        var line = ProtocolWriter.WriteLine(ProtocolParser.Parse(ProtocolFixtures.Line(fixture)));

        Assert.Equal((byte)0x0A, line[^1]);
        Assert.NotEqual((byte)0x0A, line[^2]);
        Assert.DoesNotContain((byte)0x0A, line[..^1]);
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void WriteLine_Carries_No_Byte_Order_Mark(string fixture)
    {
        var line = ProtocolWriter.WriteLine(ProtocolParser.Parse(ProtocolFixtures.Line(fixture)));

        Assert.True(line.Length > 3);
        Assert.False(line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF);
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void WriteLine_Is_Single_Line_Compact_Json(string fixture)
    {
        var text = Encoding.UTF8.GetString(ProtocolWriter.Encode(ProtocolParser.Parse(ProtocolFixtures.Line(fixture))));

        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);

        // Compact: no whitespace anywhere outside a string literal. A substring
        // check for ": " would be wrong — a diagnostic detail may legitimately
        // contain ", " or ": " — so the string literals are blanked first.
        Assert.DoesNotContain(' ', WithoutStringLiterals(text));
        Assert.DoesNotContain('\t', WithoutStringLiterals(text));
    }

    /// <summary>
    /// The encoder escapes every non-ASCII code point, so the output is pure
    /// ASCII on every platform and no host encoding can change it. It is also what
    /// guarantees a raw LF or CR can never appear inside a string value, which
    /// §1 forbids.
    /// </summary>
    [Fact]
    public void Non_Ascii_Detail_Is_Escaped_Rather_Than_Emitted_As_Bytes()
    {
        var error = new ErrorMessage { Reason = ProtocolReason.StepMismatch, Detail = "café \u00e9\u4e2d" };
        var payload = ProtocolWriter.Encode(error);

        Assert.All(payload, b => Assert.True(b < 0x80));
        Assert.Equal(error, ProtocolParser.ParseError(payload));
    }

    [Fact]
    public void Embedded_Newlines_In_A_Detail_Are_Escaped_Not_Emitted()
    {
        var error = new ErrorMessage { Reason = ProtocolReason.StepMismatch, Detail = "a\nb\rc" };
        var payload = ProtocolWriter.Encode(error);

        Assert.DoesNotContain((byte)0x0A, payload);
        Assert.DoesNotContain((byte)0x0D, payload);
        Assert.Equal(error, ProtocolParser.ParseError(payload));
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Round_Trip_Preserves_The_Message(string fixture)
    {
        var parsed = ProtocolParser.Parse(ProtocolFixtures.Line(fixture));
        var reparsed = ProtocolParser.Parse(ProtocolWriter.WriteLine(parsed));

        Assert.Equal(parsed, reparsed);
    }

    [Fact]
    public void Round_Trip_Preserves_Every_Optional_Branch()
    {
        // Equality is structural over every field, so a round trip that dropped an
        // optional role, a transit, or a conditional target would fail here even
        // though the line still parsed.
        var full = ProtocolParser.ParseObservation(ProtocolFixtures.Line("observation_full.jsonl"));
        Assert.Equal(full, ProtocolParser.ParseObservation(ProtocolWriter.WriteLine(full)));

        var collect = ProtocolParser.ParseAction(ProtocolFixtures.Line("action_collect.jsonl"), expectedStep: 5);
        Assert.Equal(collect, ProtocolParser.ParseAction(ProtocolWriter.WriteLine(collect), expectedStep: 5));

        var hello = ProtocolParser.ParseHello(ProtocolFixtures.Line("hello.jsonl"));
        Assert.Equal(hello, ProtocolParser.ParseHello(ProtocolWriter.WriteLine(hello)));
    }

    [Fact]
    public void The_Discriminator_Is_Written_First_And_Exactly_Once()
    {
        var payload = Encoding.UTF8.GetString(ProtocolWriter.Encode(
            ProtocolParser.Parse(ProtocolFixtures.Line("observation_full.jsonl"))));

        Assert.StartsWith("{\"type\":\"observation\"", payload, StringComparison.Ordinal);
        Assert.Equal(1, payload.Split("\"type\"").Length - 1);
    }

    /// <summary>
    /// The field order of the §4.1 examples is the field order of the §5.3 tables,
    /// and the writer reproduces it. The examples are the contract's own
    /// illustration, so matching them byte for byte is the strongest available
    /// statement that a reader implementing against the spec sees what the spec
    /// shows.
    /// </summary>
    [Theory]
    [InlineData("hello.jsonl", "protocol", "scenario", "seed", "agent_slot", "max_ticks", "agent_count", "limits")]
    [InlineData("observation.jsonl", "step", "agent_id", "map", "agent_states", "claims", "step_number")]
    [InlineData("action.jsonl", "step", "kind")]
    public void Field_Order_Follows_The_Spec_Tables(string fixture, params string[] expectedOrder)
    {
        var text = Encoding.UTF8.GetString(ProtocolWriter.Encode(ProtocolParser.Parse(ProtocolFixtures.Line(fixture))));

        var previous = -1;
        foreach (var field in expectedOrder)
        {
            var at = text.IndexOf($"\"{field}\"", StringComparison.Ordinal);
            Assert.True(at > previous, $"'{field}' is out of order in {text}");
            previous = at;
        }
    }

    [Fact]
    public void Limits_Fields_Are_Emitted_In_Table_Order()
    {
        var text = Encoding.UTF8.GetString(ProtocolWriter.Encode(ProtocolParser.Parse(ProtocolFixtures.Line("hello.jsonl"))));

        var step = text.IndexOf("\"step_timeout_ms\"", StringComparison.Ordinal);
        var match = text.IndexOf("\"match_timeout_ms\"", StringComparison.Ordinal);
        Assert.True(step >= 0 && match > step);
    }

    [Fact]
    public void WriteLine_To_A_Stream_Emits_Exactly_One_LF_Terminated_Line()
    {
        var message = ProtocolParser.Parse(ProtocolFixtures.Line("hello_ack.jsonl"));
        using var stream = new MemoryStream();

        ProtocolWriter.WriteLine(message, stream);

        Assert.Equal(ProtocolWriter.WriteLine(message), stream.ToArray());
    }

    /// <summary>
    /// The §7 host-side gate. An outbound line that Lattice itself would violate
    /// is refused before sending, and refused as a <b>host</b> fault — the one code
    /// that is not a loss for the external agent (§9.1, §9.3).
    /// </summary>
    [Fact]
    public void EncodeChecked_Accepts_A_Message_Within_The_Limits()
    {
        var message = ProtocolParser.Parse(ProtocolFixtures.Line("observation.jsonl"));

        Assert.Equal(ProtocolWriter.Encode(message), ProtocolWriter.EncodeChecked(message));
    }

    [Fact]
    public void EncodeChecked_Refuses_An_Oversized_Outbound_Line_As_HostLimit()
    {
        // 1 MiB of diagnostic text in an outbound line is the shape a large map
        // produces, and it is Lattice's own doing. The message is built directly
        // rather than parsed, because an over-long line cannot survive the inbound
        // reader — which is the whole asymmetry: the outbound gate exists so the
        // host never emits a line its own reader would refuse.
        var overhead = ProtocolWriter.Encode(new ErrorMessage { Reason = ProtocolReason.StepMismatch, Detail = string.Empty }).Length;
        var message = new ErrorMessage
        {
            Reason = ProtocolReason.StepMismatch,
            Detail = new string('x', ProtocolLimits.MaxLineBytes + 1 - overhead),
        };

        Assert.True(ProtocolWriter.Encode(message).Length > ProtocolLimits.MaxLineBytes);

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolWriter.EncodeChecked(message)));
        Assert.Equal(ProtocolReason.HostLimit, violation.Reason);
    }

    /// <summary>
    /// The depth half of the host-side gate is unreachable through the closed
    /// schema, and this is the test that says so rather than leaving it as an
    /// untested branch: the deepest conforming message nests five containers, so
    /// no <c>ErrorMessage</c>, <c>HelloMessage</c>, <c>HelloAckMessage</c>,
    /// <c>ActionMessage</c>, or <c>ObservationMessage</c> can be assembled that
    /// nests past the cap. The check stays in <c>EncodeChecked</c> as defence in
    /// depth for the same reason the <c>ActionKind</c> unknown-value branch stays
    /// in <c>ActionSpace.Validate</c>.
    /// </summary>
    [Fact]
    public void No_Conforming_Outbound_Message_Can_Reach_The_Depth_Cap()
    {
        var deepest = 0;

        foreach (var name in ProtocolFixtures.ValidNames)
        {
            var payload = ProtocolWriter.Encode(ProtocolParser.Parse(ProtocolFixtures.Line(name)));
            deepest = Math.Max(deepest, ProtocolFraming.MaxContainerDepth(payload));
        }

        Assert.Equal(5, deepest);
        Assert.True(deepest < ProtocolLimits.MaxJsonDepth);
    }

    [Fact]
    public void Write_Rejects_A_Null_Message()
    {
        Assert.Throws<ArgumentNullException>(() => ProtocolWriter.Encode(null!));
    }

    /// <summary>
    /// <paramref name="json"/> with every string literal's contents replaced by
    /// <c>0</c>s, so a whitespace assertion can distinguish structural whitespace
    /// from whitespace that is part of a value.
    /// </summary>
    private static string WithoutStringLiterals(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;

        foreach (var c in json)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                builder.Append('0');
                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append('0');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
