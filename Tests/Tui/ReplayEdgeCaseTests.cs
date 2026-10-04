using System.Text.Json;
using System.Text.Json.Nodes;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The recordings a reader can actually turn up and a viewer must not fall over
/// on: one step, no steps at all, and a file written before the format grew its
/// optional fields. Each is built from the committed infiltration recording's own
/// header and step lines, so the shape under test is the shape that reader accepts.
/// </summary>
public class ReplayEdgeCaseTests
{
    private static readonly PaneSize Cockpit = new(100, 30);

    [Fact]
    public void ARecordingOfOneStepIsAStartFrameAndThatStep()
    {
        var document = ReplaySource.Read(new StringReader(Project(Infiltration(), steps: 1, schema0: false)));

        Assert.Equal(2, document.Count);
        Assert.Equal(1, document.Header.RecordedSteps);
        Assert.True(document.Frames[0].IsStart);
        Assert.Equal(1, document.Frames[1].Tick);
        AssertAllFramesRender(document);
    }

    [Fact]
    public void ARecordingOfNoStepsIsTheStartFrameAlone()
    {
        var document = ReplaySource.Read(new StringReader(Project(Infiltration(), steps: 0, schema0: false)));

        Assert.Equal(1, document.Count);
        Assert.True(document.Frames[0].IsStart);
        Assert.Empty(document.Frames[0].Claims);
        Assert.Equal(0, document.Frames[0].Tick);

        // The start frame still carries the roster the recorded header starts with,
        // because that is a fact about the header rather than about any step.
        Assert.Equal(2, document.Frames[0].Agents.Count);
        foreach (var agent in document.Frames[0].Agents)
        {
            Assert.Equal(0, agent.Score);
        }

        AssertAllFramesRender(document);
    }

    [Fact]
    public void ARecordingWrittenBeforeTheOptionalFieldsReadsAndRendersUnchanged()
    {
        // No SchemaVersion, no StateHash, no perceptions, no digest: the pre-schema-3
        // shape, which the reader still accepts and the panes must describe rather
        // than guess at.
        var document = ReplaySource.Read(new StringReader(Project(Infiltration(), steps: 3, schema0: true)));

        Assert.Equal(4, document.Count);
        Assert.Equal(0, document.Header.SchemaVersion);
        Assert.Null(document.Header.ScenarioDigest);
        Assert.All(document.Frames, frame => Assert.Null(frame.StateDigest));
        AssertAllFramesRender(document);

        var rows = CockpitLayout.Render(new CockpitRequest(
            document,
            1,
            Cockpit,
            GlyphMode.Unicode,
            Palette.PanelBackground,
            Phase: 0.0,
            Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0))).ToLines();

        // A digest the recording does not carry is named as absent, not invented.
        Assert.Contains(rows, row => row.Contains("digest  not recorded", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("schema v0", StringComparison.Ordinal));
    }

    [Fact]
    public void ScrubbingOnAReplayOfOneStepStaysOnItsOnlyFrame()
    {
        var playback = new ReplayPlayback(ReplaySource.Read(new StringReader(Project(Infiltration(), steps: 1, schema0: false))));

        Assert.Equal(1, playback.Document.LastIndex);

        // A tenth of a replay of one frame is one frame, so a coarse scrub is a step
        // and never leaves the replay.
        Assert.Equal(0, playback.ScrubTo(0));
        Assert.Equal(1, playback.ScrubTo(playback.Index + Math.Max(1, playback.Document.Count / 10)));
        Assert.Equal(1, playback.ScrubTo(99));
        Assert.Equal(1, playback.StepForward());
        Assert.Equal(0, playback.StepBack());
        Assert.Equal(0, playback.JumpToStart());
    }

    [Fact]
    public void AStepThatCarriesNoObservationIsAFrameWithNothingInIt()
    {
        // The reader tolerates a step with no observations, so the projection has to:
        // the tick is shown, with no agents and nothing claimed, rather than the
        // viewer faulting on a shape the format permits.
        var source = Project(Infiltration(), steps: 2, schema0: false);
        var lines = source.Replace("\n", "\u0001").Split('\u0001').ToArray();
        var step = JsonNode.Parse(lines[2])!.AsObject();
        step["Result"]!.AsObject().Remove("Observations");
        var patched = string.Join('\n', lines[0], lines[1], step.ToJsonString(), lines[3]) + "\n";

        var document = ReplaySource.Read(new StringReader(patched));

        Assert.Equal(3, document.Count);
        Assert.Equal(2, document.Frames[2].Tick);
        Assert.Empty(document.Frames[2].Agents);
        Assert.Empty(document.Frames[2].Claims);

        // The step before it is untouched, so the empty frame is this step's doing
        // and not the projection's.
        Assert.NotEmpty(document.Frames[1].Agents);
        AssertAllFramesRender(document);
    }

    [Fact]
    public void AFileThatIsNotATrajectoryIsRefusedByTheReader()
    {
        Assert.Throws<InvalidDataException>(() => ReplaySource.Read(new StringReader("{\"Kind\":\"header\"}\n")));
    }

    private static void AssertAllFramesRender(ReplayDocument document)
    {
        foreach (var size in new[] { Cockpit, new PaneSize(80, 25), new PaneSize(20, 6) })
        {
            for (var index = document.FirstIndex; index <= document.LastIndex; index++)
            {
                var cells = CockpitLayout.Render(new CockpitRequest(
                    document,
                    index,
                    size,
                    GlyphMode.Unicode,
                    Palette.PanelBackground,
                    Phase: 0.0,
                    Playback: new PlaybackState(IsPaused: true, StepsPerSecond: 4.0)));

                Assert.Equal(size.Width, cells.Width);
                Assert.Equal(size.Height, cells.Height);
            }
        }
    }

    private static string Infiltration() => File.ReadAllText(Committed("site", "infiltration.jsonl"));

    /// <summary>
    /// The committed recording reduced to its first <paramref name="steps"/> steps,
    /// optionally stripped to the fields a pre-schema-3 recording carries, with a
    /// final line that agrees about how many there are.
    /// </summary>
    private static string Project(string source, int steps, bool schema0)
    {
        var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        var stepLines = lines.Skip(1).Take(steps).ToArray();
        var kept = new List<string>();

        if (schema0)
        {
            var bare = new JsonObject();
            foreach (var field in new[] { "Kind", "Seed", "Map", "SimulationConfig" })
            {
                bare[field] = header[field]!.DeepClone();
            }

            kept.Add(bare.ToJsonString());
        }
        else
        {
            kept.Add(lines[0]);
        }

        foreach (var line in stepLines)
        {
            if (!schema0)
            {
                kept.Add(line);
                continue;
            }

            var step = JsonNode.Parse(line)!.AsObject();
            var bare = new JsonObject();
            foreach (var field in new[] { "Kind", "StepNumber", "Actions", "Result" })
            {
                bare[field] = step[field]!.DeepClone();
            }

            kept.Add(bare.ToJsonString());
        }

        var final = JsonNode.Parse(lines[^1])!.AsObject();
        final["Metrics"]!.AsObject()["TotalSteps"] = steps;
        kept.Add(final.ToJsonString());

        return string.Join('\n', kept) + "\n";
    }

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

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
}