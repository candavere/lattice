using System.Text;

namespace Lattice.Tui;

/// <summary>
/// Produces the bytes that turn one frame into the next by writing only the
/// runs of cells that actually changed. Pure: two buffers and a colour depth in,
/// a string out. Nothing here touches a terminal, which is what lets the whole
/// renderer be tested against exact expected bytes.
/// </summary>
public static class FrameDiff
{
    /// <summary>
    /// Renders the difference from <paramref name="previous"/> to
    /// <paramref name="next"/>. A null <paramref name="previous"/> means
    /// "nothing has been drawn yet", so every cell is a change.
    /// </summary>
    /// <remarks>
    /// Output shape, fixed so callers can reason about it:
    /// unchanged rows contribute nothing at all; each changed row contributes
    /// one line; within a row, each contiguous run of changed cells is preceded
    /// by an absolute cursor position and carries an SGR only when its style
    /// differs from the style in force. Rows are joined with a single newline,
    /// so two identical buffers produce an empty string.
    /// </remarks>
    public static string Render(CellBuffer? previous, CellBuffer next, ColorDepth depth)
    {
        ArgumentNullException.ThrowIfNull(next);

        var lines = new List<string>();

        // Style state is tracked across the whole output, not per row: a
        // newline does not reset SGR in any terminal, so a row that needs the
        // same style as the row above it does not repeat the sequence. It
        // starts unknown, which makes the first cell of the output always emit
        // its style rather than assume the terminal was already in it.
        Cell? emitted = null;

        for (var row = 0; row < next.Height; row++)
        {
            var line = RenderRow(previous, next, row, depth, ref emitted);
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return string.Join("\n", lines);
    }

    private static string RenderRow(CellBuffer? previous, CellBuffer next, int row, ColorDepth depth, ref Cell? emitted)
    {
        var width = Math.Min(next.Width, previous?.Width ?? next.Width);
        var builder = new StringBuilder();
        var column = 0;

        while (column < width)
        {
            if (IsUnchanged(previous, next, column, row))
            {
                column++;
                continue;
            }

            var runStart = column;
            while (column < width && !IsUnchanged(previous, next, column, row))
            {
                column++;
            }

            // Absolute positioning: the diff never assumes where the cursor
            // was left, so it is correct after any partial write, including a
            // truncated one from a killed process.
            builder.Append("\u001b[").Append(row + 1).Append(';').Append(runStart + 1).Append('H');

            for (var i = runStart; i < column; i++)
            {
                var cell = next[i, row];
                if (emitted is null || !cell.SameStyleAs(emitted.Value))
                {
                    builder.Append(ColorMapper.Style(cell.Foreground, cell.Background, cell.Attributes, depth));
                    emitted = cell;
                }

                builder.Append(cell.Glyph);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnchanged(CellBuffer? previous, CellBuffer next, int column, int row)
    {
        if (previous is null)
        {
            return false;
        }

        if (row >= previous.Height || column >= previous.Width)
        {
            return false;
        }

        return previous[column, row].Matches(next[column, row]);
    }
}