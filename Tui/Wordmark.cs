namespace Lattice.Tui;

/// <summary>
/// The dotted cursive "lattice" wordmark for the slash-command console home screen.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: nearest real-glyph implementation of the owner's selected dotted-cursive
/// direction, generated from the URW Z003 Medium Italic script face (a Zapf Chancery clone)
/// at 2x4 dots per cell, size 46, depth offset 3 dots right and down, no stroke thickening;
/// a heavier variant was too blobby. The selected picture itself was a hand-drawn dot mockup,
/// so these rows are NOT the owner-approved exact shape. Stored here as committed data
/// (constants, not a font dependency at run time). A Pillow render of these rows reads as
/// "lattice"; that is NOT terminal evidence.
/// </para>
/// <para>
/// Colours: foreground cells are near-white #eeeeee; cells marked S in the mask are
/// depth-only cells, #3c3c3c. A terminal cell has one foreground colour, so a cell that
/// holds both letter dots and depth dots is drawn in the letter colour: depth shows only
/// as a dim speckle to the right and below the letters. Two colours are never faked in
/// one cell.
/// </para>
/// <para>
/// Narrow Braille exception: showing this dotted wordmark needs Braille Patterns
/// U+2800-U+28FF, which <see cref="Glyphs"/> deliberately excludes. This module is the
/// ONLY production code allowed to emit Braille, and only the characters in
/// <see cref="Rows"/> below. It never passes through <see cref="Glyphs.Resolve"/> and
/// it chooses the plain title itself in ASCII mode or when the locale is not UTF-8.
/// The policy is NOT relaxed for anything else.
/// </para>
/// <para>
/// Fallbacks: below 62x16 (or below 62x30 while the menu is open) the plain bold
/// <c>lattice</c> title is shown instead of the wordmark. In ASCII glyph mode, or when
/// the locale is not UTF-8 (see <see cref="CapabilityDetector"/>), Braille is never
/// emitted and the plain title is shown. At 16 colours or no colour the depth cells
/// are dropped. A terminal with no Braille font renders a blank area the program cannot
/// detect, so callers must keep a plain <c>lattice</c> title visible somewhere on screen
/// (input bar header or bottom line) and never replace it with the wordmark.
/// </para>
/// </remarks>
public static class Wordmark
{
    /// <summary>Wordmark width in cells.</summary>
    public const int Width = 58;

    /// <summary>Wordmark height in cells.</summary>
    public const int Height = 8;

    /// <summary>Minimum terminal width that can carry the wordmark.</summary>
    public const int MinWidth = 62;

    /// <summary>Minimum terminal height that can carry the wordmark when the menu is closed.</summary>
    public const int MinHeight = 16;

    /// <summary>Minimum terminal height that can carry the wordmark while the menu is open.</summary>
    public const int MinHeightWithMenu = 30;

    /// <summary>The plain title shown whenever the wordmark cannot be shown.</summary>
    public const string PlainTitle = "lattice";

    /// <summary>First code point of the Braille Patterns block (U+2800).</summary>
    public const int BrailleRangeStart = 0x2800;

    /// <summary>Last code point of the Braille Patterns block (U+28FF).</summary>
    public const int BrailleRangeEnd = 0x28FF;

    /// <summary>Wordmark letter colour: near-white #eeeeee (OpenCode text).</summary>
    public static readonly Rgb Foreground = Rgb.FromHex("#eeeeee");

    /// <summary>Wordmark depth-only colour: #3c3c3c (OpenCode depth step).</summary>
    public static readonly Rgb Depth = Rgb.FromHex("#3c3c3c");

