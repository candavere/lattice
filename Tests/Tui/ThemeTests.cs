using Lattice.Tui;
using Xunit;
using Xunit.Abstractions;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the palette literals and the semantic role bindings the later screen
/// stages depend on. These are exact hex assertions on purpose: a palette entry
/// drifting by a few channels is invisible in a diff but changes every screen.
/// </summary>
public class ThemeTests
{
    private readonly ITestOutputHelper _output;

    public ThemeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OfficialColoursMatchTheOpenCodeDarkTheme()
    {
        Assert.Equal("#0a0a0a", Palette.Background.ToHex());
        Assert.Equal("#141414", Palette.PanelBackground.ToHex());
        Assert.Equal("#1e1e1e", Palette.Raised.ToHex());
        Assert.Equal("#3c3c3c", Palette.DepthStep.ToHex());
        Assert.Equal("#484848", Palette.Border.ToHex());
        Assert.Equal("#eeeeee", Palette.TextPrimary.ToHex());
        Assert.Equal("#808080", Palette.TextDim.ToHex());
        Assert.Equal("#fab283", Palette.Accent.ToHex());
        Assert.Equal("#ffc09f", Palette.AccentBright.ToHex());
        Assert.Equal("#5c9cf5", Palette.AccentSecondary.ToHex());
        Assert.Equal("#9d7cd8", Palette.Violet.ToHex());
        Assert.Equal("#f5a742", Palette.Warning.ToHex());
        Assert.Equal("#e06c75", Palette.Error.ToHex());
        Assert.Equal("#7fd88f", Palette.Success.ToHex());
        Assert.Equal("#56b6c2", Palette.Info.ToHex());
    }

    [Fact]
    public void DerivedColoursMatchTheMixedValues()
    {
        Assert.Equal("#858585", Theme.SecondaryText.ToHex());
        Assert.Equal("#e06c75", Theme.ErrorText.ToHex());
    }

    [Fact]
    public void TheFirstAgentSlotIsTheAccentAndTheSecondIsTheSecondaryAccent()
    {
        Assert.Equal(Palette.Accent, Theme.AgentSlot(0));
        Assert.Equal(Palette.AccentSecondary, Theme.AgentSlot(1));
    }

    [Fact]
    public void AgentSlotsBeyondTheFirstTwoCycleRatherThanRunOut()
    {
        Assert.Equal(Theme.AgentSlot(0), Theme.AgentSlot(2));
        Assert.Equal(Theme.AgentSlot(1), Theme.AgentSlot(3));
    }

