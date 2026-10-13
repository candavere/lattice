using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// What committing a console input line means, parsed over <see cref="LaunchpadCatalog"/>:
/// leave, watch a screen, run a captured command, run the raw argv for CLI byte parity,
/// or report a refusal. Pure: no terminal, no clock, no processes.
/// </summary>
/// <remarks>
/// The mode words are the catalog's own way names (<c>screen</c>/<c>run</c>, with
/// <c>watch</c> as the display alias for <c>screen</c>); omitted, the catalog default
/// (<see cref="LaunchpadCommand.Ways"/> first) applies. Splitting reuses the documented
/// <c>--agent-cmd</c> rule (unquoted whitespace separates, double quotes group), so the
/// console and the CLI agree on what an argv is on all three operating systems.
/// Validation beyond the catalog's own ways is the child's own CLI parser: unknown
/// commands run raw so their exact unknown-command bytes and status are preserved.
/// </remarks>
public static class SlashConsoleCommit
{
    /// <summary>
    /// Parses a committed input line into how it runs, the child argv, and the display line.
    /// </summary>
    public static CommitPlan ParseCommit(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (SlashConsoleMenu.IsExit(input))
        {
            return new CommitPlan(CommitKind.Exit, [], "lattice", null);
        }

        var stripped = input.StartsWith("/", StringComparison.Ordinal) ? input[1..].Trim() : input.Trim();
        if (!AgentCommandLine.TrySplit(stripped, out var tokens, out var reason))
        {
            return new CommitPlan(CommitKind.Error, [], "lattice", $"could not split input: {reason}");
        }

        var command = LaunchpadCatalog.Commands.FirstOrDefault(c =>
            c.Name.Equals(tokens[0], StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            // Unknown on purpose: the child CLI reports the exact unknown-command
            // bytes and status, so parity needs no replication here.
            return new CommitPlan(CommitKind.Raw, tokens, "lattice " + stripped, null);
        }

        var tail = tokens[1..];
        var mode = command.ModeNamedBy(command.Ways[0]) == RunMode.Screen ? CommitKind.Screen : CommitKind.Run;
        if (tail.Length > 0 && IsModeWord(tail[0]))
        {
            var askedScreen = !tail[0].Equals("run", StringComparison.OrdinalIgnoreCase);

            if (command.Ways.Count == 1 && command.Ways[0] == "screen" && !askedScreen)
            {
                return new CommitPlan(CommitKind.Error, [], "lattice " + stripped, $"{command.Name} can only be watched, not recorded.");
            }

            if (command.Ways.Count == 1 && command.Ways[0] == "run" && askedScreen)
            {
                return new CommitPlan(CommitKind.Error, [], "lattice " + stripped, $"{command.Name} cannot be watched, only run.");
            }

            mode = askedScreen ? CommitKind.Screen : CommitKind.Run;
            tail = tail[1..];
        }

        var argv = mode == CommitKind.Screen
            ? new[] { "tui", command.Name }.Concat(tail).ToArray()
            : new[] { command.Name }.Concat(tail).ToArray();
        return new CommitPlan(mode, argv, "lattice " + stripped, null);
    }

    private static bool IsModeWord(string token) =>
        token.Equals("screen", StringComparison.OrdinalIgnoreCase)
        || token.Equals("watch", StringComparison.OrdinalIgnoreCase)
        || token.Equals("run", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How a committed input line runs.</summary>
public enum CommitKind
{
    /// <summary>Leave the console.</summary>
    Exit,

    /// <summary>Watch: leave the alternate screen and run the <c>tui</c> argv inherited.</summary>
    Screen,

    /// <summary>Run: capture the argv as a child and show it in the result panel.</summary>
    Run,

    /// <summary>Unknown command: run the raw argv captured, for exact CLI parity.</summary>
    Raw,

    /// <summary>Refused without running: the message with the usage status.</summary>
    Error,
}

/// <summary>One parsed commit: how it runs, the child argv, the display line, or the refusal.</summary>
/// <param name="Kind">How it runs.</param>
/// <param name="Arguments">The child argv (empty for <see cref="CommitKind.Exit"/> and <see cref="CommitKind.Error"/>).</param>
/// <param name="DisplayLine">The <c>lattice ...</c> line it is shown as.</param>
/// <param name="ErrorMessage">The refusal, for <see cref="CommitKind.Error"/>.</param>
public sealed record CommitPlan(CommitKind Kind, string[] Arguments, string DisplayLine, string? ErrorMessage);
