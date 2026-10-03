namespace Lattice.Tui;

/// <summary>
/// One character cell: the glyph, its colours, and its attributes. A null
/// colour channel means "the terminal default", which is distinct from any
/// colour the terminal might happen to be showing.
/// </summary>
public readonly record struct Cell(
    char Glyph,
    Rgb? Foreground = null,
    Rgb? Background = null,
    CellAttributes Attributes = CellAttributes.None)
{
    /// <summary>A blank cell in the terminal's default colours.</summary>
    public static Cell Blank => new(' ');

    /// <summary>
    /// Whether this cell paints exactly what <paramref name="other"/> paints.
    /// Record equality already does this; the method exists so call sites read
    /// as intent rather than as an operator.
    /// </summary>
    public bool Matches(Cell other) => this == other;

    /// <summary>
    /// Whether the two cells would be painted by the same SGR sequence, which
    /// ignores the glyph. This is the comparison the renderer needs when
    /// deciding whether a style has to be re-emitted mid-run: two adjacent
    /// cells with different characters but one style need no sequence between
    /// them, and treating that as a change would write two escape sequences per
    /// character.
    /// </summary>
    public bool SameStyleAs(Cell other) =>
        Foreground == other.Foreground
        && Background == other.Background
        && Attributes == other.Attributes;
}