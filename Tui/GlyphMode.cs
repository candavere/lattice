namespace Lattice.Tui;

/// <summary>
/// Which glyph vocabulary a frame is drawn from. Carried as a value rather than
/// decided inside the renderer so the choice is made once, at the edge, and
/// every pane draws from the same answer for the whole run.
/// </summary>
public enum GlyphMode
{
    /// <summary>The Unicode box-drawing and geometric glyphs.</summary>
    Unicode,

    /// <summary>The single-character ASCII stand-ins, for a terminal that cannot encode UTF-8.</summary>
    Ascii,
}

/// <summary>
/// Chooses the glyph vocabulary from what the terminal can do and what the
/// caller asked for.
/// </summary>
public static class GlyphModes
{
    /// <summary>
    /// ASCII is chosen when the caller forces it or when the terminal cannot
    /// encode UTF-8. There is no third answer: a box-drawing glyph written to a
    /// console that cannot encode it arrives as a replacement character, which
    /// is one column narrower than the grid assumes and shifts every column to
    /// its right for the rest of the frame.
    /// </summary>
    /// <param name="utf8">Whether the terminal can encode UTF-8.</param>
    /// <param name="forcedAscii">Whether the caller asked for ASCII regardless.</param>
    public static GlyphMode Resolve(bool utf8, bool forcedAscii) =>
        !utf8 || forcedAscii ? GlyphMode.Ascii : GlyphMode.Unicode;

    /// <summary>
    /// The glyph to draw for <paramref name="unicode"/> in this vocabulary.
    /// </summary>
    public static char Glyph(char unicode, GlyphMode mode) =>
        Glyphs.Resolve(unicode, mode == GlyphMode.Unicode);
}