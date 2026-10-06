using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// What the setup screen offers: that every command <c>lattice</c> can run is on
/// the list exactly once, with the flags that command really takes, the marks
/// that say which of them are required, and the defaults that command really uses.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is asserted against the command line itself rather than against a
/// copy of it, because a screen that offers a flag the parser refuses — or omits
/// one it requires — is worse than no screen. The extra-arguments field exists so
/// that even a flag nobody gave a field is still reachable.
/// </para>
/// </remarks>
public class LaunchpadCatalogTests
{
    /// <summary>
    /// The eight commands the CLI dispatches, in the order the usage text lists them.
    /// </summary>
    private static readonly string[] Commands =
    [
        "generate", "simulate", "render", "analyze", "replay", "benchmark", "evaluate", "validate-scenario",
    ];

    /// <summary>
    /// The screens that are not top-level commands of their own, reached through
    /// <c>lattice tui</c>. Listed after the commands so the list reads as "the commands,
    /// then the screens" — which is the order a reader meets them in.
    /// </summary>
    private static readonly string[] Viewers = ["ledger"];

    /// <summary>
    /// Every command the usage text lists is on the list exactly once, and so is every
    /// viewer <c>lattice tui</c> offers. Asserted as two sets rather than one list
    /// because they are two different things: the first eight are commands a reader can
    /// type on their own, and the rest are subcommands of <c>lattice tui</c>.
    /// </summary>
    [Fact]
    public void EveryCommandTheUsageTextListsIsOnTheScreenOnce()
    {
        Assert.Equal(
            [.. Commands, .. Viewers],
            LaunchpadCatalog.Commands.Select(entry => entry.Name).ToArray());
    }

    /// <summary>
    /// A viewer that is not a top-level command is reached through <c>lattice tui</c>,
    /// and the catalog says so rather than leaving the reader to type a line that would
    /// report an unknown command.
    /// </summary>
    [Fact]
    public void EveryViewerIsReachedThroughTheTuiPrefixAndNoCommandIs()
    {
        foreach (var name in Viewers)
        {
            var viewer = Assert.Single(LaunchpadCatalog.Commands, entry => entry.Name == name);
            Assert.Equal(LaunchpadCatalog.TuiPrefix, viewer.Prefix.ToArray());
        }

        foreach (var name in Commands)
        {
            Assert.Empty(LaunchpadCatalog.Commands.Single(entry => entry.Name == name).Prefix);
        }
    }

