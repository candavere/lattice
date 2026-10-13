namespace Lattice.Tui;

/// <summary>
/// The eight glyphs a <see cref="CellBuffer.DrawBorder"/> needs, so a caller can
/// choose light, rounded or ASCII framing without the geometry code
/// knowing which family is in use.
/// </summary>
public readonly record struct BorderGlyphs(
    char TopLeft,
    char TopRight,
    char BottomLeft,
    char BottomRight,
    char Horizontal,
    char Vertical)
{
    /// <summary>A square-cornered light box.</summary>
    public static BorderGlyphs Light => new('┌', '┐', '└', '┘', '─', '│');

    /// <summary>A rounded box.</summary>
    public static BorderGlyphs Rounded => new('╭', '╮', '╰', '╯', '─', '│');

    /// <summary>The ASCII fallback framing used when the terminal is not UTF-8.</summary>
    public static BorderGlyphs Ascii => new('+', '+', '+', '+', '-', '|');
}