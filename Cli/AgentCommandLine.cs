using System.Text;

namespace Lattice.Cli;

/// <summary>
/// The spec §3.3 splitter: the one place a <c>--agent-cmd</c> <b>string</b>
/// becomes a program and an argument list, with no shell anywhere in it.
/// </summary>
/// <remarks>
/// <para>
/// The rule is fixed in the spec and implemented here exactly as written:
/// unquoted whitespace separates, a double quote groups, a backslash escapes
/// only <c>"</c> and <c>\</c> inside a group and is an ordinary character
/// everywhere else, and there is no globbing, no variable expansion, and no
/// single-quote semantics. Two inputs are usage errors rather than an argv: an
/// unterminated quote, and an empty command.
/// </para>
/// <para>
/// <b>Why a fixed splitter rather than a shell.</b> §3.2 makes the launch an argv
/// precisely so that no argument can become a command; a shell would undo the
/// only guarantee the launch contract has. It is also the only way the behaviour
/// is the same on all three CI operating systems instead of inheriting three
/// different shells' rules — which is why the Windows cases in the tests are
/// ordinary unit tests rather than a platform branch.
/// </para>
/// <para>
/// The function is pure: no environment, no filesystem, no globals, and no
/// exception on bad input. <see cref="TrySplit"/> returns the reason it refused,
/// and turning that into a message and an exit status is the caller's job — so
/// the rule can be tested without a process, a writer, or an exit code.
/// </para>
/// </remarks>
public static class AgentCommandLine
{
    /// <summary>
    /// Splits <paramref name="commandLine"/> into an argv by the §3.3 rule.
    /// </summary>
    /// <param name="commandLine">The raw <c>--agent-cmd</c> value, or <see langword="null"/>.</param>
    /// <param name="argv">
    /// The elements, in order, with the program first. Empty when the split fails;
    /// never partially filled, so a caller cannot mistake a half-split command
    /// for a good one.
    /// </param>
    /// <param name="reason">
    /// Why the split was refused, phrased for a human reading stderr. Empty on
    /// success.
    /// </param>
    /// <returns>True when the command line yielded at least one non-empty program.</returns>
    public static bool TrySplit(string? commandLine, out string[] argv, out string reason)
    {
        argv = [];
        reason = string.Empty;

        if (commandLine is null)
        {
            reason = "the flag requires a command line.";
            return false;
        }

        var elements = new List<string>();
        var current = new StringBuilder();
        var inGroup = false;
        var inElement = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (inGroup)
            {
                // The only two escapes a group has. Anything else after a
                // backslash is that character, backslash included in the pair.
                if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\')
                {
                    current.Append(commandLine[i + 1]);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inGroup = false;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c == '"')
            {
                inGroup = true;
                inElement = true;
                continue;
            }

            // Space and tab, and only those two: a newline in a command line is a
            // byte in an argument, not a separator, so the rule cannot depend on
            // how a platform happens to treat line endings.
            if (c is ' ' or '\t')
            {
                if (inElement)
                {
                    elements.Add(current.ToString());
                    current.Clear();
                    inElement = false;
                }

                continue;
            }

            current.Append(c);
            inElement = true;
        }

        if (inGroup)
        {
            reason = "the command line has an unterminated double quote.";
            return false;
        }

        if (inElement)
        {
            elements.Add(current.ToString());
        }

        if (elements.Count == 0)
        {
            reason = "the command line is empty.";
            return false;
        }

        if (elements[0].Length == 0)
        {
            reason = "the command line has no program to run.";
            return false;
        }

        argv = [.. elements];
        return true;
    }

    /// <summary>
    /// Splits <paramref name="commandLine"/> or throws the usage error a caller
    /// reports as exit status 2.
    /// </summary>
    /// <param name="commandLine">The raw <c>--agent-cmd</c> value.</param>
    /// <returns>The argv, program first.</returns>
    /// <exception cref="UsageError">
    /// The command line cannot be split into a program and arguments. No match is
    /// played and no artifact is written when this is thrown (§3.3).
    /// </exception>
    public static string[] Require(string? commandLine)
    {
        if (!TrySplit(commandLine, out var argv, out var reason))
        {
            throw new UsageError($"--agent-cmd could not be split: {reason}");
        }

        return argv;
    }
}
