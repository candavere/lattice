using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the escape sequence produced for every colour at every depth. The
/// nearest-colour expectations were derived independently from the xterm
/// palette definition, so they are an external reference rather than a
/// restatement of the lookup under test.
/// </summary>
public class ColorMapperTests
{
    [Theory]
    [InlineData(173, 203, 102, "\u001b[0;38;2;173;203;102m")]
    [InlineData(31, 36, 32, "\u001b[0;38;2;31;36;32m")]
    [InlineData(0, 0, 0, "\u001b[0;38;2;0;0;0m")]
    [InlineData(255, 255, 255, "\u001b[0;38;2;255;255;255m")]
    public void TrueColorIsEmittedDirectly(int r, int g, int b, string expected)
    {
        var color = new Rgb((byte)r, (byte)g, (byte)b);

        Assert.Equal(expected, ColorMapper.Foreground(color, ColorDepth.TrueColor));
    }

    [Fact]
    public void TrueColorBackgroundUsesThe48Prefix()
    {
        Assert.Equal(
            "\u001b[0;48;2;31;36;32m",
            ColorMapper.Background(Rgb.FromHex("#1f2420"), ColorDepth.TrueColor));
    }

    [Theory]
    [InlineData(0, 0, 0, "\u001b[0;38;5;0m")]
    [InlineData(255, 255, 255, "\u001b[0;38;5;15m")]
    [InlineData(0, 0, 128, "\u001b[0;38;5;4m")]
    [InlineData(173, 203, 102, "\u001b[0;38;5;149m")]
    [InlineData(217, 100, 90, "\u001b[0;38;5;167m")]
    [InlineData(111, 208, 200, "\u001b[0;38;5;80m")]
    public void Ansi256MapsToTheNearestPaletteEntry(int r, int g, int b, string expected)
    {
        var color = new Rgb((byte)r, (byte)g, (byte)b);

        Assert.Equal(expected, ColorMapper.Foreground(color, ColorDepth.Ansi256));
    }

    [Theory]
    [InlineData(0, 0, 0, "\u001b[0;30m")]
    [InlineData(255, 255, 255, "\u001b[0;97m")]
    [InlineData(255, 0, 0, "\u001b[0;91m")]
    [InlineData(128, 128, 128, "\u001b[0;90m")]
    public void Ansi16MapsToTheNearestBaseColour(int r, int g, int b, string expected)
    {
        var color = new Rgb((byte)r, (byte)g, (byte)b);

        Assert.Equal(expected, ColorMapper.Foreground(color, ColorDepth.Ansi16));
    }

    [Theory]
    [InlineData(230, 200, 74, "\u001b[0;93m")]
    public void Ansi16NormalIntensityUsesThe30Block(int r, int g, int b, string expected)
    {
        var color = new Rgb((byte)r, (byte)g, (byte)b);

        Assert.Equal(expected, ColorMapper.Foreground(color, ColorDepth.Ansi16));
    }

    [Fact]
    public void NoDepthEmitsNoEscapeAtAll()
    {
        var color = Rgb.FromHex("#adcb66");

        Assert.Equal(string.Empty, ColorMapper.Foreground(color, ColorDepth.None));
        Assert.Equal(string.Empty, ColorMapper.Background(color, ColorDepth.None));
        Assert.Equal(
            string.Empty,
            ColorMapper.Style(color, Palette.Raised, CellAttributes.Bold | CellAttributes.Underline, ColorDepth.None));
    }

    [Fact]
    public void ANullChannelContributesNothing()
    {
        var style = ColorMapper.Style(Palette.Accent, null, CellAttributes.None, ColorDepth.TrueColor);

        Assert.Equal("\u001b[0;38;2;173;203;102m", style);
    }

    [Fact]
    public void AttributesPrecedeColoursInOneSequence()
    {
        var style = ColorMapper.Style(
            Palette.Accent,
            Palette.Raised,
            CellAttributes.Bold | CellAttributes.Dim | CellAttributes.Underline | CellAttributes.Reverse,
            ColorDepth.TrueColor);

        Assert.Equal(
            "\u001b[0;1;2;4;7;38;2;173;203;102;48;2;44;54;37m",
            style);
    }

    [Fact]
    public void AnEmptyStyleIsJustAReset()
    {
        Assert.Equal("\u001b[0m", ColorMapper.Style(null, null, CellAttributes.None, ColorDepth.TrueColor));
    }

    [Fact]
    public void ColourMappingIsAPureFunctionOfItsInputs()
    {
        var first = ColorMapper.Foreground(Palette.AccentSecondary, ColorDepth.Ansi256);
        var second = ColorMapper.Foreground(Palette.AccentSecondary, ColorDepth.Ansi256);

        Assert.Equal(first, second);
    }
}