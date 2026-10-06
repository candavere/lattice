using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Whole Launchpad frames, pinned cell by cell, for the six states a reader can be
/// in and the two terminal sizes they can be in.
/// </summary>
/// <remarks>
/// <para>
/// The same discipline as the cockpit goldens: the rows are accounted for by a
/// source other than <see cref="LaunchpadLayout"/> before the frozen file is
/// compared — the command list rows are composed here from the catalog's own names
/// and summaries with the column width written out longhand, the form rows are
/// checked against the form's own values, and the frame, pane titles and key hints
/// are literals at their known places. Only then is the whole frame compared, so a
/// golden cannot drift into agreeing with a regression.
/// </para>
/// <para>
/// The eight replay goldens are untouched by this file: they are frames of a
/// different screen and are checked by <see cref="ReplayGoldenTests"/>.
/// </para>
/// </remarks>
public class LaunchpadGoldens
{
    private static readonly PaneSize Full = new(100, 30);
    private static readonly PaneSize Narrow = new(80, 25);

    /// <summary>The first column of the command-list pane.</summary>
    private const int ListLeft = 1;

    /// <summary>The form pane's left column: one past the 34-column list.</summary>
    private const int FormLeft = ListLeft + 34 + 1;

    /// <summary>The columns inside the command-list pane at 100x30.</summary>
    private const int ListInnerWidth = 32;

    /// <summary>The columns inside the form pane at 100x30.</summary>
    private const int FormInnerWidth = 100 - 4 - 34 - 2 - 1;

    /// <summary>The first column of the form pane's contents: one inside its border.</summary>
    private const int FormContentLeft = FormLeft + 1;

    /// <summary>The first column a value can occupy, after the label and its gap.</summary>
    private const int ValueColumn = FormContentLeft + GenerateLabelWidth + LabelGap;

    /// <summary>
    /// The first content row of either content pane. Row 0 is the screen's own
    /// border and row 1 is the panes' own top borders, so both panes' contents begin
    /// at row 2.
    /// </summary>
    private const int ContentFirstRow = 2;

    /// <summary>
    /// The label column width the generate form draws at, which is its widest label:
    /// <c>extra arguments</c> is fifteen columns, <c>--min-fairness</c> thirteen, and
    /// no required mark adds to either.
    /// </summary>
    private const int GenerateLabelWidth = 15;

    /// <summary>One blank column between the label and the value.</summary>
    private const int LabelGap = 1;

    [Fact]
    public void TheListAtTheDesignedSizeIsEveryCommandInOrderWithItsOwnDescription()
    {
        var lines = Golden("launchpad-filled-100x30", Typed("42"));

        for (var index = 0; index < LaunchpadCatalog.Commands.Count; index++)
        {
            var command = LaunchpadCatalog.Commands[index];
            var marker = index == 0 ? "> " : "  ";

            // Composed here from the catalog rather than read back out of the frame,
            // so the golden is compared against the thing it claims to show.
            var expected = CellText.Clip(
                marker + command.Name + " " + command.Summary,
                ListInnerWidth,
                GlyphMode.Unicode);

            Assert.Equal(
                Pad(expected, ListInnerWidth),
                Slice(lines[ContentFirstRow + index], ListLeft + 1, ListInnerWidth));
        }
    }

    [Fact]
    public void TheScreenOpensInNavigationModeWithItsNavigationHints()
    {
        var lines = Golden("launchpad-open-100x30", []);

        Assert.Contains(TitleRow(), lines[0][1..^1].Trim(BorderGlyphs.Rounded.Horizontal), StringComparison.Ordinal);
        Assert.Equal(new string(BorderGlyphs.Rounded.TopLeft, 1), lines[0][..1]);
        Assert.Equal($"{BorderGlyphs.Rounded.BottomLeft}{BorderGlyphs.Rounded.BottomRight}", BottomCorners(lines));
        Assert.Equal(LaunchpadLayout.NavigationHints, HintRow(lines));
        Assert.Equal(LaunchpadMode.Navigation, Drive([]).Mode);
    }

    /// <summary>The title row's command count, read from the catalog rather than written out.</summary>
    private static string TitleRow() =>
        $"LATTICE LAUNCHPAD  {LaunchpadCatalog.Commands.Count} commands";

