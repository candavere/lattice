using System.Text;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// One rejection fixture per reason code reachable in-process, each asserting the
/// <b>exact</b> code.
/// </summary>
/// <remarks>
/// These are the tests that make the closed set meaningful. A rejection that
/// carries the wrong reason is a real defect, not a cosmetic one: §9.3 scores
/// agent codes as losses and excludes <c>host_limit</c> from the paired
/// statistics, so a misattributed code changes a published number. Each test
/// therefore asserts the code, not merely that something was refused.
/// <para>
/// <c>line_too_long</c> has no committed fixture: the only way to trigger it is a
/// payload past 1 MiB, and
/// <see cref="ProtocolLimitTests.MaxLineBytes_Boundary_Is_Exact"/> builds both
/// sides of that boundary arithmetically, which is both smaller and stronger.
/// </para>
/// </remarks>
public class ProtocolRejectionTests
{
    private static ProtocolReason Reject(string fixture)
    {
        var line = ProtocolFixtures.Line(fixture);
        var exception = Record.Exception(() => ProtocolParser.Parse(line));

        var violation = Assert.IsType<ProtocolViolation>(exception);
        return violation.Reason;
    }

    [Fact]
    public void MalformedJson_Truncated_Line()
    {
        Assert.Equal(ProtocolReason.MalformedJson, Reject("reject_malformed_json.jsonl"));
    }

    [Fact]
    public void MalformedJson_Two_Values_On_One_Line()
    {
        // "exactly one JSON value per line" is not a formatting preference: two
        // values means the framing has already lost track of the exchange.
        Assert.Equal(ProtocolReason.MalformedJson, Reject("reject_malformed_json_trailing.jsonl"));
    }

