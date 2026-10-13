using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins what <see cref="Rgb.FromHex"/> accepts. The parser read each byte pair
/// with <c>NumberStyles.HexNumber</c>, which permits leading and trailing white
/// space, so <c>"# 00000"</c> parsed as <c>#000000</c>: a seven-character string
/// that is not six hex digits came back as black. The contract promises a
/// <see cref="FormatException"/> for anything else, so every pair is now read as
/// hex digits and nothing else.
///
/// The accepted cases are pinned as literals, not by re-parsing, so a future
/// loosening of the parser shows up as a test failure rather than as a colour
/// that quietly changed.
/// </summary>
public class RgbHexParsingTests
{
    [Theory]
    [InlineData("# 00000")]
    [InlineData("#0 0000")]
    [InlineData("#00 000")]
    [InlineData("#000 00")]
    [InlineData("#0000 0")]
    [InlineData("#00000 ")]
    [InlineData("#00\t000")]
    [InlineData("#\t00000")]
    public void WhiteSpaceInsideAnyBytePairIsRejected(string hex)
    {
        Assert.Throws<FormatException>(() => Rgb.FromHex(hex));
    }

    [Theory]
    [InlineData("#+00000")]
    [InlineData("#-00000")]
    [InlineData("# 0000+")]
    [InlineData("#0x0000")]
    [InlineData("#0000x")]
    [InlineData("#00x000")]
    [InlineData("#00_000")]
    [InlineData("#00,000")]
    public void AnythingThatIsNotSixHexDigitsIsRejected(string hex)
    {
        Assert.Throws<FormatException>(() => Rgb.FromHex(hex));
    }

    [Fact]
    public void SixHexDigitsParseToTheChannelValues()
    {
        Assert.Equal(new Rgb(0x14, 0x14, 0x14), Rgb.FromHex("#141414"));
        Assert.Equal(new Rgb(0xFA, 0xB2, 0x83), Rgb.FromHex("#fab283"));
        Assert.Equal(new Rgb(0x00, 0x00, 0x00), Rgb.FromHex("#000000"));
        Assert.Equal(new Rgb(0xFF, 0xFF, 0xFF), Rgb.FromHex("#ffffff"));
    }

    [Fact]
    public void EitherCaseParsesToTheSameColour()
    {
        Assert.Equal(Rgb.FromHex("#dde1dc"), Rgb.FromHex("#DDE1DC"));
        Assert.Equal(Rgb.FromHex("#adcb66"), Rgb.FromHex("#AdCb66"));
    }

    [Fact]
    public void EveryPaletteColourParsesToItsLiteralChannels()
    {
        Assert.Equal(new Rgb(0x0A, 0x0A, 0x0A), Palette.Background);
        Assert.Equal(new Rgb(0x14, 0x14, 0x14), Palette.PanelBackground);
        Assert.Equal(new Rgb(0x1E, 0x1E, 0x1E), Palette.Raised);
        Assert.Equal(new Rgb(0x3C, 0x3C, 0x3C), Palette.DepthStep);
        Assert.Equal(new Rgb(0x48, 0x48, 0x48), Palette.Border);
        Assert.Equal(new Rgb(0xEE, 0xEE, 0xEE), Palette.TextPrimary);
        Assert.Equal(new Rgb(0x80, 0x80, 0x80), Palette.TextDim);
        Assert.Equal(new Rgb(0xFA, 0xB2, 0x83), Palette.Accent);
        Assert.Equal(new Rgb(0xFF, 0xC0, 0x9F), Palette.AccentBright);
        Assert.Equal(new Rgb(0x5C, 0x9C, 0xF5), Palette.AccentSecondary);
        Assert.Equal(new Rgb(0x9D, 0x7C, 0xD8), Palette.Violet);
        Assert.Equal(new Rgb(0xF5, 0xA7, 0x42), Palette.Warning);
        Assert.Equal(new Rgb(0xE0, 0x6C, 0x75), Palette.Error);
        Assert.Equal(new Rgb(0x7F, 0xD8, 0x8F), Palette.Success);
        Assert.Equal(new Rgb(0x56, 0xB6, 0xC2), Palette.Info);
    }

    [Fact]
    public void AParsedColourRoundTripsBackToTheSameHex()
    {
        Assert.Equal("#adcb66", Rgb.FromHex("#adcb66").ToHex());
        Assert.Equal("#000000", Rgb.FromHex("#000000").ToHex());
        Assert.Equal("#ffffff", Rgb.FromHex("#FFFFFF").ToHex());
    }
}