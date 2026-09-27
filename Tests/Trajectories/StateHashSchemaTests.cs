using Lattice.Environment;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// Schema plumbing for the per-step state hash: the writer stamps a digest on
/// every step line, the reader round-trips it and rejects a malformed one, and
/// a hash-less recording still serializes to exactly the bytes it did before
/// the field existed — which is what keeps pre-hash files readable and
/// byte-stable.
/// </summary>
public class StateHashSchemaTests
{
    private static readonly MapGraph Map = TestMaps.TriangleWithResources();

    private static AgentAction[][] Episode() => new[]
    {
        new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
        new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) },
    };

    private static TrajectoryRecording Recorded() =>
        TrajectoryWriter.Record(Map, new SimulationConfig(2, 20), 0xF17EUL, Episode(), new StringWriter());

    private static string Jsonl(TrajectoryRecording recording)
    {
        var buffer = new StringWriter();
        TrajectoryWriter.Write(recording, buffer);
        return buffer.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// A recording as a pre-hash build wrote it: schema 2, with no digest on
    /// any step.
    /// </summary>
    private static TrajectoryRecording Legacy()
    {
        var recording = Recorded();
        return recording with
        {
            Header = recording.Header with { SchemaVersion = 2 },
            Steps = recording.Steps.Select(step => step with { StateHash = null }).ToArray(),
        };
    }

    [Fact]
    public void CurrentSchemaVersion_IsThree()
    {
        Assert.Equal(3, TrajectorySchema.CurrentVersion);
    }

    [Fact]
    public void RecordedHeader_StampsTheCurrentSchemaVersion()
    {
        Assert.Equal(TrajectorySchema.CurrentVersion, Recorded().Header.SchemaVersion);
    }

    [Fact]
    public void Record_StampsAStateHashOnEveryStep()
    {
        var recording = Recorded();

        Assert.NotEmpty(recording.Steps);
        Assert.All(recording.Steps, step => Assert.Matches("^[0-9a-f]{64}$", step.StateHash ?? string.Empty));
    }

    [Fact]
    public void Record_ProducesADistinctHashPerStep()
    {
        var hashes = Recorded().Steps.Select(step => step.StateHash).ToArray();

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void Write_EmitsTheStateHashFieldOnEachStepLine()
    {
        var lines = Jsonl(Recorded()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var stepLines = lines.Where(line => line.Contains("\"Kind\":\"step\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, stepLines.Length);
        Assert.All(stepLines, line => Assert.Contains("\"StateHash\":\"", line, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadBack_PreservesEveryStateHash()
    {
        var original = Recorded();

        var readBack = TrajectoryReader.Read(new StringReader(Jsonl(original)));

        Assert.Equal(
            original.Steps.Select(step => step.StateHash).ToArray(),
            readBack.Steps.Select(step => step.StateHash).ToArray());
    }

    [Fact]
    public void HashlessSteps_SerializeWithoutTheStateHashField()
    {
        // A recording whose steps carry no hash must serialize to exactly the
        // pre-hash shape, so a file written by an older build is reproduced
        // byte-for-byte by a newer one.
        var hashless = Recorded() with
        {
            Steps = Recorded().Steps.Select(step => step with { StateHash = null }).ToArray(),
        };

        var jsonl = Jsonl(hashless);

        Assert.DoesNotContain("StateHash", jsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsAMalformedStateHash()
    {
        var jsonl = Jsonl(Recorded()).Replace(
            "\"StateHash\":\"",
            "\"StateHash\":\"zz",
            StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));

        Assert.Contains("StateHash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsATruncatedStateHash()
    {
        // A genuine 63-character digest, not merely a malformed one.
        var jsonl = Jsonl(Recorded());
        var marker = "\"StateHash\":\"";
        var start = jsonl.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var cut = jsonl[..(start + 63)] + jsonl[(start + 64)..];

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(cut)));

        Assert.Contains("StateHash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsAnUppercaseStateHash()
    {
        var jsonl = Jsonl(Recorded()).Replace(
            "\"StateHash\":\"",
            "\"StateHash\":\"A",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));
    }

    /// <summary>
    /// Rewriting a recording that has no digests must not relabel it as the
    /// current schema: a v3 file is defined as carrying a digest at every step,
    /// so stamping v3 onto a hash-less recording manufactures a file that
    /// advertises state verification it does not contain.
    /// </summary>
    [Fact]
    public void Write_PreservesTheHeaderSchemaVersionOfALegacyRecording()
    {
        var legacy = Legacy();

        Assert.Equal(2, legacy.Header.SchemaVersion);
        var jsonl = Jsonl(legacy);

        Assert.Contains("\"SchemaVersion\":2", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"SchemaVersion\":{TrajectorySchema.CurrentVersion}", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("StateHash", jsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_PreservesTheHeaderSchemaVersionOfACurrentRecording()
    {
        var jsonl = Jsonl(Recorded());

        Assert.Contains($"\"SchemaVersion\":{TrajectorySchema.CurrentVersion}", jsonl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The round trip a legacy file actually takes: read a hash-less recording,
    /// write it back, and confirm it still reads as the same legacy file.
    /// </summary>
    [Fact]
    public void LegacyRecording_SurvivesAWriteReadRoundTripAsLegacy()
    {
        var rewritten = Jsonl(Legacy());

        var readBack = TrajectoryReader.Read(new StringReader(rewritten));

        Assert.Equal(2, readBack.Header.SchemaVersion);
        Assert.All(readBack.Steps, step => Assert.Null(step.StateHash));
    }

    /// <summary>
    /// The digest reads every map position, so a map whose zones carry no
    /// position is now a structural defect rather than a tolerated oddity —
    /// <see cref="Simulation.TransitTicks"/> reads <c>Zone.Position</c> for the
    /// kinematic edge length. A file like that is rejected at the reader, with a
    /// message naming the field, instead of surviving the read and faulting
    /// later inside verification.
    /// </summary>
    [Fact]
    public void Reader_RejectsAMapWhoseZoneHasNoPosition()
    {
        var jsonl = Jsonl(Recorded())
            .Replace("\"Position\":{\"X\":0,\"Y\":0}", "\"Position\":null", StringComparison.Ordinal);

        Assert.Contains("\"Position\":null", jsonl, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));

        Assert.Contains("Position", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsAMapWithANullZoneEntry()
    {
        var jsonl = Jsonl(Recorded()).Replace("\"Zones\":[{\"Id\":", "\"Zones\":[null,{\"Id\":", StringComparison.Ordinal);

        Assert.Contains("\"Zones\":[null,", jsonl, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));

        Assert.Contains("Zones", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsAMapWhoseResourceHasNoPosition()
    {
        var jsonl = Jsonl(Recorded())
            .Replace("\"Position\":{\"X\":0,\"Y\":0}", "\"Position\":null", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(jsonl)));

        Assert.Contains("Position", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defense in depth: a recording can also be built in memory rather than
    /// read from a file, so the digest itself must be total. A corrupt map
    /// yields a canonical text that no recorded digest can equal, never a
    /// null dereference.
    /// </summary>
    [Fact]
    public void CanonicalText_TotalOverACorruptMap()
    {
        var state = new SimulationState(
            Map,
            new[] { new AgentState(0, 0, 0) },
            Array.Empty<int>(),
            0);

        var corrupt = state with
        {
            Map = Map with { Zones = new Zone?[] { null, Map.Zones[1] }! },
        };

        var text = SimulationStateHash.CanonicalText(corrupt, 0xF17EUL);

        Assert.Contains("zone.0=", text, StringComparison.Ordinal);
        Assert.Equal(64, SimulationStateHash.Compute(corrupt, 0xF17EUL).Length);
    }
}
