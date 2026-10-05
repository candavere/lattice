namespace Lattice.Tui;

/// <summary>
/// The setup screen: the commands <c>lattice</c> can run on the left, the selected
/// command's form on the right, and the command line that form adds up to
/// underneath.
/// </summary>
/// <remarks>
/// <para>
/// Pure — a request in, a grid the size of the terminal out — so the whole screen
/// can be asserted against its exact cells with no terminal attached, exactly as
/// <see cref="CockpitLayout"/> is.
/// </para>
/// <para>
/// <b>Below the minimum size the panes stack.</b> A two-column form at 80 columns is
/// two unreadable columns each, so the list goes to a band at the top and the form
/// below it, and at 60x10 the notice leads with the size it measured so that it
/// survives being clipped.
/// </para>
/// <para>
/// <b>Nothing overflows a pane.</b> Every string is cut to the pane it is drawn in
/// and marked when it was cut. A form's own text is unbounded, so this is the one
/// place the screen can be pushed into drawing over its border.
/// </para>
/// </remarks>
public static class LaunchpadLayout
{
    /// <summary>The narrowest terminal the two-pane layout is drawn for.</summary>
    public const int MinimumWidth = TerminalCapabilities.MinimumWidth;

    /// <summary>The shortest terminal the two-pane layout is drawn for.</summary>
    public const int MinimumHeight = TerminalCapabilities.MinimumHeight;

    /// <summary>The controls in navigation mode: letters steer, and one of them quits.</summary>
    public const string NavigationHints =
        "j/k or arrows command  tab form  enter run  q quit";

    /// <summary>The controls while a field has focus: letters type, and nothing steers.</summary>
    public const string EditingHints =
        "tab next field  enter commit  esc leave field  ctrl-c quit";

    /// <summary>The row the key hints are on, counted from the bottom.</summary>
    public const int KeyHintRowFromBottom = 1;

    /// <summary>The columns the command list occupies in the full layout.</summary>
    private const int ListWidth = 34;

    /// <summary>The rows the command list's own header and footer occupy.</summary>
    private const int ListChrome = 2;

    /// <summary>What a pane's title looks like in its top border.</summary>
    public static string PaneTitle(string title) => $" {title} ";

    /// <summary>
    /// Every glyph a closed box can be drawn from, in both vocabularies. A test
    /// checks each pane's four corners against this set, which is what "the pane is
    /// closed on all four sides" means without hard-coding either glyph set.
    /// </summary>
    public static IReadOnlyList<char> BorderGlyphSet { get; } = BoxGlyphs();

    private static char[] BoxGlyphs()
    {
        var glyphs = new HashSet<char>();

        foreach (var mapping in Glyphs.Mappings)
        {
            glyphs.Add(mapping.Unicode);
            glyphs.Add(mapping.Ascii);
        }

        return [.. glyphs];
    }

    /// <summary>Whether this terminal gets the two-pane layout.</summary>
    public static bool IsTwoPane(PaneSize size) =>
        size.Width >= MinimumWidth && size.Height >= MinimumHeight;

    /// <summary>The line a smaller terminal is shown instead of the two panes.</summary>
    public static string ResizeNotice(PaneSize size) =>
        $"terminal is {Invariant(size.Width)}x{Invariant(size.Height)}; " +
        $"the full layout needs {MinimumWidth}x{MinimumHeight}; resize for it";

    /// <summary>
    /// The panes a frame of this size is drawn in: the list, the form, and the
    /// command-line band. Named rather than computed inside the drawing, so a test
    /// can check that every one of them is actually closed on all four sides.
    /// </summary>
    public static IReadOnlyList<Rect> Panes(PaneSize size)
    {
        var panes = new List<Rect>();

        if (size.Width < 2 || size.Height < 2)
        {
            return panes;
        }

        if (IsTwoPane(size))
        {
            panes.Add(new Rect(1, 1, ListWidth, size.Height - 4));
            panes.Add(new Rect(ListWidth + 2, 1, size.Width - ListWidth - 3, size.Height - 4));
            panes.Add(new Rect(1, size.Height - 4, size.Width - 2, 3));
            return panes;
        }

        // The stacked layout: the list in a band under the title, the form below it,
        // with one row between them for the notice that says what size the terminal
        // is. The form gets the larger share, because it is the pane a reader works
        // in and the list scrolls anyway.
        var available = Math.Max(0, size.Height - 2 - 1);
        var listHeight = Math.Clamp(Math.Min(ListRowsWanted, available / 2), 2, Math.Max(2, available - 3));
        var formHeight = Math.Max(2, available - listHeight - 1);

        panes.Add(new Rect(1, 1, size.Width - 2, listHeight));
        panes.Add(new Rect(1, 1 + listHeight + 1, size.Width - 2, formHeight));
        return panes;
    }

