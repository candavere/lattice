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
/// terminal out, no terminal touched. Home shows the centred dotted wordmark (or the
/// centred plain-title fallback) as one vertically centred composition with the input
/// in a centred bounded column, and the working directory pinned bottom-left; the slash
/// menu sits above the input in the same column with aligned names and descriptions;
/// a result panel keeps the top-anchored flow with a truncation marker so long output
/// stays readable; the bottom row carries key hints right-aligned.
/// </summary>
/// <remarks>
/// Colours come only from <see cref="Theme"/>, <see cref="Palette"/> and
/// <see cref="Wordmark"/> (uniform near-white, flat, no depth shading). No hex literal
/// lives here. Panels and the screen fill paint only at truecolor; below that the user's
/// own background stays. Border and edge glyphs are decoration, never the sole carrier
/// of meaning. Never prints design-review labels.
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

    /// <summary>Maximum width of the centred input/menu column.</summary>
    public const int ContentMaxWidth = 88;

    /// <summary>Minimum breathing room left and right of the centred column.</summary>
    public const int ContentMargin = 4;

    /// <summary>Fixed width of the menu name column, keeping descriptions aligned.</summary>
    public const int NameColumnWidth = 20;

    /// <summary>Whether this frame draws the wordmark rather than the centred fallback.</summary>
    public static bool ShowsWordmark(SlashConsoleRequest request) =>
        Wordmark.ShouldShowWordmark(request.Size.Width, request.Size.Height, request.Utf8, request.Glyphs, request.MenuOpen);

    /// <summary>
    /// One frame's vertical geometry: every row the renderer draws, derived from the
    /// terminal size alone. The renderer and <see cref="InputCursor"/> share this
    /// calculation, so the input the screen draws and the input position it reports
    /// can never disagree.
    /// </summary>
    /// <param name="Centered">Whether the wordmark/menu/input group is centred as one
    /// composition in the usable canvas. False when a result is on show: the result
    /// body keeps the top-anchored flow so a long output stays fully readable
    /// instead of being squeezed into a centred decorative block.</param>
    /// <param name="NoticeY">The notice row, or -1 when there is no notice.</param>
    /// <param name="HeadY">The wordmark top row, or the fallback title row.</param>
    /// <param name="HeadIsWordmark">Whether the head block is the wordmark.</param>
    /// <param name="HeadDrawn">Whether the head block is drawn at all.</param>
    /// <param name="MenuTop">The first menu row.</param>
    /// <param name="MenuRows">How many menu rows draw.</param>
    /// <param name="MenuStart">The first visible menu index (selection scrolling).</param>
    /// <param name="InputY">The input row.</param>
    /// <param name="ResultTop">The first result row.</param>
    public readonly record struct ConsoleGeometry(
        bool Centered,
        int NoticeY,
        int HeadY,
        bool HeadIsWordmark,
        bool HeadDrawn,
        int MenuTop,
        int MenuRows,
        int MenuStart,
        int InputY,
        int ResultTop);

    /// <summary>Rows the footer owns: the working-directory row and the hints row.</summary>
    public const int FooterRows = 2;

    /// <summary>Gap rows between the head block and the menu/input below it.</summary>
    public const int HeadGap = 2;

    /// <summary>Gap rows between the menu and the input below it.</summary>
    public const int MenuGap = 1;

    /// <summary>
    /// Computes one frame's vertical geometry from the terminal size alone: no terminal
    /// touched, no exceptions for any size. The usable canvas is the terminal minus the
    /// pinned footer; the wordmark/menu/input group is centred in it as one composition
    /// whenever it fits, with the free space above and below differing by at most one
    /// row. When it cannot fit, the group clamps to the top with a bounded menu viewport
    /// that always leaves the input row above the footer.
    /// </summary>
    public static ConsoleGeometry ComputeGeometry(SlashConsoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var width = request.Size.Width;
        var height = request.Size.Height;
        var usable = Math.Max(0, height - FooterRows);
        var menuCount = request.MenuOpen && request.Menu.Count > 0 ? request.Menu.Count : 0;
        var wantNotice = !string.IsNullOrEmpty(request.Notice);
        var showed = ShowsWordmark(request);
        var headH = showed ? Wordmark.Height : 1;
        var hasResult = request.ResultTitle is { Length: > 0 } || request.ResultLines is { Count: > 0 };

        if (!hasResult)
        {
            var full = (wantNotice ? 1 : 0) + headH + HeadGap + menuCount + (menuCount > 0 ? MenuGap : 0) + 1;
            int top;
            int menuRows;
            if (full <= usable)
            {
                top = (usable - full) / 2;
                menuRows = menuCount;
            }
            else
            {
                top = 0;
                var room = usable - ((wantNotice ? 1 : 0) + headH + HeadGap + 1);
                if (menuCount > 0)
                {
                    room -= MenuGap;
                }

                menuRows = Math.Max(0, Math.Min(menuCount, room));
            }

            top = Math.Max(0, top);
            var y = top;
            var noticeY = wantNotice ? y++ : -1;
            var headY = y;
            y += headH;
            y += HeadGap;
            var menuTop = y;
            y += menuRows;
            if (menuRows > 0)
            {
                y += MenuGap;
            }

            var inputY = y;
            return new ConsoleGeometry(
                true, noticeY, headY, showed, true, menuTop, menuRows,
                MenuStartIndex(request, menuRows), inputY, inputY + MenuGap + 1);
        }

        // Results keep the top-anchored flow so the whole output body stays
        // readable; clearing the result restores the centred home composition.
        var headYLegacy = 2;
        var headDrawn = showed && headYLegacy + Wordmark.Height < height - 4;
        int contentTop = headDrawn
            ? headYLegacy + Wordmark.Height + HeadGap
            : Math.Min(height - 4, 4);
        contentTop = Math.Max(0, Math.Min(contentTop, Math.Max(0, height - 3)));

        var roomLegacy = height - FooterRows - contentTop - 1 - 1;
        var menuRowsLegacy = Math.Max(0, Math.Min(menuCount, roomLegacy));
        var noticeYLegacy = wantNotice && contentTop > 0 ? contentTop - 1 : -1;
        var inputYLegacy = contentTop + menuRowsLegacy + (menuRowsLegacy > 0 ? MenuGap : 0);
        return new ConsoleGeometry(
            false, noticeYLegacy, headYLegacy, showed, headDrawn, contentTop, menuRowsLegacy,
            MenuStartIndex(request, menuRowsLegacy), inputYLegacy, inputYLegacy + MenuGap + 1);
    }

    private static int MenuStartIndex(SlashConsoleRequest request, int rowsToShow)
    {
        if (rowsToShow >= request.Menu.Count)
        {
            return 0;
        }

        return Math.Clamp(request.SelectedIndex - rowsToShow + 1, 0, request.Menu.Count - rowsToShow);
    }

    /// <summary>
    /// The input text position from the same geometry the renderer draws: the input
    /// row and the column just past the last visible input character (the text origin
    /// when the line is empty). The console session hides the terminal cursor while
    /// the screen owns it; this is the shared calculation any cursor placement must use.
    /// </summary>
    public static (int Row, int Col) InputCursor(SlashConsoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var geometry = ComputeGeometry(request);
        var (colX, colW) = ContentColumn(request.Size.Width);
        var textX = colX + 2;
        var room = Math.Max(0, colX + colW - 1 - textX);
        var visible = request.Input.Length > 0
            ? CellText.Clip(request.Input, room, request.Glyphs).Length
            : 0;
        var col = Math.Max(textX, Math.Min(textX + visible, colX + colW - 1));
        var row = Math.Max(0, Math.Min(geometry.InputY, Math.Max(0, request.Size.Height - 1)));
        return (row, col);
    }

    /// <summary>The centred bounded column: left origin and width.</summary>
    public static (int X, int Width) ContentColumn(int terminalWidth)
    {
        var width = Math.Min(ContentMaxWidth, Math.Max(10, terminalWidth - ContentMargin * 2));
        width = Math.Min(width, Math.Max(10, terminalWidth));
        var x = Math.Max(0, (terminalWidth - width) / 2);
        return (x, width);
    }

    /// <summary>
    /// The working-directory label: truthful home-relative "~" shortening when the path
    /// sits under the user's home; otherwise the path unchanged. Never hard-codes a sample.
    /// </summary>
    public static string DisplayWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return workingDirectory;
        }

        string? home = null;
        try
        {
            home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            home = null;
        }

        if (string.IsNullOrEmpty(home))
        {
            home = System.Environment.GetEnvironmentVariable("HOME");
        }

        if (!string.IsNullOrEmpty(home))
        {
            if (workingDirectory.Equals(home, StringComparison.Ordinal))
            {
                return "~";
            }

            var prefix = home.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar;
            var altPrefix = home.TrimEnd('/', '\\') + (System.IO.Path.DirectorySeparatorChar == '/' ? '\\' : '/');
            if (workingDirectory.StartsWith(prefix, StringComparison.Ordinal))
            {
                return "~" + workingDirectory.Substring(home.Length);
            }

            if (workingDirectory.StartsWith(altPrefix, StringComparison.Ordinal))
            {
                return "~" + workingDirectory.Substring(home.Length);
            }

            // POSIX home with forward slashes even on Windows-style separators.
            var slashHome = home.Replace('\\', '/').TrimEnd('/');
            var slashPath = workingDirectory.Replace('\\', '/');
            if (slashPath.Equals(slashHome, StringComparison.Ordinal))
            {
                return "~";
            }

            if (slashPath.StartsWith(slashHome + "/", StringComparison.Ordinal))
            {
                return "~" + slashPath.Substring(slashHome.Length);
            }
        }

        return workingDirectory;
    }

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
        var (colX, colW) = ContentColumn(size.Width);
        var geometry = ComputeGeometry(request);

        if (geometry.HeadDrawn)
        {
            if (geometry.HeadIsWordmark)
            {
                var x = Math.Max(0, (size.Width - Wordmark.Width) / 2);
                Wordmark.DrawRows(cells, x, geometry.HeadY, request.Depth);
            }
            else if (size.Height > 4 && size.Width >= Wordmark.PlainTitle.Length + 2)
            {
                // Centred fallback title only; no competing top-left header.
                var title = Wordmark.PlainTitle;
                var start = Math.Max(0, (size.Width - title.Length) / 2);
                var style = new Cell(' ', Palette.TextPrimary, panelFill, CellAttributes.Bold);
                for (var i = 0; i < title.Length; i++)
                {
                    var bx = start + i;
                    if (bx >= 0 && bx < size.Width)
                    {
                        cells[bx, geometry.HeadY] = style with { Glyph = title[i] };
                    }
                }
            }
        }

        if (geometry.NoticeY >= 0 && !string.IsNullOrEmpty(request.Notice))
        {
            var noticeStyle = new Cell(' ', Theme.SecondaryText, panelFill);
            DrawClippedInColumn(cells, colX, geometry.NoticeY, colW, request.Notice, noticeStyle);
        }

        if (geometry.MenuRows > 0)
        {
            DrawMenu(cells, size, request, geometry, colX, colW, edge, panelFill);
        }

        DrawInputBar(cells, size, request, geometry.InputY, colX, colW, edge, panelFill);

        if (request.ResultLines is { Count: > 0 } || request.ResultTitle is { Length: > 0 })
        {
            DrawResult(cells, size, request, geometry.ResultTop, colX, colW, panelFill);
        }

        DrawBottom(cells, size, request, panelFill);

        return cells;
    }

    private static void DrawInputBar(CellBuffer cells, PaneSize size, SlashConsoleRequest request, int y, int colX, int colW, char edge, Rgb? panelFill)
    {
        if (y < 0 || y >= size.Height)
        {
            return;
        }

        // Bounded darker panel across the centred column.
        if (panelFill is not null && colW > 0)
        {
            var fill = new Cell(' ', null, panelFill);
            var fx = Math.Max(0, colX);
            var fw = Math.Min(colW, size.Width - fx);
            if (fw > 0)
            {
                cells.Fill(fx, y, fw, 1, fill);
            }
        }

        if (colX >= 0 && colX < size.Width)
        {
            cells[colX, y] = new Cell(edge, Palette.AccentSecondary, panelFill, CellAttributes.Bold);
        }

        var text = request.Input.Length > 0 ? request.Input : Placeholder;
        var style = request.Input.Length > 0
            ? new Cell(' ', Palette.TextPrimary, panelFill)
            : new Cell(' ', Theme.Muted, panelFill);

        var textX = colX + 2;
        var textRoom = Math.Max(0, colX + colW - 1 - textX);
        var clipped = CellText.Clip(text, textRoom, request.Glyphs);
        for (var i = 0; i < clipped.Length; i++)
        {
            var bx = textX + i;
            if (bx < 0 || bx >= size.Width)
            {
                break;
            }

            cells[bx, y] = style with { Glyph = clipped[i] };
        }
    }

    private static void DrawMenu(CellBuffer cells, PaneSize size, SlashConsoleRequest request, ConsoleGeometry geometry, int colX, int colW, char edge, Rgb? panelFill)
    {
        var firstY = geometry.MenuTop;
        var rowsToShow = geometry.MenuRows;
        var y = firstY;
        var nameCol = Math.Min(NameColumnWidth, Math.Max(8, colW - 10));
        var nameX = colX + 2;
        var descX = nameX + nameCol + 2;

        // The visible window follows the selection, from the shared geometry.
        var start = geometry.MenuStart;

        for (var r = 0; r < rowsToShow; r++)
        {
            var i = start + r;
            if (i < 0 || i >= request.Menu.Count)
            {
                break;
            }

            if (y >= size.Height - 2)
            {
                break;
            }

            var item = request.Menu[i];
            var selected = i == request.SelectedIndex;
            var rowBg = selected && request.Depth == ColorDepth.TrueColor ? Theme.SelectionBackground : panelFill;

            if (request.Depth == ColorDepth.TrueColor)
            {
                var fx = Math.Max(0, colX);
                var fw = Math.Min(colW, size.Width - fx);
                if (fw > 0)
                {
                    for (var col = fx; col < fx + fw; col++)
                    {
                        cells[col, y] = new Cell(' ', null, rowBg);
                    }
                }
            }

            if (selected && colX >= 0 && colX < size.Width)
            {
                cells[colX, y] = new Cell(edge, Palette.Accent, rowBg, CellAttributes.Bold);
            }

            var nameStyle = selected
                ? new Cell(' ', Palette.Accent, rowBg, CellAttributes.Bold)
                : new Cell(' ', Palette.TextPrimary, rowBg);
            var descStyle = selected
                ? new Cell(' ', Palette.Accent, rowBg)
                : new Cell(' ', Theme.Muted, rowBg);

            var name = CellText.Clip(item.Name, nameCol, request.Glyphs);
            var x = nameX;
            foreach (var c in name)
            {
                if (x >= colX + colW - 1 || x >= size.Width - 1)
                {
                    break;
                }

                if (x >= 0)
                {
                    cells[x, y] = nameStyle with { Glyph = c };
                }

                x++;
            }

            var room = Math.Max(0, Math.Min(colX + colW - 1, size.Width - 1) - descX);
            if (room > 0)
            {
                var desc = CellText.Clip(item.Description, room, request.Glyphs);
                x = descX;
                foreach (var c in desc)
                {
                    if (x >= colX + colW || x >= size.Width)
                    {
                        break;
                    }

                    if (x >= 0)
                    {
                        cells[x, y] = descStyle with { Glyph = c };
                    }

                    x++;
                }
            }

            y++;
        }
    }

    private static void DrawResult(CellBuffer cells, PaneSize size, SlashConsoleRequest request, int firstY, int colX, int colW, Rgb? panelFill)
    {
        var y = firstY;
        if (y >= size.Height - 2)
        {
            return;
        }

        var textX = colX + 2;
        var textW = Math.Max(0, colX + colW - 1 - textX);

        if (request.ResultTitle is { Length: > 0 } title)
        {
            var titleStyle = title.Contains("failed", StringComparison.Ordinal) || title.Contains("cancelled", StringComparison.Ordinal)
                ? new Cell(' ', Palette.Error, panelFill, CellAttributes.Bold)
                : title.Contains("ok", StringComparison.Ordinal) || title.StartsWith("exit 0", StringComparison.Ordinal)
                    ? new Cell(' ', Palette.Success, panelFill, CellAttributes.Bold)
                    : new Cell(' ', Palette.TextPrimary, panelFill, CellAttributes.Bold);
            DrawClippedInColumn(cells, textX, y, textW, title, titleStyle);
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

            DrawClippedInColumn(cells, textX, y, textW, line, new Cell(' ', Palette.TextPrimary, panelFill));
            shown++;
            y++;
        }

        if (request.ResultTruncated && y < size.Height - 2)
        {
            DrawClippedInColumn(cells, textX, y, textW, TruncationMarker, new Cell(' ', Palette.Warning, panelFill, CellAttributes.Bold));
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
            var display = DisplayWorkingDirectory(request.WorkingDirectory);
            var maxCwd = Math.Max(10, size.Width / 2);
            display = ClipLeft(display, maxCwd, request.Glyphs);
            DrawClipped(cells, 2, cwdY, Math.Max(0, Math.Min(maxCwd, size.Width - 2)), display, new Cell(' ', Theme.Muted, panelFill));
        }

        var hints = request.MenuOpen ? MenuOpenHints : HomeHints;
        var hintRoom = Math.Max(0, size.Width - 4);
        var clippedHints = CellText.Clip(hints, hintRoom, request.Glyphs);
        var hintX = Math.Max(2, size.Width - 2 - clippedHints.Length);
        if (clippedHints.Length >= size.Width - 2)
        {
            hintX = 0;
        }

        DrawClipped(cells, hintX, hintY, size.Width - hintX, clippedHints, new Cell(' ', Theme.KeyHint, panelFill));
    }

    private static string ClipLeft(string text, int maxWidth, GlyphMode glyphs)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        var clipped = CellText.Clip(text, maxWidth, glyphs);
        if (clipped.Length <= maxWidth)
        {
            // CellText.Clip clips the right side; if it fits, keep it whole.
            // When the original is longer, keep the tail so the leaf directory stays visible.
            if (text.Length <= maxWidth)
            {
                return clipped;
            }
        }

        if (text.Length <= maxWidth)
        {
            return CellText.Clip(text, maxWidth, glyphs);
        }

        var ascii = glyphs == GlyphMode.Ascii;
        var ellipsis = ascii ? "..." : "…";
        var keep = Math.Max(0, maxWidth - ellipsis.Length);
        var tail = text.Length > keep ? text[^keep..] : text;
        return ellipsis + tail;
    }

    private static void DrawClippedInColumn(CellBuffer cells, int x, int y, int width, string text, Cell style)
    {
        DrawClipped(cells, x, y, width, text, style);
    }

    private static void DrawClipped(CellBuffer cells, int x, int y, int width, string text, Cell style)
    {
        if (y < 0 || y >= cells.Height || width <= 0)
        {
            return;
        }

        var mode = style.Glyph < 128 && text.All(c => c < 128) ? GlyphMode.Ascii : GlyphMode.Unicode;
        var clipped = CellText.Clip(text, Math.Min(width, Math.Max(0, cells.Width - x)), mode);
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