    [Fact]
    public void EveryCommandHasAOneLineDescription()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            Assert.NotEqual("", entry.Summary);
            Assert.DoesNotContain('\n', entry.Summary);
        }
    }

    /// <summary>
    /// The flags offered must be exactly the flags the command's own parser
    /// accepts. Both sets are read from the code under test, so a flag added to a
    /// command and not to its form fails here rather than at run time.
    /// </summary>
    [Fact]
    public void EveryCommandOffersExactlyTheFlagsItsParserAccepts()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            var offered = entry.Fields
                .Where(field => field.Kind == LaunchpadFieldKind.Flag)
                .Select(field => field.Label)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                entry.AllowedFlags.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                offered);
        }
    }

    /// <summary>
    /// A required flag the form has no field for would leave a reader unable to
    /// fill it in, so the mark and the field are asserted together.
    /// </summary>
    [Fact]
    public void EveryRequiredFlagHasARequiredField()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            var required = entry.Fields
                .Where(field => field.Kind == LaunchpadFieldKind.Flag && field.Required)
                .Select(field => field.Label)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(entry.RequiredFlags.OrderBy(name => name, StringComparer.Ordinal).ToArray(), required);
        }
    }

    /// <summary>
    /// A positional path is a field too, and not a flag: the commands that take
    /// one take it as a bare token rather than as <c>--name value</c>.
    /// </summary>
    [Fact]
    public void TheCommandsThatTakeAPositionalPathSaySo()
    {
        Assert.True(LaunchpadCatalog.Commands.Single(entry => entry.Name == "replay").TakesPositionalPath);
        Assert.True(LaunchpadCatalog.Commands.Single(entry => entry.Name == "validate-scenario").TakesPositionalPath);
        Assert.True(LaunchpadCatalog.Commands.Single(entry => entry.Name == "ledger").TakesPositionalPath);

        foreach (var entry in LaunchpadCatalog.Commands.Where(entry => !entry.TakesPositionalPath))
        {
            Assert.DoesNotContain(entry.Fields, field => field.Kind == LaunchpadFieldKind.PositionalPath);
        }
    }

    /// <summary>
    /// A screen that takes more than one artifact has a field for each, because a field
    /// is addressed by its label and two paths cannot share one. The first is required
    /// because the command refuses without any at all.
    /// </summary>
    [Fact]
    public void TheViewerOfSeveralArtifactsHasAFieldForEach()
    {
        var ledger = LaunchpadCatalog.Commands.Single(entry => entry.Name == "ledger");

        var paths = ledger.Fields.Where(field => field.Kind == LaunchpadFieldKind.PositionalPath).ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Equal(LaunchpadCatalog.PositionalPathLabel, paths[0].Label);
        Assert.Equal(LaunchpadCatalog.SecondPathLabel, paths[1].Label);

        Assert.True(paths[0].Required);
        Assert.False(paths[1].Required);

        // The two labels are distinct, which is what keeps the second field addressable.
        Assert.NotEqual(paths[0].Label, paths[1].Label);
    }

    /// <summary>
    /// The defaults the form pre-fills are the defaults the command applies when
    /// the flag is absent, so a reader sees the command's own behaviour rather than
    /// an invented one. A field with no default must start empty: a pre-filled
    /// guess would be a claim the parser never made.
    /// </summary>
    [Fact]
    public void EveryDefaultShownIsTheDefaultTheCommandApplies()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            foreach (var field in entry.Fields.Where(field => field.Default is not null))
            {
                Assert.NotEqual("", field.Default);
                Assert.Equal(field.Default, entry.DefaultFor(field.Label));
            }
        }
    }

    /// <summary>
    /// Every command carries one raw field, so no flag the catalog does not model
    /// is unreachable from the screen.
    /// </summary>
    [Fact]
    public void EveryCommandHasExactlyOneExtraArgumentsField()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            var extra = Assert.Single(entry.Fields, field => field.Kind == LaunchpadFieldKind.ExtraArguments);
            Assert.False(extra.Required);

            // No default: a pre-filled tail would be an argument the reader did not
            // ask for, and the command line under the form would claim it.
            Assert.True(string.IsNullOrEmpty(extra.Default), $"'{extra.Default}' is a pre-filled tail.");
        }
    }

    /// <summary>
    /// A numeric field has to say how it will be read, because that is what decides
    /// whether <c>007</c> and a negative number are accepted.
    /// </summary>
    [Fact]
    public void EveryNumericFieldNamesItsFormat()
    {
        foreach (var entry in LaunchpadCatalog.Commands)
        {
            foreach (var field in entry.Fields.Where(field =>
                         field.Kind == LaunchpadFieldKind.Flag && field.ValueFormat != LaunchpadValueFormat.Text))
            {
                Assert.NotEqual(LaunchpadValueFormat.Text, field.ValueFormat);
            }
        }
    }

    [Fact]
    public void TheSeedFieldIsReadAsAnUnsignedIntegerEverywhereItIsOffered()
    {
        foreach (var entry in LaunchpadCatalog.Commands.Where(entry => entry.RequiredFlags.Contains("--seed")))
        {
            var seed = Assert.Single(entry.Fields, field => field.Label == "--seed");
            Assert.Equal(LaunchpadValueFormat.UnsignedInteger, seed.ValueFormat);
        }
    }
}
