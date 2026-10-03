using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins which SGR block the 16-colour path emits for each channel. SGR 30-37
/// and 90-97 select a foreground; 40-47 and 100-107 select a background. This
/// is the only depth where the two are told apart by the code's position
/// rather than by a 38/48 prefix, so it is the only place the channel can be
/// silently dropped.
///
/// The expected codes are read off the SGR standard rather than recomputed the
/// way the mapper computes them, so they are an external reference and can
/// disagree with the implementation.
/// </summary>
public class ColorMapperAnsi16ChannelTests
{
    private static readonly Rgb PureRed = new(0xFF, 0x00, 0x00);

    private static readonly Rgb DarkRed = new(0x80, 0x00, 0x00);

    [Fact]
    public void ABrightBackgroundUsesTheBrightBackgroundBlock()
    {
        // 100-107 is the bright background range. 90-97 is a bright foreground,
        // which is what this used to emit.
        Assert.Equal("\u001b[0;101m", ColorMapper.Background(PureRed, ColorDepth.Ansi16));
    }

    [Fact]
    public void ADarkBackgroundUsesTheDarkBackgroundBlock()
    {
        Assert.Equal("\u001b[0;41m", ColorMapper.Background(DarkRed, ColorDepth.Ansi16));
    }

    [Fact]
    public void TheSameColourPicksDifferentCodesForTheTwoChannels()
    {
        Assert.NotEqual(
            ColorMapper.Foreground(PureRed, ColorDepth.Ansi16),
            ColorMapper.Background(PureRed, ColorDepth.Ansi16));
    }

    [Fact]
    public void TheForegroundKeepsTheForegroundBlock()
    {
        Assert.Equal("\u001b[0;91m", ColorMapper.Foreground(PureRed, ColorDepth.Ansi16));
        Assert.Equal("\u001b[0;31m", ColorMapper.Foreground(DarkRed, ColorDepth.Ansi16));
    }

    [Fact]
    public void AStyleWithBothChannelsUsesAForegroundThenABackgroundCode()
    {
        Assert.Equal(
            "\u001b[0;91;101m",
            ColorMapper.Style(PureRed, PureRed, CellAttributes.None, ColorDepth.Ansi16));
    }

    [Fact]
    public void AStyleWithOnlyABackgroundStillUsesTheBackgroundBlock()
    {
        Assert.Equal(
            "\u001b[0;101m",
            ColorMapper.Style(null, PureRed, CellAttributes.None, ColorDepth.Ansi16));
    }

    [Fact]
    public void AttributesPrecedeTheSixteenColourCodes()
    {
        Assert.Equal(
            "\u001b[0;1;91;101m",
            ColorMapper.Style(PureRed, PureRed, CellAttributes.Bold, ColorDepth.Ansi16));
    }

    [Theory]
    [InlineData(0xFF, 0x00, 0xFF, "\u001b[0;105m")] // bright magenta, index 13
    [InlineData(0xFF, 0xFF, 0xFF, "\u001b[0;107m")] // bright white, index 15
    [InlineData(0x80, 0x00, 0x80, "\u001b[0;45m")]  // dark magenta, index 5
    [InlineData(0xC0, 0xC0, 0xC0, "\u001b[0;47m")]  // dark white, index 7
    public void EveryBaseIndexPicksTheBackgroundBlockForItsOwnIntensity(
        int r, int g, int b, string expected)
    {
        var color = new Rgb((byte)r, (byte)g, (byte)b);

        Assert.Equal(expected, ColorMapper.Background(color, ColorDepth.Ansi16));
    }
}