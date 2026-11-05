using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

public class BuildIdentityTests
{
    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Lattice.sln")))
        {
            root = Directory.GetParent(root)?.FullName;
        }

        Assert.True(root is not null, "could not locate the repository root.");
        return root!;
    }

    [Fact]
    public void BuildIdentity_IsWritten_Without_A_Shell()
    {
        var project = File.ReadAllLines(Path.Combine(RepoRoot(), "Cli", "Lattice.Cli.csproj"));

        Assert.Contains(
            project,
            line => line.Contains("WriteLinesToFile") && line.Contains("BuildIdentity.g.cs"));
        Assert.DoesNotContain(
            project,
            line => line.Contains("<Exec") && line.Contains("BuildIdentity.g.cs"));
    }

    [Fact]
    public void SourceCommit_IsFullShaOrUnknown()
    {
        var commit = CliApp.SourceCommit;
        Assert.True(
            commit == "unknown" || Regex.IsMatch(commit, "^[0-9a-f]{40}$"),
            $"SourceCommit '{commit}' is not a 40-hex SHA or 'unknown'.");
    }

    [Fact]
    public void InformationalVersion_ContainsSourceCommit()
    {
        Assert.Contains(CliApp.SourceCommit, CliApp.InformationalVersion);
    }
}
