using System.Text;

namespace Lattice.Tui;

/// <summary>
/// Makes a string safe to draw in a one-character-per-cell grid, and cuts it to a
/// pane's width in whole columns with the cut marked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract is: every drawn cell is one column; anything else is
/// replaced.</b> <see cref="CellBuffer"/> holds one <see cref="Cell"/> per column
/// and <see cref="Cell.Glyph"/> is a single <see cref="char"/>, so the grid has
/// no room for a character a terminal draws wider than one column and no way to
/// say "this cell is two columns wide". Rather than add a wide-cell model to a
/// zero-dependency library, text that comes from a recording — role names, action
/// text, labels — is made column-safe on the way in: every rune is treated as
/// exactly one column, and every rune that is not one column is replaced, with
/// <c>?</c> in ASCII mode and U+FFFD REPLACEMENT CHARACTER in Unicode mode.
/// </para>
/// <para>
/// <b>This is not full Unicode width support, and does not claim to be.</b> The
/// rule is one column per rune with a named table of exceptions, not a port of
/// <c>wcwidth</c>. Two limits are stated rather than hidden: a character outside
/// the table is counted as one column even where a particular terminal might draw
/// it wider — the East Asian Ambiguous class is the case that matters, and it is
/// counted as one — and the combining-mark ranges cover the blocks a recorded
/// label realistically carries, so a mark outside them is counted as one column
/// too. Both limits are cosmetic: one mis-shaped cell, never a wrong number.
/// </para>
/// <para>
/// <b>The order is fixed: sanitise, then measure, then clip.</b> Measuring first
/// would measure UTF-16 units rather than the columns the grid holds; a surrogate
/// pair is two units, would be counted as two columns, and would then be split
/// across two cells. The contract is stated on the <em>sanitised</em> string: a
/// string comes back unchanged only when it needed no sanitising and already
/// fitted.
/// </para>
/// <para>
/// A clipped string ends in a marker, because a clipped row that looks like a
/// short one is a row the reader cannot check. The marker is three characters in
/// ASCII mode — one column each, and the only characters a terminal that cannot
/// encode UTF-8 can be relied on to have — and the one-column U+2026 HORIZONTAL
/// ELLIPSIS in Unicode mode, chosen only after <see cref="IsSingleColumn"/> has
/// said so. A pane too narrow to hold the whole marker gets the marker's own
/// leading columns rather than no marker at all.
/// </para>
/// </remarks>
public static class CellText
{
    /// <summary>
    /// The three-character marker used in ASCII mode: three columns, and the only
    /// form of "this was cut" a terminal that cannot encode UTF-8 can be relied on
    /// to carry.
    /// </summary>
    public const string AsciiEllipsis = "...";

    /// <summary>
    /// The one-column marker used in Unicode mode. Chosen only because
    /// <see cref="IsSingleColumn"/> classifies it as one column.
    /// </summary>
    public const char UnicodeEllipsis = '\u2026';

    /// <summary>
    /// The marker's replacement character in Unicode mode. U+FFFD is itself one
    /// column under this contract, so a replacement never widens what is drawn.
    /// </summary>
    private const char UnicodeReplacement = '\uFFFD';

    /// <summary>The replacement character in ASCII mode.</summary>
    private const char AsciiReplacement = '?';

