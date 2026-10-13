namespace Lattice.Tui;

/// <summary>
/// The raw palette: the OpenCode dark colour scheme.
/// </summary>
/// <remarks>
/// Source: the official dark theme in <c>packages/tui/src/theme/assets/opencode.json</c>
/// in sst/opencode at commit c1615b1db9c2e7a955f2be1024e84bb74c90cc6b (dark defs only).
/// Official entries carry the official hex; entries marked derived were mixed for this
/// project by the stated rule and are "derived, not from the official theme".
/// </remarks>
public static class Palette
{
    // ---- Official OpenCode dark theme ----

    /// <summary>Official background #0a0a0a.</summary>
    public static readonly Rgb Background = Rgb.FromHex("#0a0a0a");

    /// <summary>Official backgroundPanel #141414.</summary>
    public static readonly Rgb PanelBackground = Rgb.FromHex("#141414");

    /// <summary>Official backgroundElement #1e1e1e.</summary>
    public static readonly Rgb Raised = Rgb.FromHex("#1e1e1e");

    /// <summary>Official borderSubtle #3c3c3c (also the wordmark depth step).</summary>
    public static readonly Rgb DepthStep = Rgb.FromHex("#3c3c3c");

    /// <summary>Official border #484848 (decoration only, never the sole carrier of meaning).</summary>
    public static readonly Rgb Border = Rgb.FromHex("#484848");

    /// <summary>Official textMuted #808080.</summary>
    public static readonly Rgb TextDim = Rgb.FromHex("#808080");

    /// <summary>Official text #eeeeee.</summary>
    public static readonly Rgb TextPrimary = Rgb.FromHex("#eeeeee");

    /// <summary>Official primary #fab283.</summary>
    public static readonly Rgb Accent = Rgb.FromHex("#fab283");

    /// <summary>Official primary-bright #ffc09f.</summary>
    public static readonly Rgb AccentBright = Rgb.FromHex("#ffc09f");

    /// <summary>Official secondary #5c9cf5.</summary>
    public static readonly Rgb AccentSecondary = Rgb.FromHex("#5c9cf5");

    /// <summary>Official accent #9d7cd8 (violet).</summary>
    public static readonly Rgb Violet = Rgb.FromHex("#9d7cd8");

    /// <summary>Official warning #f5a742.</summary>
    public static readonly Rgb Warning = Rgb.FromHex("#f5a742");

    /// <summary>Official error #e06c75.</summary>
    public static readonly Rgb Error = Rgb.FromHex("#e06c75");

    /// <summary>Official success #7fd88f.</summary>
    public static readonly Rgb Success = Rgb.FromHex("#7fd88f");

    /// <summary>Official info #56b6c2.</summary>
    public static readonly Rgb Info = Rgb.FromHex("#56b6c2");
}

/// <summary>
/// The semantic colour roles the screens bind to, and the two policy decisions that
/// keep output legible on degraded terminals: whether a panel gets a painted background
/// at all, and which colour the warning and error roles carry.
/// </summary>
public static class Theme
{
    /// <summary>The border colour for every panel and box (decoration only).</summary>
    public static Rgb Sage => Palette.Border;

    /// <summary>
    /// The accent assigned to an agent by its slot number. Slots beyond the
    /// defined ones cycle rather than run off the end of the palette.
    /// Slot 0 is the official primary orange, slot 1 the official secondary blue.
    /// </summary>
    /// <param name="slot">Zero-based agent slot.</param>
    public static Rgb AgentSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        return slot % 2 == 0 ? Palette.Accent : Palette.AccentSecondary;
    }

    /// <summary>A choke under contention, which is a warning state.</summary>
    public static Rgb ContestedChoke => Palette.Warning;

    /// <summary>A closed edge reads as muted text.</summary>
    public static Rgb ClosedEdge => Palette.TextDim;

    /// <summary>The mark drawn on a closed edge, in the error colour.</summary>
    public static Rgb ClosedEdgeMark => Palette.Error;

    /// <summary>
    /// An open edge: text-grade primary text, visibly brighter than
    /// <see cref="ClosedEdge"/>. The world map's open edge carries meaning, so it
    /// is never the decoration-only border colour.
    /// </summary>
    public static Rgb OpenEdge => Palette.TextPrimary;

    /// <summary>The foreground of a selected item (official primary on the element band, Bold at call sites).</summary>
    public static Rgb SelectionForeground => Palette.Accent;

    /// <summary>The background of a selected item (the element band).</summary>
    public static Rgb SelectionBackground => Palette.Raised;

    /// <summary>
    /// Derived, not from the official theme: the smallest grey lightening of the official
    /// muted #808080 that meets 4.5:1 on both the panel and the element backgrounds.
    /// Use for instructions, column headers, required metadata and values the user must read.
    /// <see cref="Palette.TextDim"/> stays reserved for decoration and genuinely disabled content.
    /// </summary>
    public static Rgb SecondaryText => Rgb.FromHex("#858585");

    /// <summary>Keyboard hints: must be read, so the readable secondary, not dim.</summary>
    public static Rgb KeyHint => SecondaryText;

    /// <summary>Table column headers: the readable secondary, drawn Bold at call sites.</summary>
    public static Rgb TableHeader => SecondaryText;

    /// <summary>
    /// Derived, not from the official theme: the smallest lightening of the official error
    /// colour that meets 4.5:1 on the panel and element backgrounds. The official error
    /// already meets it, so the rule yields the official error value itself.
    /// <see cref="Palette.Error"/> remains the mark colour for small glyphs where 3:1
    /// non-text contrast applies.
    /// </summary>
    public static Rgb ErrorText => Rgb.FromHex("#e06c75");

    /// <summary>
    /// The console screen background (official background). The console reuses this file.
    /// </summary>
    public static Rgb ScreenBackground => Palette.Background;

    /// <summary>
    /// Muted console text (official muted). For non-essential text on the screen background,
    /// where it meets 4.5:1; essential text uses <see cref="SecondaryText"/>.
    /// </summary>
    public static Rgb Muted => Palette.TextDim;

    /// <summary>The console info role (official info).</summary>
    public static Rgb Info => Palette.Info;

    /// <summary>The console accent role (official violet accent).</summary>
    public static Rgb Violet => Palette.Violet;

    /// <summary>
    /// The background a panel should be filled with, or <c>null</c> to leave the
    /// terminal's own background alone. Panels are painted only when truecolor
    /// is available: on 256- and 16-colour terminals the palette has no entry
    /// close enough to the official panel fill to be worth replacing the user's
    /// background with. "16 colours" means the ANSI base set
    /// <see cref="AnsiTables.Ansi16"/> degrades onto, whatever a given terminal
    /// renders those indices as.
    /// </summary>
    public static Rgb? PanelFill(ColorDepth depth) =>
        depth == ColorDepth.TrueColor ? Palette.PanelBackground : null;
}
