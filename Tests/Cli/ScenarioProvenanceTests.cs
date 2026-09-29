using System.Text.Json;
using Lattice.Cli;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-2 coverage for scenario provenance: the SHA-256 of the descriptor's
/// exact source bytes travelling from the file into the trajectory header, and
/// the backward-compatibility contract that a schema 0-4 recording still reads,
/// replays, and verifies unchanged.
/// <para>
/// These tests drive the real <see cref="CliApp.Run"/> entry point and compare
/// parsed JSON objects field by field. They deliberately do <b>not</b> assert
/// raw file-byte equality against a pre-schema-5 recording: the new header field
/// and the version stamp necessarily change those bytes, and pretending
/// otherwise would be the circular, self-confirming test this suite is written
/// to avoid.
/// </para>
/// </summary>
public class ScenarioProvenanceTests
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

    private static string Record(params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-prov-{Guid.NewGuid():N}.jsonl");
        try
        {
            var (exit, _, stderr) = Run(args.Concat(["--out", path, "--quiet"]).ToArray());
            Assert.Equal(0, exit);
            Assert.NotEmpty(stderr);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static JsonElement Header(string jsonl) =>
        JsonDocument.Parse(jsonl.Split('\n')[0]).RootElement.Clone();

    private static string HeaderField(string jsonl, string name) =>
        Header(jsonl).TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// Canonicalizes a header with the given fields removed, so two recordings
    /// can be compared on everything that remains. Removal is explicit and
    /// listed by the caller, which is what keeps "identical apart from the
    /// digest" an auditable claim rather than a vague one.
    /// </summary>
    private static string Canonical(JsonElement header, params string[] drop)
    {
        var fields = header.EnumerateObject()
            .Where(property => !drop.Contains(property.Name))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={property.Value.GetRawText()}")
            .ToArray();
        return string.Join("|", fields);
    }

    private static IReadOnlyList<JsonElement> BodyLines(string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    // ------------------------------------------------------- the digest field

    [Fact]
    public void AFileLoadedRun_CarriesTheDescriptorsExactSha256()
    {
        var path = Committed("scenarios", "dungeon-infiltration.json");
        var expected = ScenarioLoader.ComputeDigest(File.ReadAllBytes(path));

        var jsonl = Record("simulate", "--seed", "42", "--scenario", path);

        Assert.Equal(expected, HeaderField(jsonl, "ScenarioSha256"));
        Assert.Equal(TrajectorySchema.CurrentVersion, Header(jsonl).GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public void ABuiltInRun_CarriesItsCommittedDescriptorsSha256()
    {
        // A built-in resolves through its committed descriptor, so its header
        // digest is a digest a reader can look up in the tree.
        var expected = ScenarioLoader.ComputeDigest(
            File.ReadAllBytes(Committed("scenarios", "dungeon-infiltration.json")));

        var jsonl = Record("simulate", "--seed", "42", "--scenario", "infiltration");

        Assert.Equal(expected, HeaderField(jsonl, "ScenarioSha256"));
    }

    [Fact]
    public void TheStandardBuiltIn_CarriesTheStandardDescriptorsSha256()
    {
        var expected = ScenarioLoader.ComputeDigest(
            File.ReadAllBytes(Committed("scenarios", "collection-skirmish.json")));

        Assert.Equal(expected, HeaderField(Record("simulate", "--seed", "42"), "ScenarioSha256"));
    }

    [Fact]
    public void TheEmbeddedBuiltInBytes_AreTheCommittedFileBytes()
    {
        // This is what makes a built-in's header digest identify a file that is
        // really in the tree rather than a resource that merely claims to be.
        foreach (var id in BuiltInScenarioCatalog.Ids)
        {
            var embedded = BuiltInScenarioCatalog.Read(id);
            var committed = File.ReadAllBytes(Committed("scenarios", BuiltInScenarioCatalog.FileName(id)));
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(committed)),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(embedded)));
        }
    }

    [Fact]
    public void EveryCommittedDescriptor_ValidatesFromItsEmbeddedBytes()
    {
        foreach (var id in BuiltInScenarioCatalog.Ids)
        {
            var descriptor = ScenarioLoader.Load(BuiltInScenarioCatalog.Read(id));
            Assert.Equal(id, descriptor.Id);
        }
    }

    [Fact]
    public void ADigestIsWrittenForEveryRun_NotOnlyFileLoadedOnes()
    {
        // DESIGN DECISION 1b: the digest is recorded for EVERY run, built-in or
        // file-loaded. A run with no descriptor bytes would have no digest, so
        // this asserts the opposite for each built-in form.
        Assert.NotEqual(string.Empty, HeaderField(Record("simulate", "--seed", "7"), "ScenarioSha256"));
        Assert.NotEqual(
            string.Empty,
            HeaderField(Record("simulate", "--seed", "7", "--scenario", "infiltration"), "ScenarioSha256"));
        Assert.NotEqual(
            string.Empty,
            HeaderField(Record("simulate", "--seed", "7", "--scenario", Committed("scenarios", "gated-vault-duel.json")), "ScenarioSha256"));
    }

    [Fact]
    public void TheDigestChangesWhenTheDescriptorBytesChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-mutated-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, File.ReadAllBytes(Committed("scenarios", "gated-vault-duel.json")));
        try
        {
            var before = HeaderField(Record("simulate", "--seed", "7", "--scenario", path), "ScenarioSha256");

            // Re-indent the file: semantically identical, different bytes, and
            // therefore a different identity.
            var text = File.ReadAllText(path).Replace("\n  \"", "\n    \"");
            File.WriteAllText(path, text);
            var after = HeaderField(Record("simulate", "--seed", "7", "--scenario", path), "ScenarioSha256");

            Assert.NotEqual(before, after);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARunIsDeterministic_AndItsDigestIsToo()
    {
        var first = Record("simulate", "--seed", "1234", "--scenario", Committed("scenarios", "bottleneck-contention.json"));
        var second = Record("simulate", "--seed", "1234", "--scenario", Committed("scenarios", "bottleneck-contention.json"));

        Assert.Equal(first, second);
    }

    // ------------------------------------------------------------ equivalence

    [Theory]
    [InlineData(42)]
    [InlineData(1001)]
    [InlineData(2001)]
    public void AFileLoadedRun_MatchesTheBuiltInApartFromDigestAndLabels(int seed)
    {
        // The equivalence claim, made precisely: the file-loaded recording and
        // the built-in one agree on every field of every line except the new
        // digest and the two presentation labels, which each spells its own way
        // (the descriptor's id and roles vs the built-in's own). Map, config,
        // every action, every result, and every state hash are identical.
        const string Labels = "ScenarioSha256|Scenario|AgentRoles";

        var path = Committed("scenarios", "dungeon-infiltration.json");
        var builtIn = Record("simulate", "--seed", seed.ToString(), "--scenario", "infiltration");
        var fileLoaded = Record("simulate", "--seed", seed.ToString(), "--scenario", path);

        var builtInBody = BodyLines(builtIn);
        var fileBody = BodyLines(fileLoaded);
        Assert.Equal(builtInBody.Count, fileBody.Count);

        for (var i = 0; i < builtInBody.Count; i++)
        {
            Assert.Equal(
                Canonical(builtInBody[i], Labels.Split('|')),
                Canonical(fileBody[i], Labels.Split('|')));
        }
    }

    [Theory]
    [InlineData(42)]
    [InlineData(1001)]
    [InlineData(2001)]
    public void PerTickStateHashes_AreIdenticalAcrossTheBuiltInAndFileForms(int seed)
    {
        var path = Committed("scenarios", "dungeon-infiltration.json");
        var builtIn = BodyLines(Record("simulate", "--seed", seed.ToString(), "--scenario", "infiltration"));
        var fileLoaded = BodyLines(Record("simulate", "--seed", seed.ToString(), "--scenario", path));

        var left = builtIn.Where(line => line.GetProperty("Kind").GetString() == "step")
            .Select(line => line.GetProperty("StateHash").GetString()).ToArray();
        var right = fileLoaded.Where(line => line.GetProperty("Kind").GetString() == "step")
            .Select(line => line.GetProperty("StateHash").GetString()).ToArray();

        Assert.NotEmpty(left);
        Assert.Equal(left, right);
    }

    [Fact]
    public void TheScenarioDigestIsNotConfusedWithThePerTickStateHash()
    {
        // They are computed over different things and must never be equal by
        // accident or compared to each other. A recording whose descriptor is
        // the empty-string digest cannot coincide with any tick digest.
        var jsonl = Record("simulate", "--seed", "42", "--scenario", Committed("scenarios", "gated-vault-duel.json"));

        var digest = HeaderField(jsonl, "ScenarioSha256");
        var stateHashes = BodyLines(jsonl)
            .Where(line => line.GetProperty("Kind").GetString() == "step")
            .Select(line => line.GetProperty("StateHash").GetString())
            .ToArray();

        Assert.DoesNotContain(digest, stateHashes);
    }

    // ------------------------------------------------- legacy compatibility

    [Fact]
    public void TheCommittedSchema4Fixture_StillReadsReplaysAndVerifies()
    {
        // site/infiltration.jsonl is a schema-4 recording made before the
        // scenario digest existed. It must remain readable, replayable, and
        // verifiable, and must NOT be silently relabelled or acquire a digest.
        var path = Committed("site", "infiltration.jsonl");
        var recording = TrajectoryReader.Read(new StringReader(File.ReadAllText(path)));

        Assert.Equal(4, recording.Header.SchemaVersion);
        Assert.Null(recording.Header.ScenarioSha256);
        Assert.Empty(TrajectoryReplay.Verify(recording));
    }

    [Fact]
    public void TheCommittedSchema3Fixtures_StillVerifyWithNoDigestNotice()
    {
        foreach (var relative in new[] { "site/demo.jsonl", "Tests/fixtures/golden_trajectory.jsonl" })
        {
            var recording = TrajectoryReader.Read(new StringReader(File.ReadAllText(Committed(relative.Split('/')))));
            var report = TrajectoryReplay.VerifyDetailed(recording);

            Assert.Null(recording.Header.ScenarioSha256);
            Assert.Empty(report.Problems);
            // A pre-schema-5 recording predates the field, so it is not nagged
            // about a digest it never claimed to have.
            Assert.DoesNotContain(TrajectoryReplay.NoScenarioDigestNotice, report.Notices);
        }
    }

    [Fact]
    public void ARewriteOfALegacyRecording_AddsNoDigestAndKeepsItsOwnVersion()
    {
        // The migration invariant, now including the new field: rewriting a
        // pre-digest file must not relabel it as schema 5 with no digest
        // present, and must not invent a digest for it.
        var original = File.ReadAllText(Committed("site", "demo.jsonl"));
        var recording = TrajectoryReader.Read(new StringReader(original));

        using var rewritten = new StringWriter();
        TrajectoryWriter.Write(recording, rewritten);
        var text = rewritten.ToString();

        Assert.DoesNotContain("\"ScenarioSha256\"", text, StringComparison.Ordinal);
        Assert.Contains("\"SchemaVersion\":3", text, StringComparison.Ordinal);
        Assert.Equal(original, text);
    }

    [Fact]
    public void ASchema5Recording_WithoutADigest_VerifiesWithANotice()
    {
        // The digest is provenance about a file a self-contained recording does
        // not reopen, so replay cannot recompute it. Its absence is therefore a
        // notice, never a failure — but it is said out loud, so a reader is
        // never left thinking the digest was checked.
        var jsonl = Record("simulate", "--seed", "42", "--scenario", Committed("scenarios", "gated-vault-duel.json"));
        var recording = TrajectoryReader.Read(new StringReader(jsonl));

        var stripped = new TrajectoryRecording(
            recording.Header with { ScenarioSha256 = null }, recording.Steps, recording.Final);
        var report = TrajectoryReplay.VerifyDetailed(stripped);

        Assert.Empty(report.Problems);
        Assert.Contains(TrajectoryReplay.NoScenarioDigestNotice, report.Notices);
    }

    [Fact]
    public void ALegacyRecordingWithADigestStamped_IsStillReadable()
    {
        // A hand-edited schema-3 file that also carries a digest must read: the
        // field is optional on the wire, and a version stamp does not gate it.
        var original = File.ReadAllText(Committed("site", "demo.jsonl"));
        var tampered = original.Replace(
            "\"SchemaVersion\":3",
            "\"SchemaVersion\":3,\"ScenarioSha256\":\"" + new string('a', 64) + "\"",
            StringComparison.Ordinal);

        var recording = TrajectoryReader.Read(new StringReader(tampered));

        Assert.Equal(new string('a', 64), recording.Header.ScenarioSha256);
        Assert.Empty(TrajectoryReplay.Verify(recording));
    }

    [Theory]
    [InlineData("\"ScenarioSha256\":\"ABCDEF\"")]
    [InlineData("\"ScenarioSha256\":\"not-a-digest\"")]
    [InlineData("\"ScenarioSha256\":\"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789\"")]
    public void AMalformedScenarioDigest_IsRejectedAtReadTime(string injected)
    {
        var original = File.ReadAllText(Committed("site", "demo.jsonl"));
        var tampered = original.Replace("\"SchemaVersion\":3", $"\"SchemaVersion\":3,{injected}", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(
            () => TrajectoryReader.Read(new StringReader(tampered)));
        Assert.Contains("ScenarioSha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderRoundTrip_PreservesTheDigest()
    {
        var jsonl = Record("simulate", "--seed", "42", "--scenario", Committed("scenarios", "bottleneck-contention.json"));
        var recording = TrajectoryReader.Read(new StringReader(jsonl));

        using var rewritten = new StringWriter();
        TrajectoryWriter.Write(recording, rewritten);

        Assert.Equal(jsonl, rewritten.ToString());
    }

    // ------------------------------------------------------------- the digest
    // ------------------------------------------------------------ as provenance

    [Fact]
    public void AHeaderFromADifferentDescriptor_HasADifferentDigest()
    {
        var left = HeaderField(Record("simulate", "--seed", "42", "--scenario", Committed("scenarios", "gated-vault-duel.json")), "ScenarioSha256");
        var right = HeaderField(Record("simulate", "--seed", "42", "--scenario", Committed("scenarios", "dungeon-infiltration.json")), "ScenarioSha256");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void ReplayVerify_SucceedsOnARecordingThatCarriesADigest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-prov-{Guid.NewGuid():N}.jsonl");
        try
        {
            var (exit, _, _) = Run("simulate", "--seed", "42", "--quiet",
                "--scenario", Committed("scenarios", "dungeon-infiltration.json"), "--out", path);
            Assert.Equal(0, exit);

            var (verifyExit, _, verifyStderr) = Run("replay", path, "--verify");
            Assert.Equal(0, verifyExit);
            Assert.Contains($"schema v{TrajectorySchema.CurrentVersion}", verifyStderr, StringComparison.Ordinal);
            Assert.DoesNotContain(TrajectoryReplay.NoScenarioDigestNotice, verifyStderr, StringComparison.Ordinal);
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