    /// <summary>
    /// A trailing CR is a framing violation, not whitespace to be trimmed
    /// (spec §1). The line is built here rather than committed as a fixture
    /// because the repository's <c>.gitattributes</c> normalises
    /// <c>*.jsonl</c> to LF: a stored CR would be stripped on commit and the
    /// fixture would pass locally while failing on every fresh clone. A test input
    /// that cannot survive its own version control is not a fixture.
    /// </summary>
    [Fact]
    public void MalformedJson_Trailing_Carriage_Return()
    {
        var withCr = new List<byte>(ProtocolFixtures.Line("hello_ack.jsonl")) { 0x0D };

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(withCr.ToArray())));
        Assert.Equal(ProtocolReason.MalformedJson, violation.Reason);
    }

    /// <summary>
    /// A CR is refused wherever it appears, not only at the end of a line: an
    /// embedded one is equally a violation of the one-value-per-line framing,
    /// because a JSON text may not contain a raw CR at all (spec §1).
    /// </summary>
    [Fact]
    public void MalformedJson_Embedded_Carriage_Return()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"hello_ack\",\"protocol\":1}");
        var withCr = new byte[payload.Length + 1];
        payload.CopyTo(withCr, 0);
        withCr[^2] = 0x0D;

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(withCr)));
        Assert.Equal(ProtocolReason.MalformedJson, violation.Reason);
    }

    [Fact]
    public void MalformedJson_Leading_Byte_Order_Mark()
    {
        Assert.Equal(ProtocolReason.MalformedJson, Reject("reject_malformed_json_bom.jsonl"));
    }

    [Fact]
    public void MalformedJson_Invalid_Utf8()
    {
        // 0xC3 starts a two-byte sequence; 0x28 is not a valid continuation byte.
        var line = new byte[] { (byte)'{', 0xC3, 0x28, (byte)'}' };

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(line)));
        Assert.Equal(ProtocolReason.MalformedJson, violation.Reason);
    }

    [Fact]
    public void SchemaViolation_Missing_Required_Field()
    {
        // A missing required field is refused, and no default is substituted
        // (spec §8.3). An observation with no "map" is used rather than a
        // handshake, because on a handshake a missing field is specifically the
        // missing "protocol", which spec §2 makes protocol_mismatch instead.
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_missing.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Missing_Conditional_Target()
    {
        // zone_id is required for Move (spec §6.1).
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_absent_target.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Unknown_Type_Value()
    {
        // A type outside the closed five (spec §8.3).
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_type.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Root_Is_Not_An_Object()
    {
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_not_object.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Unknown_Kind_Spelling()
    {
        // "move" is not "Move", and §6.1 is explicit that no other casing is
        // accepted. This is what keeps ActionKind's unknown-value branch
        // unreachable from the wire.
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_kind.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Conditional_Field_That_Must_Be_Absent()
    {
        // zone_id must be absent for Wait (spec §6.1).
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_conditional.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Explicit_Minus_One_Sentinel()
    {
        // -1 is the record's sentinel, not an addressable zone; sending it
        // explicitly is a schema violation (spec §6.1).
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_sentinel.jsonl"));
    }

    [Fact]
    public void SchemaViolation_Unknown_Reason_Code()
    {
        Assert.Equal(ProtocolReason.SchemaViolation, Reject("reject_schema_violation_reason.jsonl"));
    }

    [Fact]
    public void UnknownField_At_The_Top_Level()
    {
        Assert.Equal(ProtocolReason.UnknownField, Reject("reject_unknown_field.jsonl"));
    }

    [Fact]
    public void UnknownField_In_A_Nested_Record()
    {
        // The refusal has to reach inside the map: a protocol-2 agent adding a
        // field to a zone is the exact case §8.3 calls out.
        Assert.Equal(ProtocolReason.UnknownField, Reject("reject_unknown_field_nested.jsonl"));
    }

    [Fact]
    public void DepthExceeded_Container_Nesting_Past_The_Cap()
    {
        Assert.Equal(ProtocolReason.DepthExceeded, Reject("reject_depth_exceeded.jsonl"));
    }

    [Fact]
    public void ProtocolMismatch_Different_Integer()
    {
        Assert.Equal(ProtocolReason.ProtocolMismatch, Reject("reject_protocol_mismatch.jsonl"));
    }

    [Fact]
    public void ProtocolMismatch_String_Version()
    {
        // The version is an integer precisely so that 1 and "1" are
        // distinguishable (spec §2).
        Assert.Equal(ProtocolReason.ProtocolMismatch, Reject("reject_protocol_mismatch_string.jsonl"));
    }

    [Fact]
    public void ProtocolMismatch_Missing_Version()
    {
        Assert.Equal(ProtocolReason.ProtocolMismatch, Reject("reject_protocol_mismatch_missing.jsonl"));
    }

    [Fact]
    public void StepMismatch_Action_Answers_A_Different_Step()
    {
        // The fixture answers step 3; the caller expects step 7.
        var line = ProtocolFixtures.Line("reject_step_mismatch.jsonl");
        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.ParseAction(line, expectedStep: 7)));

        Assert.Equal(ProtocolReason.StepMismatch, violation.Reason);
    }

    [Fact]
    public void IllegalAction_Move_Targets_A_Zone_The_Map_Does_Not_Have()
    {
        // Zone 9 is well formed and inside the wire schema, but outside the
        // action space for a two-zone map (spec §6.3). This is the shape-level
        // check; ActionSpace.Validate remains the authority at receipt.
        var line = ProtocolFixtures.Line("reject_illegal_action.jsonl");
        var action = ProtocolParser.ParseAction(line, expectedStep: 0);

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolActionGuard.RequireWithinActionSpace(action, zoneCount: 2, resourceCount: 1)));
        Assert.Equal(ProtocolReason.IllegalAction, violation.Reason);
    }

    [Fact]
    public void IllegalAction_Collect_Targets_A_Resource_The_Map_Does_Not_Have()
    {
        var action = new ActionMessage { Step = 0, Kind = ProtocolActionKind.Collect, ResourceId = 4 };

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolActionGuard.RequireWithinActionSpace(action, zoneCount: 2, resourceCount: 1)));
        Assert.Equal(ProtocolReason.IllegalAction, violation.Reason);
    }

    /// <summary>
    /// An in-shape but ineffective action MUST be applied, not refused (spec
    /// §6.3): the shape check says nothing about adjacency, choke passability, or
    /// whether the agent is in transit.
    /// </summary>
    [Fact]
    public void In_Shape_But_Ineffective_Action_Is_Not_Refused()
    {
        var action = new ActionMessage { Step = 0, Kind = ProtocolActionKind.Move, ZoneId = 0 };

        ProtocolActionGuard.RequireWithinActionSpace(action, zoneCount: 2, resourceCount: 1);
    }

    [Fact]
    public void Wait_Outside_The_Action_Space_Is_Accepted()
    {
        // Wait carries no target, so the map's shape cannot invalidate it.
        var action = new ActionMessage { Step = 0, Kind = ProtocolActionKind.Wait };

        ProtocolActionGuard.RequireWithinActionSpace(action, zoneCount: 0, resourceCount: 0);
    }

    /// <summary>
    /// The §6.1 conditional rule is a schema matter, so it is reported as
    /// <c>schema_violation</c> and not as <c>illegal_action</c> — an absent target
    /// is a malformed action, not an out-of-range one. §8.4's precedence puts both
    /// codes in the same tier, and the parser's ordering is what decides it.
    /// </summary>
    [Fact]
    public void Conditional_Breach_Inside_The_Action_Space_Check_Stays_A_Schema_Violation()
    {
        var action = new ActionMessage { Step = 0, Kind = ProtocolActionKind.Wait, ZoneId = 1 };

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolActionGuard.RequireWithinActionSpace(action, zoneCount: 4, resourceCount: 4)));
        Assert.Equal(ProtocolReason.SchemaViolation, violation.Reason);
    }

    [Fact]
    public void Every_Code_In_The_Closed_Set_That_Is_Reachable_In_Process_Has_A_Fixture()
    {
        // A guard against the set growing without a rejection test: the two
        // process-level codes and the two timeouts are raised by the stage-3
        // runner, not by this assembly, and host_limit is raised by the writer.
        var reachableHere = new[]
        {
            ProtocolReason.ProtocolMismatch,
            ProtocolReason.MalformedJson,
            ProtocolReason.SchemaViolation,
            ProtocolReason.UnknownField,
            ProtocolReason.LineTooLong,
            ProtocolReason.DepthExceeded,
            ProtocolReason.StepMismatch,
            ProtocolReason.IllegalAction,
        };

        var covered = new[]
        {
            ProtocolReason.ProtocolMismatch,
            ProtocolReason.MalformedJson,
            ProtocolReason.SchemaViolation,
            ProtocolReason.UnknownField,
            ProtocolReason.DepthExceeded,
            ProtocolReason.StepMismatch,
            ProtocolReason.IllegalAction,
            ProtocolReason.LineTooLong,
        };

        Assert.Equal(reachableHere.OrderBy(r => r), covered.OrderBy(r => r));
    }

    /// <summary>
    /// Spec §8.4: exactly one reason is reported, and the first detected wins. A
    /// line that is over-long <em>and</em> malformed must not produce a message
    /// naming two codes.
    /// </summary>
    [Fact]
    public void Exactly_One_Reason_Is_Reported_Per_Line()
    {
        var oversized = new byte[ProtocolLimits.MaxLineBytes + 1];
        Array.Fill(oversized, (byte)0xC3); // also invalid UTF-8

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(oversized)));

        // line_too_long outranks the other framing codes (§8.4).
        Assert.Equal(ProtocolReason.LineTooLong, violation.Reason);
        Assert.Contains("line_too_long", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed_json", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Violation_Detail_Is_Diagnostic_And_Never_Empty()
    {
        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(ProtocolFixtures.Line("reject_unknown_field.jsonl"))));

        Assert.False(string.IsNullOrWhiteSpace(violation.Detail));
        Assert.Contains("agent_count", violation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The typed rejection has to land inside the repository's existing graceful
    /// boundary, or the fuzz targets would report every clean refusal as a crash.
    /// </summary>
    [Fact]
    public void ProtocolViolation_Is_Inside_The_Graceful_Exception_Contract()
    {
        Assert.True(Fuzz.ExceptionContract.IsGraceful(
            new ProtocolViolation(ProtocolReason.MalformedJson, "test")));
    }

    /// <summary>
    /// The corpus is fed to the fuzzer as text, so a stray CR or a missing
    /// terminator in a fixture would show up as an unexplained mutation outcome
    /// rather than as a fixture problem. The one fixture that must contain a CR is
    /// the one whose entire purpose is to be refused for having one, so the
    /// CR-freedom assertion is stated over the conforming corpus and the
    /// single-terminator assertion over all of it.
    /// </summary>
    [Fact]
    public void Every_Fixture_Is_Single_LF_Terminated()
    {
        foreach (var path in Directory.GetFiles(ProtocolFixtures.Directory, "*.jsonl"))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            Assert.Equal((byte)0x0A, bytes[^1]);
        }
    }

    [Fact]
    public void The_Conforming_Corpus_Carries_No_Carriage_Return()
    {
        foreach (var name in ProtocolFixtures.ValidNames)
        {
            var bytes = ProtocolFixtures.Bytes(name);
            Assert.DoesNotContain((byte)0x0D, bytes[..^1]);
        }
    }

    /// <summary>
    /// No committed fixture carries a CR, because the repository's
    /// <c>.gitattributes</c> normalises <c>*.jsonl</c> to LF: a stored CR would be
    /// stripped on commit, so the fixture would exercise a different line in CI
    /// than in the working tree. The CR-rejection case is therefore constructed in
    /// test code, and this asserts the invariant that keeps it that way.
    /// </summary>
    [Fact]
    public void No_Committed_Fixture_Carries_A_Carriage_Return()
    {
        var carriers = Directory.GetFiles(ProtocolFixtures.Directory, "*.jsonl")
            .Where(path => File.ReadAllBytes(path).Contains((byte)0x0D))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(carriers);
    }
}
