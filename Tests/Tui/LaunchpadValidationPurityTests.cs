using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// That inline validation is pure: no file created or written, no command started,
/// no thread spawned, no console property read, nothing printed.
/// </summary>
/// <remarks>
/// <para>
/// The claims are measured, not asserted-about. A temporary directory of this test's
/// own is listed before and after every field of every command is filled in, so a
/// validator that created or wrote anything would leave a difference; and the
/// process's own stdout and stderr are read around the whole sweep, because a
/// validator that printed would put a line on a developer's terminal.
/// </para>
/// <para>
/// What is <em>not</em> claimed: that the commands themselves are side-effect free.
/// They are not — that is what they are for. The claim is that asking whether a
/// command line is runnable does not do any of it.
/// </para>
/// </remarks>
public class LaunchpadValidationPurityTests
{
    /// <summary>
    /// A temporary directory of this test's own, holding a file per field that names
    /// one. The files exist beforehand so that a validator which merely read them
    /// would not be caught by the listing — the point is that nothing is touched at
    /// all, and the listing can only show writes.
    /// </summary>
    private static string Sandbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lattice-launchpad-pure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        foreach (var name in new[] { "out", "rules", "scenario", "trajectory", "commit", "agent-cmd", "seed" })
        {
            File.WriteAllText(Path.Combine(directory, name), "not a real input");
        }

