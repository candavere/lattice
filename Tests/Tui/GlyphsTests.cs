using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Covers the whole glyph set rather than a sample: a glyph added without a
/// fallback would render as a replacement character and shift every column to
/// its right, and that only shows up if the enumeration itself is walked.
/// </summary>
public class GlyphsTests
{
    [Fact]
    public void EveryGlyphInTheSetHasAnAsciiFallback()
    {
        foreach (var mapping in Glyphs.Mappings)
        {
            var ascii = Glyphs.ToAscii(mapping.Unicode);

            Assert.True(ascii < 128, $"U+{(int)mapping.Unicode:X4} falls back to non-ASCII '{ascii}'.");
        }
    }

    [Fact]
    public void EveryGlyphComesFromAnAllowedBlock()
    {
        foreach (var mapping in Glyphs.Mappings)
        {
            var point = (int)mapping.Unicode;

            var allowed =
                point >= Glyphs.BoxDrawingBlockStart && point <= Glyphs.BoxDrawingBlockEnd
                || point >= Glyphs.BlockElementsBlockStart && point <= Glyphs.BlockElementsBlockEnd
                || point >= Glyphs.GeometricShapesBlockStart && point <= Glyphs.GeometricShapesBlockEnd;

            Assert.True(
                allowed,
                $"U+{point:X4} is outside the Box Drawing, Block Elements and Geometric Shapes blocks.");
        }
    }

    [Fact]
    public void TheSetHasNoDuplicateUnicodeEntries()
    {
        var seen = new HashSet<char>();

        foreach (var mapping in Glyphs.Mappings)
        {
            Assert.True(seen.Add(mapping.Unicode), $"U+{(int)mapping.Unicode:X4} is listed twice.");
        }
    }

    [Theory]
    [InlineData('─', '-')]
    [InlineData('│', '|')]
    [InlineData('┌', '+')]
    [InlineData('╭', '+')]
    [InlineData('█', '#')]
    [InlineData('░', '.')]
    [InlineData('▀', '^')]
    [InlineData('▌', '[')]
    [InlineData('▐', ']')]
    [InlineData('■', '*')]
    [InlineData('○', 'o')]
    [InlineData('◀', '<')]
    [InlineData('▶', '>')]
    [InlineData('◆', '*')]
    [InlineData('◇', 'o')]
    [InlineData('▪', '*')]
    [InlineData('▫', 'o')]
    public void KnownGlyphsFallBackAsDocumented(char unicode, char expected)
    {
        Assert.Equal(expected, Glyphs.ToAscii(unicode));
    }

    [Fact]
    public void AnUnmappedGlyphIsAnErrorRatherThanASilentReplacement()
    {
        Assert.Throws<KeyNotFoundException>(() => Glyphs.ToAscii('\u0001'));
    }

    [Fact]
    public void ResolvePassesUnicodeThroughOnAUtf8Terminal()
    {
        Assert.Equal('─', Glyphs.Resolve('─', utf8: true));
        Assert.Equal('█', Glyphs.Resolve('█', utf8: true));
    }

    [Fact]
    public void ResolveFallsBackOnANonUtf8Terminal()
    {
        Assert.Equal('-', Glyphs.Resolve('─', utf8: false));
        Assert.Equal('#', Glyphs.Resolve('█', utf8: false));
    }

    [Fact]
    public void TheAsciiBorderIsExactlyTheAsciiFallbackOfTheLightBorder()
    {
        var light = BorderGlyphs.Light;
        var ascii = BorderGlyphs.Ascii;

        Assert.Equal(Glyphs.ToAscii(light.Horizontal), ascii.Horizontal);
        Assert.Equal(Glyphs.ToAscii(light.Vertical), ascii.Vertical);
        Assert.Equal(Glyphs.ToAscii(light.TopLeft), ascii.TopLeft);
        Assert.Equal(Glyphs.ToAscii(light.TopRight), ascii.TopRight);
        Assert.Equal(Glyphs.ToAscii(light.BottomLeft), ascii.BottomLeft);
        Assert.Equal(Glyphs.ToAscii(light.BottomRight), ascii.BottomRight);
    }

    [Fact]
    public void EveryBorderStyleResolvesOnANonUtf8Terminal()
    {
        var styles = new[]
        {
            BorderGlyphs.Light,
            BorderGlyphs.Heavy,
            BorderGlyphs.Double,
            BorderGlyphs.Rounded,
        };

        foreach (var style in styles)
        {
            foreach (var glyph in new[]
            {
                style.TopLeft, style.TopRight, style.BottomLeft,
                style.BottomRight, style.Horizontal, style.Vertical,
            })
            {
                Assert.True(Glyphs.Resolve(glyph, utf8: false) < 128);
            }
        }
    }
}