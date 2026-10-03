using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the palette literals and the semantic role bindings the later screen
/// stages depend on. These are exact hex assertions on purpose: a palette entry
/// drifting by a few channels is invisible in a diff but changes every screen.
/// </summary>
public class ThemeTests
{
    [Fact]
    public void SampledColoursMatchTheReferenceImage()
    {
        Assert.Equal("#1f2420", Palette.PanelBackground.ToHex());
        Assert.Equal("#272b22", Palette.PanelBackgroundAlt.ToHex());
        Assert.Equal("#2c3625", Palette.Raised.ToHex());
        Assert.Equal("#81897d", Palette.Border.ToHex());
        Assert.Equal("#dde1dc", Palette.TextPrimary.ToHex());
        Assert.Equal("#696f65", Palette.TextDim.ToHex());
        Assert.Equal("#adcb66", Palette.Accent.ToHex());
        Assert.Equal("#c8db73", Palette.AccentBright.ToHex());
        Assert.Equal("#e8a7bc", Palette.AccentSecondary.ToHex());
    }

    [Fact]
    public void DerivedColoursMatchTheMixedValues()
    {
        Assert.Equal("#e6c84a", Palette.Warning.ToHex());
        Assert.Equal("#d9645a", Palette.Error.ToHex());
        Assert.Equal("#6fd0c8", Palette.Info.ToHex());
        Assert.Equal("#a1c35e", Palette.Success.ToHex());
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
    public void SelectionIsLimeOnTheRaisedSurface()
    {
        Assert.Equal(Palette.Accent, Theme.SelectionForeground);
        Assert.Equal(Palette.Raised, Theme.SelectionBackground);
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
        Assert.Throws<FormatException>(() => Rgb.FromHex("1f2420"));
        Assert.Throws<FormatException>(() => Rgb.FromHex("#1f242"));
        Assert.Throws<FormatException>(() => Rgb.FromHex("#zzzzzz"));
    }
}