namespace Lattice.Protocol;

/// <summary>
/// The closed reason-code set of spec §8, in spec-table order.
/// </summary>
/// <remarks>
/// The set is closed: Lattice MUST NOT invent, extend, or re-purpose a member
/// (spec §8). Thirteen members are <em>agent-attributable</em> and are scored as
/// a loss for the external agent (spec §9.1); the fourteenth,
/// <see cref="HostLimit"/>, is a <em>host</em> fault and is recorded as a void
/// run instead (spec §9.1, §9.3). That partition is normative, not commentary —
/// see <see cref="ProtocolReasons.IsAgentFault"/>.
/// <para>
/// The wire spelling of every member is fixed by
/// <see cref="ProtocolReasons.ToWireString"/> and is asserted member-by-member
/// against the spec table in the test suite; it is deliberately not derived from
/// the member name, so renaming a member can never silently change the wire.
/// </para>
/// </remarks>
public enum ProtocolReason
{
    /// <summary><c>protocol_mismatch</c> — the handshake version is not exactly <c>1</c> (spec §2).</summary>
    ProtocolMismatch,

    /// <summary><c>malformed_json</c> — the line is not a single well-formed JSON value (spec §8).</summary>
    MalformedJson,

    /// <summary><c>schema_violation</c> — well-formed JSON that violates the message schema (spec §8).</summary>
    SchemaViolation,

    /// <summary><c>unknown_field</c> — the object carries a member not in the schema for that type (spec §8).</summary>
    UnknownField,

    /// <summary><c>line_too_long</c> — a line the <em>agent</em> sent exceeded <see cref="ProtocolLimits.MaxLineBytes"/> (spec §7).</summary>
    LineTooLong,

    /// <summary><c>depth_exceeded</c> — a JSON value the <em>agent</em> sent exceeded <see cref="ProtocolLimits.MaxJsonDepth"/> (spec §7).</summary>
    DepthExceeded,

    /// <summary><c>step_mismatch</c> — the action's <c>step</c> did not equal the <c>step</c> it answers (spec §6.2).</summary>
    StepMismatch,

    /// <summary><c>illegal_action</c> — well-formed action outside <c>ActionSpace</c> for this map (spec §6.3).</summary>
    IllegalAction,

    /// <summary><c>timeout_handshake</c> — no valid <c>hello_ack</c> within <c>step_timeout_ms</c> of <c>hello</c> (spec §2, §7).</summary>
    TimeoutHandshake,

    /// <summary><c>timeout_step</c> — no <c>action</c> within <c>step_timeout_ms</c> of an <c>observation</c> (spec §7).</summary>
    TimeoutStep,

    /// <summary><c>timeout_match</c> — the match exceeded <c>match_timeout_ms</c> (spec §7).</summary>
    TimeoutMatch,

    /// <summary><c>agent_exited</c> — the process closed stdout or exited <c>0</c> before the exchange completed (spec §8).</summary>
    AgentExited,

    /// <summary><c>agent_crashed</c> — the process was signalled or exited non-zero before the exchange completed (spec §8).</summary>
    AgentCrashed,

    /// <summary>
    /// <c>host_limit</c> — <b>Lattice's own</b> outbound message would have breached
    /// <see cref="ProtocolLimits.MaxLineBytes"/> or
    /// <see cref="ProtocolLimits.MaxJsonDepth"/>, so Lattice refused the match before
    /// sending. A void run; <b>not</b> a loss for the external agent (spec §7, §9.1, §9.3).
    /// </summary>
    HostLimit,
}
