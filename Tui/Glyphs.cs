using System.Collections.Immutable;

namespace Lattice.Tui;

/// <summary>One glyph and the ASCII text that stands in for it.</summary>
/// <param name="Unicode">The Unicode box-drawing or geometric glyph.</param>
/// <param name="Ascii">
/// The fallback, always exactly one character so a fallback never shifts a
/// grid column.
/// </param>
public readonly record struct GlyphMapping(char Unicode, char Ascii);

/// <summary>
/// The glyph set: box drawing and geometric shapes only. No images, no
/// dingbats, no icon font — the set is restricted to the Box Drawing block
/// (U+2500-U+257F), the Block Elements block (U+2580-U+259F) and the Geometric
/// Shapes block (U+25A0-U+25FF). Those three are the blocks every terminal
/// with box-drawing support also has, and confining the set to them is what
/// makes the glyphs share font metrics, which is the property a cell grid
/// depends on. Dingbats and Miscellaneous Symbols are excluded even though they
/// would read more naturally, because their presence varies far more between
/// terminals.
///
/// Each entry carries a single-character ASCII stand-in for terminals that
/// cannot encode UTF-8, because a wider fallback would break the column
/// alignment the grid is built on.
/// </summary>
public static class Glyphs
{
    /// <summary>First code point of the Box Drawing block.</summary>
    public const int BoxDrawingBlockStart = 0x2500;

    /// <summary>Last code point of the Box Drawing block.</summary>
    public const int BoxDrawingBlockEnd = 0x257F;

    /// <summary>First code point of the Block Elements block.</summary>
    public const int BlockElementsBlockStart = 0x2580;

    /// <summary>Last code point of the Block Elements block.</summary>
    public const int BlockElementsBlockEnd = 0x259F;

    /// <summary>First code point of the Geometric Shapes block.</summary>
    public const int GeometricShapesBlockStart = 0x25A0;

    /// <summary>Last code point of the Geometric Shapes block.</summary>
    public const int GeometricShapesBlockEnd = 0x25FF;

    /// <summary>A horizontal line.</summary>
    public const char Horizontal = '─';

    /// <summary>A vertical line.</summary>
    public const char Vertical = '│';

    /// <summary>The full block, used for a solid bar.</summary>
    public const char FullBlock = '█';

    /// <summary>The lightest shade, used for an inactive fill.</summary>
    public const char LightShade = '░';

    /// <summary>A small filled square, used for a closed edge.</summary>
    public const char ClosedMark = '▪';

    /// <summary>A small hollow square, used for an open edge.</summary>
    public const char OpenMark = '▫';

    /// <summary>A filled diamond, used for an agent at rest.</summary>
    public const char FilledDiamond = '◆';

    /// <summary>A hollow circle, used for a resource the recording does not claim.</summary>
    public const char HollowCircle = '○';

    /// <summary>A filled circle, used for a resource the recording claims.</summary>
    public const char FilledCircle = '●';

    /// <summary>A full crossing, used for an edge that runs equally on both axes.</summary>
    public const char Cross = '┼';

    /// <summary>A hollow diamond, used for an agent in transit.</summary>
    public const char HollowDiamond = '◇';

    /// <summary>A left-pointing triangle, used for an inbound edge.</summary>
    public const char LeftTriangle = '◀';

    /// <summary>A right-pointing triangle, used for an outbound edge.</summary>
    public const char RightTriangle = '▶';

    /// <summary>An up-pointing triangle.</summary>
    public const char UpTriangle = '▲';

    /// <summary>A down-pointing triangle.</summary>
    public const char DownTriangle = '▼';

