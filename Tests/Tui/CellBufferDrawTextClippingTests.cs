using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins <see cref="CellBuffer.DrawText"/>'s clipping at both edges. Text
/// starting left of column 0 keeps only its visible tail, and text running past
/// the last column keeps only its visible head, so a caller can hand a
/// fixed-width string to a panel and a scrolled string to a viewport without
/// doing the arithmetic itself.
/// </summary>
public class CellBufferDrawTextClippingTests
{
    [Fact]
    public void AnOriginLeftOfTheGridWritesTheVisibleTailOnly()
    {
        var buffer = new CellBuffer(4, 1);

        buffer.DrawText(-2, 0, "abcdef", Cell.Blank);

        // 'a' and 'b' are left of column 0 and are clipped away.
        Assert.Equal("cdef", buffer.ToLines()[0]);
    }

    [Fact]
    public void AnOriginLeftOfTheGridByOneClipsExactlyOneCharacter()
    {
        var buffer = new CellBuffer(4, 1);

        buffer.DrawText(-1, 0, "abcde", Cell.Blank);

        Assert.Equal("bcde", buffer.ToLines()[0]);
    }

    [Fact]
    public void TextStartingLeftOfTheGridKeepsItsStyleOnTheVisiblePart()
    {
        var buffer = new CellBuffer(3, 1);

        buffer.DrawText(-2, 0, "abcd", new Cell(' ', Palette.Accent));

        // 'c' lands on column 0 and 'd' on column 1; column 2 is past the end of
        // the text and keeps the buffer's blank.
        Assert.Equal('c', buffer[0, 0].Glyph);
        Assert.Equal('d', buffer[1, 0].Glyph);
        Assert.Equal(Palette.Accent, buffer[0, 0].Foreground);
        Assert.Equal(Palette.Accent, buffer[1, 0].Foreground);
        Assert.Null(buffer[2, 0].Foreground);
    }

    [Fact]
    public void TextEntirelyLeftOfTheGridWritesNothing()
    {
        var buffer = new CellBuffer(4, 1);

        buffer.DrawText(-6, 0, "abc", Cell.Blank);

        Assert.Equal("    ", buffer.ToLines()[0]);
    }

    [Fact]
    public void BothEdgesClipAtOnceWhenTheTextIsWiderThanTheRow()
    {
        var buffer = new CellBuffer(3, 1);

        // Origin 2 left of the grid, and 6 characters into a 3-wide row: the
        // middle three survive.
        buffer.DrawText(-2, 0, "abcdefgh", Cell.Blank);

        Assert.Equal("cde", buffer.ToLines()[0]);
    }

    [Fact]
    public void ClippingIsPerRowAndLeavesOtherRowsAlone()
    {
        var buffer = new CellBuffer(3, 2);

        buffer.DrawText(-1, 1, "abcd", Cell.Blank);

        Assert.Equal("   ", buffer.ToLines()[0]);
        Assert.Equal("bcd", buffer.ToLines()[1]);
    }
}