    /// <summary>
    /// Every frozen frame that the Ledger catalog entry changed, named with the one
    /// reason it changed.
    /// <para>
    /// Adding an entry to the catalog has exactly two visible effects on the screen: the
    /// title row counts one more command, and the command list has one more row. This
    /// asserts both of those on each affected frame, and — through the geometry of the
    /// frames above — that nothing else about them moved.
    /// </para>
    /// <para>
    /// <c>launchpad-fallback-80x25</c> changed its title only: at the fallback size the
    /// list band is eight rows tall by the layout's own choice, so the ninth command is
    /// reachable by scrolling rather than by being drawn where there was no row.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("launchpad-open-100x30", "the title counts the new entry")]
    [InlineData("launchpad-filled-100x30", "the title counts it and the list shows it")]
    [InlineData("launchpad-editing-100x30", "the title counts it and the list shows it")]
    [InlineData("launchpad-error-100x30", "the title counts it and the list shows it")]
    [InlineData("launchpad-long-value-100x30", "the title counts it and the list shows it")]
    [InlineData("launchpad-ascii-100x30", "the title counts it and the list shows it in ASCII")]
    [InlineData("launchpad-fallback-80x25", "the title counts it; the list band has no ninth row at this size")]
    public void EveryFrameTheLedgerEntryChangedSaysWhyItChanged(string name, string because)
    {
        var lines = Frozen(name);
        var ascii = name.Contains("ascii", StringComparison.Ordinal);
        var frame = ascii ? BorderGlyphs.Ascii : BorderGlyphs.Rounded;

        // The title, and only the title, carries the count. Trimmed with the framing
        // this frame is drawn in, so an ASCII frame is read as an ASCII frame.
        Assert.Equal(TitleRow(), lines[0][1..^1].Trim(frame.Horizontal));
        Assert.Single(lines, line => line.Contains("commands", StringComparison.Ordinal));

        if (!name.Contains("fallback", StringComparison.Ordinal))
        {
            // The new entry is on the list, in catalog order, in this frame's vocabulary.
            var list = LaunchpadCatalog.Commands[^1];
            var glyphs = ascii ? CellText.AsciiEllipsis : CellText.UnicodeEllipsis.ToString();
            Assert.Contains(list.Name, string.Join('\n', lines), StringComparison.Ordinal);

            // The summary beside it is cut to the list's own width and marked, so the
            // row is as long as every other row and no longer.
            Assert.Contains(glyphs, string.Join('\n', lines), StringComparison.Ordinal);
        }

        Assert.NotEqual("", because);
    }

    [Fact]
    public void EveryRowOfTheFrameIsThePaneItClaimsToBe()
    {
        var lines = Golden("launchpad-filled-100x30", Typed("42"));

        // Every pane's top border starts and ends on the same glyph, and the two
        // side-by-side panes meet exactly one column apart. A frame whose panes were
        // misaligned by one column would still read correctly as text.
        foreach (var pane in LaunchpadLayout.Panes(Full))
        {
            var top = Slice(lines[pane.Y], pane.X, pane.Width);

            Assert.Contains(top[0], LaunchpadLayout.BorderGlyphSet);
            Assert.Contains(top[^1], LaunchpadLayout.BorderGlyphSet);
            Assert.Equal(pane.Width, top.Length);
        }

        var (list, form) = (LaunchpadLayout.Panes(Full)[0], LaunchpadLayout.Panes(Full)[1]);
        Assert.Equal(34, list.Width);
        Assert.Equal(list.X + list.Width + 1, form.X);
    }

