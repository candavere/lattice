using System.Xml.Linq;
using Lattice.Environment;
using Lattice.Visualization;
using Xunit;

namespace Lattice.Tests.Visualization;

/// <summary>
/// Validates <see cref="SvgRenderer"/> output: byte-exact frame text for a
/// hand-built fixture, claim-driven resource fills, per-agent color coding,
/// coordinate/id tooltips, XML well-formedness, and determinism. The exact
/// document assertion pins attribute order and number formatting so any
/// accidental drift in the projection fails loudly.
/// </summary>
public class SvgRendererTests
{
    /// <summary>
    /// A single-zone, single-resource, single-agent fixture with deliberately
    /// small coordinates so the projected pixel values are trivially
    /// hand-verifiable: zone 0 at (40,40), resource at (60,40).
    /// </summary>
    private static readonly MapGraph TinyMap = new(
        new[] { new Zone(0, new GridPoint(1, 1)) },
        new[] { new ResourceNode(0, 0, new GridPoint(2, 1)) },
        Array.Empty<ChokePoint>());

    /// <summary>
    /// An L-shaped map (already used by the ASCII renderer tests) for claim
    /// color and choke-edge coverage assertions.
    /// </summary>
    private static readonly MapGraph LMap = new(
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

    private static readonly AgentState[] TinyAgents = { new(0, 0, 0) };

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
    public void RendersExactDocumentForTinyFixture()
    {
        var expected = string.Join("\n",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"80\" viewBox=\"0 0 100 80\">",
            "<rect width=\"100\" height=\"80\" fill=\"#0F111A\"/>",
            "<g id=\"edges\">",
            "</g>",
            "<g id=\"resources\">",
            "<circle cx=\"60\" cy=\"40\" r=\"5\" fill=\"#E0AF68\"/>",
            "</g>",
            "<g id=\"zones\">",
            "<g id=\"zone-0\"><title>zone 0 (1, 1)</title><circle cx=\"40\" cy=\"40\" r=\"12\" fill=\"#10131F\" stroke=\"#7AA2F7\" stroke-width=\"2\"/><text x=\"40\" y=\"40\" text-anchor=\"middle\" dominant-baseline=\"central\" font-family=\"monospace\" font-size=\"13\" fill=\"#C0CAF5\">0</text></g>",
            "</g>",
            "<g id=\"agents\">",
            "<g id=\"agent-0\"><title>agent 0, zone 0, score 0</title><circle cx=\"40\" cy=\"40\" r=\"7\" fill=\"#F7768E\" stroke=\"#FFFFFF\" stroke-width=\"1.5\"/><text x=\"40\" y=\"40\" text-anchor=\"middle\" dominant-baseline=\"central\" font-family=\"monospace\" font-size=\"9\" fill=\"#FFFFFF\">0</text></g>",
            "</g>",
            "</svg>");

        Assert.Equal(expected, SvgRenderer.RenderFrame(TinyMap, TinyAgents, Array.Empty<int>()));
    }

    [Fact]
    public void ClaimSetSwitchesResourceFill()
    {
        var agents = new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 0) };

        var unclaimed = SvgRenderer.RenderFrame(LMap, agents, Array.Empty<int>());
        Assert.Equal(2, Occurrences(unclaimed, SvgRenderer.UnclaimedResourceFill));
        Assert.Equal(0, Occurrences(unclaimed, SvgRenderer.ClaimedResourceFill));

        var oneClaimed = SvgRenderer.RenderFrame(LMap, agents, new[] { 0 });
        Assert.Equal(1, Occurrences(oneClaimed, SvgRenderer.UnclaimedResourceFill));
        Assert.Equal(1, Occurrences(oneClaimed, SvgRenderer.ClaimedResourceFill));
        Assert.DoesNotContain("#3DA66B\" opacity", oneClaimed);
    }

    [Fact]
    public void ChokeEdgesAreDrawnWithVisualWeight()
    {
        var agents = new[] { new AgentState(0, 0, 0), new AgentState(1, 1, 0) };
        var frame = SvgRenderer.RenderFrame(LMap, agents, Array.Empty<int>());

        Assert.Contains("<g id=\"edges\">", frame);
        Assert.Equal(2, Occurrences(frame, $"stroke=\"{SvgRenderer.EdgeStroke}\""));
        Assert.Equal(2, Occurrences(frame, "stroke-width=\"5\""));
    }

    [Fact]
    public void ZoneTooltipsCarryIdAndCoordinates()
    {
        var frame = SvgRenderer.RenderFrame(LMap, TinyAgents, Array.Empty<int>());

        Assert.Contains("<title>zone 1 (2, 0)</title>", frame);
        Assert.Contains("<title>zone 2 (2, 2)</title>", frame);
    }

    [Fact]
    public void AgentTokensAreColorCodedById()
    {
        var agents = new[]
        {
            new AgentState(0, 0, 0),
            new AgentState(1, 1, 0),
            new AgentState(2, 2, 0),
        };
        var frame = SvgRenderer.RenderFrame(LMap, agents, Array.Empty<int>());

        foreach (var color in SvgRenderer.AgentPalette.Take(3))
        {
            Assert.Contains(color, frame);
        }

        Assert.DoesNotContain(SvgRenderer.AgentPalette[3], frame); // only 3 agents present
    }

    [Fact]
    public void ObservationOverload_RendersIdenticalFrame()
    {
        var observation = new Observation(0, LMap, TinyAgents, new[] { 0 });

        Assert.Equal(
            SvgRenderer.RenderFrame(LMap, TinyAgents, new[] { 0 }),
            SvgRenderer.RenderFrame(LMap, observation));
    }

    [Fact]
    public void OutputIsWellFormedXml()
    {
        var frame = SvgRenderer.RenderFrame(LMap, TinyAgents, Array.Empty<int>());
        var document = XDocument.Parse(frame);

        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", document.Root.Name.NamespaceName);
    }

    [Fact]
    public void SameState_ByteIdenticalDocuments()
    {
        var first = SvgRenderer.RenderFrame(LMap, TinyAgents, Array.Empty<int>());
        var second = SvgRenderer.RenderFrame(LMap, TinyAgents, Array.Empty<int>());

        Assert.Equal(first, second);
    }

    [Fact]
    public void EmptyMap_Throws()
    {
        var empty = new MapGraph(Array.Empty<Zone>(), Array.Empty<ResourceNode>(), Array.Empty<ChokePoint>());

        Assert.Throws<ArgumentException>(() => SvgRenderer.RenderFrame(empty, TinyAgents, Array.Empty<int>()));
    }
}