using Lattice.Cli;
using Lattice.Cli.Presentation;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Relocated from the parked TUI test files: the version-string contract as it
/// stood before build-identity stamping. Kept verbatim (plain version, no commit
/// suffix) pending the version-contract decision.
/// </summary>
public class VersionContractTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void TheVersionIsUnchanged(string flag)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(new[] { flag }, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal($"{CliApp.InformationalVersion}{System.Environment.NewLine}", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void TheVersionIsUnchangedTrimmed()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(new[] { "--version" }, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Equal(CliApp.InformationalVersion, stdout.ToString().Trim());
        Assert.Equal("", stderr.ToString());
    }
}