    /// <summary>
    /// The code points that are not one column, as inclusive ranges, sorted. One
    /// table rather than four so the rule can be read end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>East Asian Wide (W) and Fullwidth (F)</b>, from Unicode Standard Annex
    /// #11, <i>East Asian Width</i> (eastasianwidth.txt). These are the code points
    /// a terminal gives two columns to.
    /// </para>
    /// <para>
    /// <b>Emoji</b>: the pictographic blocks that are also East Asian Wide, from
    /// the Unicode emoji data file (emoji-data.txt) — the same source that carries
    /// the presentation sequences this contract rejects.
    /// </para>
    /// <para>
    /// <b>Zero width</b>: zero-width space, the joiners, the bidirectional
    /// formatting controls, and the variation selectors — which select a
    /// presentation rather than marking anything themselves.
    /// </para>
    /// <para>
    /// <b>Combining marks</b>: general categories Mn (nonspacing mark) and Me
    /// (enclosing mark) from the Unicode Character Database. This part of the table
    /// is deliberately partial, as the remarks say: a mark outside it is counted as
    /// one column.
    /// </para>
    /// <list type="bullet">
    /// <item>0x0300-0x036F, 0x0483-0x0489, 0x0591-0x05C7, 0x0610-0x061A, 0x064B-0x0670, 0x06D6-0x06DC, 0x0900-0x0957, 0x0E31-0x0E3A — combining marks: diacritics, Cyrillic, Hebrew, Arabic, Devanagari, Thai</item>
    /// <item>0x1AB0-0x1AFF, 0x1DC0-0x1DFF, 0x20D0-0x20F0 — combining diacritical marks extended, supplement, and marks for symbols</item>
    /// <item>0x1100-0x115F — Hangul Jamo initial consonants</item>
    /// <item>0x200B-0x200F — zero-width space, non-joiner, joiner, LRM, RLM</item>
    /// <item>0x2E80-0x303E, 0x3041-0x33FF, 0x3400-0x4DBF, 0x4E00-0x9FFF, 0xA000-0xA4CF, 0xAC00-0xD7A3, 0xF900-0xFAFF, 0x20000-0x3FFFD — CJK radicals, kana, CJK unified ideographs and their extensions, Yi, Hangul syllables, CJK compatibility ideographs</item>
    /// <item>0xFE00-0xFE0F — variation selectors 1-16</item>
    /// <item>0xFE10-0xFE6F — vertical forms, combining half marks, CJK compatibility forms</item>
    /// <item>0xFF00-0xFF60 — fullwidth forms</item>
/// <item>0xFFE0-0xFFE6 — fullwidth signs</item>
    /// <item>0x1B000-0x1B16F — kana supplement and kana extended</item>
    /// <item>0x1F300-0x1F64F, 0x1F680-0x1F6FF, 0x1F900-0x1F9FF, 0x1FA70-0x1FAFF — emoji: pictographs, emoticons, transport and map, supplemental pictographs</item>
    /// </list>
    /// </remarks>
    private static readonly (int First, int Last)[] NotOneColumn =
    {
        (0x0300, 0x036F),
        (0x0483, 0x0489),
        (0x0591, 0x05C7),
        (0x0610, 0x061A),
        (0x064B, 0x0670),
        (0x06D6, 0x06DC),
        (0x0900, 0x0957),
        (0x0E31, 0x0E3A),
        (0x1100, 0x115F),
        (0x200B, 0x200F),
        (0x20D0, 0x20F0),
        (0x2E80, 0x303E),
        (0x3041, 0x33FF),
        (0x3400, 0x4DBF),
        (0x4E00, 0x9FFF),
        (0xA000, 0xA4CF),
        (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF),
        (0xFE00, 0xFE0F),
        (0xFE10, 0xFE6F),
        (0xFF00, 0xFF60),
        (0xFFE0, 0xFFE6),
        (0x1AB0, 0x1AFF),
        (0x1B000, 0x1B16F),
        (0x1DC0, 0x1DFF),
        (0x1F300, 0x1F64F),
        (0x1F680, 0x1F6FF),
        (0x1F900, 0x1F9FF),
        (0x1FA70, 0x1FAFF),
        (0x20000, 0x3FFFD),
    };

    /// <summary>
    /// The marker this glyph vocabulary uses, as a string. One column per
    /// character in both forms.
    /// </summary>
    public static string Ellipsis(GlyphMode glyphs) =>
        glyphs == GlyphMode.Unicode ? UnicodeEllipsis.ToString() : AsciiEllipsis;

