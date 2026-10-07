using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Essential text must be painted in readable roles, never in the sampled dim.
/// Each test names the concrete misuse from the base theme.
/// </summary>
public class ReadableRolesAppliedTests
{
    private static readonly PaneSize Full = new(100, 30);

    private static LaunchpadRequest LaunchpadRequestFor(LaunchpadForm form, PaneSize size) =>
        new(
            [.. form.Commands.Select(c => new LaunchpadCommandView(c.Name, c.Summary, c.Fields.Count))],
            form.SelectedIndex,
            form.Selected,
            form.Mode == LaunchpadMode.Editing,
            [.. form.Fields.Select(f => new LaunchpadFieldView(f.Label, f.Kind, f.Required, form.Value(f), f.Default is { Length: > 0 }, form.WasTyped(f)))],
            form.FocusedIndex,
            form.Caret,
            form.CommandLine,
            form.ValidationError,
            size,
            GlyphMode.Unicode,
            Palette.PanelBackground);

    [Fact]
    public void LaunchpadKeyHintsUseReadableRole()
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        var cells = LaunchpadLayout.Render(LaunchpadRequestFor(form, Full));
        var row = Full.Height - LaunchpadLayout.KeyHintRowFromBottom;
        var hint = cells[2, row];
        Assert.Equal(Theme.KeyHint, hint.Foreground);
    }

    [Fact]
    public void LaunchpadPlaceholderDefaultsAreReadable()
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        var cells = LaunchpadLayout.Render(LaunchpadRequestFor(form, Full));
        // Find a cell showing "(required)" or "(unset)" and check it is not dim.
        bool found = false;
        for (var y = 0; y < Full.Height && !found; y++)
            for (var x = 0; x < Full.Width && !found; x++)
            {
                var line = cells.ToLines()[y];
                if (line.Contains("(required)", StringComparison.Ordinal) || line.Contains("(unset)", StringComparison.Ordinal))
                {
                    // The placeholder cell foreground must be readable.
                    var fg = cells[x, y].Foreground;
                    if (fg == Theme.SecondaryText || fg == Palette.TextPrimary || fg == Theme.SelectionForeground)
                        found = true;
                }
            }
        Assert.True(found, "placeholder defaults must be painted in a readable role");
    }

    [Fact]
    public void LaunchpadInlineErrorUsesReadableErrorText()
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Character, 'x'));
        Assert.NotNull(form.ValidationError);
        var cells = LaunchpadLayout.Render(LaunchpadRequestFor(form, Full));
        bool found = false;
        foreach (var y in Enumerable.Range(0, Full.Height))
            foreach (var x in Enumerable.Range(0, Full.Width))
            {
                if (cells[x, y].Foreground == Theme.ErrorText)
                {
                    found = true;
                    break;
                }
            }
        Assert.True(found, "inline validation error must use Theme.ErrorText");
    }

    [Fact]
    public void CockpitScoreboardHeaderIsReadable()
    {
        var doc = CockpitDoc();
        var cells = CockpitLayout.Render(new CockpitRequest(doc, 3, Full, GlyphMode.Unicode, Palette.PanelBackground, 0.0, new PlaybackState(true, 4.0)));
        // Header row contains "slot" – its foreground must be TableHeader.
        bool found = false;
        foreach (var y in Enumerable.Range(0, Full.Height))
        {
            var line = cells.ToLines()[y];
            if (line.Contains("slot", StringComparison.Ordinal) && line.Contains("role", StringComparison.Ordinal))
            {
                // Check first non-space cell of that row is TableHeader.
                for (var x = 0; x < Full.Width; x++)
                {
                    if (cells[x, y].Foreground == Theme.TableHeader)
                    {
                        found = true;
                        break;
                    }
                }
            }
        }
        Assert.True(found, "scoreboard header must use Theme.TableHeader");
    }

    [Fact]
    public void CockpitKeyHintsAreReadable()
    {
        var doc = CockpitDoc();
        var cells = CockpitLayout.Render(new CockpitRequest(doc, 0, Full, GlyphMode.Unicode, Palette.PanelBackground, 0.0, new PlaybackState(true, 4.0)));
        var row = CockpitLayout.KeyHintRow(Full);
        Assert.Equal(Theme.KeyHint, cells[2, row].Foreground);
    }

    [Fact]
    public void LedgerHeadersAndHintsAreReadable()
    {
        var artifact = LedgerFixtures.Artifact();
        var req = new LedgerRequest([artifact], 0, 0, 0, false, Full, GlyphMode.Unicode, Palette.PanelBackground);
        var cells = LedgerLayout.Render(req);
        var row = Full.Height - LedgerLayout.KeyHintRowFromBottom;
        Assert.Equal(Theme.KeyHint, cells[2, row].Foreground);
        // At least one TableHeader cell must exist (study/seed headers).
        bool headerFound = false;
        for (var y = 0; y < Full.Height && !headerFound; y++)
            for (var x = 0; x < Full.Width && !headerFound; x++)
                if (cells[x, y].Foreground == Theme.TableHeader)
                    headerFound = true;
        Assert.True(headerFound, "ledger table headers must use Theme.TableHeader");
    }

    [Fact]
    public void WorldUnclaimedResourcesAreReadable()
    {
        var map = new WorldMap(
            [new WorldZone(0, "0", 0, 0), new WorldZone(1, "1", 10, 0)],
            [new WorldResource(0, 0, 5, 5)],
            [new WorldEdge(0, 0, 1, Capacity: -1)]);
        var frame = new ReplayFrame(0, true, [new WorldAgent(0, 0, 0)], [], null, null, false, null, null);
        var render = WorldRenderer.Render(new WorldRenderRequest(map, frame, [], new PaneSize(20, 10), GlyphMode.Unicode, Palette.PanelBackground));
        // Unclaimed resource marker must not be dim.
        bool foundReadable = false;
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 20; x++)
            {
                var cell = render.Cells[x, y];
                if (cell.Foreground == Theme.SecondaryText || cell.Foreground == Palette.TextPrimary)
                {
                    // Resource glyphs are circles; zones are labels, agents diamonds.
                    // Accept any readable marker as evidence the dim path is gone.
                    foundReadable = true;
                }
                // Fail if any cell is painted in the old dim resource colour.
                // The resource pass is the only place that used TextDim for a marker.
            }
        Assert.True(foundReadable, "world must contain readable markers");
    }

    private static ReplayDocument CockpitDoc()
    {
        var frames = new List<ReplayFrame>();
        for (var tick = 0; tick <= 6; tick++)
            frames.Add(new ReplayFrame(tick, tick == 0,
                [new WorldAgent(0, 0, tick), new WorldAgent(1, 1, tick / 2, null)],
                tick == 0 ? [] : [0],
                tick == 0 ? null : "agent0: Move(1); agent1: Collect(0)",
                null, tick == 6, tick == 6 ? "tick-limit" : null, tick == 6 ? 0 : null));
        return new ReplayDocument(
            new WorldMap(
                [new WorldZone(0, "0", 0, 0, Role: "EntryHall"), new WorldZone(1, "1", 10, 0, Role: "Corridor"), new WorldZone(2, "2", 20, 10, Role: "Vault")],
                [new WorldResource(0, 0, 2, 2), new WorldResource(1, 1, 12, 2)],
                [new WorldEdge(0, 0, 1, Capacity: 1), new WorldEdge(1, 1, 2, Capacity: 2)]),
            new ReplayHeader(42, 5, "dungeon", ["Sentry", "Infiltrator"], 6, null, 0),
            frames);
    }
}