    /// <summary>
    /// The 8 wordmark rows, each exactly 58 characters of Braille U+2800-U+28FF and spaces only.
    /// </summary>
    public static readonly string[] Rows =
    [
        "        ⣀⡴⠿⢟⣁⣀                        ⣀                   ",
        "       ⣼⡿⣠⡴⠛⠛⠁                       ⠺⡿⣣⣄                 ",
        "      ⢰⣿⢳⣿⠃           ⢠⣶⠇⢀    ⣴⡾ ⡀    ⠈⠻⠋                 ",
        "      ⣾⡟⣿⡏  ⣠⡴⠚⠻⣿⣿⣃⣀  ⣾⣿⣾⡟   ⢰⣿⣷⣿⠃ ⣠⣴⣾⠃⣀  ⣀⡴⠾⣿⣆⡀  ⣠⠖⢻⣿⣆⡀  ",
        "     ⢰⣿⢻⣿⠁ ⣼⡟⣠⡶⠋⣿⣟⣿⡏⠉⢹⣿⣿⣿⣏⣀⡈⠉⣿⣿⣿⣿⣁⣈⠁⣿⣿⣿⡏ ⣼⡏⣠⡴⠛⢻⡿ ⣾⠏⣠⣾⡿⣿⣿  ",
        "     ⣼⡿⣿⡏ ⢸⣿⣱⣿⠁⢸⣿⣻⣿⠁ ⣼⡿⣿⡟   ⢠⣿⢿⣿⠃  ⢰⣿⢻⣿⠁⣸⣿⣰⡿    ⣸⣿⣼⡟⠁⣠⠿⠃  ",
        "    ⢠⣿⣷⣿⣣⠄⣿⣿⣿⣇⡤⣾⣿⣿⣯⠄⢠⣿⣷⣿⣃⡤  ⣼⣿⣾⣟⣠⠄ ⣾⣿⣿⣏⡄⣿⣿⣿⡇ ⣀⡤ ⣿⣿⣿⡷⠛⣁⠄   ",
        "    ⠸⣿⣿⣿⣀⡤⢿⣿⣿⣏⣠⣿⢿⣿⣇⡤⠾⣿⣿⣿⣁⣠⠴⠂⢿⣿⣿⣏⣀⡤⠖⣿⣿⣿⣇⣠⢿⣿⣿⡷⠚⠁⣠⠴⠻⣿⣿⡷⠞⠁⣠⠔  ",
    ];

    /// <summary>
    /// Mask for <see cref="Rows"/>: F = foreground cell, S = depth-only cell, . = empty.
    /// The source of truth for colours; same 8x58 shape as <see cref="Rows"/>.
    /// </summary>
    public static readonly string[] Mask =
    [
        "........FFFFFS........................F...................",
        ".......FFSSSSS.......................FFFS.................",
        "......FFFSS...........FFFS....FF.S....SSS.................",
        "......FFSS..FFFFFFFS..FFSS...FFFSS.FFFFS..FFFFFS..FFFFFS..",
        ".....FFFSS.FFSSSFFSSFFFFFFSFFFFFFFFFFFSS.FFSSFSS.FFSFFFS..",
        ".....FFSS.FFFSSFFFSS.FFSS...FFFSS..FFFSSFFSS....FFFFFSSS..",
        "....FFFFFFFFSFFFFFFFFFFSFF..FFSFFF.FFSFFFFSS.FF.FFSSSFF...",
        "....FFFFSSFFFFFFFFSSFFFFFSSSFFFFSSSFFFFSFFFFFFSSFFFFFFSS..",
    ];

    /// <summary>Whether <paramref name="c"/> is a Braille Patterns character U+2800-U+28FF.</summary>
    public static bool IsBraille(char c) => c >= BrailleRangeStart && c <= BrailleRangeEnd;

    /// <summary>Whether <paramref name="c"/> may appear in <see cref="Rows"/>: Braille or space.</summary>
    public static bool IsWordmarkChar(char c) => c == ' ' || IsBraille(c);

    /// <summary>
    /// Whether the wordmark (rather than the plain title) should be drawn for this
    /// terminal state. ASCII mode and non-UTF-8 never show Braille; below 62x16
    /// (or below 62x30 while the menu is open) the plain title is shown.
    /// Colour depth does NOT gate the wordmark itself: at 16 colours or no colour
    /// the depth cells are dropped but the letters still draw.
    /// </summary>
    public static bool ShouldShowWordmark(int width, int height, bool utf8, GlyphMode glyphs, bool menuOpen)
    {
        if (!utf8 || glyphs == GlyphMode.Ascii)
        {
            return false;
        }

        if (width < MinWidth)
        {
            return false;
        }

        return height >= (menuOpen ? MinHeightWithMenu : MinHeight);
    }

