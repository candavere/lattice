using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The replay viewer read against the two recordings the repository ships:
/// <c>site/demo.jsonl</c> (four zones, instantaneous transit) and
/// <c>site/infiltration.jsonl</c> (six zones, capacity-one portcullises,
/// multi-tick transit).
/// </summary>
/// <remarks>
/// <para>
/// The golden files under <c>Tests/Tui/Goldens</c> are the grid, glyph by glyph.
/// Each was generated once from this renderer and then read against the recording
/// it claims to draw — the agents in the recorded zones at the recorded scores,
/// the resources the recorded claims, the choke capacities the recorded map
/// declares — before being frozen here. They are a specification, not a snapshot
/// of whatever the code happened to do: a change in the renderer that moves a
/// mark fails here until the golden is re-read and re-justified.
/// </para>
/// <para>
/// Both recordings are read read-only from the repository, exactly as a user
/// would; neither is copied, rewritten, or re-recorded.
/// </para>
/// </remarks>
public class ReplayGoldenTests
{
    /// <summary>The world pane's inner area at the cockpit's 100x30 minimum.</summary>
    private static readonly PaneSize WorldPane = new(63, 23);

    /// <summary>The window the cockpit gives the world pane below the minimum size.</summary>
    private static readonly PaneSize FallbackPane = new(58, 14);

    [Fact]
    public void DemoStartFrameIsTheRecordedStartState()
    {
        var document = Demo();

        Assert.True(document.Frames[0].IsStart);
        Assert.Equal(0, document.Frames[0].Tick);
        Assert.Equal(new[] { 0, 0 }, document.Frames[0].Agents.Select(agent => agent.Score).ToArray());
        Assert.Equal(new[] { 0, 1 }, document.Frames[0].Agents.Select(agent => agent.ZoneId).ToArray());
        AssertGolden("demo-frame-00-start", document, 0);
    }

    [Fact]
    public void DemoFrameOneShowsTheResourceItRecordedAsClaimed()
    {
        var document = Demo();
        var frame = document.Frames[1];

        Assert.Equal(new[] { 0 }, frame.Claims);
        Assert.Equal(1, frame.Agents[0].Score);
        AssertGolden("demo-frame-01-claimed", document, 1);
    }

    [Fact]
    public void DemoLastFrameIsTheTerminalTickTheRecordingStates()
    {
        var document = Demo();
        var frame = document.Frames[document.LastIndex];

        Assert.True(frame.IsTerminal);
        Assert.Equal("resources-exhausted", frame.TerminalReason);
        Assert.Equal(0, frame.WinnerSlot);
        Assert.Equal(27, frame.Tick);
        AssertGolden("demo-frame-27-last", document, document.LastIndex);
    }

    [Fact]
    public void InfiltrationStartFrameIsTheRecordedStartState()
    {
        var document = Infiltration();

        Assert.True(document.Frames[0].IsStart);
        Assert.Equal(new[] { 0, 1 }, document.Frames[0].Agents.Select(agent => agent.ZoneId).ToArray());
        AssertGolden("infiltration-frame-00-start", document, 0);
    }

    [Fact]
    public void InfiltrationFrameTwoDrawsTheAgentItsCountdownPlacesMidCrossing()
    {
        var document = Infiltration();
        var frame = document.Frames[2];

        Assert.Equal(new WorldTransit(2, 4, RemainingTicks: 1, TotalTicks: 2), frame.Agents[1].Transit);
        AssertGolden("infiltration-frame-02-transit", document, 2);
    }

    [Fact]
    public void InfiltrationLastFrameIsTheTerminalTickTheRecordingStates()
    {
        var document = Infiltration();
        var frame = document.Frames[document.LastIndex];

        Assert.True(frame.IsTerminal);
        Assert.Equal("resources-exhausted", frame.TerminalReason);
        Assert.Equal(1, frame.WinnerSlot);
        Assert.Equal(20, frame.Tick);
        AssertGolden("infiltration-frame-20-last", document, document.LastIndex);
    }

