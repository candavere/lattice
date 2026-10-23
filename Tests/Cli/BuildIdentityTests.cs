using System.Text.RegularExpressions;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

public class BuildIdentityTests
{
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
