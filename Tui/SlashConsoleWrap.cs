namespace Lattice.Tui;

/// <summary>
/// Wraps captured output into pane-width rows without ever dropping a character
/// to an ellipsis. Newlines split first, then each segment fills rows up to
/// <c>width</c>; a full row breaks at its last space so words stay whole, and a
/// word longer than the width breaks mid-word. The result workspace scrolls these
/// rows, so what it shows is the whole stream, wrapped, never clipped.
/// </summary>
public static class SlashConsoleWrap
{
    /// <summary>Wraps one text into rows of at most <paramref name="width"/> cells.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<string>();
        foreach (var segment in text.Split('\n'))
        {
            WrapSegment(segment, width, rows);
        }

        // An empty text still occupies one blank row, so callers can render it.
        if (rows.Count == 0)
        {
            rows.Add(string.Empty);
        }

        return rows;
    }

    /// <summary>Wraps each line, keeping the lines' own order and boundaries.</summary>
    public static IReadOnlyList<string> WrapLines(IReadOnlyList<string> lines, int width)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rows = new List<string>();
        foreach (var line in lines)
        {
            WrapSegment(line, width, rows);
        }

        if (rows.Count == 0)
        {
            rows.Add(string.Empty);
        }

        return rows;
    }

    private static void WrapSegment(string segment, int width, List<string> rows)
    {
        if (width <= 0)
        {
            // No room for even one cell: every character takes its own row.
            foreach (var c in segment)
            {
                rows.Add(c.ToString());
            }

            if (segment.Length == 0)
            {
                rows.Add(string.Empty);
            }

            return;
        }

        if (segment.Length == 0)
        {
            rows.Add(string.Empty);
            return;
        }

        var row = new System.Text.StringBuilder();
        var column = 0;
        var index = 0;
        while (index < segment.Length)
        {
            row.Append(segment[index]);
            column++;
            index++;

            if (column < width)
            {
                continue;
            }

            // The row is full. If the next character is the word separator, the
            // row already ends at a word boundary: emit it whole and skip the space.
            if (index < segment.Length && segment[index] == ' ')
            {
                rows.Add(row.ToString().TrimEnd());
                row.Clear();
                column = 0;
                index++;
                continue;
            }

            var space = LastSpace(row);
            if (space >= 0)
            {
                rows.Add(row.ToString(0, space));
                var tail = row.ToString(space + 1, row.Length - space - 1);
                row.Clear();
                row.Append(tail);

                // The carried word occupies the next row. When the separator was the
                // row's last character the carry is empty and the row count is left at
                // its bound, so the following word begins on its own row (the console
                // never merges a word onto the row that just broke off it).
                if (tail.Length > 0)
                {
                    column = tail.Length;
                }

                continue;
            }

            rows.Add(row.ToString());
            row.Clear();
            column = 0;
        }

        if (row.Length > 0)
        {
            rows.Add(row.ToString());
        }
    }

    private static int LastSpace(System.Text.StringBuilder row)
    {
        for (var i = row.Length - 1; i >= 0; i--)
        {
            if (row[i] == ' ')
            {
                return i;
            }
        }

        return -1;
    }
}
