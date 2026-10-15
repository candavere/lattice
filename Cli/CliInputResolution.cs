namespace Lattice.Cli;

/// <summary>
/// Pure input-resolution for the two commands that take a path two ways.
/// Extracted from <c>CliApp</c> so the form and the console share the same
/// presence and arity rules without re-parsing; the CLI calls these and its
/// bytes and status are unchanged.
/// </summary>
public static class CliInputResolution
{
    /// <summary>
    /// Resolves the replay trajectory path from a parsed flag set and
    /// positionals. Both supplied is an arity error naming the positional;
    /// neither is the CLI's missing-path usage error.
    /// </summary>
    public static string ResolveReplayTrajectory(
        Dictionary<string, string> flags,
        List<string> positionals)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(positionals);

        var trajectoryPath = flags.TryGetValue("--trajectory", out var flaggedPath)
            ? flaggedPath
            : positionals.Count == 1 ? positionals[0] : null;
        if (trajectoryPath is null)
        {
            throw new UsageError(
                "missing trajectory path (pass a positional path or --trajectory <file>).");
        }

        if (positionals.Count > (flags.ContainsKey("--trajectory") ? 0 : 1))
        {
            throw new UsageError($"unexpected argument '{positionals[^1]}'.");
        }

        return trajectoryPath;
    }

    /// <summary>
    /// Resolves the validate-scenario descriptor path. The <c>--out</c> flag
    /// names the same path as the positional; both supplied is an arity error
    /// naming the positional, neither is the CLI's missing-path usage error.
    /// </summary>
    public static string ResolveValidateScenarioPath(
        Dictionary<string, string> flags,
        List<string> positionals)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(positionals);

        var allowedPositionals = flags.ContainsKey("--out") ? 0 : 1;
        if (positionals.Count > allowedPositionals)
        {
            throw new UsageError(
                $"unexpected argument '{positionals[allowedPositionals]}'.");
        }

        var path = flags.TryGetValue("--out", out var flagged)
            ? flagged
            : positionals.Count == 1 ? positionals[0] : null;
        if (path is null)
        {
            throw new UsageError(
                "missing scenario path (pass a positional path or --out <file>).");
        }

        return path;
    }
}
