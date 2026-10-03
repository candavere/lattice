namespace Lattice.Tui;

/// <summary>
/// How much colour a terminal can actually render. Ordered from least to most
/// capable; <see cref="ColorDepth.None"/> means "emit no colour at all".
/// </summary>
public enum ColorDepth
{
    /// <summary>No colour: the output carries glyphs and nothing else.</summary>
    None = 0,

    /// <summary>
    /// The 16 base ANSI colours: SGR 30-37 and 90-97 select a foreground, 40-47
    /// and 100-107 select a background.
    /// </summary>
    Ansi16 = 1,

    /// <summary>The xterm 256-colour palette (SGR 38;5;n).</summary>
    Ansi256 = 2,

    /// <summary>Direct 24-bit colour (SGR 38;2;r;g;b).</summary>
    TrueColor = 3,
}