    /// <summary>
    /// Every glyph in the set with its fallback. This is the authority the
    /// coverage test walks, so a glyph used anywhere without an entry here
    /// fails that test.
    ///
    /// The mapping table is exposed as an <see cref="ImmutableArray{T}"/> rather
    /// than a bare array behind an interface: an interface alone only hides the
    /// mutation at compile time, and a caller casting back to
    /// <c>GlyphMapping[]</c> could still rewrite a shared global for the whole
    /// process. An immutable array makes the guarantee hold at runtime too.
    /// </summary>
    public static ImmutableArray<GlyphMapping> Mappings { get; } = ImmutableArray.Create(
        // Box Drawing: light.
        new GlyphMapping('─', '-'),
        new GlyphMapping('│', '|'),
        new GlyphMapping('┌', '+'),
        new GlyphMapping('┐', '+'),
        new GlyphMapping('└', '+'),
        new GlyphMapping('┘', '+'),
        new GlyphMapping('├', '+'),
        new GlyphMapping('┤', '+'),
        new GlyphMapping('┬', '+'),
        new GlyphMapping('┴', '+'),
        new GlyphMapping('┼', '+'),

        // Box Drawing: heavy.
        new GlyphMapping('━', '='),
        new GlyphMapping('┃', '|'),
        new GlyphMapping('┏', '#'),
        new GlyphMapping('┓', '#'),
        new GlyphMapping('┗', '#'),
        new GlyphMapping('┛', '#'),
        new GlyphMapping('┣', '#'),
        new GlyphMapping('┫', '#'),
        new GlyphMapping('┳', '#'),
        new GlyphMapping('┻', '#'),
        new GlyphMapping('╋', '#'),

        // Box Drawing: double.
        new GlyphMapping('═', '='),
        new GlyphMapping('║', '|'),
        new GlyphMapping('╔', '#'),
        new GlyphMapping('╗', '#'),
        new GlyphMapping('╚', '#'),
        new GlyphMapping('╝', '#'),
        new GlyphMapping('╠', '#'),
        new GlyphMapping('╣', '#'),
        new GlyphMapping('╦', '#'),
        new GlyphMapping('╩', '#'),
        new GlyphMapping('╬', '#'),

        // Box Drawing: rounded corners.
        new GlyphMapping('╭', '+'),
        new GlyphMapping('╮', '+'),
        new GlyphMapping('╰', '+'),
        new GlyphMapping('╯', '+'),

        // Box Drawing: block elements and shades.
        new GlyphMapping('█', '#'),
        new GlyphMapping('▓', '#'),
        new GlyphMapping('▒', ':'),
        new GlyphMapping('░', '.'),
        new GlyphMapping('▀', '^'),
        new GlyphMapping('▄', '_'),
        new GlyphMapping('▌', '['),
        new GlyphMapping('▐', ']'),

        // Geometric Shapes.
        new GlyphMapping('■', '*'),
        new GlyphMapping('□', 'o'),
        new GlyphMapping('▪', '*'),
        new GlyphMapping('▫', 'o'),
        new GlyphMapping('●', '*'),
        new GlyphMapping('○', 'o'),
        new GlyphMapping('◼', '*'),
        new GlyphMapping('◻', 'o'),
        new GlyphMapping('▲', '^'),
        new GlyphMapping('▼', 'v'),
        new GlyphMapping('◀', '<'),
        new GlyphMapping('▶', '>'),
        new GlyphMapping('◆', '*'),
        new GlyphMapping('◇', 'o'));

    /// <summary>
    /// The glyph to draw for <paramref name="unicode"/>: itself when the
    /// terminal can encode UTF-8, otherwise its ASCII stand-in.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// The glyph is not in <see cref="Mappings"/>. Falling back to an unknown
    /// glyph would emit a replacement character and break alignment, so an
    /// unmapped glyph is an error the tests catch instead.
    /// </exception>
    public static char Resolve(char unicode, bool utf8) => utf8 ? unicode : ToAscii(unicode);

    /// <summary>The single-character ASCII stand-in for <paramref name="unicode"/>.</summary>
    /// <exception cref="KeyNotFoundException">The glyph has no mapping.</exception>
    public static char ToAscii(char unicode)
    {
        foreach (var mapping in Mappings)
        {
            if (mapping.Unicode == unicode)
            {
                return mapping.Ascii;
            }
        }

        throw new KeyNotFoundException($"No glyph mapping for U+{(int)unicode:X4}.");
    }
}