namespace Lattice.Agents.External;

/// <summary>
/// Thrown when the agent process could not be <b>started</b> at all — a missing
/// executable, a path that is not a program, or a host that refused to launch it.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>not</b> one of the fourteen §8 reason codes, and that is
/// the point rather than an omission. Every code in the closed set is a claim
/// about a message that was exchanged or not sent within a time limit, and
/// <c>host_limit</c> is a claim about Lattice's own outbound line. "Lattice was
/// handed a program it cannot run" is a caller and configuration error, and §8
/// forbids inventing a code for it: the set is closed, and a new code is a
/// protocol-2 change.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/>, which the repository's
/// existing exception contract already treats as a graceful typed rejection, so
/// nothing outside this assembly had to learn about the type. It surfaces before
/// any match is played, which is the right time to learn that a
/// <c>--agent-cmd</c> names something that is not a program.
/// </para>
/// </remarks>
public sealed class ExternalAgentLaunchException : InvalidOperationException
{
    /// <summary>Initializes the exception with a diagnostic message.</summary>
    public ExternalAgentLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a diagnostic message and the underlying cause.</summary>
    public ExternalAgentLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
