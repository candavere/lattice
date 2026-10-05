using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The form as a value: which command is selected, which field has focus, what the
/// reader has typed, and what command line that adds up to. Every key a reader can
/// press is one of these tests, so nothing about the screen's behaviour rests on a
/// terminal.
/// </summary>
/// <remarks>
/// <para>
/// The two modes are the whole design. Navigation mode is where letters mean
/// commands; editing mode is where every letter means a character. Both halves are
/// asserted, because a screen that let a navigation shortcut fire while a field had
/// focus could not type a path, and one that let letters through in navigation mode
/// could not be steered.
/// </para>
/// <para>
/// The validation messages here are the CLI parser's own, asserted against
/// <c>CliApp</c>'s helpers rather than against a copy of them. A form that reported
/// its own wording would be a second validator that could disagree with the first
/// about what is runnable.
/// </para>
/// </remarks>
public class LaunchpadFormTests
{
    private static LaunchpadForm Form() => LaunchpadForm.For(LaunchpadCatalog.Commands);

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    private static LaunchpadForm Drive(LaunchpadForm form, params TuiKey[] keys)
    {
        foreach (var key in keys)
        {
            form.Apply(key);
        }

        return form;
    }

    private static LaunchpadForm Type(LaunchpadForm form, string text)
    {
        foreach (var glyph in text)
        {
            form.Apply(Character(glyph));
        }

        return form;
    }

