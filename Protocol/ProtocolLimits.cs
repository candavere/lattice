namespace Lattice.Protocol;

/// <summary>
/// The two protocol-constant limits of
/// <c>docs/EXTERNAL_AGENT_PROTOCOL.md</c> §7, together with the protocol
/// version of §2.
/// </summary>
/// <remarks>
/// Only the two <em>protocol constants</em> live here. The two named per-match
/// time limits (<c>step_timeout_ms</c>, <c>match_timeout_ms</c>) are carried in
/// <c>hello.limits</c> and their defaults are fixed by spec §7 (§14, U-2):
/// <c>step_timeout_ms</c> defaults to <c>5000</c> and <c>match_timeout_ms</c> is
/// computed as <c>step_timeout_ms × max_ticks + 30000</c>. Those defaults live in
/// the spec and are applied by the stage-3 runner, and <b>this type still asserts
/// no time value</b> — the leaf protocol project deliberately holds no timeout
/// policy it would then have to keep in step with §3.1 and §7.
/// </remarks>
public static class ProtocolLimits
{
    /// <summary>
    /// The wire version integer of spec §2. Negotiation is an exact match and
    /// there is no downgrade path, so this is the only value that can appear in
    /// a <c>protocol</c> field on a conforming line.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// <c>max_line_bytes</c> from spec §7: <c>1048576</c> bytes, <b>excluding</b>
    /// the terminating LF. A line of exactly this many payload bytes is
    /// accepted; one byte more is refused with
    /// <see cref="ProtocolReason.LineTooLong"/>.
    /// </summary>
    public const int MaxLineBytes = 1_048_576;

    /// <summary>
    /// <c>max_json_depth</c> from spec §7: <c>32</c>, measured as the maximum
    /// number of <em>nested JSON containers</em> (objects and arrays) enclosing
    /// any value. A bare scalar line is depth <c>0</c>; the single object of a
    /// <c>hello_ack</c> line is depth <c>1</c>; the deepest container in a
    /// conforming <c>observation</c> is the fifth
    /// (<c>observation → map → zones[] → position</c>), which is why the cap is
    /// headroom against a hostile peer rather than a constraint on legitimate
    /// traffic. A payload nesting <c>33</c> containers is refused with
    /// <see cref="ProtocolReason.DepthExceeded"/>.
    /// </summary>
    public const int MaxJsonDepth = 32;
}
