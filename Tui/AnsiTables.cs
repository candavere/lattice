namespace Lattice.Tui;

/// <summary>
/// The fixed colour tables the mapper degrades onto: the 16 base ANSI colours
/// and the xterm 256-colour palette. The 16 ANSI entries use the standard
/// xterm base values (<c>#000000</c> black through <c>#ffffff</c> white), which
/// is what the SGR 30-37 / 90-97 codes denote on every mainstream terminal.
/// </summary>
public static class AnsiTables
{
    /// <summary>
    /// The 16 base ANSI colours, indexed by SGR colour number 0-15. Index 0-7
    /// are the normal-intensity set (SGR 30-37), 8-15 the bright set (90-97).
    /// </summary>
    public static readonly Rgb[] Ansi16 =
    {
        new(0x00, 0x00, 0x00), // 0  black
        new(0x80, 0x00, 0x00), // 1  red
        new(0x00, 0x80, 0x00), // 2  green
        new(0x80, 0x80, 0x00), // 3  yellow
        new(0x00, 0x00, 0x80), // 4  blue
        new(0x80, 0x00, 0x80), // 5  magenta
        new(0x00, 0x80, 0x80), // 6  cyan
        new(0xC0, 0xC0, 0xC0), // 7  white
        new(0x80, 0x80, 0x80), // 8  bright black
        new(0xFF, 0x00, 0x00), // 9  bright red
        new(0x00, 0xFF, 0x00), // 10 bright green
        new(0xFF, 0xFF, 0x00), // 11 bright yellow
        new(0x00, 0x00, 0xFF), // 12 bright blue
        new(0xFF, 0x00, 0xFF), // 13 bright magenta
        new(0x00, 0xFF, 0xFF), // 14 bright cyan
        new(0xFF, 0xFF, 0xFF), // 15 bright white
    };

    /// <summary>
    /// The six channel levels of the xterm 6x6x6 colour cube, indices 16-231.
    /// </summary>
    private static readonly int[] CubeLevels = { 0, 95, 135, 175, 215, 255 };

    /// <summary>
    /// The 24 steps of the xterm greyscale ramp, indices 232-255, each level
    /// being <c>8 + 10n</c> for n in 0..23.
    /// </summary>
    private static readonly Rgb[] GreyscaleRamp = BuildGreyscaleRamp();

    /// <summary>The whole xterm 256-colour palette, indices 0-255.</summary>
    public static readonly Rgb[] Palette256 = BuildPalette256();

    private static Rgb[] BuildGreyscaleRamp()
    {
        var ramp = new Rgb[24];
        for (var i = 0; i < ramp.Length; i++)
        {
            var level = (byte)(8 + (10 * i));
            ramp[i] = new Rgb(level, level, level);
        }

        return ramp;
    }

    private static Rgb[] BuildPalette256()
    {
        var palette = new Rgb[256];

        for (var i = 0; i < Ansi16.Length; i++)
        {
            palette[i] = Ansi16[i];
        }

        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                {
                    var index = 16 + (r * 36) + (g * 6) + b;
                    palette[index] = new Rgb(
                        (byte)CubeLevels[r],
                        (byte)CubeLevels[g],
                        (byte)CubeLevels[b]);
                }
            }
        }

        for (var i = 0; i < GreyscaleRamp.Length; i++)
        {
            palette[232 + i] = GreyscaleRamp[i];
        }

        return palette;
    }

    /// <summary>
    /// Finds the index of the palette entry closest to <paramref name="color"/>.
    /// Ties resolve to the lowest index, which makes the mapping total and
    /// deterministic rather than dependent on enumeration order.
    /// </summary>
    public static int NearestIndex256(Rgb color)
    {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < Palette256.Length; i++)
        {
            var distance = color.DistanceSquaredTo(Palette256[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Finds the base ANSI colour number (0-15) closest to
    /// <paramref name="color"/>, with the same lowest-index tie-break.
    /// </summary>
    public static int NearestIndex16(Rgb color)
    {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < Ansi16.Length; i++)
        {
            var distance = color.DistanceSquaredTo(Ansi16[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }
}