    /// <summary>
    /// The character a rune that is not one column is replaced with in this
    /// vocabulary. Exposed so a caller asserting that a replacement happened reads
    /// the same one this type uses rather than writing the code point out again;
    /// the assertion is what keeps the two vocabularies from drifting apart.
    /// </summary>
    public static char ReplacementFor(GlyphMode glyphs) => Replacement(glyphs);

    /// <summary>
    /// Whether <paramref name="value"/> is one column under this contract. The
    /// named rule, exposed so the choice of marker is a measurement rather than an
    /// assumption, and so a caller can ask the clipper's question directly.
    /// </summary>
    public static bool IsSingleColumn(char value)
    {
        // C0 and C1 control characters: a recorded label carrying one is either
        // corrupt or is an escape sequence, and neither belongs in a cell.
        if (value < ' ' || (value >= '\u007F' && value <= '\u009F'))
        {
            return false;
        }

        for (var i = 0; i < NotOneColumn.Length; i++)
        {
            var (first, last) = NotOneColumn[i];
            if (value >= first && value <= last)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The string with every rune that is not one column replaced. The input
    /// instance itself when there was nothing to replace, so a draw that needs no
    /// sanitising allocates nothing.
    /// </summary>
    public static string Sanitize(string text, GlyphMode glyphs)
    {
        ArgumentNullException.ThrowIfNull(text);

        var replacement = Replacement(glyphs);
        StringBuilder? builder = null;

        for (var i = 0; i < text.Length; i++)
        {
            var value = text[i];

            if (char.IsHighSurrogate(value))
            {
                // A surrogate pair is one code point above the Basic Multilingual
                // Plane: two UTF-16 units, and in a terminal that has it two
                // columns. The grid holds one char per cell, so the pair cannot be
                // one cell, and it is never left as two halves: both units are
                // consumed here and one replacement column is emitted. A lone
                // surrogate — one unit of a pair that is not there — is replaced for
                // the same reason: it is not a character at all.
                var units = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                builder = Replace(builder, text, i, replacement);
                i += units - 1;
                continue;
            }

            // A low surrogate that is not the second half of a pair is the same
            // orphan, arriving from the other end.
            if (char.IsLowSurrogate(value) || !IsSingleColumn(value))
            {
                builder = Replace(builder, text, i, replacement);
                continue;
            }

            builder?.Append(value);
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Appends one replacement column for the source at <paramref name="at"/>,
    /// starting the builder on first use and copying in the prefix it has not yet
    /// seen. The prefix is copied rather than re-appended character by character
    /// because it was skipped: nothing was appended while the builder was null.
    /// </summary>
    private static StringBuilder Replace(
        StringBuilder? builder,
        string text,
        int at,
        char replacement) =>
        (builder ??= new StringBuilder(text.Length).Append(text, 0, at)).Append(replacement);

    /// <summary>
    /// The string sanitised and cut to <paramref name="width"/> columns, marked as
    /// cut. Never longer than <paramref name="width"/> in columns.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> is negative: a caller's arithmetic error, not a
    /// pane too small to draw.
    /// </exception>
    public static string Clip(string text, int width, GlyphMode glyphs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);

        // Measured, not counted in UTF-16 units: sanitisation has already turned
        // every surviving rune into one column, so the sanitised string's length in
        // chars is its width in columns.
        var sanitised = Sanitize(text, glyphs);
        if (sanitised.Length <= width)
        {
            return sanitised;
        }

        if (width == 0)
        {
            return string.Empty;
        }

        var marker = Ellipsis(glyphs);

        // A pane narrower than the marker gets the marker's own leading columns.
        // Returning the text unmarked instead would put the original defect back.
        return width < marker.Length
            ? marker[..width]
            : string.Concat(sanitised.AsSpan(0, width - marker.Length), marker);
    }

    private static char Replacement(GlyphMode glyphs) =>
        glyphs == GlyphMode.Unicode ? UnicodeReplacement : AsciiReplacement;
}