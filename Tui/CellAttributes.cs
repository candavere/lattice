namespace Lattice.Tui;

/// <summary>
/// Text attributes carried per cell. Flags rather than an enum of combinations
/// so a cell can hold any subset without a lookup table.
/// </summary>
[Flags]
public enum CellAttributes
{
    /// <summary>No attribute.</summary>
    None = 0,

    /// <summary>SGR 1.</summary>
    Bold = 1 << 0,

    /// <summary>SGR 2.</summary>
    Dim = 1 << 1,

    /// <summary>SGR 3.</summary>
    Italic = 1 << 2,

    /// <summary>SGR 4.</summary>
    Underline = 1 << 3,

    /// <summary>SGR 7, swap foreground and background.</summary>
    Reverse = 1 << 4,
}