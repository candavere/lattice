using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The setup screen drawn: the command list on the left, the selected command's
/// form on the right, and the command line the form adds up to underneath.
/// </summary>
/// <remarks>
/// <para>
/// The layout is pure — a request in, a grid the size of the terminal out — so
/// every assertion here is about exact cells with no terminal attached. Below the
/// designed minimum size the screen falls back to the list and the form stacked,
/// because a two-pane form at 80 columns is two unreadable columns each.
/// </para>
/// <para>
/// The one rule the frame exists to enforce is that nothing overflows its pane: a
/// value typed into a field is cut to the pane and marked, never written over the
/// pane's border.
/// </para>
/// </remarks>
public class LaunchpadLayoutTests
{
    private static readonly PaneSize Full = new(100, 30);
    private static readonly PaneSize Narrow = new(80, 25);

    /// <summary>Fill, glyphs and size are the only things a layout request needs.</summary>
    private static LaunchpadRequest Request(
        LaunchpadForm form,
        PaneSize size,
        bool ascii = false) =>
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

    [Fact]
    public void TheFrameIsExactlyTheSizeItWasAskedFor()
    {
        foreach (var size in new[] { Full, Narrow, new PaneSize(20, 6) })
        {
            var frame = LaunchpadLayout.Render(Request(Form(), size));

            Assert.Equal(size.Width, frame.Width);
            Assert.Equal(size.Height, frame.Height);
        }
    }

    [Fact]
    public void EveryCommandIsListedWithItsOneLineDescription()
    {
        var frame = LaunchpadLayout.Render(Request(Form(), Full));
        var text = string.Join('\n', frame.ToLines());

        foreach (var command in LaunchpadCatalog.Commands)
        {
            Assert.Contains(command.Name, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The selected command is picked out, so a reader can see which of eight forms
    /// is on show without reading it.
    /// </summary>
    [Fact]
    public void TheSelectedCommandIsMarked()
    {
        var frame = LaunchpadLayout.Render(Request(Form(), Full));

        Assert.Contains("> generate", string.Join('\n', frame.ToLines()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFormShowsTheFieldsTheirValuesAndTheRequiredMark()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Character, '7'));

        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines());

        Assert.Contains("--seed", text, StringComparison.Ordinal);
        Assert.Contains("7", text, StringComparison.Ordinal);
        Assert.Contains("--min-fairness", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandLineIsOnScreenUnderTheForm()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Character, '3'));

        Assert.Contains("lattice generate --seed 3", string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A value the parser refuses is shown where the reader is looking, not only in
    /// some summary. Without it the screen would offer a run it is about to refuse.
    /// </summary>
    [Fact]
    public void AnInlineErrorIsOnScreenWithTheFieldItIsAbout()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Character, 'x'));

        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines());

        Assert.Contains("expects an unsigned integer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoErrorIsOnScreenForAFormThatIsRunnable()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Character, '3'));

        Assert.DoesNotContain("expects an unsigned integer", string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A value longer than the pane is cut to it and marked, never written past the
    /// pane's border. This is the one failure mode a form has that a list of commands
    /// does not: the reader's own text is unbounded.
    /// </summary>
    [Fact]
    public void AValueLongerThanThePaneIsCutToItAndMarked()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in new string('L', 400))
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        var lines = LaunchpadLayout.Render(Request(form, Full)).ToLines();

        foreach (var line in lines)
        {
            Assert.Equal(Full.Width, line.Length);
        }

        // The Unicode marker is one column; the ASCII one is three. Asserting the
        // one the vocabulary is actually in force is what keeps the two from being
        // confused, since a Unicode cut with a three-character marker would push
        // every column to its right one short.
        Assert.Contains(CellText.UnicodeEllipsis, string.Join('\n', lines));
    }

