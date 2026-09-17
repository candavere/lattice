using System.Text;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Visualization;

/// <summary>
/// Validates <see cref="TrajectoryPlayback"/> end to end: a recorded JSONL
/// trajectory replays first to an initial-state frame, then to an ordered
/// step frame per tick whose glyphs match the recorded
/// <see cref="Observation"/>s exactly (with a terminal header naming the
/// winner), and the whole replay is byte-identical on re-runs of the same
/// file. Nothing beyond the recorded results is ever rendered.
/// </summary>
public class TrajectoryPlaybackTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();

    private static string RecordTrajectory(params AgentAction[][] turns)
    {
        var config = new SimulationConfig(2, 40);
        var sink = new StringWriter();
        TrajectoryWriter.Record(TriangleMap, config, 0xC0FFEEUL, turns, sink);
        return sink.ToString();
    }

    private static string Play(string trajectory)
    {
        var sink = new StringWriter();
        TrajectoryPlayback.Playback(new StringReader(trajectory), sink);
        return sink.ToString();
    }

    [Fact]
    public void Playback_EmitsInitialFrameThenStepFrames()
    {
        var trajectory = RecordTrajectory(
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new(ActionKind.Wait) });

        var output = Play(trajectory);

        Assert.StartsWith("== initial state ==\n", output);
        Assert.Contains("Agents: A0@Z0(0) A1@Z1(0)", output);
        Assert.Contains("\n== step 1 ==\nactions: agent0: Move(1); agent1: Wait\n", output);
        Assert.Contains("\n== step 2 ==\nactions: agent0: Collect(0); agent1: Wait\n", output);
        Assert.Contains("Resources: claimed 1 of 3", output);
        Assert.Contains("Agents: A0@Z1(1) A1@Z1(0)", output);
    }

    [Fact]
    public void Playback_TerminalStepNamesReasonAndWinner()
    {
        var trajectory = RecordTrajectory(
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new(ActionKind.Wait) });

        var output = Play(trajectory);

        Assert.Contains(
            "== step 5 - terminal (resources-exhausted), winner: agent 0 ==",
            output);
        Assert.Contains("Agents: A0@Z2(3) A1@Z1(0)", output);
        Assert.EndsWith("Z2: claimed [2] unclaimed []\n\n", output);
    }

    [Fact]
    public void Playback_SameFileProducesByteIdenticalOutput()
    {
        var trajectory = RecordTrajectory(
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new(ActionKind.Wait) });

        var first = Play(trajectory);
        var second = Play(trajectory);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Playback_ClaimMarkersMatchLastObservation()
    {
        // Full sweep: after step 5 everything is claimed, so the final canvas
        // must show every resource already claimed ('*'), never unclaimed.
        var trajectory = RecordTrajectory(
            new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new(ActionKind.Wait) },
            new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new(ActionKind.Wait) });

        var output = Play(trajectory);
        var lastCanvas = Section(output, "== step 5 - terminal (resources-exhausted), winner: agent 0 ==");

        Assert.Contains('*', lastCanvas);
        Assert.DoesNotContain('$', lastCanvas);
    }

    [Fact]
    public void Playback_ThrowsOnMalformedTrajectory()
    {
        Assert.Throws<InvalidDataException>(() =>
            Play("{\"Kind\":\"final\",\"Metrics\":null}\n"));
    }

    [Fact]
    public void FormatActions_RendersEachKind()
    {
        var actions = new[]
        {
            new AgentAction(ActionKind.Wait),
            new AgentAction(ActionKind.Move, ZoneId: 1),
            new AgentAction(ActionKind.Collect, ResourceId: 0),
        };

        Assert.Equal("agent0: Wait; agent1: Move(1); agent2: Collect(0)", TrajectoryPlayback.FormatActions(actions));
    }

    private static string Section(string output, string heading)
    {
        var start = output.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Heading '{heading}' not found in output.");
        var next = output.IndexOf("\n== step", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? output[start..] : output[start..next];
    }
}