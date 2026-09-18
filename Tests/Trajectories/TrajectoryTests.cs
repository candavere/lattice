using System.Text.Json;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Trajectories;

/// <summary>
/// Full-pipeline trajectory tests: record an episode to JSONL, read it back,
/// re-write it, and replay the recorded actions into a fresh simulation —
/// everything must stay byte-identical.
/// </summary>
public class TrajectoryTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();

    private static readonly JsonSerializerOptions Json = new();

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static AgentAction[][] FullCollectorEpisode()
    {
        var agent0Turns = new[]
        {
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 0),
            new AgentAction(ActionKind.Collect, ResourceId: 1),
            new AgentAction(ActionKind.Move, ZoneId: 2),
            new AgentAction(ActionKind.Collect, ResourceId: 2),
        };

        return agent0Turns.Select(turn => new[] { turn, new AgentAction(ActionKind.Wait) }).ToArray();
    }

    private static AgentAction[][] BuildScript(MapGraph map, int steps, int agentCount)
    {
        var resourceCount = Math.Max(1, map.Resources.Length);
        var script = new AgentAction[steps][];
        for (var step = 0; step < steps; step++)
        {
            var turn = new AgentAction[agentCount];
            turn[0] = step % 2 == 0
                ? new AgentAction(ActionKind.Move, ZoneId: step % map.Zones.Length)
                : new AgentAction(ActionKind.Collect, ResourceId: step % resourceCount);
            for (var agent = 1; agent < agentCount; agent++)
            {
                turn[agent] = new AgentAction(ActionKind.Wait);
            }

            script[step] = turn;
        }

        return script;
    }

    [Fact]
    public void WriteFullEpisode_ThenReadBack_MatchInMemory()
    {
        const ulong seed = 0xBEEFCAFEUL;
        var config = new SimulationConfig(2, 20);

        var recording = TrajectoryWriter.Record(TriangleMap, config, seed, FullCollectorEpisode(), new StringWriter());
        var readBack = TrajectoryReader.Read(new StringReader(SerializeViaWriter(recording)));

        Assert.Equal(Serialize(recording), Serialize(readBack));
        Assert.Equal(seed, readBack.Header.Seed);
        Assert.Equal(config.AgentCount, readBack.Header.SimulationConfig.AgentCount);
        Assert.Equal(config.MaxTicks, readBack.Header.SimulationConfig.MaxTicks);
        Assert.Equal(5, readBack.Steps.Length);
        Assert.Equal(0, readBack.Final.WinnerAgentId);
    }

    private static string SerializeViaWriter(TrajectoryRecording recording)
    {
        var buffer = new StringWriter();
        TrajectoryWriter.Write(recording, buffer);
        // Normalize line endings so byte-identical comparisons hold on CRLF
        // (Windows) runners as well as LF (Linux/macOS) runners.
        return buffer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void WriterOutput_ReadBack_IsIndependentlyParseableJsonl()
    {
        const ulong seed = 12345UL;
        var config = new SimulationConfig(2, 5);
        var buffer = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, seed, FullCollectorEpisode(), buffer);

        var lines = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, lines.Length);
        var header = JsonDocument.Parse(lines[0]).RootElement;
        var final = JsonDocument.Parse(lines[^1]).RootElement;
        Assert.Equal("header", header.GetProperty("Kind").GetString());
        Assert.Equal("final", final.GetProperty("Kind").GetString());
        for (var i = 1; i <= 5; i++)
        {
            var step = JsonDocument.Parse(lines[i]).RootElement;
            Assert.Equal("step", step.GetProperty("Kind").GetString());
            Assert.Equal(i, step.GetProperty("StepNumber").GetInt32());
        }
    }

    [Fact]
    public void ReadBack_Rewrite_IsByteIdentical()
    {
        const ulong seed = 0x55AA55AAUL;
        var config = new SimulationConfig(2, 10);
        var buffer = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, seed, FullCollectorEpisode(), buffer);
        // Normalize line endings so the byte-identical assertion holds on
        // CRLF (Windows) runners as well as LF (Linux/macOS) runners.
        var original = buffer.ToString().Replace("\r\n", "\n");

        var readBack = TrajectoryReader.Read(new StringReader(original));
        var rewritten = SerializeViaWriter(readBack);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void RecordedEpisode_ReplaysByteForByte()
    {
        const ulong seed = 0xC0FFEEUL;
        var config = new SimulationConfig(2, 20);
        var actions = FullCollectorEpisode();

        var recording = TrajectoryWriter.Record(TriangleMap, config, seed, actions, new StringWriter());

        Assert.Empty(TrajectoryReplay.Verify(recording));

        var driverResults = SimulationDriver.Play(TriangleMap, config, actions);
        Assert.Equal(driverResults.Count, recording.Steps.Length);
        for (var i = 0; i < driverResults.Count; i++)
        {
            Assert.Equal(Serialize(driverResults[i]), Serialize(recording.Steps[i].Result));
        }
    }

    [Fact]
    public void GeneratedMapEpisode_RecordsHeaderAndReplays()
    {
        const ulong seed = 0xFEEDABEEUL;
        var generatorConfig = new GeneratorConfig(3, 5, 1, 1, 3, 50);
        var simulationConfig = new SimulationConfig(2, 50);
        var map = MapGenerator.Generate(seed, generatorConfig);
        var actions = BuildScript(map, 30, 2);

        var recording = TrajectoryWriter.Record(map, simulationConfig, seed, actions, new StringWriter());
        var readBack = TrajectoryReader.Read(new StringReader(SerializeViaWriter(recording)));

        Assert.Equal(seed, readBack.Header.Seed);
        Assert.Equal(Serialize(map), Serialize(readBack.Header.Map));
        Assert.Empty(TrajectoryReplay.Verify(readBack));
    }

    [Fact]
    public void OutOfSpaceAction_IsRejectedAtRecordTime()
    {
        var config = new SimulationConfig(2, 10);
        var turns = new[]
        {
            new[]
            {
                new AgentAction(ActionKind.Collect, ResourceId: 999),
                new AgentAction(ActionKind.Wait),
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => TrajectoryWriter.Record(TriangleMap, config, 1UL, turns, new StringWriter()));
    }

    [Fact]
    public void MissingHeader_IsRejected()
    {
        var config = new SimulationConfig(2, 3);
        var buffer = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, 7UL, FullCollectorEpisode(), buffer);
        var lines = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerless = string.Join('\n', lines.Skip(1));

        Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(headerless)));
    }

    [Fact]
    public void DuplicateFinal_IsRejected()
    {
        var config = new SimulationConfig(2, 3);
        var buffer = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, 8UL, FullCollectorEpisode(), buffer);
        var lines = buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var duplicated = string.Join('\n', lines) + '\n' + lines[^1];

        Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(duplicated)));
    }

    [Fact]
    public void NonContiguousStepNumbers_AreRejected()
    {
        var config = new SimulationConfig(2, 3);
        var buffer = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, 9UL, FullCollectorEpisode(), buffer);
        var broken = buffer.ToString().Replace("\"StepNumber\":2", "\"StepNumber\":4", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => TrajectoryReader.Read(new StringReader(broken)));
    }

    [Fact]
    public void ReadHeader_ReturnsHeaderWithoutConsumingSteps()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xAAAAUL, FullCollectorEpisode(), new StringWriter());
        var text = SerializeViaWriter(recording);
        using var reader = new CountingReader(text);

        var header = TrajectoryReader.ReadHeader(reader);

        Assert.Equal(Serialize(recording.Header), Serialize(header));
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(text.Split('\n')[1], reader.ReadLine());
    }

    [Fact]
    public void StreamSteps_YieldsAllStepsInOrder()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xBBBBUL, FullCollectorEpisode(), new StringWriter());

        using var source = new StringReader(SerializeViaWriter(recording));
        var header = TrajectoryReader.ReadHeader(source);
        var steps = TrajectoryReader.StreamSteps(source).ToArray();

        Assert.Equal(recording.Header.Seed, header.Seed);
        Assert.Equal(recording.Steps.Length, steps.Length);
        for (var i = 0; i < steps.Length; i++)
        {
            Assert.Equal(recording.Steps[i].StepNumber, steps[i].StepNumber);
            Assert.Equal(Serialize(recording.Steps[i]), Serialize(steps[i]));
        }
    }

    [Fact]
    public void StreamSteps_IsLazy_OnlyConsumesLinesUpToRequestedStep()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xCCCCUL, FullCollectorEpisode(), new StringWriter());
        var text = SerializeViaWriter(recording);
        using var source = new CountingReader(text);
        TrajectoryReader.ReadHeader(source);

        var steps = TrajectoryReader.StreamSteps(source);
        Assert.Equal(1, source.ReadCount);
        using (var enumerator = steps.GetEnumerator())
        {
            Assert.Equal(1, source.ReadCount);
            Assert.True(enumerator.MoveNext());
            Assert.Equal(1, enumerator.Current.StepNumber);
            Assert.Equal(2, source.ReadCount);
        }

        Assert.Equal(2, source.ReadCount);
        Assert.Equal(text.Split('\n')[2], source.ReadLine());
    }

    [Fact]
    public void StreamSteps_TerminalStep_StillRequiresFinalOnFullEnumeration()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xDDDDUL, FullCollectorEpisode(), new StringWriter());
        var lines = SerializeViaWriter(recording).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var stepsOnly = string.Join('\n', lines.Take(lines.Length - 1));

        using var source = new StringReader(stepsOnly);
        TrajectoryReader.ReadHeader(source);
        using var steps = TrajectoryReader.StreamSteps(source).GetEnumerator();
        foreach (var expected in recording.Steps)
        {
            Assert.True(steps.MoveNext());
            Assert.Equal(expected.StepNumber, steps.Current.StepNumber);
        }

        Assert.True(steps.Current.Result.Info.IsTerminal);
        Assert.Throws<InvalidDataException>(() => steps.MoveNext());
    }

    [Fact]
    public void StreamSteps_RejectsMalformedLines()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xEEEEUL, FullCollectorEpisode(), new StringWriter());
        var lines = SerializeViaWriter(recording).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var source = new StringReader(lines[0] + "\n" + lines[1] + "\n{not json}\n");
        TrajectoryReader.ReadHeader(source);
        using var steps = TrajectoryReader.StreamSteps(source).GetEnumerator();
        Assert.True(steps.MoveNext());
        Assert.Equal(1, steps.Current.StepNumber);
        Assert.ThrowsAny<JsonException>(() => steps.MoveNext());
    }

    [Fact]
    public void StreamSteps_RejectsTrailingStepAfterFinal()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0xFFFFUL, FullCollectorEpisode(), new StringWriter());
        var lines = SerializeViaWriter(recording).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var withTrailingStep = string.Join('\n', lines) + "\n" + lines[1];

        using var source = new StringReader(withTrailingStep);
        TrajectoryReader.ReadHeader(source);
        Assert.Throws<InvalidDataException>(() => TrajectoryReader.StreamSteps(source).ToArray());
    }

    [Fact]
    public void StreamSteps_RejectsNonContiguousStepNumbers()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0x1111UL, FullCollectorEpisode(), new StringWriter());
        var broken = SerializeViaWriter(recording).Replace("\"StepNumber\":3", "\"StepNumber\":9", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            TrajectoryReader.StreamSteps(new StringReader(broken)).ToArray());
    }

    [Fact]
    public void Read_MatchesWriterRoundtrip_AfterStreamingHelpersAdded()
    {
        var config = new SimulationConfig(2, 3);
        var recording = TrajectoryWriter.Record(TriangleMap, config, 0x2222UL, FullCollectorEpisode(), new StringWriter());

        var readBack = TrajectoryReader.Read(new StringReader(SerializeViaWriter(recording)));

        Assert.Equal(Serialize(recording), Serialize(readBack));
    }

    private sealed class CountingReader : TextReader
    {
        private readonly StringReader inner;

        public CountingReader(string text) => inner = new StringReader(text);

        public int ReadCount { get; private set; }

        public override int Peek()
        {
            return inner.Peek();
        }

        public override int Read()
        {
            ReadCount++;
            return inner.Read();
        }

        public override string? ReadLine()
        {
            ReadCount++;
            return inner.ReadLine();
        }
    }
}