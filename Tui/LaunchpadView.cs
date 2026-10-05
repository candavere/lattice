namespace Lattice.Tui;

/// <summary>What a form field holds, and therefore how it is read and drawn.</summary>
public enum LaunchpadFieldKind
{
    /// <summary>A <c>--name value</c> flag.</summary>
    Flag,

    /// <summary>A bare path token, for the commands that take one.</summary>
    PositionalPath,

    /// <summary>
    /// The raw tail: everything after the fields, passed through unexamined, so a
    /// flag nobody modelled is still reachable.
    /// </summary>
    ExtraArguments,
}

/// <summary>How a field's text is read, decided by the flag's own parser.</summary>
public enum LaunchpadValueFormat
{
    /// <summary>Any text. The default, and what a path or a command line takes.</summary>
    Text,

    /// <summary>An unsigned integer, read the way <c>ParseULong</c> reads one.</summary>
    UnsignedInteger,

    /// <summary>A positive integer, read the way <c>ParsePositiveInt</c> reads one.</summary>
    PositiveInteger,

    /// <summary>A number in [0, 1], read the way the fairness threshold is read.</summary>
    UnitInterval,

    /// <summary>One of a named set, compared case-insensitively as the commands compare.</summary>
    Choice,
}

/// <summary>One row of the command list, as the layout reads it.</summary>
/// <param name="Name">The command's own name.</param>
/// <param name="Summary">The one line that says what it does.</param>
/// <param name="FieldCount">How many fields its form has.</param>
public readonly record struct LaunchpadCommandView(string Name, string Summary, int FieldCount);

/// <summary>One row of the form, as the layout reads it.</summary>
/// <param name="Label">The flag's name, or the field's own name.</param>
/// <param name="Kind">What the field holds.</param>
/// <param name="Required">Whether the command refuses without it.</param>
/// <param name="Value">What the reader has in it, defaults included.</param>
/// <param name="ShowsDefault">Whether <paramref name="Value"/> is still the command's own default.</param>
/// <param name="WasTyped">Whether the reader has typed in it.</param>
public readonly record struct LaunchpadFieldView(
    string Label,
    LaunchpadFieldKind Kind,
    bool Required,
    string Value,
    bool ShowsDefault,
    bool WasTyped);

/// <summary>Everything one setup-screen frame is composed from.</summary>
/// <param name="Commands">The list, in the order it is drawn.</param>
/// <param name="SelectedIndex">Which command is on show.</param>
/// <param name="SelectedName">That command's name, for the title row.</param>
/// <param name="Editing">Whether a field has focus.</param>
/// <param name="Fields">The selected command's form, in the order it is drawn.</param>
/// <param name="FocusedIndex">Which field has focus, or -1.</param>
/// <param name="Caret">The caret's column in that field.</param>
/// <param name="CommandLine">The command line the form adds up to.</param>
/// <param name="Error">Why the form cannot be run, or null.</param>
/// <param name="Size">The terminal's size, which the result matches exactly.</param>
/// <param name="Glyphs">Which glyph vocabulary to draw from.</param>
/// <param name="PanelFill">The panel background, or <c>null</c> for the terminal's own.</param>
public sealed record LaunchpadRequest(
    IReadOnlyList<LaunchpadCommandView> Commands,
    int SelectedIndex,
    string SelectedName,
    bool Editing,
    IReadOnlyList<LaunchpadFieldView> Fields,
    int FocusedIndex,
    int Caret,
    string CommandLine,
    string? Error,
    PaneSize Size,
    GlyphMode Glyphs,
    Rgb? PanelFill);
