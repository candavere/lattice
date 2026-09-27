using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// One recorded protocol failure, carrying the single §8 reason code, where it
/// happened, and the agent's stderr tail.
/// </summary>
/// <param name="Reason">
/// Exactly one code from the closed §8 set. Never inferred, never defaulted, and
/// never one of the fourteen that is not the one detected: §8.4's precedence is
/// applied by the runner that raises this, not here.
/// </param>
/// <param name="Detail">Human-readable text. Diagnostic only, exactly as §8.1 makes <c>error.detail</c>.</param>
/// <param name="Step">
/// The 0-based wire step the failure was detected on, or <c>-1</c> for a failure
/// detected in the handshake — before step 0 exists.
/// </param>
/// <param name="StderrTail">
/// The last 64 KiB the agent wrote to stderr when the failure was recorded, or
/// <see cref="string.Empty"/>. Populated for <c>agent_crashed</c>,
/// <c>agent_exited</c>, and the three <c>timeout_*</c> codes, which are the
/// cases where a human needs to know what the agent was doing (§1.1). Never
/// parsed, and never a statistic.
/// </param>
public sealed record ExternalAgentFault(
    ProtocolReason Reason,
    string Detail,
    int Step,
    string StderrTail)
{
    /// <summary>True when this failure is the agent's fault, and therefore a loss for it (§9.1).</summary>
    public bool IsAgentFault => Reason.IsAgentFault();

    /// <summary>True when this failure is Lattice's own, and therefore a void run rather than a loss (§9.1).</summary>
    public bool IsHostFault => Reason.IsHostFault();

    /// <summary>
    /// The <c>TerminationReason</c> string this failure records: the §8 wire
    /// spelling, the same string an in-process environment termination uses
    /// (<c>"resources-exhausted"</c>, <c>"tick-limit"</c>), so one nullable field
    /// tells a reader both what kind of ending it was (§9.1).
    /// </summary>
    public string TerminationReason => Reason.ToWireString();
}
