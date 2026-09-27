using System.Runtime.InteropServices;

namespace Lattice.Cli;

/// <summary>
/// Resolves the program an <c>--agent-cmd</c> argv names, once, before any match
/// is played (spec §3.3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is two sentences long and total: a value containing a directory
/// separator is a <b>path</b> and is used as one; anything else is a <b>bare
/// name</b> and is searched for on <c>PATH</c>, trying each <c>PATHEXT</c>
/// extension on Windows. There is no third case, no fallback from a path to a
/// search, and no search from a path — in particular a name that is not on
/// <c>PATH</c> and is not a file is a usage error rather than something Lattice
/// tries to interpret.
/// </para>
/// <para>
/// <b>Why this happens before the first match.</b> A program that does not
/// resolve is not a match result and not a §8 reason code: no agent ever spoke, so
/// there is no agent behaviour to attribute anything to. Discovering that
/// mid-suite would leave two bad options — score it as a loss, which blames the
/// agent for Lattice's own <c>PATH</c>, or retry, which §9.1 forbids — so the
/// resolution is done here instead, where the answer costs nothing and invents no
/// score.
/// </para>
/// <para>
/// The filesystem and the environment are injected, so the search order and the
/// Windows extension rule are testable on any host and the tests cannot depend on
/// what happens to be installed.
/// </para>
/// </remarks>
public static class AgentProgramResolver
{
    /// <summary>
    /// Resolves <paramref name="program"/> against the real filesystem and the
    /// real <c>PATH</c> of the current process.
    /// </summary>
    /// <param name="program">The first argv element, exactly as the user wrote it.</param>
    /// <param name="resolved">The path to start, or <see langword="null"/> on failure.</param>
    /// <param name="reason">Why it was refused, phrased for stderr. Empty on success.</param>
    /// <returns>True when the program names something that exists.</returns>
    public static bool TryResolve(string program, out string? resolved, out string reason) =>
        TryResolve(
            program,
            File.Exists,
            System.Environment.GetEnvironmentVariable("PATH"),
            DefaultExtensions(),
            emptyEntryIsCurrentDirectory: false,
            out resolved,
            out reason);

    /// <summary>
    /// Resolves <paramref name="program"/> against an injected filesystem and
    /// <c>PATH</c>.
    /// </summary>
    /// <param name="program">The first argv element, exactly as the user wrote it.</param>
    /// <param name="exists">Whether a candidate path names a file.</param>
    /// <param name="path">The search path, or <see langword="null"/> when unset.</param>
    /// <param name="extensions">
    /// Suffixes to try after the bare name, in order. Empty on every platform but
    /// Windows, where it is the split of <c>PATHEXT</c>.
    /// </param>
    /// <param name="emptyEntryIsCurrentDirectory">
    /// Whether an empty <paramref name="path"/> entry means the caller's own
    /// directory rather than nothing at all.
    /// </param>
    /// <param name="resolved">The path to start, or <see langword="null"/> on failure.</param>
    /// <param name="reason">Why it was refused, phrased for stderr. Empty on success.</param>
    /// <param name="examined">
    /// Every candidate tried, in order. Optional, and filled for tests and
    /// diagnostics; the resolution does not depend on it.
    /// </param>
    /// <returns>True when the program names something that exists.</returns>
    public static bool TryResolve(
        string program,
        Func<string, bool> exists,
        string? path,
        IReadOnlyList<string> extensions,
        bool emptyEntryIsCurrentDirectory,
        out string? resolved,
        out string reason,
        IList<string>? examined = null)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(extensions);

        resolved = null;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(program))
        {
            reason = "there is no program to resolve.";
            return false;
        }

        if (HasDirectorySeparator(program))
        {
            // A path is used as written, relative to the caller's current
            // directory (§3.2). It is never also searched for on PATH: a value
            // that names a location and a value that names a program are different
            // requests, and quietly answering one with the other would run
            // something the caller did not name.
            if (Try(program, exists, examined))
            {
                resolved = program;
                return true;
            }

            reason = $"'{program}' does not exist.";
            return false;
        }

        if (string.IsNullOrEmpty(path))
        {
            reason = $"'{program}' was not found: the PATH environment variable is not set, so there is nowhere to search for it.";
            return false;
        }

        foreach (var entry in path.Split(Path.PathSeparator))
        {
            var directory = entry.Length == 0
                ? (emptyEntryIsCurrentDirectory ? "." : string.Empty)
                : entry;

            if (directory.Length == 0)
            {
                continue;
            }

            if (Try(Path.Combine(directory, program), exists, examined))
            {
                resolved = Path.Combine(directory, program);
                return true;
            }

            foreach (var extension in extensions)
            {
                if (Try(Path.Combine(directory, program + extension), exists, examined))
                {
                    resolved = Path.Combine(directory, program + extension);
                    return true;
                }
            }
        }

        reason = $"'{program}' was not found on PATH.";
        return false;
    }

    /// <summary>
    /// Whether a program value names a location rather than a program. Both
    /// separators count on every platform, deliberately: a value carrying
    /// <c>C:\</c> is a path even when Lattice is running somewhere that has no
    /// such path, and the honest answer there is "does not exist" rather than a
    /// search for a file whose name happens to contain backslashes.
    /// </summary>
    private static bool HasDirectorySeparator(string program) =>
        program.Contains('/') || program.Contains('\\');

    private static bool Try(string candidate, Func<string, bool> exists, IList<string>? examined)
    {
        examined?.Add(candidate);
        return exists(candidate);
    }

    /// <summary>
    /// The extensions to try after a bare name: the split of <c>PATHEXT</c> on
    /// Windows, and nothing anywhere else. A Unix host has no equivalent, because
    /// a Unix executable is named exactly, with no extension to guess at.
    /// </summary>
    private static IReadOnlyList<string> DefaultExtensions()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        var pathExt = System.Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrEmpty(pathExt))
        {
            return [".COM", ".EXE", ".BAT", ".CMD"];
        }

        return pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }
}
