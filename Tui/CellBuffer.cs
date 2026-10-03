using System.Text;

namespace Lattice.Tui;

/// <summary>
/// A fixed-size grid of <see cref="Cell"/>s with zero-based indexing. Sized once
/// and then mutated in place, which is what makes the diff against the previous
/// frame meaningful: no cell is reallocated when the contents change.
/// </summary>
public sealed class CellBuffer
{
    private readonly Cell[] _cells;

    /// <summary>Creates a blank buffer of the given size.</summary>
    public CellBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        _cells = new Cell[width * height];

        var blank = Cell.Blank;
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = blank;
        }
    }

    /// <summary>Column count.</summary>
    public int Width { get; }

    /// <summary>Row count.</summary>
    public int Height { get; }

    /// <summary>
    /// Reads or writes one cell. Out-of-range coordinates throw rather than
    /// clamp: a screen that draws past its edge is a bug, and clamping would
    /// hide it until it drew over the wrong thing.
    /// </summary>
    public Cell this[int x, int y]
    {
        get
        {
            CheckBounds(x, y);
            return _cells[(y * Width) + x];
        }

        set
        {
            CheckBounds(x, y);
            _cells[(y * Width) + x] = value;
        }
    }

    /// <summary>Resets every cell to the terminal defaults with a space glyph.</summary>
    public void Clear()
    {
        var blank = Cell.Blank;
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = blank;
        }
    }

    /// <summary>
    /// Fills the rectangle with one cell style. The whole rectangle is checked
    /// before the first cell is written, so a rectangle that does not fit throws
    /// <see cref="ArgumentOutOfRangeException"/> and leaves the buffer untouched
    /// rather than half-filled. A zero width or height is a legal no-op.
    /// </summary>
    public void Fill(int x, int y, int width, int height, Cell style)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        CheckRectangle(x, y, width, height);

        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                this[column, row] = style;
            }
        }
    }

    /// <summary>
    /// Writes one character per column starting at <paramref name="x"/>,
    /// clipping at <em>both</em> edges. Characters past the right edge are
    /// dropped, and so are characters left of column 0: an origin before the
    /// grid keeps the visible tail of the string. That is what lets a screen
    /// hand a fixed-width label to a narrow panel, and a string scrolled past a
    /// viewport edge to the same call, without doing the arithmetic itself.
    ///
    /// The row is still bounds-checked, so <paramref name="y"/> outside the
    /// buffer throws rather than clips.
    /// </summary>
    public void DrawText(int x, int y, string text, Cell style)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckRow(y);

        for (var i = 0; i < text.Length; i++)
        {
            var column = x + i;
            if (column < 0 || column >= Width)
            {
                continue;
            }

            this[column, y] = style with { Glyph = text[i] };
        }
    }

    /// <summary>
    /// Draws a single-line box with the given corner and edge glyphs.
    /// This is the primitive panels are built from, so the box geometry lives
    /// in exactly one place.
    ///
    /// The whole rectangle is checked before the first cell is written, so a box
    /// that does not fit throws <see cref="ArgumentOutOfRangeException"/> and
    /// leaves the buffer untouched rather than half-drawn. A caller that has
    /// already checked the geometry still gets the same exception, from the
    /// explicit check rather than from the indexer partway through.
    /// </summary>
    public void DrawBorder(int x, int y, int width, int height, BorderGlyphs glyphs, Cell style)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        CheckRectangle(x, y, width, height);

        for (var column = x; column < x + width; column++)
        {
            this[column, y] = style with { Glyph = glyphs.Horizontal };
            this[column, y + height - 1] = style with { Glyph = glyphs.Horizontal };
        }

        for (var row = y; row < y + height; row++)
        {
            this[x, row] = style with { Glyph = glyphs.Vertical };
            this[x + width - 1, row] = style with { Glyph = glyphs.Vertical };
        }

        this[x, y] = style with { Glyph = glyphs.TopLeft };
        this[x + width - 1, y] = style with { Glyph = glyphs.TopRight };
        this[x, y + height - 1] = style with { Glyph = glyphs.BottomLeft };
        this[x + width - 1, y + height - 1] = style with { Glyph = glyphs.BottomRight };
    }

    /// <summary>
    /// The buffer as one string per row, glyphs only and no colour. Used by
    /// tests and by the plain-text dump path; rendering to ANSI is
    /// <see cref="FrameDiff"/>'s job.
    /// </summary>
    public string[] ToLines()
    {
        var lines = new string[Height];
        for (var row = 0; row < Height; row++)
        {
            var builder = new StringBuilder(Width);
            for (var column = 0; column < Width; column++)
            {
                builder.Append(this[column, row].Glyph);
            }

            lines[row] = builder.ToString();
        }

        return lines;
    }

    /// <summary>
    /// Whether the whole rectangle lies inside the buffer, far edge included.
    /// Checked up front by the writers so a rejected rectangle never mutates
    /// anything.
    ///
    /// The sums are computed as <see cref="long"/> on purpose: in
    /// <see cref="int"/> they wrap, so a rectangle far larger than the buffer
    /// summed back to a negative number, passed this check, and then drew
    /// nothing at all instead of being rejected.
    /// </summary>
    private void CheckRectangle(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + (long)width > Width || y + (long)height > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                x,
                $"Rectangle ({x}, {y}, {width}, {height}) does not fit inside a {Width}x{Height} buffer.");
        }
    }

    private void CheckBounds(int x, int y)
    {
        if (x < 0 || x >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, $"Column must be within [0, {Width}).");
        }

        CheckRow(y);
    }

    private void CheckRow(int y)
    {
        if (y < 0 || y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Row must be within [0, {Height}).");
        }
    }
}