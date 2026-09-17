using System.Text;
using System.Xml.Linq;
using Lattice.Environment;
using Lattice.Tests.Environment;
using Lattice.Trajectories;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Visualization;

/// <summary>
/// Validates <see cref="SvgTrajectoryExporter.Export(TextReader)"/>: a
/// recorded JSONL trajectory becomes one self-contained, CSS-animated SVG
/// whose frame count and content exactly match the recorded observations
/// (initial frame plus one per tick), which is well-formed XML and
/// byte-identical across repeated exports of the same recording.
/// </summary>
public class SvgTrajectoryExporterTests
{
    private static readonly MapGraph TriangleMap = TestMaps.TriangleWithResources();

    private static TrajectoryRecording RecordFullSweep()
    {
        var config = new SimulationConfig(2, 40);
        var sink = new StringWriter();
        return TrajectoryWriter.Record(
            TriangleMap,
            config,
            0xC0FFEEUL,
            new[]
            {
                new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Wait) },
            },
            sink);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void Export_ProducesSelfContainedMultiFrameSvg()
    {
        var svg = SvgTrajectoryExporter.Export(RecordFullSweep());

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", svg);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("<style>", svg);
        Assert.Contains("@keyframes frame-0", svg);
        Assert.Contains("@keyframes frame-5", svg);
        Assert.Equal(6, Occurrences(svg, "<g id=\"frame-"));
        Assert.Contains("<g id=\"frame-0\" class=\"frame\" data-step=\"initial\">", svg);
        Assert.Contains("<g id=\"frame-5\" class=\"frame\" data-step=\"5\">", svg);
    }

    [Fact]
    public void Export_IsWellFormedXmlWithSvgRoot()
    {
        var svg = SvgTrajectoryExporter.Export(RecordFullSweep());
        var document = XDocument.Parse(svg);

        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", document.Root.Name.NamespaceName);
    }

    [Fact]
    public void Export_FramesRenderTheRecordedObservations()
    {
        var recording = RecordFullSweep();
        var svg = SvgTrajectoryExporter.Export(recording);
        var map = recording.Header.Map;

        var initial = Simulation.CreateInitial(map, recording.Header.SimulationConfig);
        Assert.Contains(SvgRenderer.RenderScene(map, initial.Agents, initial.Claims), svg);

        foreach (var step in recording.Steps)
        {
            var observation = step.Result.Observations[0];
            Assert.Contains(SvgRenderer.RenderScene(map, observation.AgentStates, observation.Claims), svg);
        }
    }

    [Fact]
    public void Export_SameRecording_ByteIdenticalOutput()
    {
        var recording = RecordFullSweep();

        var first = SvgTrajectoryExporter.Export(recording);
        var second = SvgTrajectoryExporter.Export(recording);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Export_FromReader_MatchesInMemoryExport()
    {
        var recording = RecordFullSweep();
        var jsonl = new StringWriter();
        TrajectoryWriter.Write(recording, jsonl);

        Assert.Equal(
            SvgTrajectoryExporter.Export(recording),
            SvgTrajectoryExporter.Export(new StringReader(jsonl.ToString())));
    }

    [Fact]
    public void Export_ThrowsOnMalformedTrajectory()
    {
        Assert.Throws<InvalidDataException>(() =>
            SvgTrajectoryExporter.Export(new StringReader("{\"Kind\":\"final\",\"Metrics\":null}\n")));
    }
}