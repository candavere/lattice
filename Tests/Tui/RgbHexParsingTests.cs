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
        Assert.Equal(new Rgb(0x1F, 0x24, 0x20), Rgb.FromHex("#1f2420"));
        Assert.Equal(new Rgb(0xAD, 0xCB, 0x66), Rgb.FromHex("#adcb66"));
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
        Assert.Equal(new Rgb(0x1F, 0x24, 0x20), Palette.PanelBackground);
        Assert.Equal(new Rgb(0x27, 0x2B, 0x22), Palette.PanelBackgroundAlt);
        Assert.Equal(new Rgb(0x2C, 0x36, 0x25), Palette.Raised);
        Assert.Equal(new Rgb(0x81, 0x89, 0x7D), Palette.Border);
        Assert.Equal(new Rgb(0xDD, 0xE1, 0xDC), Palette.TextPrimary);
        Assert.Equal(new Rgb(0x69, 0x6F, 0x65), Palette.TextDim);
        Assert.Equal(new Rgb(0xAD, 0xCB, 0x66), Palette.Accent);
        Assert.Equal(new Rgb(0xC8, 0xDB, 0x73), Palette.AccentBright);
        Assert.Equal(new Rgb(0xE8, 0xA7, 0xBC), Palette.AccentSecondary);
        Assert.Equal(new Rgb(0xE6, 0xC8, 0x4A), Palette.Warning);
        Assert.Equal(new Rgb(0xD9, 0x64, 0x5A), Palette.Error);
        Assert.Equal(new Rgb(0x6F, 0xD0, 0xC8), Palette.Info);
        Assert.Equal(new Rgb(0xA1, 0xC3, 0x5E), Palette.Success);
    }

    [Fact]
    public void AParsedColourRoundTripsBackToTheSameHex()
    {
        Assert.Equal("#adcb66", Rgb.FromHex("#adcb66").ToHex());
        Assert.Equal("#000000", Rgb.FromHex("#000000").ToHex());
        Assert.Equal("#ffffff", Rgb.FromHex("#FFFFFF").ToHex());
    }
}