    /// <summary>
    /// The row the command line is drawn on below the designed minimum size, where
    /// there is no pane to give it. It is the last row the form pane has, and the
    /// form leaves it alone — a reader must be able to see what will run at every
    /// size, or the fallback size is a screen that can start a command it will not
    /// show them.
    /// </summary>
    private static int StackedCommandLineRow(PaneSize size)
    {
        var panes = Panes(size);
        return panes.Count < 2 ? size.Height - 3 : panes[1].Y + panes[1].Height - 1;
    }

    /// <summary>Draws one setup-screen frame.</summary>
    /// <remarks>
    /// A terminal too small to frame — one cell, or a shape with no room for a box —
    /// is not an error: the grid comes back at exactly the size asked for with
    /// whatever fits in it drawn.
    /// </remarks>
    public static CellBuffer Render(LaunchpadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var size = request.Size;
        var cells = new CellBuffer(size.Width, size.Height);
        var frame = Border(request.Glyphs);

        if (request.PanelFill is { } fill)
        {
            cells.Fill(0, 0, size.Width, size.Height, new Cell(' ', null, fill));
        }

        if (size.Width < 2 || size.Height < 2)
        {
            return cells;
        }

        cells.DrawBorder(0, 0, size.Width, size.Height, frame, new Cell(' ', Theme.Sage, request.PanelFill));
        cells.DrawText(
            2,
            0,
            Clip(request, TitleRow(request), Math.Max(0, size.Width - 4)),
            new Cell(' ', Palette.Accent, request.PanelFill));

        if (size.Height >= 4)
        {
            DrawKeyHints(request, cells);
        }

        DrawContent(request, cells, frame);
        return cells;
    }

    private static void DrawContent(LaunchpadRequest request, CellBuffer cells, BorderGlyphs frame)
    {
        var panes = Panes(request.Size);
        if (panes.Count == 0)
        {
            return;
        }

        if (IsTwoPane(request.Size))
        {
            var (list, form, command) = (panes[0], panes[1], panes[2]);

            DrawPane(request, cells, frame, list, "COMMANDS");
            DrawPane(request, cells, frame, form, "FORM");
            DrawPane(request, cells, frame, command, "COMMAND");

            DrawCommandList(request, cells, list);
            DrawForm(request, cells, form);
            DrawCommandLine(request, cells, command);
            return;
        }

        var (narrowList, narrowForm) = (panes[0], panes[1]);

        DrawPane(request, cells, frame, narrowList, "COMMANDS");
        DrawPane(request, cells, frame, narrowForm, request.SelectedName);

        DrawCommandList(request, cells, narrowList);
        DrawForm(request, cells, narrowForm);
        DrawResizeNotice(request, cells, narrowForm);
        DrawStackedCommandLine(request, cells);
    }

    /// <summary>
    /// The command line at the fallback size, where there is no pane to give it. It
    /// goes on the form pane's own bottom border row — the same trick the key hints
    /// use on the screen's — because a form that hides what it will run is a form
    /// that cannot be trusted to run it.
    /// </summary>
    private static void DrawStackedCommandLine(LaunchpadRequest request, CellBuffer cells)
    {
        var row = StackedCommandLineRow(request.Size);
        if (row < 1 || row >= request.Size.Height - LaunchpadLayout.KeyHintRowFromBottom)
        {
            return;
        }

        var glyphs = Border(request.Glyphs);
        for (var column = 1; column < request.Size.Width - 1; column++)
        {
            cells[column, row] = cells[column, row] with { Glyph = glyphs.Horizontal };
        }

        cells.DrawText(
            2,
            row,
            Clip(request, request.CommandLine, Math.Max(0, request.Size.Width - 4)),
            new Cell(' ', Palette.AccentBright, request.PanelFill));
    }

    /// <summary>
    /// Below the designed size the two panes do not fit side by side, so the screen
    /// says so on the row above the form rather than leaving the reader to wonder
    /// why the form is narrower than the screen.
    /// </summary>
    private static void DrawResizeNotice(LaunchpadRequest request, CellBuffer cells, Rect form)
    {
        if (IsTwoPane(request.Size) || form.Y < 3)
        {
            return;
        }

        cells.DrawText(
            2,
            form.Y - 1,
            Clip(request, ResizeNotice(request.Size), Math.Max(0, request.Size.Width - 4)),
            new Cell(' ', Palette.TextDim, request.PanelFill));
    }