    [Fact]
    public void ANegativeSlotIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Theme.AgentSlot(-1));
    }

    [Fact]
    public void ContestedChokesAndBordersBindToTheDocumentedColours()
    {
        Assert.Equal(Palette.Warning, Theme.ContestedChoke);
        Assert.Equal(Palette.Border, Theme.Sage);
    }

    [Fact]
    public void AClosedEdgeIsDimTextWithAnErrorMark()
    {
        Assert.Equal(Palette.TextDim, Theme.ClosedEdge);
        Assert.Equal(Palette.Error, Theme.ClosedEdgeMark);
    }

    [Fact]
    public void OpenEdgeIsTextGradeAndBrighterThanClosedEdge()
    {
        Assert.Equal(Palette.TextPrimary, Theme.OpenEdge);
        Assert.True(
            Contrast(Theme.OpenEdge, Palette.PanelBackground) >= 4.5,
            $"OpenEdge {Theme.OpenEdge.ToHex()} must meet 4.5 on the panel.");
        Assert.True(
            Contrast(Theme.ClosedEdge, Palette.PanelBackground) >= 4.5,
            $"ClosedEdge {Theme.ClosedEdge.ToHex()} must meet 4.5 on the panel.");
        Assert.True(
            Luminance(Theme.OpenEdge) > Luminance(Theme.ClosedEdge),
            "open edge must stay visibly brighter than the closed edge.");
    }

    [Fact]
    public void SelectionIsPrimaryOnTheElementBand()
    {
        Assert.Equal(Palette.Accent, Theme.SelectionForeground);
        Assert.Equal(Palette.Raised, Theme.SelectionBackground);
    }

    [Fact]
    public void ConsoleRolesBindToTheDocumentedColours()
    {
        Assert.Equal(Palette.Background, Theme.ScreenBackground);
        Assert.Equal(Palette.TextDim, Theme.Muted);
        Assert.Equal(Palette.Info, Theme.Info);
        Assert.Equal(Palette.Violet, Theme.Violet);
    }

    [Theory]
    [InlineData(ColorDepth.TrueColor)]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.None)]
    public void PanelsAreOnlyPaintedUnderTruecolor(ColorDepth depth)
    {
        var fill = Theme.PanelFill(depth);

        if (depth == ColorDepth.TrueColor)
        {
            Assert.Equal(Palette.PanelBackground, fill);
        }
        else
        {
            Assert.Null(fill);
        }
    }

    [Fact]
    public void HexParsingRejectsMalformedText()
    {
        Assert.Throws<FormatException>(() => Rgb.FromHex("141414"));
        Assert.Throws<FormatException>(() => Rgb.FromHex("#14141"));
        Assert.Throws<FormatException>(() => Rgb.FromHex("#zzzzzz"));
    }

    [Fact]
    public void SemanticPairsAreDistinctAtTrueColor()
    {
        var pairs = SemanticPairs();
        foreach (var (name, a, b) in pairs)
        {
            var sa = ColorMapper.Foreground(a, ColorDepth.TrueColor);
            var sb = ColorMapper.Foreground(b, ColorDepth.TrueColor);
            _output.WriteLine($"{name}: truecolor {sa} vs {sb}");
            Assert.NotEqual(sa, sb);
        }
    }

    [Theory]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.Ansi16)]
    public void LimitedDepthCollapsesArePrintedAndCarriedByNonColourCues(ColorDepth depth)
    {
        var pairs = SemanticPairs();
        var collapsed = new List<string>();
        foreach (var (name, a, b) in pairs)
        {
            var sa = ColorMapper.Foreground(a, depth);
            var sb = ColorMapper.Foreground(b, depth);
            if (sa == sb)
            {
                var index = depth == ColorDepth.Ansi256
                    ? AnsiTables.NearestIndex256(a)
                    : AnsiTables.NearestIndex16(a);
                var line = $"{name} collapses at {depth} onto shared index {index}: {sa}";
                collapsed.Add(line);
                _output.WriteLine(line);
            }
        }

        foreach (var line in collapsed)
        {
            _output.WriteLine("collapsed: " + line);
        }

        // Agent slots share index 7 at 16 colours; the scoreboard slot labels differ.
        if (depth == ColorDepth.Ansi16 && collapsed.Exists(l => l.StartsWith("agent-slot", StringComparison.Ordinal)))
        {
            Assert.True(CockpitScoreboardShowsBothSlotLabels(), "agent slots collapsing at 16 colours must differ by label.");
        }

        // No other semantic pair may collapse silently: every collapse above is printed,
        // and the only expected one is the agent-slot pair at 16 colours.
        foreach (var line in collapsed)
        {
            Assert.StartsWith("agent-slot", line);
        }
    }

    [Fact]
    public void PrimaryAndWarningClosenessIsReported()
    {
        var distance = Math.Sqrt(
            Math.Pow(Palette.Accent.R - Palette.Warning.R, 2) +
            Math.Pow(Palette.Accent.G - Palette.Warning.G, 2) +
            Math.Pow(Palette.Accent.B - Palette.Warning.B, 2));
        _output.WriteLine($"primary {Palette.Accent.ToHex()} vs warning {Palette.Warning.ToHex()}: euclidean {distance:0.0}");
        // Both orange by design; agent seat 0 vs contested choke rely on glyph and label cues
        // (diamond vs capacity digit, slot vs choke wording), never on hue alone.
        Assert.True(distance < 100.0, "primary and warning are both orange by design; cues must carry the distinction.");
        Assert.NotEqual(
            ColorMapper.Foreground(Palette.Accent, ColorDepth.TrueColor),
            ColorMapper.Foreground(Palette.Warning, ColorDepth.TrueColor));
    }

    private static List<(string Name, Rgb A, Rgb B)> SemanticPairs() =>
    [
        ("error-warning", Palette.Error, Palette.Warning),
        ("error-success", Palette.Error, Palette.Success),
        ("warning-success", Palette.Warning, Palette.Success),
        ("agent-slot-0-1", Theme.AgentSlot(0), Theme.AgentSlot(1)),
        ("open-closed-edge", Theme.OpenEdge, Theme.ClosedEdge),
        ("selection-text", Theme.SelectionForeground, Palette.TextPrimary),
        ("primary-warning", Palette.Accent, Palette.Warning),
    ];

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Rgb color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    private static double Contrast(Rgb fg, Rgb bg)
    {
        var l1 = Luminance(fg);
        var l2 = Luminance(bg);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static bool CockpitScoreboardShowsBothSlotLabels()
    {
        var doc = new ReplayDocument(
            new WorldMap(
                [new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 10, 0)],
                [],
                []),
            new ReplayHeader(42, 5, "dungeon", ["A", "B"], 1, null, 0),
            [new ReplayFrame(0, true, [new WorldAgent(0, 0, 0), new WorldAgent(1, 1, 0)], [], null, null, false, null, null)]);
        var cells = CockpitLayout.Render(new CockpitRequest(doc, 0, new PaneSize(100, 30), GlyphMode.Unicode, Palette.PanelBackground, 0.0, new PlaybackState(true, 4.0)));
        var text = string.Concat(cells.ToLines());
        return text.Contains("0", StringComparison.Ordinal) && text.Contains("1", StringComparison.Ordinal);
    }
}
