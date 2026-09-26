namespace Lattice.Protocol;

/// <summary>
/// The wire spelling of <see cref="ProtocolReason"/>, and the fault partition
/// that decides scoring (spec §8, §9.3).
/// </summary>
/// <remarks>
/// The two directions are held in one table on purpose. Spec §8 closes the
/// reason set, and a set that can be spelled two ways is not closed: keeping the
/// forward and reverse maps derived from a single source makes it impossible for
/// a code to parse under one spelling and be written under another.
/// </remarks>
public static class ProtocolReasons
{
    private static readonly string[] WireStrings =
    [
        "protocol_mismatch",
        "malformed_json",
        "schema_violation",
        "unknown_field",
        "line_too_long",
        "depth_exceeded",
        "step_mismatch",
        "illegal_action",
        "timeout_handshake",
        "timeout_step",
        "timeout_match",
        "agent_exited",
        "agent_crashed",
        "host_limit",
    ];

    private static readonly Dictionary<ProtocolReason, string> ToWire = BuildForward();
    private static readonly Dictionary<string, ProtocolReason> FromWire = BuildReverse();
    private static readonly HashSet<ProtocolReason> AgentFaults =
    [
        ProtocolReason.ProtocolMismatch,
        ProtocolReason.MalformedJson,
        ProtocolReason.SchemaViolation,
        ProtocolReason.UnknownField,
        ProtocolReason.LineTooLong,
        ProtocolReason.DepthExceeded,
        ProtocolReason.StepMismatch,
        ProtocolReason.IllegalAction,
        ProtocolReason.TimeoutHandshake,
        ProtocolReason.TimeoutStep,
        ProtocolReason.TimeoutMatch,
        ProtocolReason.AgentExited,
        ProtocolReason.AgentCrashed,
    ];

    /// <summary>
    /// Every member of the closed set, in spec-table order. The test suite
    /// asserts that this list, the wire strings, and the spec §8 table agree.
    /// </summary>
    public static IReadOnlyList<ProtocolReason> All { get; } =
        (IReadOnlyList<ProtocolReason>)WireStrings.Select((_, index) => (ProtocolReason)index).ToArray();

    /// <summary>The exact <c>error.reason</c> string for <paramref name="reason"/>.</summary>
    public static string ToWireString(this ProtocolReason reason) =>
        ToWire.TryGetValue(reason, out var wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a member of the closed reason set.");

    /// <summary>
    /// Resolves a wire <c>error.reason</c> string. Forward compatibility is
    /// refused, not guessed, so an unrecognised string is a plain
    /// <see langword="false"/> rather than a best-effort match: no case folding,
    /// no trimming, no prefix matching.
    /// </summary>
    public static bool TryParse(string? wire, out ProtocolReason reason)
    {
        if (wire is not null && FromWire.TryGetValue(wire, out var parsed))
        {
            reason = parsed;
            return true;
        }

        reason = default;
        return false;
    }

    /// <summary>
    /// True for the thirteen codes the external agent is responsible for. Every
    /// one of them is scored as a loss for that agent (spec §9.1) and is counted
    /// in the report's <c>AgentFailures</c> (spec §9.3).
    /// </summary>
    public static bool IsAgentFault(this ProtocolReason reason) => AgentFaults.Contains(reason);

    /// <summary>
    /// True only for <see cref="ProtocolReason.HostLimit"/>. A host fault is
    /// recorded as a void run and is <b>not</b> scored as a loss for the external
    /// agent, and is <b>not</b> counted in <c>AgentFailures</c> (spec §9.1, §9.3).
    /// </summary>
    public static bool IsHostFault(this ProtocolReason reason) => reason == ProtocolReason.HostLimit;

    private static Dictionary<ProtocolReason, string> BuildForward()
    {
        var map = new Dictionary<ProtocolReason, string>(WireStrings.Length);
        for (var index = 0; index < WireStrings.Length; index++)
        {
            map.Add((ProtocolReason)index, WireStrings[index]);
        }

        return map;
    }

    private static Dictionary<string, ProtocolReason> BuildReverse()
    {
        var map = new Dictionary<string, ProtocolReason>(WireStrings.Length, StringComparer.Ordinal);
        for (var index = 0; index < WireStrings.Length; index++)
        {
            map.Add(WireStrings[index], (ProtocolReason)index);
        }

        return map;
    }
}
