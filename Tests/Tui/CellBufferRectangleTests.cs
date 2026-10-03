using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the atomicity contract for the two rectangle writers: a rectangle that
/// does not fit throws <see cref="ArgumentOutOfRangeException"/> before any
/// cell is touched, so a rejected call leaves the buffer byte-for-byte as it
/// was.
///
/// This matters because both methods used to write cell by cell and let the
/// indexer's bounds check throw partway through, which left a half-drawn box on
/// screen and reported the failure at a column that had nothing to do with the
/// mistake.
/// </summary>
public class CellBufferRectangleTests
{
    private const int Width = 6;

    private const int Height = 4;

    private static CellBuffer Filled()
    {
        var buffer = new CellBuffer(Width, Height);
        buffer.Fill(0, 0, Width, Height, new Cell('.', Palette.TextPrimary));
        return buffer;
    }

    private static void AssertUnchanged(CellBuffer buffer)
    {
        var lines = buffer.ToLines();

        for (var row = 0; row < Height; row++)
        {
            Assert.Equal(new string('.', Width), lines[row]);
        }
    }

    [Fact]
    public void ABorderThatOverrunsTheRightEdgeIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(4, 0, 4, 3, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderThatOverrunsTheBottomEdgeIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, 2, 3, 4, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderStartingLeftOfTheGridIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(-1, 0, 3, 3, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderStartingAboveTheGridIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, -1, 3, 3, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderOneColumnTooWideIsRejectedEvenThoughItsFirstColumnFits()
    {
        // The specific shape the old writer got wrong: the top and bottom rows
        // were written across the legal columns before the vertical pass threw.
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(Width - 2, 0, 3, 3, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillThatOverrunsTheRightEdgeIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(4, 0, 3, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillThatOverrunsTheBottomEdgeIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, 3, 2, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillStartingLeftOfTheGridIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(-1, 0, 2, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillStartingAboveTheGridIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, -1, 2, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ARectangleEndingExactlyOnTheFarEdgeIsAccepted()
    {
        var buffer = Filled();

        buffer.Fill(0, 0, Width, Height, new Cell('X', Palette.Error));

        Assert.Equal(new string('X', Width), buffer.ToLines()[0]);
    }

    [Fact]
    public void ABorderEndingExactlyOnTheFarEdgeIsAccepted()
    {
        var buffer = Filled();

        buffer.DrawBorder(0, 0, Width, Height, BorderGlyphs.Light, Cell.Blank);

        Assert.Equal("\u250c" + new string('\u2500', Width - 2) + "\u2510", buffer.ToLines()[0]);
    }

    [Fact]
    public void ANegativelySizedFillIsStillRejectedWithoutWriting()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, 0, -1, 2, new Cell('X', Palette.Error)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, 0, 2, -1, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AZeroSizedFillRemainsALegalNoOp()
    {
        // Not an atomicity concern: an empty rectangle has nothing to write, so
        // it is allowed and changes nothing. Pinned here so the atomicity work
        // is not later mistaken for a tightening of this rule.
        var buffer = Filled();

        buffer.Fill(0, 0, 0, 2, new Cell('X', Palette.Error));
        buffer.Fill(0, 0, 2, 0, new Cell('X', Palette.Error));

        AssertUnchanged(buffer);
    }

    [Fact]
    public void ADegenerateBorderIsStillRejected()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, 0, 1, 3, BorderGlyphs.Light, Cell.Blank));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, 0, 3, 1, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }
}