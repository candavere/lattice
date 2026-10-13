namespace Lattice.Tui;

/// <summary>One slash-menu row: the command as typed (with leading "/") and its description.</summary>
public sealed record SlashMenuItem(string Name, string Description);

/// <summary>
/// Everything the slash console needs for one pure frame: the input as typed, the menu
/// rows to show, the selection, the working directory, an optional notice and result,
/// and the terminal state. No keys, no processes, no clock.
/// </summary>
public sealed record SlashConsoleRequest(
    string Input,
    IReadOnlyList<SlashMenuItem> Menu,
    int SelectedIndex,
    bool MenuOpen,
    string WorkingDirectory,
    string? Notice,
    string? ResultTitle,
    IReadOnlyList<string>? ResultLines,
    bool ResultTruncated,
    PaneSize Size,
    GlyphMode Glyphs,
    ColorDepth Depth,
    bool Utf8);

/// <summary>
/// The slash-command console as a pure layout: a request in, a grid the size of the
/// terminal out, no terminal touched. Home shows the dotted wordmark (or the plain-title
/// fallback), one input bar with a blue left edge, and the working directory; the slash
/// menu lists the research commands with the catalog's own descriptions; a result panel
/// shows a clipped run with a truncation marker; the bottom row carries key hints.
/// </summary>
/// <remarks>
/// Colours come only from <see cref="Theme"/>, <see cref="Palette"/> and
/// <see cref="Wordmark"/> (whose near-white and depth values match the official text
/// and depth step). No hex literal lives here. Panels and the screen fill paint only
/// at truecolor; below that the user's own background stays. Border and edge glyphs
/// are decoration, never the sole carrier of meaning.
/// </remarks>
public static class SlashConsoleLayout
{
    /// <summary>The input bar placeholder.</summary>
    public const string Placeholder = "Type / to see commands";

    /// <summary>Key hints while the menu is open. Implemented by the host in the next stage.</summary>
    public const string MenuOpenHints = "up/down select  tab complete  enter run  esc close";

    /// <summary>Key hints on home. Implemented by the host in the next stage.</summary>
    public const string HomeHints = "/ commands  /exit leave  ctrl-c quit";

    /// <summary>Visible marker that the result buffer was truncated to its bound.</summary>
    public const string TruncationMarker = "[truncated]";

    /// <summary>How many result lines the frame will show at most; the host bounds the buffer itself.</summary>
    public const int MaxResultLinesShown = 12;

    /// <summary>Whether this frame draws the wordmark rather than the centred fallback.</summary>
    public static bool ShowsWordmark(SlashConsoleRequest request) =>
        Wordmark.ShouldShowWordmark(request.Size.Width, request.Size.Height, request.Utf8, request.Glyphs, request.MenuOpen);