    /// <summary>
    /// The list, scrolled so the selected command is always on show: eight commands
    /// fit at the designed size, and a shorter terminal has to scroll rather than
    /// hide the command the reader is on.
    /// </summary>
    private static void DrawCommandList(LaunchpadRequest request, CellBuffer cells, Rect pane)
    {
        var capacity = pane.Height - 2;
        if (capacity <= 0)
        {
            return;
        }

        var commands = request.Commands;
        if (commands.Count == 0)
        {
            return;
        }

        // The first row shown, chosen so the selection is inside the window without
        // scrolling further than it has to.
        var first = Math.Clamp(request.SelectedIndex - (capacity - 1), 0, Math.Max(0, commands.Count - capacity));
        var row = pane.Y + 1;
        var width = pane.Width - 2;

        for (var index = first; index < commands.Count && row < pane.Y + pane.Height - 1; index++)
        {
            var command = commands[index];
            var selected = index == request.SelectedIndex;
            var marker = selected ? "> " : "  ";
            var text = marker + command.Name;

            // The summary goes on the same row when there is room for it, which keeps
            // the two halves of the line — which command, and what it does — in one
            // glance. Where there is not, the name is what survives the cut.
            var room = width - text.Length - 1;
            if (room >= 8)
            {
                text += " " + command.Summary;
            }

            cells.DrawText(
                pane.X + 1,
                row++,
                Clip(request, text, width),
                selected
                    ? new Cell(' ', Theme.SelectionForeground, Theme.SelectionBackground)
                    : new Cell(' ', Palette.TextPrimary, request.PanelFill));
        }
    }

    /// <summary>
    /// The form: one row per field, the label and its required mark on the left and
    /// the value on the right, with the focused row picked out and the caret drawn
    /// immediately after the value it is in.
    /// <para>
    /// The caret shares its field's row rather than taking one of its own. A row per
    /// caret would cost a long form one of its few visible fields, and the notice
    /// about the fields it had to drop along with it — which is exactly the row a
    /// reader needs to know the form is longer than the pane.
    /// </para>
    /// </summary>
    private static void DrawForm(LaunchpadRequest request, CellBuffer cells, Rect pane)
    {
        if (request.Fields.Count == 0 || pane.Height < 3)
        {
            return;
        }

        // The panes' own borders take a row each, and the notices below the fields
        // take one row each — but only when there is one to show. Reserving rows for
        // notices that do not exist is how a small terminal ends up with a form pane
        // that draws nothing at all.
        // One row for the command line at the fallback size, where it is drawn on the
        // form pane's own bottom border rather than in a pane of its own.
        var reserved = IsTwoPane(request.Size) ? 0 : 1;
        var notices = reserved
            + (request.Fields.Count > pane.Height - 2 - reserved ? 1 : 0)
            + (request.Error is { Length: > 0 } ? 1 : 0);
        var capacity = pane.Height - 2 - notices;
        if (capacity <= 0)
        {
            return;
        }

        var labelWidth = LabelWidth(request.Fields, pane.Width);
        var valueColumn = pane.X + 1 + (labelWidth > 0 ? labelWidth + 1 : 0);
        var valueWidth = pane.X + pane.Width - 1 - valueColumn;
        if (valueWidth < 4)
        {
            // Too narrow for a label and a value side by side: the value goes in the
            // label's columns, which is the only way both stay legible.
            labelWidth = 0;
            valueColumn = pane.X + 1;
            valueWidth = pane.Width - 2;
        }

        var first = request.FocusedIndex < 0
            ? 0
            : Math.Clamp(request.FocusedIndex - (capacity - 1), 0, Math.Max(0, request.Fields.Count - capacity));
        var shown = Math.Min(request.Fields.Count - first, capacity);
        var dropped = request.Fields.Count - first - shown;
        var row = pane.Y + 1;

        for (var index = first; index < first + shown; index++)
        {
            var field = request.Fields[index];
            var focused = index == request.FocusedIndex;
            var style = focused
                ? new Cell(' ', Theme.SelectionForeground, Theme.SelectionBackground)
                : new Cell(' ', Palette.TextPrimary, request.PanelFill);
            var value = Value(request, field, valueWidth);

            if (labelWidth > 0)
            {
                cells.DrawText(
                    pane.X + 1,
                    row,
                    Clip(request, field.Label + (field.Required ? "*" : string.Empty), labelWidth),
                    style);
            }

            cells.DrawText(
                valueColumn,
                row,
                value,
                field.ShowsDefault && !field.WasTyped && !focused
                    ? new Cell(' ', Palette.TextDim, request.PanelFill)
                    : style);

            if (focused)
            {
                // Clamped inside the value's own columns: a caret further right than
                // the value can show would land on the pane's border otherwise.
                var caretColumn = valueColumn + Math.Clamp(request.Caret, 0, Math.Max(0, value.Length - 1));
                if (caretColumn < pane.X + pane.Width - 1)
                {
                    cells[caretColumn, row] = cells[caretColumn, row] with
                    {
                        Attributes = cells[caretColumn, row].Attributes | CellAttributes.Reverse,
                    };
                }
            }

            row++;
        }

        if (dropped > 0)
        {
            cells.DrawText(
                pane.X + 1,
                row++,
                Clip(request, $"+{Invariant(dropped)} more; tab reaches them", pane.Width - 2),
                new Cell(' ', Palette.TextDim, request.PanelFill));
        }

        if (request.Error is { Length: > 0 } error && row < pane.Y + pane.Height - 1)
        {
            cells.DrawText(
                pane.X + 1,
                row,
                Clip(request, error, pane.Width - 2),
                new Cell(' ', Palette.Error, request.PanelFill));
        }
    }

