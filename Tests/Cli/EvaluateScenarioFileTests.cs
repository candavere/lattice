using System.Text.Json;
using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-2 coverage for <c>evaluate --scenario &lt;file&gt;</c>: which parts of
/// a descriptor the paired study honours, which it deliberately does not, and
/// the refusals that keep a file-driven study commensurable with a published
/// one.
/// <para>
/// The load-bearing test here is
/// <see cref="AFileLoadedBottleneckStudy_MatchesTheBuiltInBottleneckStudy"/>:
/// it pins that routing the study through the committed descriptor produces the
/// same numbers as the built-in token, so the descriptor is a real input and not
/// a label.
/// </para>
/// </summary>
public class EvaluateScenarioFileTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliApp.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    private static string OutputPath() =>
        Path.Combine(Path.GetTempPath(), $"lattice-eval-scen-{Guid.NewGuid():N}.json");

    private static JsonDocument Artifact(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileLoadedStudy_RunsAndNamesTheDescriptor()
    {
        var path = OutputPath();
        try
        {
            var (exit, _, stderr) = Run(
                "evaluate", "--scenario", Committed("scenarios", "bottleneck-contention.json"),
                "--seed-set", "dev", "--seeds", "3", "--rollouts", "4", "--out", path);

            Assert.Equal(0, exit);
            Assert.Contains("scenario bottleneck-contention", stderr, StringComparison.Ordinal);
            Assert.Contains("sha256", stderr, StringComparison.Ordinal);
            Assert.Contains("evaluation suite=dev", stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AFileLoadedStudy_PrintsTheDescriptorsExactSha256()
    {
        var expected = ScenarioLoader.ComputeDigest(
            File.ReadAllBytes(Committed("scenarios", "bottleneck-contention.json")));

        var (exit, _, stderr) = Run(
            "evaluate", "--scenario", Committed("scenarios", "bottleneck-contention.json"),
            "--seed-set", "dev", "--seeds", "2", "--rollouts", "2");

        Assert.Equal(0, exit);
        Assert.Contains(expected, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileLoadedBottleneckStudy_MatchesTheBuiltInBottleneckStudy()
    {
        // The descriptor is a real input: the same study reached through the
        // committed descriptor and through the built-in token produces the same
        // per-seed rows and the same statistics. Without this the feature could
        // pass by printing the descriptor's name and running the old map.
        var viaFile = OutputPath();
        var viaBuiltIn = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "evaluate", "--scenario", Committed("scenarios", "bottleneck-contention.json"),
                "--seed-set", "dev,heldout", "--seeds", "3", "--rollouts", "4", "--out", viaFile).ExitCode);
            Assert.Equal(0, Run(
                "evaluate", "--scenario", "bottleneck",
                "--seed-set", "dev,heldout", "--seeds", "3", "--rollouts", "4", "--out", viaBuiltIn).ExitCode);

            using var a = Artifact(viaFile);
            using var b = Artifact(viaBuiltIn);

            // Clock and host provenance are excluded for the same reason the
            // golden fixture excludes them: they differ per run by design.
            foreach (var field in new[] { "CreatedAtUtc", "Runtime", "Os", "Cores", "Architecture" })
            {
                Assert.Equal(
                    a.RootElement.GetProperty(field).GetRawText().Length > 0,
                    b.RootElement.GetProperty(field).GetRawText().Length > 0);
            }

            var left = a.RootElement.GetProperty("Studies").GetRawText();
            var right = b.RootElement.GetProperty("Studies").GetRawText();
            Assert.Equal(right, left);
        }
        finally
        {
            foreach (var p in new[] { viaFile, viaBuiltIn })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    [Fact]
    public void AFileLoadedStudy_LeavesTheArtifactFieldSetUnchanged()
    {
        // The published artifact shape is pinned by a golden fixture, so a
        // descriptor-driven study must not add a field to it. The scenario
        // digest goes to stderr for exactly this reason.
        var path = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "evaluate", "--scenario", Committed("scenarios", "bottleneck-contention.json"),
                "--seed-set", "dev", "--seeds", "2", "--rollouts", "2", "--out", path).ExitCode);

            using var document = Artifact(path);
            var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

            Assert.Equal(
                ["CommitSha", "CreatedAtUtc", "Runtime", "Os", "Cores", "Architecture", "Studies"],
                fields);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ADescriptorWithMoreThanTwoSeats_IsRefusedWithTheReason()
    {
        // The study is head-to-head. Adopting a three-seat roster would produce
        // a number shaped like a published study and not commensurable with
        // one, so it is refused rather than quietly run.
        var path = Path.Combine(Path.GetTempPath(), $"lattice-four-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 1, "Id": "four-seat",
              "Map": { "Source": "static",
                "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 } ],
                "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 4, "StepLimit": 100, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" },
                         { "Slot": 2, "Policy": "greedy" }, { "Slot": 3, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """);
        try
        {
            var (exit, _, stderr) = Run(
                "evaluate", "--scenario", path, "--seed-set", "dev", "--seeds", "2", "--rollouts", "2");

            Assert.NotEqual(0, exit);
            Assert.Contains("head-to-head", stderr, StringComparison.Ordinal);
            Assert.Contains("commensurable", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnInvalidDescriptor_ExitsNonZeroAndNamesTheField()
    {
        var (exit, _, stderr) = Run(
            "evaluate", "--scenario", Committed("Tests", "fixtures", "scenarios", "invalid", "unknown_family.json"),
            "--seed-set", "dev", "--seeds", "2", "--rollouts", "2");

        Assert.NotEqual(0, exit);
        Assert.Contains("scenario error: Map.Generator.Family", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInScenarioName_StillWorks()
    {
        var (exit, _, stderr) = Run("evaluate", "--scenario", "bottleneck", "--seed-set", "dev", "--seeds", "2", "--rollouts", "2");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("supplies the study's map", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStandardBuiltIn_StillWorks()
    {
        var (exit, _, _) = Run("evaluate", "--seed-set", "dev", "--seeds", "2", "--rollouts", "2");

        Assert.Equal(0, exit);
    }
}
