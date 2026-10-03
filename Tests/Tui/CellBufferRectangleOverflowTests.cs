using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the overflow half of the rectangle contract. The check that makes a
/// rejected rectangle atomic compares <c>x + width</c> against the grid, and
/// unchecked int addition wraps: a rectangle far larger than the buffer summed
/// back to a negative number, sailed through the check, and then drew nothing at
/// all instead of throwing. Once one dimension wrapped and the other did not,
/// the writers got further and left a partial rectangle behind.
///
/// These tests hold the shapes that used to slip through, plus the two edges the
/// check must keep accepting: a rectangle that ends exactly on the far edge, and
/// an empty rectangle.
/// </summary>
public class CellBufferRectangleOverflowTests
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
        Assert.All(buffer.ToLines(), line => Assert.Equal(new string('.', Width), line));
    }

    [Fact]
    public void AFillWhoseWidthOverflowsTheColumnSumIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(1, 0, int.MaxValue, 1, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillWhoseHeightOverflowsTheRowSumIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, 1, 4, int.MaxValue, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillHugeInBothDimensionsIsRejectedInsteadOfSilentlyDrawingNothing()
    {
        // The shape from the field report: two dimensions around 1.5e9 sum past
        // int.MaxValue, wrap negative, and the fill quietly becomes a no-op.
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(1_500_000_000, 0, 1_500_000_000, 1, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillWhoseWidthOverflowsIsRejectedEvenWhenTheRowCountIsLegal()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(1, 0, int.MaxValue, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillStartingNearTheEndOfTheColumnRangeIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(int.MaxValue, 0, 2, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void AFillStartingNearTheEndOfTheRowRangeIsRejectedAndWritesNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.Fill(0, int.MaxValue, 2, 2, new Cell('X', Palette.Error)));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderWhoseWidthOverflowsIsRejectedAndDrawsNothing()
    {
        // The column pass is skipped by the wrapped sum and the row pass writes
        // the first column before the far column throws, so the buffer used to be
        // left with a stray glyph on it.
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(1, 0, int.MaxValue, 2, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderWhoseHeightOverflowsIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, Height - 1, 2, int.MaxValue, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderStartingNearTheEndOfTheColumnRangeIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(int.MaxValue, 0, 2, 2, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ABorderStartingNearTheEndOfTheRowRangeIsRejectedAndDrawsNothing()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, int.MaxValue, 2, int.MaxValue, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }

    [Fact]
    public void ARectangleEndingExactlyOnTheFarEdgeFromAnInnerOriginIsAccepted()
    {
        var buffer = Filled();

        buffer.Fill(2, 1, 4, 3, new Cell('X', Palette.Error));

        Assert.Equal(
            new[] { "......", "..XXXX", "..XXXX", "..XXXX" },
            buffer.ToLines());
    }

    [Fact]
    public void ABorderEndingExactlyOnTheFarEdgeFromAnInnerOriginIsAccepted()
    {
        var buffer = Filled();

        buffer.DrawBorder(2, 1, 4, 3, BorderGlyphs.Light, Cell.Blank);

        Assert.Equal(
            new[] { "......", "..┌──┐", "..│..│", "..└──┘" },
            buffer.ToLines());
    }

    [Fact]
    public void AZeroSizedFillInsideTheGridIsStillALegalNoOp()
    {
        var buffer = Filled();

        buffer.Fill(2, 1, 0, 3, new Cell('X', Palette.Error));
        buffer.Fill(2, 1, 4, 0, new Cell('X', Palette.Error));

        AssertUnchanged(buffer);
    }

    [Fact]
    public void ADegenerateBorderIsStillRejectedBeforeAnyOverflowIsConsidered()
    {
        var buffer = Filled();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, 0, int.MaxValue, 1, BorderGlyphs.Light, Cell.Blank));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.DrawBorder(0, 0, 1, int.MaxValue, BorderGlyphs.Light, Cell.Blank));
        AssertUnchanged(buffer);
    }
}