using System.Text;
using Lattice.Cli.Presentation;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage 2: saving a result's full raw output. The file holds the complete
/// streams (memory head plus spool), never just what fit on screen, and a
/// failure is reported, never thrown.
/// </summary>
public class ResultArtifactWriterTests
{
    [Fact]
    public void SaveWritesHeadPlusSpoolForBothStreams()
    {
        using var temp = new TempDir();
        var spool = Path.Combine(temp.Path, "stdout.log");
        File.WriteAllText(spool, "SPILLED\n");

        var outcome = ResultArtifactWriter.SaveRaw(
            temp.Path,
            "lattice-console-generate-1.log",
            "HEAD\n",
            spool,
            "ERR\n",
            null,
            ["# command: lattice generate --seed 42"]);

        Assert.True(outcome.Success, outcome.Reason);
        Assert.Equal(Path.Combine(temp.Path, "lattice-console-generate-1.log"), outcome.Path);
        var text = File.ReadAllText(outcome.Path!);
        Assert.Contains("HEAD\nSPILLED", text, StringComparison.Ordinal);
        Assert.Contains("ERR", text, StringComparison.Ordinal);
        Assert.Contains("# command: lattice generate --seed 42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveWithoutSpoolWritesHeadsOnly()
    {
        using var temp = new TempDir();

        var outcome = ResultArtifactWriter.SaveRaw(temp.Path, "r.log", "OUT\n", null, "", null, []);

        Assert.True(outcome.Success, outcome.Reason);
        Assert.Contains("OUT", File.ReadAllText(outcome.Path!), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveBumpsTheNameOnCollision()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "r.log"), "old");

        var outcome = ResultArtifactWriter.SaveRaw(temp.Path, "r.log", "new", null, "", null, []);

        Assert.True(outcome.Success, outcome.Reason);
        Assert.Equal(Path.Combine(temp.Path, "r-2.log"), outcome.Path);
        Assert.Contains("new", File.ReadAllText(outcome.Path!), StringComparison.Ordinal);
        Assert.Contains("old", File.ReadAllText(Path.Combine(temp.Path, "r.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveFailureIsReportedNotThrown()
    {
        using var temp = new TempDir();
        var badDir = Path.Combine(temp.Path, "\0invalid");

        var outcome = ResultArtifactWriter.SaveRaw(badDir, "r.log", "OUT", null, "", null, []);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Reason);
        Assert.Null(outcome.Path);
    }

    [Fact]
    public void SaveFailsWhenRecordedSpoolIsMissing()
    {
        using var temp = new TempDir();
        var missingSpool = Path.Combine(temp.Path, "missing-stdout.log");

        var outcome = ResultArtifactWriter.SaveRaw(
            temp.Path,
            "r.log",
            "HEAD\n",
            missingSpool,
            "ERR\n",
            null,
            []);

        Assert.False(outcome.Success, outcome.Reason);
        Assert.NotNull(outcome.Reason);
        Assert.Null(outcome.Path);
        Assert.False(File.Exists(Path.Combine(temp.Path, "r.log")));
    }

    [Fact]
    public void SaveFailsWhenRecordedSpoolCannotBeRead()
    {
        using var temp = new TempDir();
        // A directory is reported by File.Exists as existing but cannot be read
        // as a file: deterministic on this platform, no permission dependence.
        var unreadableSpool = temp.Path;
        var subDir = Path.Combine(temp.Path, "spool-dir");
        Directory.CreateDirectory(subDir);

        var outcome = ResultArtifactWriter.SaveRaw(
            temp.Path,
            "r.log",
            "HEAD\n",
            subDir,
            "ERR\n",
            null,
            []);

        Assert.False(outcome.Success, outcome.Reason);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("unreadable", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(outcome.Path);
        Assert.False(File.Exists(Path.Combine(temp.Path, "r.log")));
    }

    [Fact]
    public void SaveFailsWhenRecordedStderrSpoolIsMissing()
    {
        using var temp = new TempDir();

        var outcome = ResultArtifactWriter.SaveRaw(
            temp.Path,
            "r.log",
            "OUT\n",
            null,
            "ERR\n",
            Path.Combine(temp.Path, "missing-stderr.log"),
            []);

        Assert.False(outcome.Success, outcome.Reason);
        Assert.NotNull(outcome.Reason);
        Assert.Null(outcome.Path);
        Assert.False(File.Exists(Path.Combine(temp.Path, "r.log")));
    }

    [Fact]
    public void SaveFailsWhenRecordedStderrSpoolCannotBeRead()
    {
        using var temp = new TempDir();
        var subDir = Path.Combine(temp.Path, "stderr-spool-dir");
        Directory.CreateDirectory(subDir);

        var outcome = ResultArtifactWriter.SaveRaw(
            temp.Path,
            "r.log",
            "OUT\n",
            null,
            "ERR\n",
            subDir,
            []);

        Assert.False(outcome.Success, outcome.Reason);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("unreadable", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(outcome.Path);
        Assert.False(File.Exists(Path.Combine(temp.Path, "r.log")));
    }

    private sealed class TempDir : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lattice-save-" + Guid.NewGuid().ToString("N"));

        internal TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
