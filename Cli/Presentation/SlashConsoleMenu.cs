using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The slash console's menu model over <see cref="LaunchpadCatalog"/>: the nine research
/// commands with the catalog's own one-line descriptions, plus <c>/exit</c> (alias
/// <c>/quit</c>) to leave. Pure: no terminal, no clock, no processes.
/// </summary>
/// <remarks>
/// The nine entries reuse the catalog's names and summaries verbatim, in catalog order,
/// so no flag is invented, renamed or hidden here. Filtering is a case-insensitive
/// prefix match on the command name; the exit entry additionally matches its <c>quit</c>
/// alias. Command lines are derived by stripping the leading slash, so
/// <c>/simulate --seed 42</c> becomes <c>lattice simulate --seed 42</c>.
/// Validation itself stays with the Launchpad catalog and the CLI parser at run time.
/// </remarks>
public static class SlashConsoleMenu
{
    /// <summary>The console command that leaves.</summary>
    public const string ExitName = "/exit";

    /// <summary>The alias that also leaves.</summary>
    public const string QuitAlias = "/quit";

    /// <summary>What the exit row says. A console row, not a catalog description.</summary>
    public const string ExitDescription = "Leave the console. (alias /quit)";

    /// <summary>
    /// Every menu entry in the order the console shows them: the catalog's nine in
    /// catalog order, then the exit row.
    /// </summary>
    public static IReadOnlyList<SlashMenuItem> AllEntries()
    {
        var entries = new List<SlashMenuItem>(LaunchpadCatalog.Commands.Count + 1);
        foreach (var command in LaunchpadCatalog.Commands)
        {
            entries.Add(new SlashMenuItem("/" + command.Name, command.Summary));
        }

        entries.Add(new SlashMenuItem(ExitName, ExitDescription));
        return entries;
    }

    /// <summary>
    /// The entries matching <paramref name="input"/> as the user types. The filter is the
    /// first token after the leading slash, matched as a case-insensitive prefix on the
    /// command name; an empty filter matches everything.
    /// </summary>
    public static IReadOnlyList<SlashMenuItem> Filter(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var all = AllEntries();
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return [];
        }

        var token = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (token.Length == 0)
        {
            return all;
        }

        return all
            .Where(entry =>
                entry.Name[1..].StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                (entry.Name == ExitName && "quit".StartsWith(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// The <c>lattice ...</c> command line for <paramref name="input"/>, for display under
    /// the input bar. Strips one leading slash and trims; returns <c>lattice</c> for bare <c>/</c>.
    /// </summary>
    public static string ToCommandLine(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rest = input.StartsWith("/", StringComparison.Ordinal) ? input[1..].Trim() : input.Trim();
        return rest.Length == 0 ? "lattice" : "lattice " + rest;
    }

    /// <summary>Whether <paramref name="input"/> asks to leave (<c>/exit</c> or <c>/quit</c> alone).</summary>
    public static bool IsExit(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var token = input.Trim();
        return token.Equals(ExitName, StringComparison.OrdinalIgnoreCase)
            || token.Equals(QuitAlias, StringComparison.OrdinalIgnoreCase);
    }
}