    /// <summary>
    /// A field's value as drawn: cut to the pane and marked, with the caret's
    /// position measured in the same columns the cut was made in, so the caret cannot
    /// end up past the end of a clipped value.
    /// </summary>
    private static string Value(LaunchpadRequest request, LaunchpadFieldView field, int width)
    {
        var value = field.Value.Length == 0 ? Placeholder(field) : field.Value;
        var clipped = CellText.Clip(value, width, request.Glyphs);
        return clipped;
    }

    /// <summary>
    /// What an empty field shows. The required mark is the one thing a form must
    /// make findable, and a bare blank row says nothing at all.
    /// </summary>
    private static string Placeholder(LaunchpadFieldView field) =>
        field.Required ? "(required)" : "(unset)";

    /// <summary>
    /// The widest field label on the form, capped at half the pane so one long flag
    /// name cannot squeeze every value down to a character or two.
    /// </summary>
    private static int LabelWidth(IReadOnlyList<LaunchpadFieldView> fields, int paneWidth)
    {
        var widest = fields
            .Select(field => field.Label.Length + (field.Required ? 1 : 0))
            .DefaultIfEmpty(0)
            .Max();

        return Math.Min(widest, Math.Max(0, paneWidth / 2 - 2));
    }

    private static void DrawCommandLine(LaunchpadRequest request, CellBuffer cells, Rect pane)
    {
        cells.DrawText(
            pane.X + 1,
            pane.Y + 1,
            Clip(request, request.CommandLine, pane.Width - 2),
            new Cell(' ', Palette.AccentBright, request.PanelFill));
    }

    private static void DrawPane(
        LaunchpadRequest request,
        CellBuffer cells,
        BorderGlyphs frame,
        Rect pane,
        string title)
    {
        cells.Fill(pane.X, pane.Y, pane.Width, pane.Height, new Cell(' ', null, request.PanelFill));
        cells.DrawBorder(pane.X, pane.Y, pane.Width, pane.Height, frame, new Cell(' ', Theme.Sage, request.PanelFill));
        cells.DrawText(
            pane.X + 2,
            pane.Y,
            Clip(request, PaneTitle(title), Math.Max(0, pane.Width - 4)),
            new Cell(' ', Palette.Accent, request.PanelFill));
    }

    private static void DrawKeyHints(LaunchpadRequest request, CellBuffer cells)
    {
        cells.DrawText(
            2,
            request.Size.Height - KeyHintRowFromBottom,
            request.Editing ? EditingHints : NavigationHints,
            new Cell(' ', Palette.TextDim, request.PanelFill));
    }

    private static string TitleRow(LaunchpadRequest request) =>
        $"LATTICE LAUNCHPAD  {Invariant(request.Commands.Count)} commands";

    /// <summary>
    /// How many rows of the command list the stacked layout would like: enough for
    /// the whole list at the fallback size, and the rest of the terminal goes to the
    /// form.
    /// </summary>
    private const int ListRowsWanted = 8;

    private static string Clip(LaunchpadRequest request, string text, int width) =>
        CellText.Clip(text, Math.Max(0, width), request.Glyphs);

    private static BorderGlyphs Border(GlyphMode glyphs) =>
        glyphs == GlyphMode.Unicode ? BorderGlyphs.Rounded : BorderGlyphs.Ascii;

    private static string Invariant(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A pane's rectangle, in cells.</summary>
    public readonly record struct Rect(int X, int Y, int Width, int Height);
}
