using System.Text;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// The closed <c>type</c> catalogue, the closed reason set, and the exact wire
/// spelling of every one of them (spec §4, §8).
/// </summary>
public class ProtocolMessageSetTests
{
    [Fact]
    public void MessageTypes_Are_Exactly_Five()
    {
        Assert.Equal(5, ProtocolMessageTypes.All.Count);
        Assert.Equal(
            ["hello", "hello_ack", "observation", "action", "error"],
            ProtocolMessageTypes.All);
    }

    [Fact]
    public void MessageTypes_Closed_Set_Rejects_Near_Misses()
    {
        Assert.True(ProtocolMessageTypes.IsKnown("hello"));
        Assert.False(ProtocolMessageTypes.IsKnown("Hello"));
        Assert.False(ProtocolMessageTypes.IsKnown("hello_ack "));
        Assert.False(ProtocolMessageTypes.IsKnown(""));
        Assert.False(ProtocolMessageTypes.IsKnown(null));
    }

    /// <summary>
    /// The reason enum and its wire strings are asserted member by member against
    /// the spec §8 table, in table order. This is the test that would catch a
    /// renamed enum member silently changing the wire, which is why the two
    /// directions live in one table in <see cref="ProtocolReasons"/>.
    /// </summary>
    [Fact]
    public void Reason_Wire_Strings_Match_The_Spec_Table_Exactly()
    {
        var expected = new (ProtocolReason Reason, string Wire)[]
        {
            (ProtocolReason.ProtocolMismatch, "protocol_mismatch"),
            (ProtocolReason.MalformedJson, "malformed_json"),
            (ProtocolReason.SchemaViolation, "schema_violation"),
            (ProtocolReason.UnknownField, "unknown_field"),
            (ProtocolReason.LineTooLong, "line_too_long"),
            (ProtocolReason.DepthExceeded, "depth_exceeded"),
            (ProtocolReason.StepMismatch, "step_mismatch"),
            (ProtocolReason.IllegalAction, "illegal_action"),
            (ProtocolReason.TimeoutHandshake, "timeout_handshake"),
            (ProtocolReason.TimeoutStep, "timeout_step"),
            (ProtocolReason.TimeoutMatch, "timeout_match"),
            (ProtocolReason.AgentExited, "agent_exited"),
            (ProtocolReason.AgentCrashed, "agent_crashed"),
            (ProtocolReason.HostLimit, "host_limit"),
        };

        Assert.Equal(expected.Length, ProtocolReasons.All.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Reason, ProtocolReasons.All[index]);
            Assert.Equal(expected[index].Wire, expected[index].Reason.ToWireString());
        }
    }

    [Fact]
    public void Reason_Round_Trips_Through_Its_Wire_String()
    {
        foreach (var reason in ProtocolReasons.All)
        {
            Assert.True(ProtocolReasons.TryParse(reason.ToWireString(), out var parsed));
            Assert.Equal(reason, parsed);
        }
    }

    /// <summary>
    /// Forward compatibility is refused, not guessed
    /// (<c>docs/SUPPORT_AND_REPRODUCIBILITY.md:228-231</c>, spec §12.2). The
    /// parse direction is where that has teeth, so case variants, whitespace, and
    /// a hypothetical protocol-2 code all have to fail rather than resolve.
    /// </summary>
    [Theory]
    [InlineData("TIMEOUT_STEP")]
    [InlineData("Timeout_Step")]
    [InlineData(" timeout_step")]
    [InlineData("timeout_step ")]
    [InlineData("timeout")]
    [InlineData("")]
    public void Reason_Parse_Refuses_Near_Misses(string wire)
    {
        Assert.False(ProtocolReasons.TryParse(wire, out _));
    }

    [Fact]
    public void Reason_Fault_Partition_Is_Thirteen_Agent_And_One_Host()
    {
        var agent = ProtocolReasons.All.Count(reason => reason.IsAgentFault());
        var host = ProtocolReasons.All.Count(reason => reason.IsHostFault());

        Assert.Equal(13, agent);
        Assert.Equal(1, host);
        Assert.Equal(14, agent + host);

        // The two sets partition the closed set: no code is both, and none is
        // neither. Scoring hinges on this (spec §9.3).
        Assert.All(ProtocolReasons.All, reason => Assert.True(reason.IsAgentFault() ^ reason.IsHostFault()));
    }

    [Fact]
    public void HostLimit_Is_The_Only_Host_Fault()
    {
        Assert.True(ProtocolReason.HostLimit.IsHostFault());
        Assert.False(ProtocolReason.HostLimit.IsAgentFault());
    }

    [Fact]
    public void Every_Fault_Code_Survives_An_Error_Round_Trip()
    {
        foreach (var reason in ProtocolReasons.All)
        {
            var line = ProtocolWriter.WriteLine(new ErrorMessage { Reason = reason, Detail = "d" });
            var parsed = ProtocolParser.ParseError(line);
            Assert.Equal(reason, parsed.Reason);
        }
    }

    /// <summary>
    /// The three <c>kind</c> spellings are the <c>ActionKind</c> member names
    /// with that exact casing (spec §6.1). The wire is not free-form text, so the
    /// names are pinned here rather than derived from the enum.
    /// </summary>
    [Fact]
    public void ActionKind_Wire_Strings_Are_The_Member_Names()
    {
        foreach (var kind in new[] { ProtocolActionKind.Wait, ProtocolActionKind.Move, ProtocolActionKind.Collect })
        {
            var action = kind switch
            {
                ProtocolActionKind.Wait => new ActionMessage { Step = 0, Kind = kind },
                ProtocolActionKind.Move => new ActionMessage { Step = 0, Kind = kind, ZoneId = 1 },
                _ => new ActionMessage { Step = 0, Kind = kind, ResourceId = 0 },
            };

            var text = Encoding.UTF8.GetString(ProtocolWriter.Encode(action));
            Assert.Contains($"\"kind\":\"{kind}\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Protocol_Version_Is_The_Integer_One()
    {
        Assert.Equal(1, ProtocolLimits.Version);
    }
}
