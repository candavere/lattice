using System.Text;

namespace Lattice.Tui;

/// <summary>
/// Turns a colour and a <see cref="ColorDepth"/> into the SGR fragment that
/// selects it, and folds foreground, background and attributes into one
/// minimal sequence. Pure: no terminal is touched, and the same inputs always
/// produce the same bytes.
/// </summary>
public static class ColorMapper
{
    /// <summary>The SGR that clears every attribute back to terminal defaults.</summary>
    public const string Reset = "\u001b[0m";

    /// <summary>
    /// The SGR that sets foreground <paramref name="color"/>, or an empty
    /// string when <paramref name="depth"/> is <see cref="ColorDepth.None"/>.
    /// </summary>
    public static string Foreground(Rgb color, ColorDepth depth) => Select(color, depth, foreground: true);

    /// <summary>
    /// The SGR that sets background <paramref name="color"/>, or an empty
    /// string when <paramref name="depth"/> is <see cref="ColorDepth.None"/>.
    /// </summary>
    public static string Background(Rgb color, ColorDepth depth) => Select(color, depth, foreground: false);

    /// <summary>
    /// One complete SGR sequence for a style: reset, then attributes, then
    /// foreground, then background. A null colour means "leave this channel at
    /// the terminal default" and contributes nothing. When nothing at all needs
    /// changing the result is <see cref="Reset"/>, which is what guarantees a
    /// cell painted over a coloured run returns to the default colours.
    ///
    /// At <see cref="ColorDepth.None"/> the result is empty rather than a bare
    /// reset: no SGR has been emitted on that output stream, so there is no
    /// state to clear and a reset would be pure bytes on every run boundary.
    /// </summary>
    public static string Style(Rgb? foreground, Rgb? background, CellAttributes attributes, ColorDepth depth)
    {
        if (depth == ColorDepth.None)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("\u001b[0");

        if ((attributes & CellAttributes.Bold) != 0)
        {
            builder.Append(";1");
        }

        if ((attributes & CellAttributes.Dim) != 0)
        {
            builder.Append(";2");
        }

        if ((attributes & CellAttributes.Italic) != 0)
        {
            builder.Append(";3");
        }

        if ((attributes & CellAttributes.Underline) != 0)
        {
            builder.Append(";4");
        }

        if ((attributes & CellAttributes.Reverse) != 0)
        {
            builder.Append(";7");
        }

        if (foreground is not null)
        {
            Append(builder, foreground.Value, depth, foreground: true);
        }

        if (background is not null)
        {
            Append(builder, background.Value, depth, foreground: false);
        }

        builder.Append('m');
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, Rgb color, ColorDepth depth, bool foreground)
    {
        switch (depth)
        {
            case ColorDepth.TrueColor:
                builder.Append(foreground ? ";38;2;" : ";48;2;");
                builder.Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B);
                break;
            case ColorDepth.Ansi256:
                builder.Append(foreground ? ";38;5;" : ";48;5;")
                    .Append(AnsiTables.NearestIndex256(color));
                break;
            case ColorDepth.Ansi16:
                // The base codes are the one place the channel is positional
                // rather than prefixed: 30-37 / 90-97 are foregrounds and
                // 40-47 / 100-107 are backgrounds. At TrueColor and Ansi256 the
                // channel rides in the 38 / 48 prefix, so it cannot be dropped
                // there; here it has to be added explicitly.
                var index = AnsiTables.NearestIndex16(color);
                var baseCode = foreground
                    ? (index < 8 ? 30 + index : 90 + (index - 8))
                    : (index < 8 ? 40 + index : 100 + (index - 8));
                builder.Append(';').Append(baseCode);
                break;
            case ColorDepth.None:
                break;
        }
    }

    private static string Select(Rgb color, ColorDepth depth, bool foreground)
    {
        if (depth == ColorDepth.None)
        {
            return string.Empty;
        }

        return Style(foreground ? color : null, foreground ? null : color, CellAttributes.None, depth);
    }
}