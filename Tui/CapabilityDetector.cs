namespace Lattice.Tui;

/// <summary>
/// What the attached terminal can actually do, resolved once at start-up so no
/// screen has to consult the environment while it is drawing.
/// </summary>
/// <param name="Depth">How much colour can be emitted.</param>
/// <param name="Utf8">Whether Unicode glyphs can be written directly.</param>
/// <param name="InputRedirected">Standard input is not the terminal.</param>
/// <param name="OutputRedirected">Standard output is not the terminal.</param>
/// <param name="Width">Terminal columns.</param>
/// <param name="Height">Terminal rows.</param>
public readonly record struct TerminalCapabilities(
    ColorDepth Depth,
    bool Utf8,
    bool InputRedirected,
    bool OutputRedirected,
    int Width,
    int Height)
{
    /// <summary>The narrowest layout the screens are designed against.</summary>
    public const int MinimumWidth = 100;

    /// <summary>The shortest layout the screens are designed against.</summary>
    public const int MinimumHeight = 30;

    /// <summary>
    /// Whether the terminal is at least <see cref="MinimumWidth"/> by
    /// <see cref="MinimumHeight"/>. Later stages read this to decide between
    /// the full screen and the reduced one rather than each measuring again.
    /// </summary>
    public bool MeetsMinimumSize => Width >= MinimumWidth && Height >= MinimumHeight;

    /// <summary>
    /// Whether the panel background may be painted. Painted only under
    /// truecolor, and never when output is redirected.
    /// </summary>
    public Rgb? PanelFill => OutputRedirected ? null : Theme.PanelFill(Depth);
}

/// <summary>
/// The inputs capability detection reads, as a value so detection can be tested
/// without a terminal. <see cref="Detect"/> fills this from the environment and
/// the console; tests fill it with literals.
/// </summary>
public readonly record struct TerminalEnvironment(
    string? ColorTerm,
    string? Term,
    string? NoColor,
    string? Lang,
    bool InputRedirected,
    bool OutputRedirected,
    int Width,
    int Height)
{
    /// <summary>The width a screen falls back to when the console reports none.</summary>
    private const int ConventionalWidth = 80;

    /// <summary>The height a screen falls back to when the console reports none.</summary>
    private const int ConventionalHeight = 25;

    /// <summary>
    /// Reads the real environment. Width and height come from the console and
    /// fall back to the conventional 80x25 when the console will not report
    /// them, which is what a redirected stream does on every platform.
    /// </summary>
    public static TerminalEnvironment Current() => new(
        Environment.GetEnvironmentVariable("COLORTERM"),
        Environment.GetEnvironmentVariable("TERM"),
        Environment.GetEnvironmentVariable("NO_COLOR"),
        Environment.GetEnvironmentVariable("LANG")
            ?? Environment.GetEnvironmentVariable("LC_ALL")
            ?? Environment.GetEnvironmentVariable("LC_CTYPE"),
        Console.IsInputRedirected,
        Console.IsOutputRedirected,
        UsableWidth(SafeWidth()),
        UsableHeight(SafeHeight()));

    /// <summary>
    /// The width a screen may lay out against: what the console reported when
    /// that is a usable number, otherwise the conventional 80. A console asked
    /// for a size it does not have answers with zero or a negative number
    /// rather than failing, so the value has to be checked, not only caught.
    /// </summary>
    public static int UsableWidth(int reported) => reported > 0 ? reported : ConventionalWidth;

    /// <summary>
    /// The height a screen may lay out against, on the same terms as
    /// <see cref="UsableWidth"/>.
    /// </summary>
    public static int UsableHeight(int reported) => reported > 0 ? reported : ConventionalHeight;

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return ConventionalWidth;
        }
    }

    private static int SafeHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            return ConventionalHeight;
        }
    }
}

/// <summary>
/// Decides colour depth, encoding and size from the environment. The rules are
/// fixed and stated here rather than scattered, because getting them wrong is
/// how a TUI ends up emitting escape sequences a terminal prints literally.
/// </summary>
public static class CapabilityDetector
{
    /// <summary>Detects against the real environment.</summary>
    public static TerminalCapabilities Detect() => Detect(TerminalEnvironment.Current());

    /// <summary>
    /// Detects against injected values. Redirection is decided by the caller,
    /// not probed here, so a redirected run always reports
    /// <see cref="ColorDepth.None"/> regardless of what TERM claims.
    /// </summary>
    public static TerminalCapabilities Detect(TerminalEnvironment environment) => new(
        ColorDepthOf(environment),
        IsUtf8(environment),
        environment.InputRedirected,
        environment.OutputRedirected,
        environment.Width,
        environment.Height);

    /// <summary>
    /// The colour depth implied by the environment. NO_COLOR wins outright when
    /// set to anything other than an empty string, following the convention at
    /// no-color.org; otherwise a redirected output means no colour at all; then
    /// COLORTERM's truecolor claim, then TERM's advertised palette size.
    /// </summary>
    public static ColorDepth ColorDepthOf(TerminalEnvironment environment)
    {
        if (!string.IsNullOrEmpty(environment.NoColor))
        {
            return ColorDepth.None;
        }

        if (environment.OutputRedirected)
        {
            return ColorDepth.None;
        }

        var colorTerm = environment.ColorTerm ?? string.Empty;
        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.TrueColor;
        }

        var term = environment.Term ?? string.Empty;
        if (term.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || term.Contains("direct", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.TrueColor;
        }

        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.Ansi256;
        }

        if (string.IsNullOrEmpty(term) || term.Contains("dumb", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.None;
        }

        if (term.Contains("color", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.Ansi16;
        }

        // screen, vt100, vt220, xterm and friends all speak the base set.
        return ColorDepth.Ansi16;
    }

    /// <summary>
    /// Whether UTF-8 can be written directly, decided from the locale rather
    /// than the terminal: on every platform the locale is what determines the
    /// console encoding. An unset locale is treated as UTF-8, matching the
    /// current default in .NET.
    /// </summary>
    public static bool IsUtf8(TerminalEnvironment environment)
    {
        var lang = environment.Lang;
        if (string.IsNullOrEmpty(lang))
        {
            return true;
        }

        return lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            || lang.Contains("UTF8", StringComparison.OrdinalIgnoreCase);
    }
}