    /// <summary>
    /// The same decision from detected capabilities.
    /// </summary>
    public static bool ShouldShowWordmark(TerminalCapabilities capabilities, GlyphMode glyphs, bool menuOpen) =>
        ShouldShowWordmark(capabilities.Width, capabilities.Height, capabilities.Utf8, glyphs, menuOpen);

    /// <summary>
    /// Draws the wordmark (or the plain-title fallback) with its top-left at
    /// (<paramref name="x"/>, <paramref name="y"/>). Returns true when the wordmark
    /// was drawn and false when the plain title was drawn instead.
    /// Never calls <see cref="Glyphs.Resolve"/>: Braille has no entry there by design.
    /// </summary>
    public static bool Draw(
        CellBuffer buffer,
        int x,
        int y,
        int terminalWidth,
        int terminalHeight,
        bool utf8,
        GlyphMode glyphs,
        ColorDepth depth,
        bool menuOpen)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!ShouldShowWordmark(terminalWidth, terminalHeight, utf8, glyphs, menuOpen))
        {
            DrawPlainTitleCentred(buffer, y, terminalWidth, depth);
            return false;
        }

        DrawRows(buffer, x, y, depth);
        return true;
    }

    /// <summary>
    /// Draws the 8x58 rows at (<paramref name="x"/>, <paramref name="y"/>), honouring
    /// the depth-drop rule: at 16 colours or no colour only foreground cells draw.
    /// Callers must have checked <see cref="ShouldShowWordmark"/> first when they need
    /// the fallback; this method draws rows unconditionally.
    /// </summary>
    public static void DrawRows(CellBuffer buffer, int x, int y, ColorDepth depth)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var showDepth = depth is ColorDepth.TrueColor or ColorDepth.Ansi256;
        var foregroundCell = new Cell(' ', Foreground);
        var depthCell = new Cell(' ', Depth);

        for (var row = 0; row < Height; row++)
        {
            var glyphs = Rows[row];
            var mask = Mask[row];
            for (var column = 0; column < Width; column++)
            {
                var kind = mask[column];
                if (kind == '.')
                {
                    continue;
                }

                if (kind == 'S' && !showDepth)
                {
                    continue;
                }

                var cell = (kind == 'F' ? foregroundCell : depthCell) with { Glyph = glyphs[column] };
                var bx = x + column;
                var by = y + row;
                if (bx >= 0 && bx < buffer.Width && by >= 0 && by < buffer.Height)
                {
                    buffer[bx, by] = cell;
                }
            }
        }
    }

    /// <summary>
    /// Draws the plain <c>lattice</c> title in Bold, centred on row <paramref name="y"/>.
    /// Used as the fallback wherever the wordmark cannot draw, and kept visible elsewhere
    /// on the console so a terminal with no Braille font never shows a blank area alone.
    /// </summary>
    public static void DrawPlainTitleCentred(CellBuffer buffer, int y, int terminalWidth, ColorDepth depth)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var start = Math.Max(0, (terminalWidth - PlainTitle.Length) / 2);
        DrawPlainTitleAt(buffer, start, y, depth);
    }

    /// <summary>Draws the plain title at an explicit origin.</summary>
    public static void DrawPlainTitleAt(CellBuffer buffer, int x, int y, ColorDepth depth)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (y < 0 || y >= buffer.Height)
        {
            return;
        }

        var style = depth == ColorDepth.None
            ? new Cell(' ', null, null, CellAttributes.Bold)
            : new Cell(' ', Foreground, null, CellAttributes.Bold);

        for (var i = 0; i < PlainTitle.Length; i++)
        {
            var bx = x + i;
            if (bx < 0 || bx >= buffer.Width)
            {
                continue;
            }

            buffer[bx, y] = style with { Glyph = PlainTitle[i] };
        }
    }
}
