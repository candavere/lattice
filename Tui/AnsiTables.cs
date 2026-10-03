using System.Collections.Immutable;

namespace Lattice.Tui;

/// <summary>
/// The fixed colour tables the mapper degrades onto: the 16 base ANSI colours
/// and the xterm 256-colour palette.
/// </summary>
public static class AnsiTables
{
    /// <summary>
    /// The 16 base ANSI colours, indexed by SGR colour number 0-15. Index 0-7
    /// are the normal-intensity set (SGR 30-37 / 40-47), 8-15 the bright set
    /// (90-97 / 100-107).
    /// </summary>
    /// <remarks>
    /// The values are the VGA/"standard" set rather than xterm's own defaults:
    /// the normal colours sit at <c>0x80</c> on each lit channel and index 7 is
    /// <c>0xC0C0C0</c> grey, where xterm uses darker, more saturated darks such
    /// as <c>#cd0000</c> and a <c>#e5e5e5</c> white. The values are kept exactly
    /// as they are because they are what the nearest-colour mapping has always
    /// been calibrated against, and changing them would change rendered output;
    /// only this description was wrong.
    ///
    /// The table is an <see cref="ImmutableArray{T}"/> rather than a bare array
    /// because it is process-wide state read on every styled cell: an array
    /// would let any caller rewrite an entry and silently change every later
    /// colour decision in the process.
    /// </remarks>
    public static ImmutableArray<Rgb> Ansi16 { get; } = ImmutableArray.Create<Rgb>(
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
        new(0xFF, 0xFF, 0xFF)); // 15 bright white

    /// <summary>
    /// The six channel levels of the xterm 6x6x6 colour cube, indices 16-231.
    /// </summary>
    private static readonly int[] CubeLevels = { 0, 95, 135, 175, 215, 255 };

    /// <summary>
    /// The 24 steps of the xterm greyscale ramp, indices 232-255, each level
    /// being <c>8 + 10n</c> for n in 0..23.
    /// </summary>
    private static readonly Rgb[] GreyscaleRamp = BuildGreyscaleRamp();

    /// <summary>
    /// The whole xterm 256-colour palette, indices 0-255: the base 16, the
    /// 6x6x6 cube at 16-231 and the greyscale ramp at 232-255. Immutable for
    /// the same reason as <see cref="Ansi16"/>.
    /// </summary>
    public static ImmutableArray<Rgb> Palette256 { get; } = BuildPalette256();

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

    private static ImmutableArray<Rgb> BuildPalette256()
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

        return ImmutableArray.Create(palette);
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