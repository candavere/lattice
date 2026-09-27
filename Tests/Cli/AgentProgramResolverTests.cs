using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Program resolution, the second half of §3.3: which file the first argv element
/// actually names.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is separated from splitting because the two answer different
/// questions and fail differently. Splitting is pure and total; resolution reads
/// the filesystem and can come back empty-handed. Keeping them apart is what lets
/// the splitting rule be tested with no process, no platform, and no
/// <c>PATH</c>.
/// </para>
/// <para>
/// The existence check is injected rather than hard-wired, so the search order
/// and the Windows extension rule are testable on every host. A test that depended
/// on what happens to be installed would be a test of the machine.
/// </para>
/// </remarks>
public class AgentProgramResolverTests
{
    [Fact]
    public void A_Program_Containing_A_Separator_Is_Used_As_A_Path()
    {
        var asked = new List<string>();
        var resolved = Resolve("/opt/agents/python3", exists: candidate => candidate == "/opt/agents/python3", asked);

        Assert.Equal("/opt/agents/python3", resolved);

        // Exactly one candidate, and it is the path itself: a path is a path, and
        // also searching for it on PATH would be a second, silent way to run
        // something the caller did not name.
        Assert.Equal(["/opt/agents/python3"], asked);
    }

    [Fact]
    public void A_Relative_Path_Resolves_Against_The_Callers_Directory()
    {
        // §3.2 fixes the working directory as the caller's own, so a relative
        // program is looked for exactly where the user is standing.
        var resolved = Resolve("bin/agent", exists: candidate => candidate == "bin/agent", path: "/usr/bin");
        Assert.Equal("bin/agent", resolved);
    }

    [Fact]
    public void A_Backslash_Makes_A_Windows_Path_A_Path_Even_On_Unix()
    {
        // The separator test is deliberately not platform-conditional: a
        // Windows-style value is a path everywhere, so it is never mistaken for a
        // bare name and searched for on PATH as if the backslashes were part of
        // the file name. On a host where it cannot exist the answer is the honest
        // "not found" rather than a search.
        var asked = new List<string>();
        Assert.Null(Resolve("C:\\agents\\python.exe", exists: nothing => false, asked, path: "/usr/bin"));
        Assert.Equal(["C:\\agents\\python.exe"], asked);
    }

    [Fact]
    public void A_Bare_Name_Is_Searched_On_Path_In_Order()
    {
        // Two host-dependent details have to be spelled the host's way, and both
        // come from the same place: the resolver splits PATH on
        // Path.PathSeparator (';' on Windows) and joins each entry to the program
        // with Path.Combine ('\' on Windows). Hard-coding ':' would make the whole
        // string one directory entry on Windows; hard-coding '/' in the expected
        // candidates would compare a combined path against an uncombined one.
        // The rule under test is the search *order*, which is neither.
        var asked = new List<string>();
        var first = Path.Combine("/usr/bin", "python3");
        var second = Path.Combine("/usr/local/bin", "python3");
        var resolved = Resolve(
            "python3",
            exists: candidate => candidate == second,
            asked,
            path: string.Join(Path.PathSeparator, ["/usr/bin", "/usr/local/bin", "/bin"]));

        Assert.Equal(second, resolved);

        // First match wins, and the search really walked the entries in order.
        Assert.Equal([first, second], asked);
    }

    [Fact]
    public void The_First_Path_Entry_Wins_When_Two_Entries_Have_The_Program()
    {
        var first = Path.Combine("/first", "agent");
        var second = Path.Combine("/second", "agent");

        var resolved = Resolve(
            "agent",
            exists: candidate => candidate == first || candidate == second,
            path: string.Join(Path.PathSeparator, ["/first", "/second"]));

        Assert.Equal(first, resolved);
    }

