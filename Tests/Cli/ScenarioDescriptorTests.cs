using System.Text;
using Lattice.Cli;
using Lattice.Environment;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// Stage-1 coverage for the declarative scenario contract: the loader accepts
/// what the documented schema allows, rejects everything else with the
/// offending field's JSON path, and takes its SHA-256 over the descriptor's
/// exact source bytes.
/// <para>
/// The invalid cases are driven from committed fixtures under
/// <c>Tests/fixtures/scenarios/invalid/</c> rather than from inline strings, so
/// a regression names a file a reader can open and diff. Every fixture is
/// rejected for a stated reason; the tests additionally pin the field path, so
/// a loader that started rejecting the right file for the wrong reason would
/// fail here.
/// </para>
/// </summary>
public class ScenarioDescriptorTests
{
    private static string ScenarioRoot()
    {
        // Walk up from the test assembly until the repository root (the
        // directory holding Lattice.sln) is found, so the path is independent
        // of the working directory the runner happens to use.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Committed(string relative) => Path.Combine(ScenarioRoot(), relative);

    private static string Load(string relative) =>
        ScenarioLoader.LoadFile(Committed(relative)).Descriptor.Id;

    private static IReadOnlyList<ScenarioError> Reject(string relative) =>
        Assert.Throws<ScenarioValidationException>(
            () => ScenarioLoader.LoadFile(Committed(relative))).Errors;

    private static IReadOnlyList<ScenarioError> RejectText(string json) =>
        Assert.Throws<ScenarioValidationException>(
            () => ScenarioLoader.Load(Encoding.UTF8.GetBytes(json))).Errors;

    // ------------------------------------------------------------ accepted

    [Theory]
    [InlineData("scenarios/collection-skirmish.json")]
    [InlineData("scenarios/bottleneck-contention.json")]
    [InlineData("scenarios/dungeon-infiltration.json")]
    [InlineData("scenarios/gated-vault-duel.json")]
    [InlineData("scenarios/skeleton-with-override.json")]
    public void EveryCommittedDescriptor_IsValid(string relative)
    {
        var (descriptor, digest) = ScenarioLoader.LoadFile(Committed(relative));

        Assert.Equal(ScenarioLoader.CurrentSchemaVersion, descriptor.SchemaVersion);
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", descriptor.Id);
        Assert.Equal(64, digest.Length);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void AStaticMap_IsMaterializedExactlyAsAuthored()
    {
        var (descriptor, _) = ScenarioLoader.LoadFile(Committed("scenarios/dungeon-infiltration.json"));

        var map = Assert.IsType<ScenarioMapSpec.Static>(descriptor.Map).Map;
        Assert.Equal(6, map.Zones.Length);
        Assert.Equal(7, map.ChokePoints.Length);
        Assert.Equal(3, map.Resources.Length);
        Assert.All(map.ChokePoints, choke => Assert.Equal(1, choke.MaxOccupancy));

        // A static map is the same graph for every seed, which is the point of
        // hand-authoring: the topology is in the file, not in a generator.
        Assert.Equal(map, descriptor.BuildMap(1));
        Assert.Equal(map, descriptor.BuildMap(999_999));
    }

    [Fact]
    public void AGeneratedMap_VariesBySeed()
    {
        var (descriptor, _) = ScenarioLoader.LoadFile(Committed("scenarios/collection-skirmish.json"));

        var first = descriptor.BuildMap(1001);
        var second = descriptor.BuildMap(2001);
        // Compared through the serialized form, not record equality: Zone[] is
        // a reference type inside the MapGraph record, so two graphs with
        // identical contents are not Equal.
        Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));

        // ...and is a pure function of the seed, so a rerun is identical.
        // Serialized rather than record-compared, because the arrays inside
        // MapGraph compare by reference.
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(descriptor.BuildMap(1001)));
    }

    [Fact]
    public void AnOverride_NarrowsOneZoneAndLeavesTheRestOfTheFamilyAlone()
    {
        var (withOverride, _) = ScenarioLoader.LoadFile(Committed("scenarios/skeleton-with-override.json"));
        var (plain, _) = ScenarioLoader.LoadFile(Committed("scenarios/collection-skirmish.json"));

        var overridden = withOverride.BuildMap(1001);
        var baseline = plain.BuildMap(1001);

        Assert.Equal(1, overridden.Zones[0].MaxOccupancy);
        // The standard family leaves every zone unbounded, so exactly one zone
        // differs and the rest of the graph is the family's own seeded layout.
        Assert.NotEqual(baseline.Zones[0].MaxOccupancy, overridden.Zones[0].MaxOccupancy);
        Assert.Equal(
            baseline.Zones.Skip(1).Select(zone => zone.Position),
            overridden.Zones.Skip(1).Select(zone => zone.Position));
        Assert.Equal(baseline.ChokePoints, overridden.ChokePoints);
        Assert.Equal(baseline.Resources, overridden.Resources);
    }

    [Fact]
    public void RosterOrder_IsTheArrayOrderAndIsTotal()
    {
        var (descriptor, _) = ScenarioLoader.LoadFile(Committed("scenarios/dungeon-infiltration.json"));

        Assert.Equal(descriptor.AgentCount, descriptor.Slots.Count);
        Assert.Equal([0, 1], descriptor.Slots.Select(slot => slot.Slot));
        Assert.Equal(["sentry", "infiltrator"], descriptor.Slots.Select(slot => slot.Policy));
        Assert.Equal([1, 0], descriptor.Slots.Select(slot => slot.RivalSlot));
    }

    // ------------------------------------------------------------ rejected

    [Theory]
    [InlineData("unknown_field.json", "Weather")]
    [InlineData("unknown_policy.json", "Slots.Policy")]
    [InlineData("unknown_victory.json", "Victory.Condition")]
    [InlineData("unknown_family.json", "Map.Generator.Family")]
    [InlineData("bad_schema_version.json", "SchemaVersion")]
    [InlineData("bad_agent_count.json", "Simulation.AgentCount")]
    [InlineData("bad_step_limit.json", "Simulation.StepLimit")]
    [InlineData("non_integer.json", "Map.Zones[0].X")]
    [InlineData("disconnected.json", "Map.ChokePoints")]
    [InlineData("duplicate_ids.json", "Map.Zones[].Id")]
    [InlineData("duplicate_coordinates.json", "Map.Zones[].X/.Y")]
    [InlineData("bad_endpoint.json", "Map.ChokePoints[].ToZoneId")]
    [InlineData("bad_capacity.json", "Map.ChokePoints[0].MaxOccupancy")]
    [InlineData("inaccessible_resource.json", "Map.Resources[].ZoneId")]
    [InlineData("roster_mismatch.json", "Slots")]
    [InlineData("slot_out_of_order.json", "Slots[0].Slot")]
    [InlineData("missing_rival.json", "Slots[0].RivalSlot")]
    [InlineData("stray_rival.json", "Slots[0].RivalSlot")]
    [InlineData("stray_vision.json", "Slots[0].Vision")]
    [InlineData("self_loop_choke.json", "Map.ChokePoints[0].ToZoneId")]
    [InlineData("generator_on_static.json", "Map.Generator")]
    [InlineData("zones_on_generated.json", "Map")]
    [InlineData("malformed.json", "(document)")]
    [InlineData("not_an_object.json", "(document)")]
    public void AnInvalidFixture_IsRejectedNamingTheOffendingField(string fixture, string expectedField)
    {
        var errors = Reject(Path.Combine("Tests", "fixtures", "scenarios", "invalid", fixture));

        Assert.Contains(errors, error => error.FieldPath == expectedField);
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    [Fact]
    public void AMissingId_IsRejected()
    {
        var errors = RejectText("""
            { "SchemaVersion": 1,
              "Map": { "Source": "static", "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 } ], "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 2, "StepLimit": 10, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """);

        Assert.Contains(errors, error => error.FieldPath == "Id" && error.Message.Contains("required", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdThatIsNotKebabCase_IsRejected()
    {
        var errors = RejectText("""
            { "SchemaVersion": 1, "Id": "Not Kebab_Case",
              "Map": { "Source": "static", "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 } ], "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 2, "StepLimit": 10, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """);

        Assert.Contains(errors, error => error.FieldPath == "Id" && error.Message.Contains("kebab-case", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateJsonKey_IsAcceptedWithTheLastValueWinning()
    {
        // JSON prescribes that a repeated member resolves to the last one, and
        // System.Text.Json follows that before the loader ever sees the
        // document. This pins the resulting behaviour explicitly so a future
        // change to reject duplicates is a deliberate, visible one rather than
        // an accident of the parser.
        var descriptor = ScenarioLoader.Load(Encoding.UTF8.GetBytes("""
            { "SchemaVersion": 1, "Id": "a", "Id": "b",
              "Map": { "Source": "static", "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 } ], "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 2, "StepLimit": 10, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """));

        Assert.Equal("b", descriptor.Id);
    }

    [Fact]
    public void ATrailingComma_IsRejectedAsMalformed()
    {
        // The comma sits before a *following* member, which is what makes it
        // illegal; a comma after the very last member is legal JSON and is not
        // tested here.
        var errors = RejectText("""
            { "SchemaVersion": 1, "Id": "a",
              "Map": { "Source": "static", "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 }, ], "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 2, "StepLimit": 10, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """);

        Assert.Contains(errors, error => error.FieldPath == "(document)");
    }

    [Fact]
    public void AComment_IsRejectedAsMalformed()
    {
        var errors = RejectText("""
            { "SchemaVersion": 1, "Id": "a", // a comment
              "Map": { "Source": "static", "Zones": [ { "Id": 0, "X": 0, "Y": 0 }, { "Id": 1, "X": 4, "Y": 0 } ], "ChokePoints": [ { "Id": 0, "FromZoneId": 0, "ToZoneId": 1 } ] },
              "Simulation": { "AgentCount": 2, "StepLimit": 10, "TransitSpeed": 0 },
              "Slots": [ { "Slot": 0, "Policy": "greedy" }, { "Slot": 1, "Policy": "greedy" } ],
              "Victory": { "Condition": "first-of-either" }, "Scoring": { "Scheme": "resources-claimed" } }
            """);

        Assert.Contains(errors, error => error.FieldPath == "(document)");
    }

    [Fact]
    public void ADescriptorLargerThanTheLimit_IsRejectedBeforeItIsParsed()
    {
        var oversized = new byte[ScenarioLoader.MaxDescriptorBytes + 1];
        Array.Fill(oversized, (byte)' ');

        var path = Path.Combine(Path.GetTempPath(), $"lattice-oversized-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, oversized);
        try
        {
            var errors = Assert.Throws<ScenarioValidationException>(
                () => ScenarioLoader.LoadFile(path)).Errors;
            Assert.Contains(errors, error => error.Message.Contains("above the", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFile_IsReportedAsAFileError()
    {
        var errors = Assert.Throws<ScenarioValidationException>(
            () => ScenarioLoader.LoadFile(Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.json"))).Errors;

        Assert.Contains(errors, error => error.Message.Contains("does not exist", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- digest

    [Fact]
    public void TheDigestIsTheSha256OfTheExactSourceBytes()
    {
        var path = Committed("scenarios/collection-skirmish.json");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        var (_, digest) = ScenarioLoader.LoadFile(path);

        Assert.Equal(expected, digest);
    }

    [Fact]
    public void TheDigestIsTakenOverTheFileBytesNotTheParsedMeaning()
    {
        // Two descriptors that declare the same scenario, differing only in
        // insignificant whitespace, are semantically identical and must still
        // hash differently: the digest identifies the file, so that a reader
        // holding a recording can name the exact bytes that produced it.
        var canonical = File.ReadAllText(Committed("scenarios/collection-skirmish.json"));
        var respaced = canonical.Replace("\n", "\n\n");

        var left = ScenarioLoader.Load(Encoding.UTF8.GetBytes(canonical));
        var right = ScenarioLoader.Load(Encoding.UTF8.GetBytes(respaced));

        Assert.Equal(left.Id, right.Id);
        Assert.NotEqual(
            ScenarioLoader.ComputeDigest(Encoding.UTF8.GetBytes(canonical)),
            ScenarioLoader.ComputeDigest(Encoding.UTF8.GetBytes(respaced)));
    }

    [Fact]
    public void TheDigestChangesWhenASingleByteOfMeaningChanges()
    {
        var canonical = File.ReadAllText(Committed("scenarios/collection-skirmish.json"));
        var nudged = canonical.Replace("\"StepLimit\": 100", "\"StepLimit\": 101");

        Assert.NotEqual(canonical, nudged);
        Assert.NotEqual(
            ScenarioLoader.ComputeDigest(Encoding.UTF8.GetBytes(canonical)),
            ScenarioLoader.ComputeDigest(Encoding.UTF8.GetBytes(nudged)));
    }

    [Fact]
    public void TheDigestIsLowercaseHex()
    {
        var digest = ScenarioLoader.ComputeDigest(Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(digest.ToLowerInvariant(), digest);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }
}
