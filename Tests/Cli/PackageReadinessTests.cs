using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Package readiness: the version-parity set includes the production TUI and
/// Protocol projects, and production projects stay dependency-free.
/// </summary>
public class PackageReadinessTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string VersionOf(string relative)
    {
        var text = File.ReadAllText(Path.Combine(Root(), relative));
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success, $"{relative} declares no <Version>");
        return m.Groups[1].Value.Trim();
    }

    [Fact]
    public void AllVersionedProjectsAgreeIncludingProtocolAndTui()
    {
        var projects = new[]
        {
            "Environment/Lattice.Environment.csproj",
            "Agents/Lattice.Agents.csproj",
            "Protocol/Lattice.Protocol.csproj",
            "Tui/Lattice.Tui.csproj",
            "Trajectories/Lattice.Trajectories.csproj",
            "Analytics/Lattice.Analytics.csproj",
            "Visualization/Lattice.Visualization.csproj",
            "Cli/Lattice.Cli.csproj",
            "Generator/Lattice.Generator.csproj",
            "Tests/Lattice.Tests.csproj",
        };
        var versions = projects.Select(VersionOf).Distinct().ToArray();
        Assert.Single(versions);
    }

    [Fact]
    public void ReleaseParityGateListsProtocolAndTui()
    {
        var yml = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "release.yml"));
        Assert.Contains("Protocol/Lattice.Protocol.csproj", yml, StringComparison.Ordinal);
        Assert.Contains("Tui/Lattice.Tui.csproj", yml, StringComparison.Ordinal);
        Assert.Contains("The 10 production/test projects", yml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionProjectsDeclareNoPackageReferences()
    {
        var production = new[]
        {
            "Environment/Lattice.Environment.csproj",
            "Agents/Lattice.Agents.csproj",
            "Protocol/Lattice.Protocol.csproj",
            "Tui/Lattice.Tui.csproj",
            "Trajectories/Lattice.Trajectories.csproj",
            "Analytics/Lattice.Analytics.csproj",
            "Visualization/Lattice.Visualization.csproj",
            "Cli/Lattice.Cli.csproj",
            "Generator/Lattice.Generator.csproj",
        };
        foreach (var proj in production)
        {
            var text = File.ReadAllText(Path.Combine(Root(), proj));
            Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
        }
    }
}