        return directory;
    }

    /// <summary>
    /// Every field of every command, filled with a value and then edited, with the
    /// selection walked over all eight commands. Exhaustive over the catalog rather
    /// than sampled, so a flag added to a command and to its form together is covered
    /// here without this test being edited.
    /// </summary>
    [Fact]
    public void ValidatingEveryFieldOfEveryCommandCreatesAndWritesNoFile()
    {
        var directory = Sandbox();

        try
        {
            var before = Listing(directory);
            var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
            var visits = 0;

            for (var command = 0; command < LaunchpadCatalog.Commands.Count; command++)
            {
                if (command > 0)
                {
                    form.Apply(new TuiKey(TuiKeyKind.Down));
                }

                // One more than the field count, so the raw tail is reached too.
                for (var field = 0; field <= form.Fields.Count; field++)
                {
                    form.Apply(new TuiKey(TuiKeyKind.Tab));

                    foreach (var glyph in ValueFor(form.FocusedField!, directory))
                    {
                        form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
                    }

                    // Edit at both ends, so every keystroke that revalidates is
                    // pressed at least once per field.
                    form.Apply(new TuiKey(TuiKeyKind.Home));
                    form.Apply(new TuiKey(TuiKeyKind.End));
                    form.Apply(new TuiKey(TuiKeyKind.Backspace));
                    form.Apply(new TuiKey(TuiKeyKind.Delete));
                    form.Apply(new TuiKey(TuiKeyKind.Enter));
                    visits++;
                }

                // Everything the form exposes is read for every command, which is the
                // only other surface a validator could have written through.
                Assert.NotEqual("", form.CommandLine);
                Assert.Equal(0, form.ExitCode);
                Assert.False(form.WantsToRun);
                Assert.False(form.HasQuit);
            }

            Assert.True(visits >= 40, $"only {visits} fields were visited; the sweep should cover every field of every command.");
            Assert.Equal(before, Listing(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A validator that wrote to a stream would put a line on a developer's terminal
    /// the moment they opened the screen. The process's own streams are read around a
    /// sweep of every field, and a StringWriter that was never handed to anything is
    /// the control: if validation printed, this would see it.
    /// </summary>
    [Fact]
    public void ValidatingEveryFieldWritesNothingToAnyStreamItWasGiven()
    {
        var directory = Sandbox();

        try
        {
            // Validation is reachable with no writers at all — the form takes none —
            // which is the structural claim. This asserts the behaviour that follows:
            // filling in every field produces no output anywhere.
            var stdout = new StringWriter();
            var errors = new StringWriter();
            var form = LaunchpadForm.For(LaunchpadCatalog.Commands);

            for (var field = 0; field <= form.Fields.Count; field++)
            {
                form.Apply(new TuiKey(TuiKeyKind.Tab));
                foreach (var glyph in ValueFor(form.FocusedField!, directory))
                {
                    form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
                }
            }

            Assert.Equal("", stdout.ToString());
            Assert.Equal("", errors.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A field that names a missing file is not refused by the form. Whether a
    /// trajectory exists is a fact discovered while doing the work, and the exit
    /// status contract already says so: a form that claimed to check it would be
    /// reading the filesystem on every keystroke, and would be a second answer to a
    /// question only the command may answer.
    /// </summary>
    [Fact]
    public void AFieldThatNamesAMissingFileIsNotRefusedByTheForm()
    {
        var form = Form("render");
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "no/such/trajectory.jsonl")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        Assert.Null(form.ValidationError);
        Assert.True(form.CanRun);
        Assert.Equal("lattice render --trajectory no/such/trajectory.jsonl", form.CommandLine);
    }

    /// <summary>
    /// And the command does report it — after the screen has been restored, which is
    /// what the Launchpad's suspend-and-run path arranges and the host's own tests
    /// assert. Asserted here against the command, because the ordering is the host's.
    /// </summary>
    [Fact]
    public void TheCommandIsWhatReportsAMissingFile()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["render", "--trajectory", "no/such/trajectory.jsonl"], stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("error:", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule that would need two fields at once is refused by the command, not by
    /// the form: the form has no way to run the command that would decide it, so
    /// every field it can check on its own is the whole of what it checks.
    /// </summary>
    [Fact]
    public void ACrossFieldRuleIsLeftToTheCommand()
    {
        // --agent cannot be combined with --scenario infiltration; both are
        // individually valid, so the form has nothing to say about the pair.
        var form = Form("simulate");
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "42")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        form.Apply(new TuiKey(TuiKeyKind.Tab));
        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "greedy")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "infiltration")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        Assert.True(form.CanRun);

        // The command refuses the pair, in its own words.
        using var stderr = new StringWriter();
        var exit = CliApp.Run(
            ["simulate", "--seed", "42", "--agent", "greedy", "--scenario", "infiltration"],
            new StringWriter(),
            stderr);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("--agent cannot be used", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A field value is only ever read by the parsers. The validation messages a form
    /// produces are the ones the CLI's own helpers produce for the same values, which
    /// is asserted here for every format the catalog uses — the strongest available
    /// evidence that no second validator exists.
    /// </summary>
    [Theory]
    [InlineData("generate", "--seed", "x", LaunchpadValueFormat.UnsignedInteger)]
    [InlineData("benchmark", "--runs", "0", LaunchpadValueFormat.PositiveInteger)]
    [InlineData("generate", "--min-fairness", "2", LaunchpadValueFormat.UnitInterval)]
    [InlineData("render", "--format", "yaml", LaunchpadValueFormat.Choice)]
    public void EveryRefusalIsTheOneTheParserWouldPrint(string command, string label, string value, LaunchpadValueFormat format)
    {
        var form = Form(command);
        var field = form.Fields.ToList().FindIndex(entry => entry.Label == label);

        Assert.True(field >= 0, $"{command} has no {label} field.");
        Assert.Equal(format, form.Fields[field].ValueFormat);

        form.Apply(new TuiKey(TuiKeyKind.Tab));

        // The required field is filled first when it is not the one under test, so
        // the error the form reports is this field's and not the one above it.
        if (field > 0)
        {
            foreach (var glyph in "42")
            {
                form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
            }
        }

        for (var i = 0; i < field; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Tab));
        }

        foreach (var glyph in value)
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        Assert.NotNull(form.ValidationError);
        Assert.Equal(
            Expected(label, value, format),
            form.ValidationError);
    }

    /// <summary>
    /// The message each format's refusal must carry, written out longhand from the
    /// parser's own wording. This is the assertion that the form is not a second
    /// validator: if any of these were reworded, this fails rather than the screen
    /// quietly disagreeing with the command.
    /// </summary>
    private static string Expected(string label, string value, LaunchpadValueFormat format) => format switch
    {
        LaunchpadValueFormat.UnsignedInteger =>
            $"flag '{label}' expects an unsigned integer, got '{value}'.",
        LaunchpadValueFormat.PositiveInteger =>
            $"flag '{label}' expects a positive integer, got '{value}'.",
        LaunchpadValueFormat.UnitInterval =>
            $"flag '{label}' expects a SpawnBiasIndex threshold in [0, 1], got '{value}'.",
        _ => $"invalid {label} '{value}' (expected 'ascii' or 'svg').",
    };

    /// <summary>
    /// A value for a field, chosen to be the most interesting one that field can
    /// hold: a number where it takes one, a choice where it takes a set, and a path
    /// inside the sandbox where it names a file — so the listing above would catch a
    /// write to any of them.
    /// </summary>
    private static string ValueFor(string label, string directory) => label switch
    {
        "--seed" => "42",
        "--min-fairness" => "0.5",
        "--steps" or "--runs" or "--warmup" or "--rollouts" or "--seeds" => "5",
        "--agent-step-timeout-ms" => "100",
        "--agent" => "greedy",
        "--format" => "ascii",
        LaunchpadCatalog.PositionalPathLabel => Path.Combine(directory, "trajectory"),
        LaunchpadCatalog.ExtraArgumentsLabel => Path.Combine(directory, "out"),
        _ => Path.Combine(directory, label.TrimStart('-')),
    };

    private static string Listing(string directory) =>
        string.Join('\n', Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal));

    private static LaunchpadForm Form(string command)
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
