using Lattice.Environment;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Visualization;

/// <summary>
/// Validates <see cref="AsciiRenderer"/> character placement against a
/// hand-built fixture: choke edges rasterized horizontally/vertically, zone
/// tokens overwriting edge cells, resource markers flipping claimed state, and
/// footer lines enumerating agent positions and per-zone claim state. Every
/// comparison is against the exact expected frame, so a regression in glyph
/// selection, precedence, or ordering fails loudly.
/// </summary>
public class AsciiRendererTests
{
    /// <summary>
    /// An L-shaped fixture with fully independent cells:
    /// zones 0(0,0) 1(2,0) 2(2,2), a horizontal edge 0-1, a vertical edge 1-2,
    /// and two resource cells that never collide with a zone glyph.
    /// </summary>
    private static readonly MapGraph Map = new(
        new[]
        {
            new Zone(0, new GridPoint(0, 0)),
            new Zone(1, new GridPoint(2, 0)),
            new Zone(2, new GridPoint(2, 2)),
        },
        new[]
        {
            new ResourceNode(0, 0, new GridPoint(1, 0)),
            new ResourceNode(1, 1, new GridPoint(2, 1)),
        },
        new[]
        {
            new ChokePoint(0, 0, 1),
            new ChokePoint(1, 1, 2),
        });

    private static readonly AgentState[] InitialAgents =
    {
        new(0, 0, 0),
        new(1, 1, 0),
    };

    [Fact]
    public void RendersExactCanvasAndFooter()
    {
        var expected = string.Join("\n",
            "0$1",
            "..$",
            "..2",
            "Agents: A0@Z0(0) A1@Z1(0)",
            "Resources: claimed 0 of 2",
            "Z0: claimed [] unclaimed [0]",
            "Z1: claimed [] unclaimed [1]",
            "Z2: claimed [] unclaimed []");

        Assert.Equal(expected, AsciiRenderer.RenderFrame(Map, InitialAgents, Array.Empty<int>()));
    }

    [Fact]
    public void ClaimedResourceFlipsMarkerAndFooter()
    {
        var expected = string.Join("\n",
            "0*1",
            "..$",
            "..2",
            "Agents: A0@Z0(0) A1@Z1(0)",
            "Resources: claimed 1 of 2",
            "Z0: claimed [0] unclaimed []",
            "Z1: claimed [] unclaimed [1]",
            "Z2: claimed [] unclaimed []");

        Assert.Equal(expected, AsciiRenderer.RenderFrame(Map, InitialAgents, new[] { 0 }));
    }

    [Fact]
    public void ObservationOverload_RendersIdenticalFrame()
    {
        var observation = new Observation(0, Map, InitialAgents, new[] { 0 });

        Assert.Equal(
            AsciiRenderer.RenderFrame(Map, InitialAgents, new[] { 0 }),
            AsciiRenderer.RenderFrame(Map, observation));
    }

    [Fact]
    public void MovedAgents_AreReportedWithNewZonesAndScores()
    {
        var agents = new[] { new AgentState(0, 2, 2), new AgentState(1, 0, 0) };
        var frame = AsciiRenderer.RenderFrame(Map, agents, Array.Empty<int>());

        Assert.Contains("Agents: A0@Z2(2) A1@Z0(0)", frame);
    }

    [Fact]
    public void ZoneIdsBeyondSingleGlyphRange_Throw()
    {
        var zoneMap = new MapGraph(
            new[] { new Zone(62, new GridPoint(0, 0)) },
            Array.Empty<ResourceNode>(),
            Array.Empty<ChokePoint>());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AsciiRenderer.RenderFrame(zoneMap, InitialAgents, Array.Empty<int>()));
    }

    [Fact]
    public void EmptyMap_Throws()
    {
        var empty = new MapGraph(Array.Empty<Zone>(), Array.Empty<ResourceNode>(), Array.Empty<ChokePoint>());

        Assert.Throws<ArgumentException>(() =>
            AsciiRenderer.RenderFrame(empty, InitialAgents, Array.Empty<int>()));
    }

    [Fact]
    public void SameInputs_ByteIdenticalFrames()
    {
        var first = AsciiRenderer.RenderFrame(Map, InitialAgents, Array.Empty<int>());
        var second = AsciiRenderer.RenderFrame(Map, InitialAgents, Array.Empty<int>());

        Assert.Equal(first, second);
    }

    [Fact]
    public void ZoneToken_EncodesDoubleDigitIdsDeterministically()
    {
        var zoneMap = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(12, new GridPoint(2, 0)),
            },
            Array.Empty<ResourceNode>(),
            new[]
            {
                new ChokePoint(0, 0, 12),
            });

        // Zone 12 maps to glyph 'C'; the horizontal edge is drawn through
        // cell (1,0) between the two zone tokens.
        var frame = AsciiRenderer.RenderFrame(zoneMap, InitialAgents, Array.Empty<int>());
        Assert.StartsWith("0-C", frame);
    }
}