    /// <summary>Renders one console frame exactly the size of the terminal.</summary>
    public static CellBuffer Render(SlashConsoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var size = request.Size;
        var cells = new CellBuffer(size.Width, size.Height);

        var panelFill = Theme.PanelFill(request.Depth);
        if (request.Depth == ColorDepth.TrueColor)
        {
            cells.Fill(0, 0, size.Width, size.Height, new Cell(' ', null, Theme.ScreenBackground));
        }

        var edge = request.Glyphs == GlyphMode.Ascii ? '|' : '│';

        // Always-visible plain title: top-left, bold, never replaced by the wordmark.
        DrawPlainHeader(cells, size);

        var showed = ShowsWordmark(request);
        int inputY;
        if (showed)
        {
            var x = Math.Max(0, (size.Width - Wordmark.Width) / 2);
            var y = 2;
            if (y + Wordmark.Height < size.Height - 4)
            {
                Wordmark.DrawRows(cells, x, y, request.Depth);
                inputY = y + Wordmark.Height + 2;
            }
            else
            {
                inputY = Math.Min(size.Height - 4, 4);
            }
        }
        else
        {
            // Centred fallback title in addition to the always-visible header.
            if (size.Height > 4 && size.Width >= Wordmark.PlainTitle.Length + 2)
            {
                var title = Wordmark.PlainTitle;
                var start = Math.Max(0, (size.Width - title.Length) / 2);
                var style = new Cell(' ', Palette.TextPrimary, panelFill, CellAttributes.Bold);
                for (var i = 0; i < title.Length; i++)
                {
                    var bx = start + i;
                    if (bx >= 0 && bx < size.Width)
                    {
                        cells[bx, 2] = style with { Glyph = title[i] };
                    }
                }
            }

            inputY = 4;
        }

        if (request.Notice is { Length: > 0 } notice && inputY > 1)
        {
            DrawClipped(cells, 2, inputY - 1, size.Width - 4, notice, new Cell(' ', Theme.SecondaryText, panelFill));
        }

        DrawInputBar(cells, size, request, inputY, edge, panelFill);

        var nextY = inputY + 1;
        if (request.MenuOpen)
        {
            nextY = DrawMenu(cells, size, request, nextY, edge, panelFill);
        }

        if (request.ResultLines is { Count: > 0 } || request.ResultTitle is { Length: > 0 })
        {
            DrawResult(cells, size, request, nextY, panelFill);
        }

        DrawBottom(cells, size, request, panelFill);

        return cells;
    }

    private static void DrawPlainHeader(CellBuffer cells, PaneSize size)
    {
        if (size.Height < 1)
        {
            return;
        }

        var title = Wordmark.PlainTitle;
        var style = new Cell(' ', Palette.TextPrimary, null, CellAttributes.Bold);
        for (var i = 0; i < title.Length && 2 + i < size.Width; i++)
        {
            cells[2 + i, 0] = style with { Glyph = title[i] };
        }
    }

    private static void DrawInputBar(CellBuffer cells, PaneSize size, SlashConsoleRequest request, int y, char edge, Rgb? panelFill)
    {
        if (y < 0 || y >= size.Height)
        {
            return;
        }

        cells[0, y] = new Cell(edge, Palette.AccentSecondary, panelFill, CellAttributes.Bold);

        var text = request.Input.Length > 0 ? request.Input : Placeholder;
        var style = request.Input.Length > 0
            ? new Cell(' ', Palette.TextPrimary, panelFill)
            : new Cell(' ', Theme.Muted, panelFill);

        var clipped = CellText.Clip(text, Math.Max(0, size.Width - 3), request.Glyphs);
        for (var i = 0; i < clipped.Length; i++)
        {
            var bx = 2 + i;
            if (bx >= size.Width)
            {
                break;
            }

            cells[bx, y] = style with { Glyph = clipped[i] };
        }
    }

    private static int DrawMenu(CellBuffer cells, PaneSize size, SlashConsoleRequest request, int firstY, char edge, Rgb? panelFill)
    {
        var y = firstY;
        var count = request.Menu.Count;
        for (var i = 0; i < count; i++)
        {
            if (y >= size.Height - 2)
            {
                break;
            }

            var item = request.Menu[i];
            var selected = i == request.SelectedIndex;
            var rowBg = selected && request.Depth == ColorDepth.TrueColor ? Theme.SelectionBackground : panelFill;

            if (selected)
            {
                // Element band at truecolor; below truecolor the user's background stays
                // and the orange text, edge and Bold carry the selection alone.
                if (request.Depth == ColorDepth.TrueColor)
                {
                    for (var col = 0; col < size.Width; col++)
                    {
                        cells[col, y] = new Cell(' ', null, rowBg);
                    }
                }

                cells[0, y] = new Cell(edge, Palette.Accent, rowBg, CellAttributes.Bold);
            }

            var nameStyle = selected
                ? new Cell(' ', Palette.Accent, rowBg, CellAttributes.Bold)
                : new Cell(' ', Palette.TextPrimary, rowBg);
            var descStyle = selected
                ? new Cell(' ', Palette.Accent, rowBg)
                : new Cell(' ', Theme.Muted, rowBg);

            var name = CellText.Clip(item.Name, Math.Max(0, Math.Min(20, size.Width - 4)), request.Glyphs);
            var x = 2;
            foreach (var c in name)
            {
                if (x >= size.Width - 1)
                {
                    break;
                }

                cells[x++, y] = nameStyle with { Glyph = c };
            }

            x++;
            var room = Math.Max(0, size.Width - x - 1);
            var desc = CellText.Clip(item.Description, room, request.Glyphs);
            foreach (var c in desc)
            {
                if (x >= size.Width)
                {
                    break;
                }

                cells[x++, y] = descStyle with { Glyph = c };
            }

            y++;
        }

        return y;
    }

