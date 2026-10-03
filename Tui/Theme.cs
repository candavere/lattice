namespace Lattice.Tui;

/// <summary>
/// The raw palette. Provenance is recorded per colour because it is not the
/// same for every entry: the nine marked <b>sampled</b> values were read off
/// the reference image at
/// https://in.pinterest.com/pin/6614730699747535/ and are therefore
/// approximate; the four marked <b>derived</b> values were mixed for this
/// project and have no sampled source.
/// </summary>
public static class Palette
{
    // ---- Sampled (approximate, from https://in.pinterest.com/pin/6614730699747535/) ----

    /// <summary>Sampled: the deepest panel fill.</summary>
    public static readonly Rgb PanelBackground = Rgb.FromHex("#1f2420");

    /// <summary>Sampled: the second panel fill, one step up from the deepest.</summary>
    public static readonly Rgb PanelBackgroundAlt = Rgb.FromHex("#272b22");

    /// <summary>Sampled: the raised surface used for selection.</summary>
    public static readonly Rgb Raised = Rgb.FromHex("#2c3625");

    /// <summary>Sampled: the sage used for every border.</summary>
    public static readonly Rgb Border = Rgb.FromHex("#81897d");

    /// <summary>Sampled: body text.</summary>
    public static readonly Rgb TextPrimary = Rgb.FromHex("#dde1dc");

    /// <summary>Sampled: secondary and disabled text.</summary>
    public static readonly Rgb TextDim = Rgb.FromHex("#696f65");

    /// <summary>Sampled: the lime accent.</summary>
    public static readonly Rgb Accent = Rgb.FromHex("#adcb66");

    /// <summary>Sampled: the brighter lime for hover and emphasis.</summary>
    public static readonly Rgb AccentBright = Rgb.FromHex("#c8db73");

    /// <summary>Sampled: the pink secondary accent.</summary>
    public static readonly Rgb AccentSecondary = Rgb.FromHex("#e8a7bc");

    // ---- Derived (mixed for this project; not sampled) ----

    /// <summary>Derived: warning and contested choke.</summary>
    public static readonly Rgb Warning = Rgb.FromHex("#e6c84a");

    /// <summary>Derived: error.</summary>
    public static readonly Rgb Error = Rgb.FromHex("#d9645a");

    /// <summary>Derived: informational cyan.</summary>
    public static readonly Rgb Info = Rgb.FromHex("#6fd0c8");

    /// <summary>Derived: success.</summary>
    public static readonly Rgb Success = Rgb.FromHex("#a1c35e");
}

/// <summary>
/// The semantic colour roles the later screen stages bind to, and the two
/// policy decisions that keep output legible on degraded terminals: whether a
/// panel gets a painted background at all, and which colour the warning and
/// error roles carry.
/// </summary>
public static class Theme
{
    /// <summary>The border colour for every panel and box.</summary>
    public static Rgb Sage => Palette.Border;

    /// <summary>
    /// The accent assigned to an agent by its slot number. Slots beyond the
    /// defined ones cycle rather than run off the end of the palette.
    /// </summary>
    /// <param name="slot">Zero-based agent slot.</param>
    public static Rgb AgentSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        return slot % 2 == 0 ? Palette.Accent : Palette.AccentSecondary;
    }

    /// <summary>A choke under contention, which is a warning state.</summary>
    public static Rgb ContestedChoke => Palette.Warning;

    /// <summary>A closed edge reads as dim text.</summary>
    public static Rgb ClosedEdge => Palette.TextDim;

    /// <summary>The mark drawn on a closed edge, in the error colour.</summary>
    public static Rgb ClosedEdgeMark => Palette.Error;

    /// <summary>The foreground of a selected item.</summary>
    public static Rgb SelectionForeground => Palette.Accent;

    /// <summary>The background of a selected item.</summary>
    public static Rgb SelectionBackground => Palette.Raised;

    /// <summary>
    /// The background a panel should be filled with, or <c>null</c> to leave the
    /// terminal's own background alone. Panels are painted only when truecolor
    /// is available: on 256- and 16-colour terminals the palette has no entry
    /// close enough to the sampled panel fill to be worth replacing the user's
    /// background with.
    /// </summary>
    public static Rgb? PanelFill(ColorDepth depth) =>
        depth == ColorDepth.TrueColor ? Palette.PanelBackground : null;
}