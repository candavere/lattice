using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Presence groups for the two commands that take a path two ways: replay
/// (positional or --trajectory) and validate-scenario (positional or --out).
/// Empty confirmation must keep the form open with the CLI's own missing-input
/// text and start nothing; each alternative alone must run with the exact argv
/// the shell would use.
/// </summary>
public class ConsolePresenceTests
{
    [Fact]
    public void CatalogMarksOneOfEachPairRequiredNotBoth()
    {
        var replay = LaunchpadCatalog.Commands.Single(entry => entry.Name == "replay");
        Assert.Equal([LaunchpadCatalog.PositionalPathLabel, "--trajectory"], replay.RequiredAnyInputs);
        Assert.Empty(replay.RequiredFlags);
        Assert.All(replay.Fields.Where(field => field.Label is "--trajectory" or "path"), field => Assert.False(field.Required));

        var validate = LaunchpadCatalog.Commands.Single(entry => entry.Name == "validate-scenario");
        Assert.Equal([LaunchpadCatalog.PositionalPathLabel, "--out"], validate.RequiredAnyInputs);
        Assert.Empty(validate.RequiredFlags);
    }

    private static LaunchpadForm ReplayForm()
    {
        var replay = LaunchpadCatalog.Commands.Single(entry => entry.Name == "replay");
        return LaunchpadForm.For([replay]);
    }

    private static LaunchpadForm ValidateForm()
    {
        var entry = LaunchpadCatalog.Commands.Single(entry => entry.Name == "validate-scenario");
        return LaunchpadForm.For([entry]);
    }

    [Fact]
    public void EmptyReplayFormIsBlockedWithTheCliMissingText()
    {
        var form = ReplayForm();
        form.FocusField(0);

        Assert.False(form.CanRun);
        Assert.Equal(
            "missing trajectory path (pass a positional path or --trajectory <file>).",
            form.ValidationError);
        Assert.Equal(["replay"], form.Arguments);
    }

    [Fact]
    public void EmptyValidateScenarioFormIsBlockedWithTheCliMissingText()
    {
        var form = ValidateForm();
        form.FocusField(0);

        Assert.False(form.CanRun);
        Assert.Equal(
            "missing scenario path (pass a positional path or --out <file>).",
            form.ValidationError);
        Assert.Equal(["validate-scenario"], form.Arguments);
    }

    [Fact]
    public void ReplayPositionalAloneIsRunnableWithExactArgv()
    {
        var form = ReplayForm();
        Assert.True(form.TrySetValue(LaunchpadCatalog.PositionalPathLabel, "site/demo.jsonl"));

        Assert.True(form.CanRun);
        Assert.Null(form.ValidationError);
        Assert.Equal(["replay", "site/demo.jsonl"], form.Arguments);
    }

    [Fact]
    public void ReplayFlagAloneIsRunnableWithExactArgv()
    {
        var form = ReplayForm();
        Assert.True(form.TrySetValue("--trajectory", "site/demo.jsonl"));

        Assert.True(form.CanRun);
        Assert.Null(form.ValidationError);
        Assert.Equal(["replay", "--trajectory", "site/demo.jsonl"], form.Arguments);
    }

    [Fact]
    public void ReplayBothSuppliedIsBlockedWithTheCliUnexpectedText()
    {
        var form = ReplayForm();
        Assert.True(form.TrySetValue(LaunchpadCatalog.PositionalPathLabel, "a.jsonl"));
        Assert.True(form.TrySetValue("--trajectory", "b.jsonl"));

        Assert.False(form.CanRun);
        Assert.Equal("unexpected argument 'a.jsonl'.", form.ValidationError);
        Assert.Equal(["replay", "a.jsonl", "--trajectory", "b.jsonl"], form.Arguments);
    }

    [Fact]
    public void ReplayWhitespaceCountsAsMissing()
    {
        var form = ReplayForm();
        Assert.True(form.TrySetValue(LaunchpadCatalog.PositionalPathLabel, "   "));

        Assert.False(form.CanRun);
        Assert.Equal(
            "missing trajectory path (pass a positional path or --trajectory <file>).",
            form.ValidationError);
    }

    [Fact]
    public void ValidatePositionalAloneIsRunnableWithExactArgv()
    {
        var form = ValidateForm();
        Assert.True(form.TrySetValue(LaunchpadCatalog.PositionalPathLabel, "scenarios/collection-skirmish.json"));

        Assert.True(form.CanRun);
        Assert.Null(form.ValidationError);
        Assert.Equal(["validate-scenario", "scenarios/collection-skirmish.json"], form.Arguments);
    }

    [Fact]
    public void ValidateOutFlagAloneIsRunnableWithExactArgv()
    {
        var form = ValidateForm();
        Assert.True(form.TrySetValue("--out", "scenarios/collection-skirmish.json"));

        Assert.True(form.CanRun);
        Assert.Null(form.ValidationError);
        Assert.Equal(["validate-scenario", "--out", "scenarios/collection-skirmish.json"], form.Arguments);
    }

    [Fact]
    public void ValidateBothSuppliedIsBlockedWithTheCliUnexpectedText()
    {
        var form = ValidateForm();
        Assert.True(form.TrySetValue(LaunchpadCatalog.PositionalPathLabel, "a.json"));
        Assert.True(form.TrySetValue("--out", "b.json"));

        Assert.False(form.CanRun);
        Assert.Equal("unexpected argument 'a.json'.", form.ValidationError);
    }

    [Fact]
    public void ValidateWhitespaceCountsAsMissing()
    {
        var form = ValidateForm();
        Assert.True(form.TrySetValue("--out", "   "));

        Assert.False(form.CanRun);
        Assert.Equal(
            "missing scenario path (pass a positional path or --out <file>).",
            form.ValidationError);
    }
}
