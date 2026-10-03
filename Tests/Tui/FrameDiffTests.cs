using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Pins the exact bytes <see cref="FrameDiff"/> emits. Every assertion is a
/// hand-written literal rather than a value recomputed the way the renderer
/// computes it, so a change in cursor addressing, run splitting or SGR framing
/// fails here instead of quietly doubling the bytes written per frame.
/// </summary>
public class FrameDiffTests
{
    private const string TrueColorSgr = "\u001b[0;38;2;173;203;102m";

    private static CellBuffer Blank(int width, int height) => new(width, height);

    [Fact]
    public void IdenticalBuffersProduceNoOutput()
    {
        var previous = Blank(6, 3);
        var next = Blank(6, 3);
        previous[2, 1] = new Cell('Z', Palette.Accent);
        next[2, 1] = new Cell('Z', Palette.Accent);

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void ASingleChangedCellWritesOneRun()
    {
        var previous = Blank(4, 3);
        var next = Blank(4, 3);
        next[1, 1] = new Cell('X');

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[2;2HX", rendered);
    }

    [Fact]
    public void AContiguousRunIsWrittenAsOneRun()
    {
        var previous = Blank(5, 1);
        var next = Blank(5, 1);
        next[0, 0] = new Cell('A');
        next[1, 0] = new Cell('B');
        next[2, 0] = new Cell('C');

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[1;1HABC", rendered);
    }

    [Fact]
    public void DisjointChangesBecomeSeparateRuns()
    {
        var previous = Blank(5, 1);
        var next = Blank(5, 1);
        next[0, 0] = new Cell('A');
        next[3, 0] = new Cell('B');

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[1;1HA\u001b[1;4HB", rendered);
    }

    [Fact]
    public void AMovedRunRepaintsOnlyWhereTheTextLanded()
    {
        // "abc" occupies columns 0-2 in the first frame and 4-6 in the second.
        // Column 3 is blank in both, so it splits the change into two runs.
        var previous = Blank(8, 1);
        previous.DrawText(0, 0, "abc", Cell.Blank);
        var next = Blank(8, 1);
        next.DrawText(4, 0, "abc", Cell.Blank);

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[1;1H   \u001b[1;5Habc", rendered);
    }

    [Fact]
    public void AChangedStyleAloneIsAChange()
    {
        var previous = Blank(2, 1);
        previous[0, 0] = new Cell('a', Palette.Accent);
        var next = Blank(2, 1);
        next[0, 0] = new Cell('a', Palette.Accent, null, CellAttributes.Bold);

        var rendered = FrameDiff.Render(previous, next, ColorDepth.TrueColor);

        Assert.Equal("\u001b[1;1H\u001b[0;1;38;2;173;203;102ma", rendered);
    }

    [Fact]
    public void UnchangedCellsInsideARunAreNotRestyled()
    {
        var previous = Blank(3, 1);
        var next = Blank(3, 1);
        next[0, 0] = new Cell('a', Palette.Accent);
        next[1, 0] = new Cell('b', Palette.Accent);
        next[2, 0] = new Cell('c', Palette.Accent);

        var rendered = FrameDiff.Render(previous, next, ColorDepth.TrueColor);

        Assert.Equal("\u001b[1;1H" + TrueColorSgr + "abc", rendered);
    }

    [Fact]
    public void ReturningToTheDefaultStyleEmitsAReset()
    {
        // The second cell loses its colour rather than never having had any, so the
        // renderer must clear the style it set on the first cell.
        var previous = Blank(2, 1);
        previous[1, 0] = new Cell('a', Palette.Accent);
        var next = Blank(2, 1);
        next[0, 0] = new Cell('a', Palette.Accent);
        next[1, 0] = Cell.Blank;

        var rendered = FrameDiff.Render(previous, next, ColorDepth.TrueColor);

        Assert.Equal("\u001b[1;1H" + TrueColorSgr + "a\u001b[0m ", rendered);
    }

    [Fact]
    public void ChangedRowsAreJoinedByNewlinesAndUnchangedRowsAreSkipped()
    {
        var previous = Blank(3, 4);
        var next = Blank(3, 4);
        next[1, 1] = new Cell('a');
        next[1, 3] = new Cell('b');

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[2;2Ha\n\u001b[4;2Hb", rendered);
    }

    [Fact]
    public void AStyleCarriedAcrossRowsIsNotRepeated()
    {
        var previous = Blank(1, 2);
        var next = Blank(1, 2);
        next[0, 0] = new Cell('X', Palette.Accent);
        next[0, 1] = new Cell('X', Palette.Accent);

        var rendered = FrameDiff.Render(previous, next, ColorDepth.TrueColor);

        Assert.Equal("\u001b[1;1H" + TrueColorSgr + "X\n\u001b[2;1HX", rendered);
    }

    [Fact]
    public void NoPreviousBufferPaintsEveryCellOfEveryRow()
    {
        var next = new CellBuffer(2, 2);
        next.Fill(0, 0, 2, 2, Cell.Blank);

        var rendered = FrameDiff.Render(null, next, ColorDepth.None);

        Assert.Equal("\u001b[1;1H  \n\u001b[2;1H  ", rendered);
    }

    [Fact]
    public void ShrinkingTheBufferOnlyDiffsTheOverlap()
    {
        var previous = Blank(6, 2);
        var next = Blank(3, 2);
        next[0, 0] = new Cell('X');

        var rendered = FrameDiff.Render(previous, next, ColorDepth.None);

        Assert.Equal("\u001b[1;1HX", rendered);
    }
}