    /// <summary>Selects a command by moving the selection down to it.</summary>
    private static LaunchpadForm Select(LaunchpadForm form, string name)
    {
        for (var i = 0; i < LaunchpadCatalog.Commands.Count; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.Down));
            if (form.Selected == name)
            {
                return form;
            }
        }

        Assert.Fail($"'{name}' is not in the command list.");
        return form;
    }

    [Fact]
    public void TheFirstCommandIsSelectedAndNoFieldIsFocusedToBeginWith()
    {
        var form = Form();

        Assert.Equal("generate", form.Selected);
        Assert.Equal(LaunchpadMode.Navigation, form.Mode);
        Assert.Null(form.FocusedField);
    }

    [Fact]
    public void TabEntersTheFormAndEscapeLeavesIt()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));

        Assert.Equal(LaunchpadMode.Editing, form.Mode);
        Assert.Equal("--seed", form.FocusedField);

        Drive(form, new TuiKey(TuiKeyKind.Escape));

        Assert.Equal(LaunchpadMode.Navigation, form.Mode);
        Assert.Null(form.FocusedField);
    }

    [Fact]
    public void TheFirstFieldIsTheRequiredOneAndItStartsEmpty()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));

        Assert.Equal("--seed", form.FocusedField);
        Assert.Equal("", form.Values["--seed"]);
        Assert.Equal("", form.Values["--min-fairness"]);
    }

    /// <summary>
    /// The message a required field produces is the parser's own, because the form
    /// is not a second validator: it asks the same helpers earlier.
    /// </summary>
    [Fact]
    public void AnEmptyRequiredFieldSaysWhatTheParserSays()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));

        Assert.Equal("missing required flag '--seed'.", form.ValidationError);
        Assert.False(form.CanRun);
    }

    /// <summary>
    /// Every printable character is typed, whatever it is. The characters that are
    /// commands outside editing are ordinary characters here, and a space among
    /// them — so a path with a space in it can be typed at all.
    /// </summary>
    [Fact]
    public void EveryPrintableCharacterIsTypedIntoTheFocusedField()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "qjk h.w");

        Assert.Equal("qjk h.w", form.Values["--seed"]);
    }

    [Fact]
    public void AFieldTheParserAcceptsBecomesRunnable()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "42");

        Assert.Null(form.ValidationError);
        Assert.True(form.CanRun);
    }

    /// <summary>
    /// The whole point of an explicit editing mode: the characters that steer the
    /// screen type like anything else while a field has focus, or no field could
    /// contain a path with a <c>q</c> in it.
    /// </summary>
    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    [InlineData('j')]
    [InlineData('k')]
    [InlineData('h')]
    [InlineData('w')]
    public void TheNavigationLettersTypeWhileAFieldIsFocusedAndDoNotSteer(char glyph)
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));
        var command = form.Selected;

        Type(form, glyph.ToString());

        Assert.Equal(LaunchpadMode.Editing, form.Mode);
        Assert.Equal(command, form.Selected);
        Assert.Equal(glyph.ToString(), form.Values["--seed"]);
        Assert.False(form.HasQuit);
    }

    [Fact]
    public void TheSameLettersSteerAndQuitOutsideEditing()
    {
        var form = Form();
        Assert.Equal("generate", form.Selected);

        Drive(form, Character('j'));
        Assert.Equal("simulate", form.Selected);
        Assert.False(form.HasQuit);

        Drive(form, Character('k'));
        Assert.Equal("generate", form.Selected);

        Drive(form, Character('q'));
        Assert.True(form.HasQuit);
    }

    [Fact]
    public void TheSelectionStopsAtBothEndsOfTheList()
    {
        var form = Form();

        for (var i = 0; i < 20; i++)
        {
            Drive(form, Character('k'));
        }

        Assert.Equal("generate", form.Selected);

        for (var i = 0; i < 40; i++)
        {
            Drive(form, Character('j'));
        }

        Assert.Equal("validate-scenario", form.Selected);
    }

    [Fact]
    public void ArrowsMoveTheSelectionTheWayJAndKDo()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Down));
        Assert.Equal("simulate", form.Selected);

        Drive(form, new TuiKey(TuiKeyKind.Up));
        Assert.Equal("generate", form.Selected);
    }

    /// <summary>
    /// Tab moves along the fields and cycles at the ends, so a form has no dead end
    /// in the middle of it and the last field is as reachable as the first.
    /// </summary>
    [Fact]
    public void TabMovesToTheNextFieldAndCyclesAtTheEnds()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));
        Assert.Equal("--seed", form.FocusedField);

        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Assert.Equal("--min-fairness", form.FocusedField);

        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Assert.Equal("--out", form.FocusedField);

        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Assert.Equal(LaunchpadCatalog.ExtraArgumentsLabel, form.FocusedField);

        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Assert.Equal("--seed", form.FocusedField);
    }

    /// <summary>
    /// Tab commits what was typed and stays in the form, so a reader filling a form
    /// in order never has to leave editing and come back.
    /// </summary>
    [Fact]
    public void TabCommitsAndMovesOnWithoutLeavingEditing()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "1");

        Drive(form, new TuiKey(TuiKeyKind.Tab));

        Assert.Equal(LaunchpadMode.Editing, form.Mode);
        Assert.Equal("--min-fairness", form.FocusedField);
        Assert.Equal("1", form.Values["--seed"]);
    }

    [Fact]
    public void EnterCommitsAndReturnsToNavigation()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "1");

        Drive(form, new TuiKey(TuiKeyKind.Enter));

        Assert.Equal(LaunchpadMode.Navigation, form.Mode);
        Assert.Null(form.FocusedField);
        Assert.Equal("1", form.Values["--seed"]);
    }

    /// <summary>
    /// Escape leaves editing and keeps the text. A reader who typed a path and
    /// pressed the wrong key must not lose the path.
    /// </summary>
    /// <summary>
    /// Escape leaves editing and keeps the text — including text the parser will
    /// refuse. A reader who typed a path and pressed the wrong key must not lose the
    /// path, and must be able to see why it will not run.
    /// </summary>
    [Fact]
    public void LeavingAFieldByEitherKeyKeepsWhatWasTyped()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "keepme");

        Drive(form, new TuiKey(TuiKeyKind.Escape));

        Assert.Equal("keepme", form.Values["--seed"]);
        Assert.Equal(
            "flag '--seed' expects an unsigned integer, got 'keepme'.",
            form.ValidationError);
    }

    [Fact]
    public void BackspaceAndDeleteEditTheFocusedFieldOnly()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "abc");

        Drive(form, new TuiKey(TuiKeyKind.Backspace));
        Assert.Equal("ab", form.Values["--seed"]);

        Drive(form, new TuiKey(TuiKeyKind.Delete));
        Assert.Equal("ab", form.Values["--seed"]);
        Assert.Equal("", form.Values["--min-fairness"]);
    }

    /// <summary>
    /// Delete removes at the caret and Backspace before it, so a caret parked in the
    /// middle of a value can edit either side of itself.
    /// </summary>
    [Fact]
    public void TheCaretMovesAndDeleteEatsWhatItIsPointingAt()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "abcd");

        Drive(form, new TuiKey(TuiKeyKind.Left), new TuiKey(TuiKeyKind.Left), new TuiKey(TuiKeyKind.Delete));

        Assert.Equal("abd", form.Values["--seed"]);
    }

    [Fact]
    public void HomeAndEndJumpToTheEndsOfTheFocusedField()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "abcd");

        Drive(form, new TuiKey(TuiKeyKind.Home), new TuiKey(TuiKeyKind.Delete));
        Assert.Equal("bcd", form.Values["--seed"]);

        Drive(form, new TuiKey(TuiKeyKind.End), new TuiKey(TuiKeyKind.Backspace));
        Assert.Equal("bc", form.Values["--seed"]);
    }

    /// <summary>
    /// A caret cannot leave its field. Clamping is the alternative and it is wrong:
    /// a Backspace at column zero that deleted the last character of the previous
    /// field would change a flag the reader was not looking at.
    /// </summary>
    [Fact]
    public void TheCaretIsClampedToTheFieldItIsIn()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));

        for (var i = 0; i < 10; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.Home), new TuiKey(TuiKeyKind.Backspace));
        }

        Assert.Equal("", form.Values["--seed"]);
        Assert.Equal("", form.Values["--min-fairness"]);
    }

    /// <summary>
    /// A value the real parser refuses is refused here, with the parser's own
    /// message. Each case is checked against the parser itself, so a change to the
    /// parser's wording cannot leave this test asserting a message the command no
    /// longer prints.
    /// </summary>
    [Theory]
    [InlineData("x", "flag '--seed' expects an unsigned integer, got 'x'.")]
    [InlineData("-1", "flag '--seed' expects an unsigned integer, got '-1'.")]
    [InlineData("1 2", "flag '--seed' expects an unsigned integer, got '1 2'.")]
    public void AValueTheRealParserRefusesIsRefusedHereWithTheSameMessage(string typed, string expected)
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), typed);

        Assert.Equal(expected, form.ValidationError);
        Assert.Equal(expected, RefusalOf(CliApp.ParseULong, typed, "--seed"));
        Assert.False(form.CanRun);
    }

    [Fact]
    public void AValueTheRealParserAcceptsIsAcceptedHere()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "42");

        Assert.Null(form.ValidationError);
    }

    /// <summary>
    /// A positive-integer field holds exactly what <c>ParsePositiveInt</c> holds:
    /// zero, a leading zero and a trailing letter are all refused.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("12x")]
    public void APositiveIntegerFieldRefusesEverythingTheRealParserRefuses(string value)
    {
        var form = Select(Form(), "simulate");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "42");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, value);

        var expected = $"flag '--steps' expects a positive integer, got '{value}'.";

        Assert.Equal(expected, form.ValidationError);
        Assert.Equal(expected, RefusalOf(CliApp.ParsePositiveInt, value, "--steps"));
    }

    /// <summary>
    /// The fairness threshold is held to the range the command holds it to, and the
    /// endpoints are inside it. The required seed is filled first, so the error under
    /// test is this field's and not the one above it.
    /// </summary>
    [Theory]
    [InlineData("0", null)]
    [InlineData("1", null)]
    [InlineData("0.5", null)]
    [InlineData("2", "flag '--min-fairness' expects a SpawnBiasIndex threshold in [0, 1], got '2'.")]
    [InlineData("abc", "flag '--min-fairness' expects a SpawnBiasIndex threshold in [0, 1], got 'abc'.")]
    public void AFairnessThresholdIsHeldToTheRangeTheCommandHoldsItTo(string value, string? expected)
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));
        Type(form, "42");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, value);

        Assert.Equal(expected, form.ValidationError);
    }

    /// <summary>
    /// A choice field refuses a value that is not one of its choices, in the
    /// command's own words.
    /// </summary>
    [Fact]
    public void AChoiceFieldRefusesAValueThatIsNotOneOfItsChoices()
    {
        var form = Select(Form(), "render");
        Drive(form, new TuiKey(TuiKeyKind.Tab), new TuiKey(TuiKeyKind.Tab));
        Type(form, "yaml");

        Assert.Equal("invalid --format 'yaml' (expected 'ascii' or 'svg').", form.ValidationError);
    }

    /// <summary>
    /// A choice field is as case-forgiving as the command, which folds the value
    /// before it compares it — and the command line keeps the reader's own casing,
    /// because the screen shows what will be run.
    /// </summary>
    [Fact]
    public void AChoiceFieldIsAsForgivingAsTheCommandIs()
    {
        var form = Select(Form(), "render");
        Drive(form, new TuiKey(TuiKeyKind.Tab), new TuiKey(TuiKeyKind.Tab));
        Type(form, "SVG");

        Assert.Null(form.ValidationError);
        Assert.Equal("lattice render --format SVG", form.CommandLine);
    }

    /// <summary>
    /// An empty optional field contributes nothing. A screen that emitted
    /// <c>--steps </c> for a field left blank would hand the parser an empty value
    /// and get a usage error the reader never caused.
    /// </summary>
    [Fact]
    public void AnEmptyOptionalFieldContributesNothing()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "1");

        Assert.Equal("lattice generate --seed 1", form.CommandLine);
    }

    /// <summary>
    /// A field left at the pre-filled default contributes nothing either: the
    /// command applies that default on its own, so repeating it would claim the
    /// reader chose it.
    /// </summary>
    [Fact]
    public void AFieldLeftAtItsDefaultContributesNothing()
    {
        var form = Select(Form(), "simulate");
        Drive(form, new TuiKey(TuiKeyKind.Tab), new TuiKey(TuiKeyKind.Tab));

        Assert.Equal("100", form.Values["--steps"]);
        Assert.Equal("lattice simulate", form.CommandLine);
    }

    [Fact]
    public void TheCommandLineNamesTheCommandItsFieldsWereTakenFrom()
    {
        var form = Select(Form(), "simulate");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "42");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "5");

        Assert.Equal("lattice simulate --seed 42 --steps 5", form.CommandLine);
    }

    /// <summary>
    /// A positional path is a bare token, so the path field comes first and the
    /// command line reads the way it would be typed.
    /// </summary>
    [Fact]
    public void APositionalPathIsAPlainTokenInTheCommandLine()
    {
        var form = Select(Form(), "replay");
        Drive(form, new TuiKey(TuiKeyKind.Tab));

        Assert.Equal(LaunchpadCatalog.PositionalPathLabel, form.FocusedField);

        Type(form, "site/demo.jsonl");

        Assert.Equal("lattice replay site/demo.jsonl", form.CommandLine);
    }

    /// <summary>
    /// The raw tail is passed through unexamined, which is what makes an unmodelled
    /// flag reachable. It is not validated: the parser decides, and a form that
    /// second-guessed the tail would refuse something the command runs.
    /// </summary>
    [Fact]
    public void TheRawTailIsAppendedUnexamined()
    {
        var form = Select(Form(), "replay");
        for (var i = 0; i < 5; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.Tab));
        }

        Assert.Equal(LaunchpadCatalog.ExtraArgumentsLabel, form.FocusedField);
        Type(form, "--verify");

        Assert.Equal("lattice replay --verify", form.CommandLine);
        Assert.True(form.CanRun);
    }

    /// <summary>
    /// The command line under the form is the screen's own claim about what it will
    /// run. It is built whether or not the form is runnable, or the reader would be
    /// shown a command line they were just told cannot be run.
    /// </summary>
    [Fact]
    public void TheCommandLineIsBuiltWhetherOrNotTheFormIsRunnable()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "x");

        Assert.Equal("lattice generate --seed x", form.CommandLine);
        Assert.False(form.CanRun);
    }

    /// <summary>
    /// Enter is both "commit" and "run", so which one it is depends on the mode and
    /// on whether the form can run. A runnable form in navigation mode asks to run;
    /// an unrunnable one puts the reader in the field that is wrong instead.
    /// </summary>
    [Fact]
    public void EnterFromNavigationRunsARunnableForm()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "1");
        Drive(form, new TuiKey(TuiKeyKind.Enter));

        Assert.False(form.WantsToRun);

        Drive(form, new TuiKey(TuiKeyKind.Enter));

        Assert.True(form.WantsToRun);
    }

    [Fact]
    public void EnterFromNavigationOnAnUnrunnableFormEditsTheFieldThatIsWrong()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "x");

        Drive(form, new TuiKey(TuiKeyKind.Escape));
        Drive(form, new TuiKey(TuiKeyKind.Enter));

        Assert.False(form.WantsToRun);
        Assert.Equal(LaunchpadMode.Editing, form.Mode);
        Assert.Equal("--seed", form.FocusedField);
    }

    /// <summary>
    /// The command line has to be runnable as written: no doubled spaces, no
    /// trailing one, and no doubled flag — which the parser refuses as a duplicate.
    /// </summary>
    [Fact]
    public void TheCommandLineHasNoDoubledOrTrailingSpaces()
    {
        var form = Select(Form(), "simulate");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "42");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "5");
        for (var i = 0; i < 3; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.Tab));
        }

        Assert.Equal("--out", form.FocusedField);
        Type(form, "report.md");

        Assert.Equal("lattice simulate --seed 42 --steps 5 --out report.md", form.CommandLine);
    }

    /// <summary>
    /// Changing the command resets the form. Carrying one command's values into
    /// another's form would hand the parser a flag that means something else there.
    /// </summary>
    [Fact]
    public void ChangingTheCommandResetsTheFormToThatCommandsOwnDefaults()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "1");
        Drive(form, new TuiKey(TuiKeyKind.Escape));

        Drive(form, Character('j'));
        Assert.Equal("simulate", form.Selected);
        Assert.Equal("", form.Values["--seed"]);
        Assert.Equal("100", form.Values["--steps"]);

        Drive(form, Character('k'));
        Assert.Equal("generate", form.Selected);
        Assert.Equal("", form.Values["--seed"]);

        // The flag that is not this command's is not carried across, so its absence
        // from the command line is not a gap the reader has to notice.
        Assert.DoesNotContain("--steps", form.CommandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coming back to a command re-reads that command's defaults rather than
    /// restoring a stale copy: the form is a view of one command's flags, not a
    /// store of what the reader once tried.
    /// </summary>
    [Fact]
    public void ReselectingACommandShowsItsDefaultsAgain()
    {
        var form = Select(Form(), "simulate");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "42");
        Drive(form, new TuiKey(TuiKeyKind.Tab));
        Type(form, "7");
        Drive(form, new TuiKey(TuiKeyKind.Escape));

        Drive(form, Character('j'), Character('k'));

        Assert.Equal("", form.Values["--seed"]);
        Assert.Equal("100", form.Values["--steps"]);
    }

    [Fact]
    public void MovingTheSelectionClearsTheFieldError()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "x");
        Assert.NotNull(form.ValidationError);

        Drive(form, new TuiKey(TuiKeyKind.Escape));
        Drive(form, Character('j'));

        Assert.Null(form.ValidationError);
    }

    /// <summary>
    /// The command list moves by a page on the page keys and stops at both ends:
    /// there are eight commands and no line to wrap round to.
    /// </summary>
    [Fact]
    public void PageKeysMoveTheSelectionByAPageAndClamp()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.PageDown));

        Assert.NotEqual("generate", form.Selected);

        for (var i = 0; i < 5; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.PageDown));
        }

        Assert.Equal("validate-scenario", form.Selected);

        for (var i = 0; i < 5; i++)
        {
            Drive(form, new TuiKey(TuiKeyKind.PageUp));
        }

        Assert.Equal("generate", form.Selected);
    }

    /// <summary>
    /// A field is one line long, so there is nothing for a page to reach and the
    /// keys must not move the caret out from under the reader.
    /// </summary>
    [Fact]
    public void PageKeysDoNothingInsideAField()
    {
        var form = Type(Drive(Form(), new TuiKey(TuiKeyKind.Tab)), "9");

        Drive(form, new TuiKey(TuiKeyKind.PageDown), new TuiKey(TuiKeyKind.PageUp));

        Assert.Equal("9", form.Values["--seed"]);
        Assert.Equal("--seed", form.FocusedField);
    }

    /// <summary>
    /// Ctrl-C leaves the program from either mode and reports success, because
    /// nothing failed: the reader asked to stop and the terminal is already back.
    /// </summary>
    [Fact]
    public void AnInterruptLeavesTheProgramFromEitherMode()
    {
        foreach (var editing in new[] { false, true })
        {
            var form = Drive(Form(), new TuiKey(TuiKeyKind.Tab));
            if (!editing)
            {
                Drive(form, new TuiKey(TuiKeyKind.Escape));
            }

            Drive(form, new TuiKey(TuiKeyKind.Interrupt));

            Assert.True(form.HasQuit);
            Assert.Equal(0, form.ExitCode);
        }
    }

    [Fact]
    public void QuittingWithQIsSuccessful()
    {
        var form = Drive(Form(), Character('q'));

        Assert.True(form.HasQuit);
        Assert.Equal(0, form.ExitCode);
    }

    [Fact]
    public void EscapeFromNavigationQuits()
    {
        var form = Drive(Form(), new TuiKey(TuiKeyKind.Escape));

        Assert.True(form.HasQuit);
    }

    /// <summary>
    /// The message the real parser raises for this value, read from the parser
    /// itself: this is the assertion that the form's wording is the command's.
    /// </summary>
    private static string? RefusalOf<T>(Func<string, string, T> parser, string text, string flag) =>
        Probe(() => parser(text, flag));

    private static string? Probe(Func<object?> parser)
    {
        try
        {
            parser();
            return null;
        }
        catch (UsageError error)
        {
            return error.Message;
        }
    }
}
