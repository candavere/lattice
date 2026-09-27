using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// The signal an <see cref="ExternalAgent"/> raises out of
/// <see cref="IAgent.Decide"/> when the protocol exchange has failed, carrying
/// the <see cref="ExternalAgentFault"/> to record.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="IAgent.Decide"/> is synchronous and returns a
/// single <c>AgentAction</c>, and a protocol failure has no action to return:
/// §9.1 requires the failure to become a recorded result, and §9.2 requires it to
/// be a loss rather than a substituted default. Returning a default would let an
/// agent improve its score by crashing, which is the exact failure mode the rule
/// exists to prevent. So <c>Decide</c> throws, and the runner turns the throw into
/// a result — the throw is the transport, not the outcome.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/>, which the repository's
/// existing exception contract already treats as a graceful typed rejection, so
/// nothing outside this assembly needed to learn about the type. It is caught by
/// <see cref="ExternalMatchRunner"/> and MUST NOT escape one: a caller that drives
/// an external agent through <see cref="ScenarioRunner"/> directly instead of
/// through the runner will see it propagate.
/// </para>
/// </remarks>
public sealed class ExternalAgentFaultException : InvalidOperationException
{
    /// <summary>Initializes the exception for <paramref name="fault"/>.</summary>
    public ExternalAgentFaultException(ExternalAgentFault fault)
        : base($"external agent failed at step {fault.Step} with {fault.Reason.ToWireString()}: {fault.Detail}")
        => Fault = fault;

    /// <summary>The failure to record. Carries exactly one §8 reason code.</summary>
    public ExternalAgentFault Fault { get; }
}
