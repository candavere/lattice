using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-2 coverage for <c>simulate --scenario &lt;file&gt;</c>: how a
/// descriptor path is distinguished from a built-in name, which flag
/// combinations are stated rather than resolved by precedence, and what the
/// recorded episode is actually built from.
/// <para>
/// The compatibility rules are asserted as refusals with a stated reason. A
/// silently-resolved precedence ("<c>--steps</c> always wins") would make the
/// recorded budget differ from the declared one without saying so, which is
/// exactly the failure these tests exist to prevent.
/// </para>
/// </summary>
public class SimulateScenarioFileTests
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
        Path.Combine(Path.GetTempPath(), $"lattice-scen-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void AFileLoadedRun_RecordsAnEpisodeFromTheDescriptor()
    {
        var path = OutputPath();
        try
        {
            var (exit, _, stderr) = Run(
                "simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "gated-vault-duel.json"), "--out", path);

            Assert.Equal(0, exit);
            Assert.Contains("scenario gated-vault-duel", stderr, StringComparison.Ordinal);
            Assert.Contains("recorded", stderr, StringComparison.Ordinal);
            Assert.True(File.Exists(path));
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
    public void StepsCannotBeCombinedWithAScenarioFile()
    {
        var (exit, _, stderr) = Run(
            "simulate", "--seed", "42", "--steps", "50",
            "--scenario", Committed("scenarios", "gated-vault-duel.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("--steps cannot be combined with a scenario file", stderr, StringComparison.Ordinal);
        Assert.Contains("StepLimit", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentCannotBeCombinedWithAScenarioFile()
    {
        var (exit, _, stderr) = Run(
            "simulate", "--seed", "42", "--agent", "mcts",
            "--scenario", Committed("scenarios", "gated-vault-duel.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("--agent cannot be used with a scenario file", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidDescriptor_ExitsNonZeroAndNamesTheField()
    {
        var (exit, _, stderr) = Run(
            "simulate", "--seed", "42",
            "--scenario", Committed("Tests", "fixtures", "scenarios", "invalid", "unknown_policy.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("scenario error: Slots.Policy", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentDescriptor_ExitsNonZero()
    {
        var (exit, _, stderr) = Run(
            "simulate", "--seed", "42",
            "--scenario", Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInNameIsNotMistakenForAFile()
    {
        // "infiltration" has no separator, so it is the built-in token, not a
        // path. It must keep working exactly as before.
        var (exit, _, stderr) = Run("simulate", "--seed", "42", "--quiet", "--scenario", "infiltration");

        Assert.Equal(0, exit);
        Assert.Contains("recorded", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownBuiltInName_IsReportedAsANameNotAMissingFile()
    {
        var (exit, _, stderr) = Run("simulate", "--seed", "42", "--scenario", "not-a-scenario");

        Assert.NotEqual(0, exit);
        Assert.Contains("invalid --scenario", stderr, StringComparison.Ordinal);
        Assert.Contains("infiltration", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWhoseNameLooksLikeABuiltIn_IsStillAFile()
    {
        // A descriptor called "infiltration" is resolved as a file because it
        // carries a path separator. The two forms are never confused.
        var descriptor = Committed("scenarios", "dungeon-infiltration.json");
        var builtIn = OutputPath();
        var file = OutputPath();
        try
        {
            Assert.Equal(0, Run("simulate", "--seed", "42", "--quiet", "--scenario", "infiltration", "--out", builtIn).ExitCode);
            Assert.Equal(0, Run("simulate", "--seed", "42", "--quiet", "--scenario", descriptor, "--out", file).ExitCode);

            var a = File.ReadAllText(builtIn);
            var b = File.ReadAllText(file);

            // Same episode, different presentation labels; the digests match
            // because both resolve through the same committed descriptor.
            Assert.Contains("ScenarioSha256", a.Split('\n')[0], StringComparison.Ordinal);
            Assert.Equal(
                a.Split('\n')[0].Split("\"ScenarioSha256\":")[1].Split(',')[0],
                b.Split('\n')[0].Split("\"ScenarioSha256\":")[1].Split(',')[0]);
        }
        finally
        {
            foreach (var path in new[] { builtIn, file })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void TheDescriptorSuppliesTheStepLimit_WhenStepsIsNotGiven()
    {
        var path = OutputPath();
        try
        {
            // gated-vault-duel declares a 40-tick budget.
            Assert.Equal(0, Run(
                "simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "gated-vault-duel.json"), "--out", path).ExitCode);

            var header = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path).Split('\n')[0]).RootElement;
            Assert.Equal(40, header.GetProperty("SimulationConfig").GetProperty("MaxTicks").GetInt32());
            Assert.Equal(2, header.GetProperty("SimulationConfig").GetProperty("TransitSpeed").GetInt32());
            Assert.Equal("gated-vault-duel", header.GetProperty("Scenario").GetString());
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
    public void TheDescriptorSuppliesTheMap_NotTheStandardFamily()
    {
        var path = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "gated-vault-duel.json"), "--out", path).ExitCode);

            var header = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path).Split('\n')[0]).RootElement;
            var zones = header.GetProperty("Map").GetProperty("Zones");

            // The hand-authored two-room map, not a generated five-zone one.
            Assert.Equal(2, zones.GetArrayLength());
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
    public void AnOverrideScenario_NarrowsTheFamilyRatherThanReplacingIt()
    {
        var overridden = OutputPath();
        var plain = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "simulate", "--seed", "1001", "--quiet",
                "--scenario", Committed("scenarios", "skeleton-with-override.json"), "--out", overridden).ExitCode);
            Assert.Equal(0, Run(
                "simulate", "--seed", "1001", "--quiet",
                "--scenario", Committed("scenarios", "collection-skirmish.json"), "--out", plain).ExitCode);

            var narrowed = System.Text.Json.JsonDocument
                .Parse(File.ReadAllText(overridden).Split('\n')[0]).RootElement;
            var baseline = System.Text.Json.JsonDocument
                .Parse(File.ReadAllText(plain).Split('\n')[0]).RootElement;

            Assert.Equal(1, narrowed.GetProperty("Map").GetProperty("Zones")[0].GetProperty("MaxOccupancy").GetInt32());
            Assert.True(baseline.GetProperty("Map").GetProperty("Zones")[0].GetProperty("MaxOccupancy").GetInt32() > 1);
        }
        finally
        {
            foreach (var p in new[] { overridden, plain })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    [Fact]
    public void APerceptionRoster_RecordsDecisionTimePerceptions()
    {
        var path = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "dungeon-infiltration.json"), "--out", path).ExitCode);

            var lines = File.ReadAllLines(path);
            var header = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement;

            // Both seats carry a cone, and the step lines carry the fog the
            // agents actually decided from.
            Assert.Equal(2, header.GetProperty("AgentVision").GetArrayLength());
            Assert.All(lines.Skip(1).Where(l => l.Contains("\"kind\":\"step\"", StringComparison.Ordinal)
                                                  || l.Contains("\"Kind\":\"step\"", StringComparison.Ordinal)),
                line => Assert.Contains("Perceptions", line, StringComparison.Ordinal));
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
    public void ANonPerceptionRoster_RecordsNoPerceptions()
    {
        var path = OutputPath();
        try
        {
            Assert.Equal(0, Run(
                "simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "gated-vault-duel.json"), "--out", path).ExitCode);

            var lines = File.ReadAllLines(path);
            Assert.DoesNotContain("Perceptions", lines[0], StringComparison.Ordinal);
            Assert.All(lines, line => Assert.DoesNotContain("\"Perceptions\"", line, StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
