using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Readability contract: essential text meets 4.5:1 on the backgrounds Lattice
/// actually paints, using standard sRGB linearisation. Failing first, then fixed
/// by semantic roles in Theme.
/// </summary>
public class ReadabilityContrastTests
{
    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Rgb color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    internal static double Contrast(Rgb fg, Rgb bg)
    {
        var l1 = Luminance(fg);
        var l2 = Luminance(bg);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [Fact]
    public void SecondaryTextMeetsFourPointFiveOnPanelBackground()
    {
        Assert.True(Contrast(Theme.SecondaryText, Palette.PanelBackground) >= 4.5,
            $"SecondaryText {Theme.SecondaryText.ToHex()} on {Palette.PanelBackground.ToHex()} = {Contrast(Theme.SecondaryText, Palette.PanelBackground):0.00}:1");
    }

    [Fact]
    public void SecondaryTextMeetsFourPointFiveOnRaisedSelectionBackground()
    {
        Assert.True(Contrast(Theme.SecondaryText, Palette.Raised) >= 4.5);
    }

    [Fact]
    public void HeadingMeetsFourPointFiveOnPanelBackground()
    {
        Assert.True(Contrast(Theme.Heading, Palette.PanelBackground) >= 4.5);
    }

    [Fact]
    public void KeyHintMeetsFourPointFiveOnPanelBackground()
    {
        Assert.True(Contrast(Theme.KeyHint, Palette.PanelBackground) >= 4.5);
    }

    [Fact]
    public void TableHeaderMeetsFourPointFiveOnPanelBackground()
    {
        Assert.True(Contrast(Theme.TableHeader, Palette.PanelBackground) >= 4.5);
    }

    [Fact]
    public void SelectionForegroundMeetsFourPointFiveOnSelectionBackground()
    {
        Assert.True(Contrast(Theme.SelectionForeground, Theme.SelectionBackground) >= 4.5);
    }

    [Fact]
    public void ErrorTextMeetsFourPointFiveOnPanelBackground()
    {
        Assert.True(Contrast(Theme.ErrorText, Palette.PanelBackground) >= 4.5,
            $"ErrorText {Theme.ErrorText.ToHex()} on {Palette.PanelBackground.ToHex()} = {Contrast(Theme.ErrorText, Palette.PanelBackground):0.00}:1");
    }

    [Fact]
    public void SampledPaletteLiteralsAreUnchanged()
    {
        Assert.Equal("#1f2420", Palette.PanelBackground.ToHex());
        Assert.Equal("#696f65", Palette.TextDim.ToHex());
        Assert.Equal("#adcb66", Palette.Accent.ToHex());
    }

    [Fact]
    public void DecorationRemainsTheSampledDim()
    {
        Assert.Equal(Palette.TextDim, Theme.Decoration);
    }
}
