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
        return buffer.ToString();
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
        var original = buffer.ToString();

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
}