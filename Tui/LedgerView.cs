namespace Lattice.Tui;

/// <summary>Which band of the screen a pane occupies, and so which table it draws.</summary>
public enum LedgerPaneKind
{
    /// <summary>The artifact's own provenance: where, when and on what it was produced.</summary>
    Summary,

    /// <summary>The external-agent block, when the artifact has one.</summary>
    Agent,

    /// <summary>One row per suite, with that suite's own statistics and verdict.</summary>
    Studies,

    /// <summary>
    /// The band that shows detail about the selected study: its per-seed rows, or the
    /// side-by-side comparison when the reader has asked for one.
    /// </summary>
    Detail,
}

/// <summary>One pane's rectangle on the screen, and the table it draws inside it.</summary>
/// <param name="Kind">Which table the pane holds.</param>
/// <param name="Area">The pane's rectangle, borders included.</param>
/// <param name="Title">What the pane's top border says.</param>
public readonly record struct LedgerPane(LedgerPaneKind Kind, LedgerLayout.Rect Area, string Title);

/// <summary>Everything one Ledger frame is composed from.</summary>
/// <param name="Artifacts">The artifacts loaded, in the order they were named.</param>
/// <param name="ArtifactIndex">Which artifact the summary and study panes are about.</param>
/// <param name="StudyIndex">Which suite is selected.</param>
/// <param name="SeedIndex">
/// Which per-seed row is selected, in the study's own row order. A separate cursor from
/// the study's, because scrolling a suite's seeds is not choosing a different suite.
/// </param>
/// <param name="Comparing">Whether the detail band shows the comparison instead of the per-seed rows.</param>
/// <param name="Size">The terminal's size, which the result matches exactly.</param>
/// <param name="Glyphs">Which glyph vocabulary to draw from.</param>
/// <param name="PanelFill">The panel background, or <c>null</c> for the terminal's own.</param>
public sealed record LedgerRequest(
    IReadOnlyList<LedgerArtifact> Artifacts,
    int ArtifactIndex,
    int StudyIndex,
    int SeedIndex,
    bool Comparing,
    PaneSize Size,
    GlyphMode Glyphs,
    Rgb? PanelFill);
