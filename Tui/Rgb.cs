namespace Lattice.Tui;

/// <summary>
/// An 8-bit-per-channel RGB colour. Pure value type: no parsing of the ambient
/// environment, no terminal state, no allocation. Hex parsing lives on the type
/// as a static factory because theme colours are authored as literals in source.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses a six-digit <c>#rrggbb</c> string.</summary>
    /// <exception cref="FormatException">The text is not six hex digits.</exception>
    /// <remarks>
    /// Each pair is read with <c>AllowHexSpecifier</c> rather than
    /// <c>HexNumber</c>: the latter also permits leading and trailing white
    /// space, so <c>"# 00000"</c> came back as <c>#000000</c> instead of being
    /// the malformed string it is.
    /// </remarks>
    public static Rgb FromHex(string hex)
    {
        if (hex is null)
        {
            throw new ArgumentNullException(nameof(hex));
        }

        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new FormatException($"Expected '#rrggbb', got '{hex}'.");
        }

        return new Rgb(
            byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Renders the colour as lower-case <c>#rrggbb</c>.</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>
    /// Squared Euclidean distance in channel space. Squared is deliberate: it
    /// orders candidates identically to the true distance while avoiding a
    /// square root per comparison on the nearest-colour path.
    /// </summary>
    public int DistanceSquaredTo(Rgb other)
    {
        var dr = R - other.R;
        var dg = G - other.G;
        var db = B - other.B;
        return (dr * dr) + (dg * dg) + (db * db);
    }
}