    [Fact]
    public void TheFormShowsItsLabelsItsValuesAndItsRequiredMark()
    {
        var form = Drive(Typed("42"));
        var lines = Golden("launchpad-filled-100x30", Typed("42"));
        var command = form.Current;

        for (var index = 0; index < command.Fields.Count; index++)
        {
            var field = command.Fields[index];
            var label = field.Label + (field.Required ? "*" : string.Empty);

            Assert.Equal(
                Pad(label, GenerateLabelWidth),
                Slice(lines[ContentFirstRow + index], FormContentLeft, GenerateLabelWidth));

            // The value is read from the form, not from the layout: this is the same
            // text the frame is supposed to be showing.
            var expected = (form.Value(field) is { Length: > 0 } value ? value : "(unset)").PadRight(FormInnerWidth - GenerateLabelWidth - LabelGap);
            Assert.Equal(expected, ValueOn(lines, ContentFirstRow + index));
        }

        Assert.Contains(LaunchpadCatalog.ExtraArgumentsLabel, lines[ContentFirstRow + command.Fields.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandLineUnderTheFormIsTheLineTheFormAddsUpTo()
    {
        var lines = Golden("launchpad-filled-100x30", Typed("42"));

        Assert.Equal("lattice generate --seed 42", CommandLineRow(lines));
    }

    [Fact]
    public void EnteringAFieldShowsTheEditingHintsAndLeavesTheSeedEmpty()
    {
        var lines = Golden("launchpad-editing-100x30", Typed(""));

        Assert.Equal(LaunchpadLayout.EditingHints, HintRow(lines));
        Assert.Equal("lattice generate", CommandLineRow(lines));
        Assert.Equal(
            Pad("--seed*", GenerateLabelWidth),
            Slice(lines[ContentFirstRow], FormContentLeft, GenerateLabelWidth));
        Assert.Equal("(required)", ValueOn(lines, ContentFirstRow).Trim());
        Assert.Equal(LaunchpadMode.Editing, Drive(Typed("")).Mode);
    }

    [Fact]
    public void AnInlineErrorIsOnScreenInTheFormPaneAndOnTheCommandLine()
    {
        var keys = Typed("x");
        var form = Drive(keys);
        var lines = Golden("launchpad-error-100x30", keys);

        // The message is the parser's own, and the command line still shows the value
        // the reader typed, so the two cannot disagree about what is on the form.
        Assert.Equal("flag '--seed' expects an unsigned integer, got 'x'.", form.ValidationError);
        Assert.Contains(lines, row => row.Contains(form.ValidationError!, StringComparison.Ordinal));
        Assert.Equal("lattice generate --seed x", CommandLineRow(lines));
        Assert.Equal("x", ValueOn(lines, ContentFirstRow).Trim());
        Assert.False(form.CanRun);
    }

    [Fact]
    public void TheFallbackSizeStacksThePanesAndSaysWhatSizeItIs()
    {
        var lines = Golden("launchpad-fallback-80x25", Typed("42"), Narrow);

        Assert.Contains(TitleRow(), lines[0], StringComparison.Ordinal);
        Assert.Equal($"{BorderGlyphs.Rounded.BottomLeft}{BorderGlyphs.Rounded.BottomRight}", BottomCorners(lines));

        var notice = $"terminal is 80x25; the full layout needs {LaunchpadLayout.MinimumWidth}x{LaunchpadLayout.MinimumHeight}; resize for it";
        Assert.Contains(notice, string.Join('\n', lines), StringComparison.Ordinal);
        // The command line has no pane of its own at this size, so it is drawn under
        // the form and is found by its text.
        Assert.Contains("lattice generate --seed 42", string.Join('\n', lines), StringComparison.Ordinal);

        // The stacked layout is two panes: the list and the form. A third pane at
        // this size would be a band too short to say anything in.
        Assert.Equal(2, LaunchpadLayout.Panes(Narrow).Count);
        Assert.Contains(LaunchpadCatalog.Commands[0].Summary, string.Join('\n', lines), StringComparison.Ordinal);
    }

    [Fact]
    public void AValueLongerThanThePaneIsCutToItAndMarked()
    {
        var keys = Typed(new string('L', 300));
        var lines = Golden("launchpad-long-value-100x30", keys);

        foreach (var line in lines)
        {
            Assert.Equal(Full.Width, line.Length);
        }

        Assert.Contains(CellText.UnicodeEllipsis, string.Join('\n', lines));
        Assert.DoesNotContain("LLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLL", lines[ContentFirstRow], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAsciiTerminalGetsAnAsciiFrameOfTheSameShape()
    {
        var keys = Typed("42漢字\U0001F600");
        var lines = Golden("launchpad-ascii-100x30", keys, Full, ascii: true);

        foreach (var line in lines)
        {
            Assert.Equal(Full.Width, line.Length);
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }

        Assert.Equal(
            $"{BorderGlyphs.Ascii.BottomLeft}{BorderGlyphs.Ascii.BottomRight}",
            BottomCorners(lines, ascii: true));
        Assert.Equal("lattice generate --seed 42???", CommandLineRow(lines));
    }

    /// <summary>
    /// Tab into the first field, then the characters of <paramref name="text"/>.
    /// The golden names are the states, and this is how each one is reached.
    /// </summary>
    private static IReadOnlyList<TuiKey> Typed(string text)
    {
        var keys = new List<TuiKey> { new(TuiKeyKind.Tab) };
        foreach (var glyph in text)
        {
            keys.Add(new TuiKey(TuiKeyKind.Character, glyph));
        }

        return keys;
    }

    private static LaunchpadForm Drive(IReadOnlyList<TuiKey> keys)
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        foreach (var key in keys)
        {
            form.Apply(key);
        }

        return form;
    }

    private static string[] Frame(IReadOnlyList<TuiKey> keys, PaneSize size, bool ascii)
    {
        var form = Drive(keys);
        return LaunchpadLayout.Render(Request(form, size, ascii)).ToLines();
    }

    private static string[] Frame(IReadOnlyList<TuiKey> keys, bool ascii = false) =>
        Frame(keys, Full, ascii);

    /// <summary>
    /// The form as the layout reads it, built from the same values the frame was
    /// drawn from — so the goldens are compared against the state and not against
    /// whatever the renderer produced.
    /// </summary>
    private static LaunchpadRequest Request(LaunchpadForm form, PaneSize size, bool ascii) =>
        new(
            [.. form.Commands.Select(command => new LaunchpadCommandView(
                command.Name,
                command.Summary,
                command.Fields.Count))],
            form.SelectedIndex,
            form.Selected,
            form.Mode == LaunchpadMode.Editing,
            [.. form.Fields.Select(field => new LaunchpadFieldView(
                field.Label,
                field.Kind,
                field.Required,
                form.Value(field),
                field.Default is { Length: > 0 },
                form.WasTyped(field)))],
            form.FocusedIndex,
            form.Caret,
            form.CommandLine,
            form.ValidationError,
            size,
            GlyphModes.Resolve(true, ascii),
            null);

    /// <summary>
    /// The frame this golden names, compared against the frozen copy of it row by
    /// row. A change in the renderer fails here until the golden has been re-read
    /// and re-justified.
    /// </summary>
    private static string[] Golden(string name, IReadOnlyList<TuiKey> keys, PaneSize? size = null, bool ascii = false)
    {
        var path = Committed("Tests", "Tui", "Goldens", name + ".txt");
        Assert.True(
            File.Exists(path),
            $"{path} is missing. A golden is generated once and frozen; the test that reads it never writes it.");

        var frame = Frame(keys, size ?? Full, ascii);
        var frozen = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(frozen.Length, frame.Length);
        for (var row = 0; row < frame.Length; row++)
        {
            Assert.True(
                frame[row] == frozen[row],
                $"row {row} of {name}:\n  frame:  |{frame[row]}|\n  frozen: |{frozen[row]}|");
        }

        return frozen;
    }

    /// <summary>
    /// The command line as it is drawn: the row inside the command pane, with the
    /// pane's own borders and the screen's own border cut away. Located through the
    /// pane rather than counted from the bottom, so a change to the pane heights
    /// cannot move the assertion off the row it is about.
    /// </summary>
    /// <summary>
    /// The frozen rows of a golden, without rendering it. Used by the test that names why
    /// each affected frame changed: it is about what is in the file, not about whether
    /// the renderer still agrees with it.
    /// </summary>
    private static string[] Frozen(string name)
    {
        var path = Committed("Tests", "Tui", "Goldens", name + ".txt");
        Assert.True(File.Exists(path), $"{path} is missing.");
        return File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string CommandLineRow(string[] lines)
    {
        var pane = LaunchpadLayout.Panes(Full).Last();
        return Slice(lines[pane.Y + 1], pane.X + 1, pane.Width - 2).TrimEnd();
    }

    /// <summary>
    /// The value a form row shows: everything in the row from the value column to the
    /// pane's inner right edge. Trimmed at the right because the pane is padded out
    /// to its border, and returned verbatim at the left so the caller's own
    /// expectation says whether the gap is there.
    /// </summary>
    private static string ValueOn(string[] lines, int row) =>
        Slice(lines[row], ValueColumn, FormInnerWidth - GenerateLabelWidth - LabelGap);

    /// <summary>
    /// The key hints as drawn: the row above the screen's own bottom border, with
    /// the screen's own border columns cut away. Located through the layout's own
    /// constant rather than counted from the end, so the assertion cannot drift onto
    /// the border row if the hint row moves.
    /// </summary>
    private static string HintRow(string[] lines, bool ascii = false) =>
        lines[^LaunchpadLayout.KeyHintRowFromBottom][1..^1]
            .Trim(ascii ? BorderGlyphs.Ascii.Horizontal : BorderGlyphs.Rounded.Horizontal)
            .Trim();

    /// <summary>The screen's own border row, in the glyph vocabulary in force.</summary>
    private static string Box(int width, bool ascii = false)
    {
        var glyphs = ascii ? BorderGlyphs.Ascii : BorderGlyphs.Rounded;
        return $"{glyphs.TopLeft}{new string(glyphs.Horizontal, width - 2)}{glyphs.TopRight}";
    }

    /// <summary>
    /// The screen's own bottom corners. The key hints are drawn on that same row, by
    /// the convention the cockpit already uses — the one row no pane can use — so the
    /// corners are what is left to assert the frame is closed with.
    /// </summary>
    private static string BottomCorners(string[] lines, bool ascii = false)
    {
        var glyphs = ascii ? BorderGlyphs.Ascii : BorderGlyphs.Rounded;
        var corners = $"{glyphs.BottomLeft}{glyphs.BottomRight}";

        Assert.Equal(corners, lines[^1][..1] + lines[^1][^1..]);
        return corners;
    }

    private static string Slice(string line, int start, int width) =>
        line.Substring(start, width);

    private static string Pad(string text, int width) =>
        CellText.Clip(text, width, GlyphMode.Unicode).PadRight(width);

    private static string Committed(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
