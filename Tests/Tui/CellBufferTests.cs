using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Covers the grid the renderer draws into: sizing, bounds, and the two
/// primitives every screen composes from. The border test asserts the whole
/// frame as one literal so a corner or off-by-one error is visible directly.
/// </summary>
public class CellBufferTests
{
    [Fact]
    public void ANewBufferIsAllBlanks()
    {
        var buffer = new CellBuffer(3, 2);

        Assert.All(buffer.ToLines(), line => Assert.Equal("   ", line));
    }

    [Fact]
    public void ANonPositiveSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(-1, 1));
    }

    [Fact]
    public void ReadingOutsideTheGridThrows()
    {
        var buffer = new CellBuffer(3, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[3, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[0, 2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1, 0]);
    }

    [Fact]
    public void CellsRoundTripThroughTheIndexer()
    {
        var buffer = new CellBuffer(3, 2);
        buffer[1, 1] = new Cell('Q', Palette.Accent, Palette.Raised, CellAttributes.Bold);

        var cell = buffer[1, 1];

        Assert.Equal(new Cell('Q', Palette.Accent, Palette.Raised, CellAttributes.Bold), cell);
    }

    [Fact]
    public void ClearResetsEveryCell()
    {
        var buffer = new CellBuffer(2, 2);
        buffer.Fill(0, 0, 2, 2, new Cell('x', Palette.Error));
        buffer.Clear();

        Assert.All(buffer.ToLines(), line => Assert.Equal("  ", line));
    }

    [Fact]
    public void FillCoversTheRequestedRectangleOnly()
    {
        var buffer = new CellBuffer(4, 3);
        buffer.Fill(1, 1, 2, 1, new Cell('#'));

        Assert.Equal(
            new[] { "    ", " ## ", "    " },
            buffer.ToLines());
    }

    [Fact]
    public void DrawTextKeepsTheStyleAndAdvancesOneColumnPerCharacter()
    {
        var buffer = new CellBuffer(5, 1);
        buffer.DrawText(1, 0, "abc", new Cell(' ', Palette.TextPrimary, Palette.PanelBackground));

        Assert.Equal(" abc ", buffer.ToLines()[0]);
        Assert.Equal(Palette.TextPrimary, buffer[1, 0].Foreground);
        Assert.Equal(Palette.PanelBackground, buffer[1, 0].Background);
    }

    [Fact]
    public void DrawTextDropsCharactersPastTheRightEdge()
    {
        var buffer = new CellBuffer(3, 1);
        buffer.DrawText(0, 0, "abcdef", Cell.Blank);

        Assert.Equal("abc", buffer.ToLines()[0]);
    }

    [Fact]
    public void DrawTextPositionsCharactersByColumnSoAnOriginLeftOfZeroSkips()
    {
        var buffer = new CellBuffer(3, 1);
        buffer.DrawText(-2, 0, "abcdef", Cell.Blank);

        // 'a' and 'b' land left of the grid and are dropped, so 'c' is column 0.
        Assert.Equal("cde", buffer.ToLines()[0]);
    }

    [Fact]
    public void DrawTextRejectsANullString()
    {
        var buffer = new CellBuffer(3, 1);

        Assert.Throws<ArgumentNullException>(() => buffer.DrawText(0, 0, null!, Cell.Blank));
    }

    [Fact]
    public void ALightBorderUsesTheLightCornersOnBothEdges()
    {
        var buffer = new CellBuffer(3, 3);

        buffer.DrawBorder(0, 0, 3, 3, BorderGlyphs.Light, new Cell(' ', Palette.TextPrimary));

        Assert.Equal(
            new[] { "┌─┐", "│ │", "└─┘" },
            buffer.ToLines());
    }

    [Fact]
    public void ARoundedBorderUsesRoundedCorners()
    {
        var buffer = new CellBuffer(3, 3);

        buffer.DrawBorder(0, 0, 3, 3, BorderGlyphs.Rounded, new Cell(' ', Palette.TextPrimary));

        Assert.Equal(
            new[] { "╭─╮", "│ │", "╰─╯" },
            buffer.ToLines());
    }

    [Fact]
    public void ADoubleBorderUsesTheDoubleGlyphs()
    {
        var buffer = new CellBuffer(4, 3);

        buffer.DrawBorder(0, 0, 4, 3, BorderGlyphs.Double, new Cell(' ', Palette.TextPrimary));

        Assert.Equal(
            new[] { "╔══╗", "║  ║", "╚══╝" },
            buffer.ToLines());
    }

    [Fact]
    public void ADegenerateBorderIsRejected()
    {
        var buffer = new CellBuffer(4, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DrawBorder(0, 0, 1, 3, BorderGlyphs.Light, Cell.Blank));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DrawBorder(0, 0, 3, 1, BorderGlyphs.Light, Cell.Blank));
    }

    [Fact]
    public void TwoCellsThatPaintIdenticallyMatchRegardlessOfConstruction()
    {
        var viaConstructor = new Cell('a', Palette.Accent, null, CellAttributes.Bold);
        var viaWith = new Cell('z', Palette.TextPrimary) with
        {
            Glyph = 'a',
            Foreground = Palette.Accent,
            Attributes = CellAttributes.Bold,
        };

        Assert.True(viaConstructor.Matches(viaWith));
        Assert.True(viaConstructor.SameStyleAs(viaWith));
    }

    [Fact]
    public void CellsWithDifferentColoursDoNotMatch()
    {
        var left = new Cell('a', Palette.Accent);
        var right = new Cell('a', Palette.AccentSecondary);

        Assert.False(left.Matches(right));
        Assert.False(left.SameStyleAs(right));
    }

    [Fact]
    public void ACellWithTheSameStyleAndADifferentGlyphStillMatchesOnStyle()
    {
        var left = new Cell('a', Palette.Accent);
        var right = new Cell('b', Palette.Accent);

        Assert.False(left.Matches(right));
        Assert.True(left.SameStyleAs(right));
    }

    [Fact]
    public void ANullColourMeansTheTerminalDefaultAndIsNotBlack()
    {
        var defaultColoured = new Cell('a');
        var blackColoured = new Cell('a', new Rgb(0, 0, 0));

        Assert.False(defaultColoured.Matches(blackColoured));
        Assert.Null(defaultColoured.Foreground);
    }
}