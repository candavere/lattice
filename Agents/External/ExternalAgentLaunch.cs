using System.Diagnostics;
using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// The launch contract of spec §3.2 (U-9): <b>an argv, never a shell</b>.
/// </summary>
/// <remarks>
/// <para>
/// The caller hands over a <see cref="Program"/> and an argument list that are
/// <b>already split</b>. Lattice adds each argument to
/// <see cref="ProcessStartInfo.ArgumentList"/> one at a time, sets
/// <see cref="ProcessStartInfo.UseShellExecute"/> to <see langword="false"/>,
/// and starts the process. It never builds a command <em>string</em>, never
/// involves a shell or <c>cmd /c</c>, and never concatenates, quotes, escapes, or
/// re-parses an argv into a string and back — so a space in an argument stays one
/// argument and a shell metacharacter (<c>&gt;</c>, <c>|</c>, <c>&amp;</c>,
/// <c>;</c>, <c>*</c>, <c>~</c>, <c>$</c>, backtick) is just bytes with no meaning.
/// </para>
/// <para>
/// How a CLI string such as <c>--agent-cmd "python3 my_agent.py"</c> becomes a
/// program and an argv is <b><see cref="Lattice.Cli.AgentCommandLine"/></b>'s
/// concern and not this type's: it is the splitter that decides, and this type
/// only ever receives the program and the already-split arguments. Everything
/// here holds unchanged whichever splitter produces the argv.
/// </para>
/// <para>
/// The working directory is the <b>caller's</b> current directory — never
/// resolved from the agent's own path — and the environment is <b>inherited</b>,
/// with exactly one variable added: <c>LATTICE_PROTOCOL=1</c>. That variable is
/// informational: it can never make an incompatible agent compatible, because
/// negotiation remains the exact-match handshake of §2. Its value comes from
/// <see cref="ProtocolLimits.Version"/>, the same constant that goes on the wire,
/// so the environment and the handshake cannot disagree.
/// </para>
/// </remarks>
public sealed record ExternalAgentLaunch
{
    /// <summary>
    /// The environment variable Lattice adds to the inherited environment: the
    /// wire version, as informational text (§3.2).
    /// </summary>
    public const string ProtocolVariable = "LATTICE_PROTOCOL";

    /// <summary>Initializes a launch from a program and an already-split argument list.</summary>
    /// <param name="program">The executable to start. Never a command line.</param>
    /// <param name="arguments">
    /// The arguments, already split. Each element is passed through verbatim as
    /// exactly one argv entry.
    /// </param>
    public ExternalAgentLaunch(string program, params string[] arguments)
        : this(program, (IReadOnlyList<string>)arguments)
    {
    }

    /// <summary>Initializes a launch from a program and an already-split argument list.</summary>
    public ExternalAgentLaunch(string program, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
        }

        Program = program;
        Arguments = arguments;
    }

    /// <summary>The executable Lattice starts. A path or a bare name resolved on PATH; never a shell command.</summary>
    public string Program { get; }

    /// <summary>The arguments, already split, one argv entry per element.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// The <see cref="ProcessStartInfo"/> this launch describes, with the §3.2
    /// contract applied: no shell, one argv entry per argument, the caller's
    /// working directory, an inherited environment plus
    /// <see cref="ProtocolVariable"/>.
    /// </summary>
    public ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Program,
            UseShellExecute = false,

            // The caller's current directory, deliberately not derived from the
            // agent's own path: a relative --agent-cmd resolves against where the
            // user invoked Lattice, which is the only reading a user can predict.
            WorkingDirectory = System.Environment.CurrentDirectory,

            // §1: stdin and stdout are the protocol, and stderr is diagnostic.
            // Redirection is what makes them pipes this side can read and write;
            // it is a transport requirement, not a shell requirement, and it is
            // still a redirection under UseShellExecute = false.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // ArgumentList is what makes this an argv. Nothing here is ever
        // concatenated into Arguments, and Arguments is deliberately left unset.
        startInfo.Environment[ProtocolVariable] = ProtocolLimits.Version.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        return startInfo;
    }
}