    [Fact]
    public void On_Windows_Each_Path_Extension_Is_Tried_After_The_Bare_Name()
    {
        // The candidates are built with Path.Combine out of a plain directory name,
        // so the test asserts the extension *order* — the rule under test — and
        // nothing about a separator or a drive letter this host may not have.
        // (A Windows-shaped PATH entry cannot be used here: on Unix a colon is
        // the PATH separator, so `C:\tools` would split in the wrong place.)
        const string directory = "tools";
        var wanted = Path.Combine(directory, "agent.EXE");

        var asked = new List<string>();
        var resolved = Resolve(
            "agent",
            exists: candidate => candidate == wanted,
            asked,
            path: directory,
            extensions: [".COM", ".EXE", ".BAT"]);

        Assert.Equal(wanted, resolved);

        // The bare name is tried first, then the extensions in PATHEXT order.
        Assert.Equal(
            [
                Path.Combine(directory, "agent"),
                Path.Combine(directory, "agent.COM"),
                wanted,
            ],
            asked);
    }

    [Fact]
    public void An_Empty_Path_Entry_Means_The_Callers_Directory()
    {
        // A bare or trailing separator on PATH is the shell convention for "here",
        // and dropping the entry instead would silently search somewhere else.
        var here = Path.Combine(".", "agent");
        var resolved = Resolve(
            "agent",
            exists: candidate => candidate == here,
            path: string.Join(Path.PathSeparator, [string.Empty, "nowhere"]),
            emptyEntryIsCurrentDirectory: true);

        Assert.Equal(here, resolved);
    }

    [Fact]
    public void A_Program_That_Is_Not_Found_Names_Itself_In_The_Reason()
    {
        var reason = NotFound("python9", exists: _ => false, path: "/usr/bin");
        Assert.Contains("python9", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Empty_Path_Resolves_Nothing_And_Says_So()
    {
        // No PATH at all is a different failure from a PATH that lacks the
        // program, and the reason says which — "not found" when there was
        // nowhere to look would send a reader looking in the wrong place.
        var reason = NotFound("agent", exists: _ => true, path: null);
        Assert.Contains("PATH", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Real_Environment_Resolves_The_Dotnet_Host_It_Is_Itself_Running_On()
    {
        // One test against the real filesystem, so the injected checks above cannot
        // drift from what the CLI actually does.
        //
        // It is conditional on the host being the muxer rather than the apphost,
        // because the assertion is that a bare name resolves through PATH -- and a
        // host launched by absolute path proves nothing about PATH. Everything else
        // in this file is host-independent; this is the one place a machine
        // dependency is unavoidable, so it is confined to a single test that says
        // what it needs.
        var host = System.Environment.ProcessPath;
        Assert.NotNull(host);

        if (!string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // The apphost case: the same resolution asked as a path rather than as a
            // bare name, which is the other half of the rule and needs no PATH.
            Assert.True(
                AgentProgramResolver.TryResolve(host, out var asPath, out var pathReason),
                $"the running host did not resolve as a path: {pathReason}");
            Assert.Equal(host, asPath);
            return;
        }

        var resolved = AgentProgramResolver.TryResolve(
            Path.GetFileNameWithoutExtension(host),
            out var path,
            out var reason);

        Assert.True(resolved, $"the running host's own executable name did not resolve on PATH: {reason}");
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    private static string? Resolve(
        string program,
        Func<string, bool> exists,
        List<string>? asked = null,
        string? path = null,
        IReadOnlyList<string>? extensions = null,
        bool emptyEntryIsCurrentDirectory = false) =>
        AgentProgramResolver.TryResolve(
            program,
            exists,
            path,
            extensions ?? [],
            emptyEntryIsCurrentDirectory,
            out var resolved,
            out _,
            asked)
            ? resolved
            : null;

    private static string NotFound(string program, Func<string, bool> exists, string? path) =>
        AgentProgramResolver.TryResolve(program, exists, path, [], false, out var resolved, out var reason)
            ? throw new InvalidOperationException($"'{program}' was expected not to resolve, but resolved to '{resolved}'.")
            : reason;
}
