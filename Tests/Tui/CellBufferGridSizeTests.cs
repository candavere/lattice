using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the constructor's cell-count contract. The grid is backed by
/// <c>new Cell[width * height]</c>, and in <see cref="int"/> that product
/// wraps: <c>65536 * 65537</c> is 4_295_032_832, which arrives as 65536. The
/// constructor therefore accepted a grid of four billion cells, allocated a
/// 65536-cell array, and reported the real problem much later as an
/// index-out-of-range partway through a draw, after some cells had already been
/// written.
///
/// The count is checked before the allocation. None of these tests allocate: each
/// size is either rejected by the boundary check or small enough to be harmless.
/// A count that *is* supported but exhausts memory is a different thing and
/// still comes from the runtime as <see cref="OutOfMemoryException"/>.
/// </summary>
public class CellBufferGridSizeTests
{
    [Fact]
    public void AGridWhoseCellCountWrapsIsRejectedInsteadOfAllocatingASmallArray()
    {
        // 65536 * 65537 = 4_295_032_832, which arrives as 65536.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(65536, 65537));
    }

    [Fact]
    public void AGridWhoseBothDimensionsAreTheLargestIntIsRejected()
    {
        // int.MaxValue squared is 4_611_686_014_132_420_609, which arrives as 1.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void AGridWhoseWidthIsTheLargestIntIsRejected()
    {
        // int.MaxValue * 2 arrives as -2.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(int.MaxValue, 2));
    }

    [Fact]
    public void AGridOneCellLargerThanTheLargestArrayTheRuntimeCanHoldIsRejected()
    {
        // Array.MaxLength cells is the largest grid this runtime could ever back
        // with an array, so one more is not a supported cell count. The count is
        // split across two ordinary axis sizes so what is under test is the
        // product rather than one absurd axis.
        var count = (long)Array.MaxLength + 1;
        var width = 2;
        while (width < 64 && count % width != 0)
        {
            width++;
        }

        var height = count / width;

        Assert.Equal(count, width * (long)height);
        Assert.True(width <= Array.MaxLength && height <= Array.MaxLength);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CellBuffer(width, (int)height));
    }

    [Fact]
    public void AnOrdinaryGridStillConstructsAndFills()
    {
        var buffer = new CellBuffer(100, 30);
        buffer.Fill(0, 0, 100, 30, new Cell('.', Palette.TextPrimary));

        Assert.Equal(100, buffer.Width);
        Assert.Equal(30, buffer.Height);
        Assert.All(buffer.ToLines(), line => Assert.Equal(new string('.', 100), line));
    }
}