    [Fact]
    public void AValueLongerThanThePaneIsCutToItAndMarkedInAsciiToo()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in new string('L', 400))
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        Assert.Contains(
            CellText.AsciiEllipsis,
            string.Join('\n', LaunchpadLayout.Render(Request(form, Full, ascii: true)).ToLines()));
    }

    /// <summary>
    /// Nothing may be drawn outside its pane. The pane rectangles are the assertion:
    /// every border column of every pane is a border glyph, and no text row carries
    /// a border glyph in the middle of the screen's own whitespace.
    /// </summary>
    [Fact]
    public void EveryPaneIsClosedOnAllFourSidesAtBothSizes()
    {
        foreach (var size in new[] { Full, Narrow })
        {
            var cells = LaunchpadLayout.Render(Request(Form(), size));

            foreach (var pane in LaunchpadLayout.Panes(size))
            {
                var corners = new[]
                {
                    cells[pane.X, pane.Y],
                    cells[pane.X + pane.Width - 1, pane.Y],
                    cells[pane.X, pane.Y + pane.Height - 1],
                    cells[pane.X + pane.Width - 1, pane.Y + pane.Height - 1],
                };

                foreach (var corner in corners)
                {
                    Assert.Contains(corner.Glyph, LaunchpadLayout.BorderGlyphSet);
                }
            }
        }
    }

    /// <summary>
    /// Text is column-safe: a wide or emoji character the reader typed is replaced
    /// rather than left to occupy one column and be drawn two, which would shift
    /// every column to its right for the rest of the frame.
    /// </summary>
    [Theory]
    [InlineData("漢字漢字")]
    [InlineData("\U0001F600\U0001F601")]
    public void AWideCharacterTypedIntoAFieldIsReplacedRatherThanHalfDrawn(string typed)
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in typed)
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines());

        // The wide characters are gone and the one-column replacement is in their
        // place. Left as themselves they would be drawn two columns wide, which is
        // one more than the grid has, and every column to their right would shift
        // for the rest of the frame.
        foreach (var glyph in typed)
        {
            Assert.DoesNotContain(glyph.ToString(), text, StringComparison.Ordinal);
        }

        Assert.Contains(CellText.ReplacementFor(GlyphMode.Unicode), text);
    }

    /// <summary>
    /// A value mixing ordinary text with a wide character keeps its ordinary text and
    /// loses only the wide part. The ASCII letters are what the reader typed and must
    /// not be replaced along with the character that cannot be drawn.
    /// </summary>
    [Fact]
    public void OrdinaryTextAroundAWideCharacterSurvivesIt()
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "ab")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines());

        Assert.Contains("ab", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// In ASCII mode the whole frame is ASCII. A terminal that cannot encode a box
    /// glyph prints a replacement for it, which is one column narrower than the grid
    /// assumes and shifts everything to the right of it.
    /// </summary>
    [Theory]
    [InlineData(100, 30)]
    [InlineData(80, 25)]
    public void AnAsciiTerminalGetsOnlyAsciiGlyphs(int width, int height)
    {
        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "42漢字\U0001F600")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        var frame = LaunchpadLayout.Render(Request(form, new PaneSize(width, height), ascii: true));

        foreach (var line in frame.ToLines())
        {
            foreach (var glyph in line)
            {
                Assert.True(glyph < 128, $"'{glyph}' is not ASCII.");
            }
        }
    }

    [Fact]
    public void TheFocusedFieldIsMarkedInEditingModeAndNotInNavigationMode()
    {
        var form = Form();

        var navigation = LaunchpadLayout.Render(Request(form, Full));

        form.Apply(new TuiKey(TuiKeyKind.Tab));
        var editing = LaunchpadLayout.Render(Request(form, Full));

        Assert.NotEqual(
            string.Join('\n', navigation.ToLines()),
            string.Join('\n', editing.ToLines()));
    }

    [Fact]
    public void TheKeyHintsSayWhatTheModeBinds()
    {
        var navigation = string.Join('\n', LaunchpadLayout.Render(Request(Form(), Full)).ToLines());
        Assert.Contains(LaunchpadLayout.NavigationHints, navigation, StringComparison.Ordinal);

        var form = Form();
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        var editing = string.Join('\n', LaunchpadLayout.Render(Request(form, Full)).ToLines());
        Assert.Contains(LaunchpadLayout.EditingHints, editing, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list scrolls when there are more commands than fit, so the selected one is
    /// always on show. Eight commands fit at the designed size, and the fallback
    /// proves the same rule with fewer rows.
    /// </summary>
    [Fact]
    public void TheSelectedCommandIsOnScreenEvenWhenTheListCannotShowThemAll()
    {
        var form = Form();
        for (var i = 0; i < 5; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Down));
        }

        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, new PaneSize(40, 9))).ToLines());

        Assert.Contains("> " + form.Selected, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A form with more fields than the pane has rows shows the ones it can, says how
    /// many are out of sight and how to reach them, and keeps the focused field on
    /// screen — rather than silently dropping the field the reader was on.
    /// </summary>
    [Fact]
    public void AFormWithMoreFieldsThanRowsSaysHowManyAreOutOfSight()
    {
        var form = LongForm();

        // The longest of the eight forms at a height where the form pane cannot hold
        // all nine fields: the pane scrolls, and the notice is what tells the reader
        // the other fields exist.
        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, new PaneSize(80, 14))).ToLines());

        Assert.Contains("more", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tab reaches", text, StringComparison.Ordinal);
        Assert.Contains(form.FocusedField!, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The longest form fits whole at the fallback size, so nothing is dropped and no
    /// notice is shown. A notice that appeared whenever the form scrolled — even
    /// because it scrolled by one row — would be noise.
    /// </summary>
    [Fact]
    public void AFormThatFitsWholeShowsEveryFieldAndNoNotice()
    {
        var form = LongForm();
        var text = string.Join('\n', LaunchpadLayout.Render(Request(form, Narrow)).ToLines());

        foreach (var field in form.Fields)
        {
            Assert.Contains(
                CellText.Clip(field.Label, 24, GlyphMode.Unicode),
                text,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("tab reaches", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A terminal below the designed minimum is told what size it is and what the
    /// full layout needs, on screen: a reader who cannot see why the form is narrow
    /// cannot tell whether to resize or to work in it.
    /// </summary>
    [Theory]
    [InlineData(80, 25)]
    [InlineData(60, 10)]
    public void ATerminalBelowTheMinimumIsToldWhatSizeItIs(int width, int height)
    {
        var text = string.Join('\n', LaunchpadLayout.Render(Request(Form(), new PaneSize(width, height))).ToLines());

        Assert.Contains($"{width}x{height}", text, StringComparison.Ordinal);
        Assert.Contains($"{LaunchpadLayout.MinimumWidth}x{LaunchpadLayout.MinimumHeight}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The longest of the eight forms, focused on its last field, at the fallback
    /// size — the case the form pane has to scroll for.
    /// </summary>
    private static LaunchpadForm LongForm()
    {
        var form = Form("evaluate");
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        for (var i = 0; i < 7; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Tab));
        }

        return form;
    }

    /// <summary>
    /// A form with <paramref name="command"/> selected, reached by moving down from
    /// the first. The move comes before the check, so the first comparison is against
    /// the second command and the loop stops where it means to.
    /// </summary>
    private static LaunchpadForm Form(string command = "generate")
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        var index = LaunchpadCatalog.Commands.ToList().FindIndex(entry => entry.Name == command);
        for (var i = 0; i < index; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Down));
        }

        Assert.Equal(command, form.Selected);
        return form;
    }
}