    [Fact]
    public void EveryRecordedStepOfBothRecordingsRendersAtTheMinimumSizeAndAtTheFallback()
    {
        foreach (var document in new[] { Demo(), Infiltration() })
        {
            for (var index = document.FirstIndex; index <= document.LastIndex; index++)
            {
                var atMinimum = Render(document, index, WorldPane);
                var fallback = Render(document, index, FallbackPane);

                Assert.Equal(WorldPane.Width, atMinimum.Cells.Width);
                Assert.Equal(WorldPane.Height, atMinimum.Cells.Height);
                Assert.Equal(FallbackPane.Width, fallback.Cells.Width);
                Assert.Equal(FallbackPane.Height, fallback.Cells.Height);

                // Nothing is dropped to fit and nothing lands outside: every zone
                // the map has is placed inside the pane it was given.
                foreach (var (render, size) in new[] { (atMinimum, WorldPane), (fallback, FallbackPane) })
                {
                    Assert.Empty(render.UnplacedZoneIds);
                    Assert.Equal(document.Map.Zones.Length, render.Zones.Count);
                    Assert.All(
                        render.Zones,
                        placement =>
                        {
                            Assert.InRange(placement.X, 0, size.Width - 1);
                            Assert.InRange(placement.Y, 0, size.Height - 1);
                        });
                }
            }
        }
    }

    [Fact]
    public void AFrameCountIsTheRecordedStepsPlusTheStartFrame()
    {
        Assert.Equal(Demo().Header.RecordedSteps + 1, Demo().Count);
        Assert.Equal(28, Demo().Count);
        Assert.Equal(Infiltration().Header.RecordedSteps + 1, Infiltration().Count);
        Assert.Equal(21, Infiltration().Count);
    }

    [Fact]
    public void EveryFramesScoresAreTheRecordedScores()
    {
        var demo = Demo();
        var infiltration = Infiltration();

        // Read off the recordings themselves, so the panes' numbers are checked
        // against the file rather than against a copy of the projection.
        Assert.Equal(
            new[] { 1, 0 },
            demo.Frames[1].Agents.Select(agent => agent.Score).ToArray());
        Assert.Equal(
            new[] { 2, 1 },
            demo.Frames[10].Agents.Select(agent => agent.Score).ToArray());
        Assert.Equal(
            new[] { 8, 1 },
            demo.Frames[demo.LastIndex].Agents.Select(agent => agent.Score).ToArray());
        Assert.Equal(
            new[] { 0, 1 },
            infiltration.Frames[10].Agents.Select(agent => agent.Score).ToArray());
        Assert.Equal(
            new[] { 0, 3 },
            infiltration.Frames[infiltration.LastIndex].Agents.Select(agent => agent.Score).ToArray());
    }

    [Fact]
    public void SteppingBackAndForwardReturnsTheIdenticalFrame()
    {
        foreach (var document in new[] { Demo(), Infiltration() })
        {
            var index = 0;
            for (var i = 0; i < 5; i++)
            {
                index = document.StepForward(index);
            }

            var forward = document[index];
            var returned = document[document.StepForward(document.StepBack(index))];

            Assert.True(forward.Equals(returned), "stepping back and forward returned a different frame.");
            Assert.False(document[index - 1].Equals(returned), "the frame before it is not the same frame.");
        }
    }

    [Fact]
    public void TwoReadsOfTheSameRecordingProduceEqualFrames()
    {
        var first = Demo();
        var second = Demo();

        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index], second[index]);
        }
    }

    [Fact]
    public void ARereadingOfTheSameFileIsTheSameDocument()
    {
        // Frames are values: a frame built twice from the same bytes compares
        // equal, which is what makes back-stepping a comparison and not a hope.
        Assert.Equal(Demo().Frames[7], Demo().Frames[7]);
    }

    private static ReplayDocument Demo() => ReplaySource.ReadFile(Committed("site", "demo.jsonl"));

    private static ReplayDocument Infiltration() =>
        ReplaySource.ReadFile(Committed("site", "infiltration.jsonl"));

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

    private static string Golden(string name) =>
        File.ReadAllText(Committed("Tests", "Tui", "Goldens", name + ".txt")).TrimEnd('\n');

    private static void AssertGolden(string name, ReplayDocument document, int index)
    {
        Assert.Equal(Golden(name), string.Join("\n", Render(document, index, WorldPane).Cells.ToLines()));
    }

    private static WorldRender Render(ReplayDocument document, int index, PaneSize size) =>
        WorldRenderer.Render(new WorldRenderRequest(
            document.Map,
            document[index],
            document.TrailBefore(index, 3),
            size,
            GlyphMode.Unicode,
            Palette.PanelBackground));
}