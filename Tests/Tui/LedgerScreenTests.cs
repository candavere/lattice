using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The Ledger's state and its keys: which artifact is on show, which study, which
/// seed row, and whether the compare pane is up. Pure — no terminal, no clock, no
/// thread, no filesystem — so the whole of it is reachable from a list of keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cursors, one key each.</b> The study table and the per-seed pane each have
/// their own selection, because a reader scrolling a study's 50 seed rows is not
/// choosing a different suite. Up and down move the suite; the page and home/end keys
/// move the seed row. Nothing does both, so no key has two meanings.
/// </para>
/// <para>
/// <b>Compare only where there is something to compare.</b> With one artifact there
/// are no columns to put beside each other, so the toggle has nothing to do and says
/// so rather than showing one column against an empty space.
/// </para>
/// </remarks>
public class LedgerScreenTests
{
    [Fact]
    public void ItOpensOnTheFirstArtifactAndItsFirstStudyAndSeed()
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json")]);

        Assert.Equal(0, screen.ArtifactIndex);
        Assert.Equal(0, screen.StudyIndex);
        Assert.Equal(0, screen.SeedIndex);
        Assert.False(screen.Comparing);
        Assert.False(screen.HasQuit);
        Assert.Equal(0, screen.ExitCode);
    }

    [Fact]
    public void ThereIsNoSuchThingAsNoArtifactsToShow()
    {
        Assert.Throws<ArgumentException>(() => LedgerScreen.For([]));
    }

    [Theory]
    [InlineData(TuiKeyKind.Tab)]
    [InlineData(TuiKeyKind.Right)]
    public void TabAndRightMoveToTheNextArtifact(TuiKeyKind kind)
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json"), Artifact("c.json")]);

        screen.Apply(new TuiKey(kind));

        Assert.Equal(1, screen.ArtifactIndex);
        Assert.Equal("b.json", screen.ArtifactLabel);
    }

    [Theory]
    [InlineData(TuiKeyKind.BackTab)]
    [InlineData(TuiKeyKind.Left)]
    public void ShiftTabAndLeftMoveBackToThePreviousArtifact(TuiKeyKind kind)
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json")]);
        screen.Apply(new TuiKey(TuiKeyKind.Tab));

        screen.Apply(new TuiKey(kind));

        Assert.Equal(0, screen.ArtifactIndex);
    }

    /// <summary>
    /// The selection stays inside the list at both ends rather than wrapping: a reader
    /// who has reached the last artifact and presses Tab again has said they are done,
    /// not asked to go back to the first.
    /// </summary>
    [Fact]
    public void MovingPastTheEndStopsAtTheEnd()
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json")]);
        screen.Apply(new TuiKey(TuiKeyKind.Tab));
        screen.Apply(new TuiKey(TuiKeyKind.Tab));

        Assert.Equal(1, screen.ArtifactIndex);
    }

    [Theory]
    [InlineData(TuiKeyKind.Down)]
    [InlineData(TuiKeyKind.Character, 'j')]
    public void DownAndJMoveToTheNextStudy(TuiKeyKind kind, char glyph = '\0')
    {
        var screen = LedgerScreen.For([LedgerFixtures.TwoSuiteArtifact()]);

        screen.Apply(new TuiKey(kind, glyph));

        Assert.Equal(1, screen.StudyIndex);
        Assert.Equal("heldout", screen.StudySuite);
    }

    [Fact]
    public void UpAndKMoveBackToThePreviousStudy()
    {
        var screen = LedgerScreen.For([LedgerFixtures.TwoSuiteArtifact()]);
        screen.Apply(new TuiKey(TuiKeyKind.Down));

        screen.Apply(new TuiKey(TuiKeyKind.Character, 'k'));

        Assert.Equal(0, screen.StudyIndex);
        Assert.Equal("dev", screen.StudySuite);
    }

    /// <summary>
    /// An artifact with one study has nothing to move between, and a reader pressing a
    /// navigation key on a single-row list must not be told it is broken.
    /// </summary>
    [Fact]
    public void MovingPastTheOnlyStudyStopsThere()
    {
        var screen = LedgerScreen.For([Artifact()]);

        screen.Apply(new TuiKey(TuiKeyKind.Down));
        screen.Apply(new TuiKey(TuiKeyKind.Up));

        Assert.Equal(0, screen.StudyIndex);
    }

    [Fact]
    public void ThePageKeysScrollTheSeedRows()
    {
        var screen = LedgerScreen.For([Artifact(seeds: LedgerFixtures.Seeds(60))]);

        screen.Apply(new TuiKey(TuiKeyKind.PageDown));
        Assert.Equal(LedgerScreen.PageSize, screen.SeedIndex);

        screen.Apply(new TuiKey(TuiKeyKind.PageUp));
        Assert.Equal(0, screen.SeedIndex);
    }

    [Fact]
    public void HomeAndEndJumpToTheFirstAndLastSeedRow()
    {
        var screen = LedgerScreen.For([Artifact(seeds: LedgerFixtures.Seeds(60))]);
        screen.Apply(new TuiKey(TuiKeyKind.End));

        Assert.Equal(59, screen.SeedIndex);

        screen.Apply(new TuiKey(TuiKeyKind.Home));
        Assert.Equal(0, screen.SeedIndex);
    }

    /// <summary>
    /// The seed row is clamped to the study's own rows: switching to a suite with
    /// fewer seeds cannot leave the selection past its end.
    /// </summary>
    [Fact]
    public void ChangingArtifactAndStudyKeepsTheSeedRowInsideTheNewOnesRows()
    {
        var screen = LedgerScreen.For([
            Artifact("big.json", seeds: LedgerFixtures.Seeds(60)),
            LedgerFixtures.TwoSuiteArtifact("small.json"),
        ]);

        screen.Apply(new TuiKey(TuiKeyKind.End));
        Assert.Equal(59, screen.SeedIndex);

        screen.Apply(new TuiKey(TuiKeyKind.Tab));
        Assert.Equal(1, screen.SeedIndex);
    }

    [Fact]
    public void CComparesTwoArtifactsAndTogglesBack()
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json")]);

        screen.Apply(new TuiKey(TuiKeyKind.Character, 'c'));
        Assert.True(screen.Comparing);

        screen.Apply(new TuiKey(TuiKeyKind.Character, 'c'));
        Assert.False(screen.Comparing);
    }

    [Fact]
    public void OneArtifactHasNothingToCompareSoTheToggleIsRefused()
    {
        var screen = LedgerScreen.For([Artifact()]);

        screen.Apply(new TuiKey(TuiKeyKind.Character, 'c'));

        Assert.False(screen.Comparing);
        Assert.False(screen.CanCompare);
    }

    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void QAndItsUppercaseQuit(char glyph)
    {
        var screen = LedgerScreen.For([Artifact()]);

        screen.Apply(new TuiKey(TuiKeyKind.Character, glyph));

        Assert.True(screen.HasQuit);
    }

    [Fact]
    public void EscapeQuitsBecauseThereIsNothingOnScreenToLeave()
    {
        var screen = LedgerScreen.For([Artifact()]);

        screen.Apply(new TuiKey(TuiKeyKind.Escape));

        Assert.True(screen.HasQuit);
    }

    [Fact]
    public void CtrlCQuitsFromAnywhere()
    {
        var screen = LedgerScreen.For([Artifact(), Artifact("b.json")]);
        screen.Apply(new TuiKey(TuiKeyKind.Character, 'c'));

        screen.Apply(new TuiKey(TuiKeyKind.Interrupt));

        Assert.True(screen.HasQuit);
    }

    /// <summary>
    /// Once the reader has asked to leave, no further key moves anything: a keypress
    /// that arrives after the quit has been read is not a navigation request.
    /// </summary>
    [Fact]
    public void NothingMovesOnceTheReaderHasAskedToLeave()
    {
        var screen = LedgerScreen.For([Artifact("a.json"), Artifact("b.json")]);
        screen.Apply(new TuiKey(TuiKeyKind.Tab));
        screen.Apply(new TuiKey(TuiKeyKind.Character, 'q'));

        screen.Apply(new TuiKey(TuiKeyKind.Tab));

        Assert.Equal(1, screen.ArtifactIndex);
        Assert.True(screen.HasQuit);
    }

    /// <summary>
    /// The screen exposes what the layout reads and nothing else it could have to
    /// keep in step: the selected artifact's whole value, the study under it, and the
    /// seed row under that. A screen that handed the layout a summary of its own
    /// state would be a second place the numbers could disagree.
    /// </summary>
    [Fact]
    public void ItExposesTheSelectedArtifactStudyAndSeedAsTheModelHasThem()
    {
        var artifact = LedgerFixtures.TwoSuiteArtifact();
        var screen = LedgerScreen.For([artifact]);
        screen.Apply(new TuiKey(TuiKeyKind.Down));

        Assert.Same(artifact, screen.Artifact);
        Assert.Same(artifact.Studies[1], screen.Study);
        Assert.Same(artifact.Studies[1].PerSeed[0], screen.Seed);

        Assert.Equal(artifact.Studies, screen.Artifacts[0].Studies);
    }

    private static LedgerArtifact Artifact(
        string label = "results.json",
        IReadOnlyList<LedgerSeed>? seeds = null) =>
        LedgerFixtures.Artifact(label, seeds: seeds);
}