    private static void DrawResult(CellBuffer cells, PaneSize size, SlashConsoleRequest request, int firstY, Rgb? panelFill)
    {
        var y = firstY;
        if (y >= size.Height - 2)
        {
            return;
        }

        if (request.ResultTitle is { Length: > 0 } title)
        {
            var titleStyle = title.Contains("failed", StringComparison.Ordinal) || title.Contains("cancelled", StringComparison.Ordinal)
                ? new Cell(' ', Palette.Error, panelFill, CellAttributes.Bold)
                : title.Contains("ok", StringComparison.Ordinal) || title.StartsWith("exit 0", StringComparison.Ordinal)
                    ? new Cell(' ', Palette.Success, panelFill, CellAttributes.Bold)
                    : new Cell(' ', Palette.TextPrimary, panelFill, CellAttributes.Bold);
            DrawClipped(cells, 2, y, size.Width - 4, title, titleStyle);
            y++;
        }

        if (request.ResultLines is null)
        {
            return;
        }

        var shown = 0;
        foreach (var line in request.ResultLines)
        {
            if (shown >= MaxResultLinesShown || y >= size.Height - 2)
            {
                break;
            }

            DrawClipped(cells, 2, y, size.Width - 4, line, new Cell(' ', Palette.TextPrimary, panelFill));
            shown++;
            y++;
        }

        if (request.ResultTruncated && y < size.Height - 2)
        {
            DrawClipped(cells, 2, y, size.Width - 4, TruncationMarker, new Cell(' ', Palette.Warning, panelFill, CellAttributes.Bold));
        }
    }

    private static void DrawBottom(CellBuffer cells, PaneSize size, SlashConsoleRequest request, Rgb? panelFill)
    {
        if (size.Height < 2)
        {
            return;
        }

        var cwdY = size.Height - 2;
        var hintY = size.Height - 1;

        if (!string.IsNullOrEmpty(request.WorkingDirectory))
        {
            DrawClipped(cells, 0, cwdY, Math.Max(0, size.Width / 2), request.WorkingDirectory, new Cell(' ', Theme.Muted, panelFill));
        }

        var hints = request.MenuOpen ? MenuOpenHints : HomeHints;
        DrawClipped(cells, 0, hintY, size.Width, hints, new Cell(' ', Theme.KeyHint, panelFill));
    }

    private static void DrawClipped(CellBuffer cells, int x, int y, int width, string text, Cell style)
    {
        if (y < 0 || y >= cells.Height || width <= 0)
        {
            return;
        }

        var mode = style.Glyph < 128 && text.All(c => c < 128) ? GlyphMode.Ascii : GlyphMode.Unicode;
        var clipped = CellText.Clip(text, Math.Min(width, cells.Width - x), mode);
        for (var i = 0; i < clipped.Length; i++)
        {
            var bx = x + i;
            if (bx < 0 || bx >= cells.Width)
            {
                continue;
            }

            cells[bx, y] = style with { Glyph = clipped[i] };
        }
    }
}
