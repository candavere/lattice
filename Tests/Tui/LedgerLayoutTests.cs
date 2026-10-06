using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// What the Ledger draws: the panes it puts on the screen, the columns each table
/// has and where they stand, the protocol check the compare pane makes, and that
/// nothing is ever drawn past a pane's own border.
/// </summary>
/// <remarks>
/// <para>
/// Every expected row here is composed from the model the fixture built — the numbers
/// are written out column by column against the column table below — so a renderer
/// that moved a column or invented a value fails rather than being agreed with. The
/// whole-frame goldens in <see cref="LedgerGoldens"/> pin the rest.
/// </para>
/// <para>
/// The panes a test reads are the ones the same call that produced the frame produced,
/// so an assertion can never be aimed at a pane the frame did not have.
/// </para>
/// <para>
/// <b>No claim words.</b> The screen shows the artifact's own verdict and its own
/// numbers, and a protocol check between artifacts. It never says one is better, and
/// the goldens are scanned for those words as well.
/// </para>
/// </remarks>
public class LedgerLayoutTests
{
    // ---------------------------------------------------------------------
    // The screen's own geometry.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(100, 30)]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    [InlineData(60, 18)]
    [InlineData(40, 12)]
    public void EveryRowIsAsWideAsTheTerminalAndNothingLeaksPastItsBorder(int width, int height)
    {
        var cells = LedgerLayout.Render(Request(width, height));

        Assert.Equal(width, cells.Width);
        Assert.Equal(height, cells.Height);

        var lines = cells.ToLines();
        Assert.All(lines, row => Assert.Equal(width, row.Length));

        if (width < 2 || height < 2)
        {
            return;
        }

        Assert.Equal(new string(BorderGlyphs.Rounded.TopLeft, 1), lines[0][..1]);
        Assert.Equal(new string(BorderGlyphs.Rounded.TopRight, 1), lines[0][^1..]);
        Assert.Equal(new string(BorderGlyphs.Rounded.BottomLeft, 1), lines[^1][..1]);
        Assert.Equal(new string(BorderGlyphs.Rounded.BottomRight, 1), lines[^1][^1..]);

        foreach (var row in lines[1..^1])
        {
            Assert.Equal(new string(BorderGlyphs.Rounded.Vertical, 1), row[..1]);
            Assert.Equal(new string(BorderGlyphs.Rounded.Vertical, 1), row[^1..]);
        }
    }

    /// <summary>
    /// Every pane is a closed box. A pane that was only drawn on three sides would
    /// still read as a box of text in a screenshot, so each corner is checked against
    /// the layout's own glyph set rather than against one hard-coded vocabulary.
    /// </summary>
    [Theory]
    [InlineData(100, 30)]
    [InlineData(80, 25)]
    [InlineData(60, 18)]
    public void EveryPaneIsClosedOnAllFourSides(int width, int height)
    {
        var size = new PaneSize(width, height);
        var artifact = LedgerFixtures.Artifact(agent: LedgerFixtures.Agent());
        var frame = Draw([artifact], size: size);

        var panes = LedgerLayout.Panes(size, hasAgent: true, studyCount: 1);
        Assert.NotEmpty(panes);

        foreach (var pane in panes)
        {
            var top = Slice(frame.Lines[pane.Area.Y], pane.Area.X, pane.Area.Width);
            var bottom = frame.Lines[pane.Area.Y + pane.Area.Height - 1];

            Assert.Contains(top[0], LedgerLayout.BorderGlyphSet);
            Assert.Contains(top[^1], LedgerLayout.BorderGlyphSet);
            Assert.Contains(bottom[pane.Area.X], LedgerLayout.BorderGlyphSet);
            Assert.Contains(bottom[pane.Area.X + pane.Area.Width - 1], LedgerLayout.BorderGlyphSet);
            Assert.Equal(pane.Area.Width, top.Length);
        }
    }

    /// <summary>
    /// A terminal too small for the panes it would otherwise draw gets the panes that
    /// fit, in reading order, and nothing is drawn past its own band — which is the
    /// failure a too-short pane produces: a content row landing on the border below it.
    /// </summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(30, 9)]
    [InlineData(20, 7)]
    public void NoPaneIsEverDrawnPastItsOwnBottomBorder(int width, int height)
    {
        var artifact = LedgerFixtures.Artifact(agent: LedgerFixtures.Agent());
        var size = new PaneSize(width, height);
        var frame = Draw([artifact], size: size);

        foreach (var pane in LedgerLayout.Panes(size, hasAgent: true, studyCount: 1))
        {
            var bottom = pane.Area.Y + pane.Area.Height - 1;
            Assert.True(bottom < height, $"the {pane.Kind} band reaches row {bottom} of a {height}-row terminal.");

            for (var row = pane.Area.Y + 1; row < bottom; row++)
            {
                var within = frame.Lines[row][pane.Area.X..(pane.Area.X + pane.Area.Width)];
                Assert.Equal(new string(BorderGlyphs.Rounded.Vertical, 1), within[..1]);
                Assert.Equal(new string(BorderGlyphs.Rounded.Vertical, 1), within[^1..]);
            }
        }
    }

    [Fact]
    public void TheHeaderNamesTheScreenTheArtifactAndWhichOfThemItIs()
    {
        var frame = Draw([LedgerFixtures.Artifact(), LedgerFixtures.Artifact("second.json")]);

        // "LATTICE TUI  ledger", then the position, then the selected artifact's own
        // file name: the reader has to be able to say which file they are looking at.
        Assert.Contains("LATTICE TUI", frame.Lines[0], StringComparison.Ordinal);
        Assert.Contains("ledger", frame.Lines[0], StringComparison.Ordinal);
        Assert.Contains("1/2", frame.Lines[0], StringComparison.Ordinal);
        Assert.Contains("results.json", frame.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("second.json", frame.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// One artifact needs no position, because there is nothing to be ambiguous about.
    /// </summary>
    [Fact]
    public void OneArtifactIsNamedWithoutAPosition()
    {
        var frame = Draw([LedgerFixtures.Artifact()]);

        Assert.Contains("results.json", frame.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("1/1", frame.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlsAreStatedOnOneRowAndDoNotReachTheCorner()
    {
        var frame = Draw([LedgerFixtures.Artifact()]);
        var hints = frame.Lines[^LedgerLayout.KeyHintRowFromBottom];

        Assert.Contains("tab", hints, StringComparison.Ordinal);
        Assert.Contains("compare", hints, StringComparison.Ordinal);
        Assert.Contains("quit", hints, StringComparison.Ordinal);

        // The hints sit on the screen's own bottom border, so a line long enough to
        // reach the edge would overwrite the corner and the screen would stop looking
        // closed. Asserted at the narrowest terminal the notices are drawn at.
        var narrow = Draw([LedgerFixtures.Artifact()], size: LedgerFixtures.Narrow);
        Assert.Equal(
            new string(BorderGlyphs.Rounded.BottomRight, 1),
            narrow.Lines[^1][^1..]);
    }

    // ---------------------------------------------------------------------
    // The summary pane.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheSummaryPaneStatesTheProvenanceWithItsScope()
    {
        var artifact = LedgerFixtures.Artifact();
        var frame = Draw([artifact]);
        var text = Text(frame, LedgerPaneKind.Summary);

        // Each fact is labelled by the word the artifact used for it, so a number is
        // never on screen without saying what it is a number of.
        Assert.Contains($"commit {artifact.CommitSha}", text, StringComparison.Ordinal);
        Assert.Contains("created 2026-09-25 19:41:20Z", text, StringComparison.Ordinal);
        Assert.Contains("runtime TestRuntime 1.0", text, StringComparison.Ordinal);
        Assert.Contains("os TestOs 1.0", text, StringComparison.Ordinal);
        Assert.Contains("cores 4", text, StringComparison.Ordinal);
        Assert.Contains("arch X64", text, StringComparison.Ordinal);
        Assert.Contains("external agent none", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An artifact that claims no commit says so rather than showing a blank, which
    /// would read as a commit that is somehow empty.
    /// </summary>
    [Fact]
    public void AnArtifactThatClaimsNoCommitSaysSo()
    {
        var frame = Draw([LedgerFixtures.Artifact(commit: null)]);

        Assert.Contains("commit none", Text(frame, LedgerPaneKind.Summary), StringComparison.Ordinal);
    }

    /// <summary>
    /// The external-agent band exists only when there is one to describe. An in-process
    /// run has no agent, and a band of zeroes would say it failed nothing.
    /// </summary>
    [Fact]
    public void TheAgentPaneIsOnlyOnScreenWhenTheArtifactHasAnAgentBlock()
    {
        Assert.DoesNotContain(
            LedgerLayout.Panes(LedgerFixtures.Full, hasAgent: false, studyCount: 1),
            pane => pane.Kind == LedgerPaneKind.Agent);

        Assert.Contains(
            LedgerLayout.Panes(LedgerFixtures.Full, hasAgent: true, studyCount: 1),
            pane => pane.Kind == LedgerPaneKind.Agent);
    }

    [Fact]
    public void TheAgentPaneStatesTheCommandTheLimitsTheFailuresTheVoidRunsAndTheForfeits()
    {
        var frame = Draw([LedgerFixtures.Artifact(agent: LedgerFixtures.Agent())]);
        var text = Text(frame, LedgerPaneKind.Agent);

        Assert.Contains("command python3 agent.py", text, StringComparison.Ordinal);
        Assert.Contains("step 5000ms", text, StringComparison.Ordinal);
        Assert.Contains("match 20000ms", text, StringComparison.Ordinal);
        Assert.Contains("void runs 3", text, StringComparison.Ordinal);
        Assert.Contains("timeout_step 1", text, StringComparison.Ordinal);
        Assert.Contains("agent_crashed 2", text, StringComparison.Ordinal);
        Assert.Contains("forfeits 2", text, StringComparison.Ordinal);
        Assert.Contains("seed 1001 seat 0 (timeout_step)", text, StringComparison.Ordinal);
        Assert.Contains("external agent scored", Text(frame, LedgerPaneKind.Summary), StringComparison.Ordinal);
    }

    /// <summary>
    /// An agent that failed no match and forfeited none is stated as such, rather than
    /// by an empty list a reader has to interpret.
    /// </summary>
    [Fact]
    public void AnAgentWithNoFailuresAndNoForfeitsSaysNoneRecorded()
    {
        var agent = new LedgerAgent([], 0, ["agent"], new LedgerAgentLimits(10, 40), []);
        var frame = Draw([LedgerFixtures.Artifact(agent: agent)]);
        var text = Text(frame, LedgerPaneKind.Agent);

        Assert.Contains("failures none recorded", text, StringComparison.Ordinal);
        Assert.Contains("forfeits 0", text, StringComparison.Ordinal);
        Assert.Contains("void runs 0", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The study table.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The study table's columns, written out longhand rather than read from the layout,
    /// so a column that moves, changes width or changes alignment fails against the
    /// number the layout is supposed to keep. A right-aligned column's heading ends on
    /// its right edge; a left-aligned one's begins on its left edge.
    /// <para>
    /// The order is the reading order and, on a narrow terminal, the order of loss: the
    /// last column is the first to be cut, which is why the outcome rates come after
    /// the verdict — a rate is recoverable from the count beside it, and a verdict is
    /// not recoverable at all.
    /// </para>
    /// </summary>
    private static readonly (int Offset, int Width, string Header, bool Right)[] StudyColumns =
    [
        (3, 9, "SUITE", false),
        (13, 5, "SEEDS", true),
        (19, 5, "MATCH", true),
        (25, 7, "MEAN", true),
        (33, 13, "95% CI", true),
        (47, 10, "VERDICT", false),
        (58, 9, "W/D/L/T", true),
        (68, 19, "WIN%/D%/L%/T%", true),
        (88, 5, "SAT", true),
    ];

    /// <summary>The per-seed table's columns, in the same longhand way.</summary>
    private static readonly (int Offset, int Width, string Header, bool Right)[] SeedColumns =
    [
        (3, 9, "SEED", true),
        (13, 5, "POL0", true),
        (19, 5, "BASE0", true),
        (25, 5, "POL1", true),
        (31, 5, "BASE1", true),
        (37, 7, "DELTA", true),
        (45, 4, "M0", true),
        (50, 4, "M1", true),
    ];

    [Fact]
    public void EveryStudyColumnHeadingStandsOverItsOwnColumn()
    {
        var frame = Draw([LedgerFixtures.Artifact()]);
        var header = Text(frame, LedgerPaneKind.Studies, firstContentRow: 0);

        foreach (var (offset, width, text, right) in StudyColumns)
        {
            var found = header.IndexOf(text, StringComparison.Ordinal);
            Assert.True(found >= 0, $"the study table has no '{text}' heading.");

            if (right)
            {
                Assert.Equal(offset + width, found + text.Length);
            }
            else
            {
                Assert.Equal(offset, found);
            }
        }
    }

    [Fact]
    public void EveryPerSeedColumnHeadingStandsOverItsOwnColumn()
    {
        var frame = Draw([LedgerFixtures.Artifact()]);
        var header = Text(frame, LedgerPaneKind.Detail, firstContentRow: 0);

        foreach (var (offset, width, text, _) in SeedColumns)
        {
            Assert.Equal(offset + width, header.IndexOf(text, StringComparison.Ordinal) + text.Length);
        }
    }

    /// <summary>
    /// One row per suite, carrying the seed and match counts, the mean delta with its
    /// interval, the artifact's own verdict, the four outcome counts with their rates,
    /// and the contention saturation — each composed here from the fixture's own
    /// statistics rather than read back out of the frame.
    /// </summary>
    [Fact]
    public void AStudyRowIsComposedFromTheArtifactsOwnStatistics()
    {
        var artifact = LedgerFixtures.Artifact();
        var study = artifact.Studies[0];
        var frame = Draw([artifact]);
        var row = Text(frame, LedgerPaneKind.Studies, firstContentRow: 1);

        Assert.Equal(study.Suite, At(row, 3, 9).Trim());
        Assert.Equal($"{study.Statistics.Seeds}", At(row, 13, 5).Trim());
        Assert.Equal($"{study.Statistics.Matches}", At(row, 19, 5).Trim());

        // The mean and its interval as this project formats them, written out here.
        Assert.Equal("1", At(row, 25, 7).Trim());
        Assert.Equal("[0,0]", At(row, 33, 13).Trim());
        Assert.Equal("Pass", At(row, 47, 10).Trim());
    }

    /// <summary>
    /// The verdict is the artifact's own word for it, read from the sentence the
    /// artifact recorded. A study the decision rule declined to grade is not a failure,
    /// and only reading the sentence can tell the two apart.
    /// </summary>
    [Fact]
    public void AStudyTheDecisionRuleDidNotGradeIsNotDrawnAsAFailure()
    {
        var frame = Draw([LedgerFixtures.Artifact(
            decision: "Not graded: 3 seeds is below the 30-seed floor of the decision rule (the canonical suites run 50).")]);
        var row = Text(frame, LedgerPaneKind.Studies, firstContentRow: 1);

        Assert.Equal("Not graded", At(row, 47, 10).Trim());
    }

    /// <summary>
    /// The outcome counts and their rates are both shown, because the counts are what
    /// the rates were computed from and a reader checking a rate needs the count.
    /// </summary>
    [Fact]
    public void TheOutcomeCountsAndTheirRatesAreBothOnTheRow()
    {
        var statistics = LedgerFixtures.Artifact().Studies[0].Statistics;
        var row = Text(Draw([LedgerFixtures.Artifact()]), LedgerPaneKind.Studies, firstContentRow: 1);

        Assert.Equal(
            $"{statistics.Wins}/{statistics.Draws}/{statistics.Losses}/{statistics.Timeouts}",
            At(row, 58, 9).Trim());

        Assert.Equal(
            $"{statistics.WinRate:0.0}/{statistics.DrawRate:0.0}/{statistics.LossRate:0.0}/{statistics.TimeoutRate:0.0}",
            At(row, 68, 19).Trim());

        Assert.Equal($"{statistics.MeanContentionSaturation:0.0}", At(row, 88, 5).Trim());
    }

    /// <summary>
    /// A number is formatted the same way whatever the terminal's locale is: a table of
    /// aligned columns stops aligning the moment a decimal separator moves.
    /// </summary>
    [Fact]
    public void NumbersAreFormattedInvariantlySoTheColumnsStayAligned()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var row = Text(Draw([LedgerFixtures.Artifact()]), LedgerPaneKind.Studies, firstContentRow: 1);

            Assert.Equal("1", At(row, 25, 7).Trim());
            Assert.Contains(".", At(row, 68, 19), StringComparison.Ordinal);
            Assert.DoesNotContain(",", At(row, 68, 19), StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheStudyOnShowIsMarked()
    {
        var frame = Draw([LedgerFixtures.TwoSuiteArtifact()]);

        Assert.Equal("> ", Text(frame, LedgerPaneKind.Studies, firstContentRow: 1)[..2]);
        Assert.Equal("  ", Text(frame, LedgerPaneKind.Studies, firstContentRow: 2)[..2]);
    }

    // ---------------------------------------------------------------------
    // The per-seed pane.
    // ---------------------------------------------------------------------

    [Fact]
    public void APerSeedRowCarriesTheSeedTheFourSeatScoresTheDeltaAndBothOutcomes()
    {
        var artifact = LedgerFixtures.Artifact();
        var seed = artifact.Studies[0].PerSeed[0];
        var row = Text(Draw([artifact]), LedgerPaneKind.Detail, firstContentRow: 1);

        Assert.Equal($"{seed.Seed}", At(row, 3, 9).Trim());
        Assert.Equal($"{seed.PolicyScoreAtSeat0}", At(row, 13, 5).Trim());
        Assert.Equal($"{seed.BaselineScoreAtSeat0}", At(row, 19, 5).Trim());
        Assert.Equal($"{seed.PolicyScoreAtSeat1}", At(row, 25, 5).Trim());
        Assert.Equal($"{seed.BaselineScoreAtSeat1}", At(row, 31, 5).Trim());
        Assert.Equal("1", At(row, 37, 7).Trim());
        Assert.Equal("L", At(row, 45, 4).Trim());
        Assert.Equal("W", At(row, 50, 4).Trim());
    }

    /// <summary>
    /// The per-seed pane's title names the suite it is showing, the two policies it
    /// compares, and whose seat a W or an L belongs to, so a letter on screen is never
    /// an undefined mark.
    /// </summary>
    [Fact]
    public void ThePerSeedPaneNamesItsSuiteItsPoliciesAndWhoseSeatALetterBelongsTo()
    {
        var frame = Draw([LedgerFixtures.Artifact()]);
        var title = frame.Lines[Pane(frame, LedgerPaneKind.Detail).Area.Y];

        Assert.Contains("heldout", title, StringComparison.Ordinal);
        Assert.Contains("MCTS", title, StringComparison.Ordinal);
        Assert.Contains("Scout", title, StringComparison.Ordinal);
        Assert.Contains("W/L", title, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The compare pane.
    // ---------------------------------------------------------------------

    [Fact]
    public void TwoArtifactsWithTheSameProtocolAreAlignedAndSaySo()
    {
        var frame = Compare([LedgerFixtures.Artifact("first.json"), LedgerFixtures.Artifact("second.json")]);
        var text = Text(frame, LedgerPaneKind.Detail);

        // Both files are named, so a reader knows which column is which.
        Assert.Contains("first.json", text, StringComparison.Ordinal);
        Assert.Contains("second.json", text, StringComparison.Ordinal);
        Assert.Contains("same", text, StringComparison.Ordinal);
        Assert.DoesNotContain("differ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Studies are aligned by suite name across the artifacts, so a reader comparing two
    /// files is never reading one suite against another's numbers.
    /// </summary>
    [Fact]
    public void SuitesAreAlignedByNameAcrossArtifacts()
    {
        var frame = Compare([
            LedgerFixtures.TwoSuiteArtifact("first.json"),
            LedgerFixtures.TwoSuiteArtifact("second.json"),
        ]);

        var rows = RowsOf(frame, LedgerPaneKind.Detail)
            .Where(row => row.StartsWith("> ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains("dev", rows[0], StringComparison.Ordinal);
        Assert.Contains("heldout", rows[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 200, 3, "rollouts", "rollouts 32/8")]
    [InlineData(32, 100, 3, "max steps", "max steps 200/100")]
    [InlineData(32, 200, 9, "seeds", "seeds 3/9")]
    public void AProtocolThatDoesNotMatchSaysWhichPartDoesNot(
        int rollouts,
        int maxSteps,
        int seedCount,
        string because,
        string expected)
    {
        var first = LedgerFixtures.Artifact("first.json", rollouts: 32, maxSteps: 200, seeds: LedgerFixtures.Seeds(3));
        var second = LedgerFixtures.Artifact("second.json", rollouts: rollouts, maxSteps: maxSteps, seeds: LedgerFixtures.Seeds(seedCount));

        var text = Text(Compare([first, second]), LedgerPaneKind.Detail);

        Assert.Contains("protocols differ", text, StringComparison.Ordinal);

        // The differing numbers are shown raw and side by side, so the reader can see
        // what the two runs actually did rather than being told only that they differ.
        Assert.Contains(expected, text, StringComparison.Ordinal);

        // The other two protocol facts agreed, so they are not reported: the line names
        // what differs, not everything that was compared.
        Assert.Equal(1, text.Split('\n').Count(row => row.Contains(because, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A suite one artifact has and the other does not is a difference in what was run,
    /// and the artifact that lacks it is named rather than shown as a blank column.
    /// </summary>
    [Fact]
    public void ASuiteOnlyOneArtifactHasIsShownAsAbsentFromTheOther()
    {
        var text = Text(
            Compare([LedgerFixtures.Artifact("first.json"), LedgerFixtures.TwoSuiteArtifact("second.json")]),
            LedgerPaneKind.Detail);

        // first.json has one suite and second.json has two, so the dev suite is the one
        // first.json never ran — and it is first.json that is named.
        Assert.Contains("only in first.json", text, StringComparison.Ordinal);
        Assert.Contains("absent", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With one artifact there is nothing to compare against, so the pane says so and
    /// then shows the per-seed rows rather than a single column against nothing.
    /// </summary>
    [Fact]
    public void OneArtifactHasNothingToCompareAndSaysSoRatherThanShowingOneColumn()
    {
        var frame = Draw([LedgerFixtures.Artifact()], comparing: true);

        Assert.Contains("compare needs 2 artifacts", Text(frame, LedgerPaneKind.Detail), StringComparison.Ordinal);
        Assert.Contains("2001", Text(frame, LedgerPaneKind.Detail), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Scrolling, clipping, ASCII, and the fallback size.
    // ---------------------------------------------------------------------

    /// <summary>
    /// More seeds than the pane has rows: the window shows the rows around the selected
    /// one and says how many are out of sight, rather than hiding the seed the reader is
    /// on.
    /// </summary>
    [Fact]
    public void APerSeedListLongerThanItsPaneScrollsAndSaysHowManyAreOutOfSight()
    {
        var frame = Draw([LedgerFixtures.Artifact(seeds: LedgerFixtures.Seeds(60))], seedIndex: 40);
        var text = Text(frame, LedgerPaneKind.Detail);

        Assert.Contains("2041", text, StringComparison.Ordinal);
        Assert.Contains("more", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2001", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArtifactLabelLongerThanTheHeaderIsCutAndMarked()
    {
        var frame = Draw([LedgerFixtures.Artifact(label: new string('L', 300))]);

        Assert.Contains(CellText.UnicodeEllipsis.ToString(), frame.Lines[0], StringComparison.Ordinal);
        Assert.Equal(100, frame.Lines[0].Length);
    }

    /// <summary>
    /// Wide and CJK text is replaced and never left as half of a character: the grid
    /// holds one column per cell, so a half is a wrong cell.
    /// </summary>
    [Fact]
    public void WideAndCjkTextIsReplacedAndNeverLeftHalfACell()
    {
        var artifact = new LedgerArtifact(
            "漢字漢字" + new string('L', 200),
            LedgerFixtures.Commit,
            LedgerFixtures.Written,
            "TestRuntime 1.0",
            "TestOs 1.0",
            4,
            "X64",
            [LedgerFixtures.Study("漢字suite", 32, 200, LedgerFixtures.Seeds(2))],
            null);

        var lines = Draw([artifact]).Lines;

        Assert.All(lines.SelectMany(line => line), glyph => Assert.False(char.IsSurrogate(glyph)));
        Assert.Contains(new string('\uFFFD', 4), lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// An ASCII terminal gets the same shape from the ASCII vocabulary, every glyph under
    /// 128, and the three-character marker rather than the one-column one.
    /// </summary>
    [Fact]
    public void AnAsciiTerminalGetsAnAsciiFrameOfTheSameShape()
    {
        var lines = Draw(
            [LedgerFixtures.Artifact(label: new string('L', 300))],
            glyphs: GlyphMode.Ascii).Lines;

        foreach (var line in lines)
        {
            Assert.Equal(100, line.Length);
            Assert.All(line, glyph => Assert.True(glyph < 128, $"'{glyph}' is not ASCII."));
        }

        Assert.Contains(CellText.AsciiEllipsis, lines[0], StringComparison.Ordinal);
        Assert.Equal(new string(BorderGlyphs.Ascii.BottomLeft, 1), lines[^1][..1]);
        Assert.Equal(new string(BorderGlyphs.Ascii.BottomRight, 1), lines[^1][^1..]);
    }

    /// <summary>
    /// Below the designed size the screen states the size it measured, so a clipped
    /// column is never mistaken for a column that is simply short.
    /// </summary>
    [Fact]
    public void BelowTheDesignedSizeTheScreenStatesTheSizeItMeasured()
    {
        Assert.True(LedgerLayout.IsFull(LedgerFixtures.Full));
        Assert.False(LedgerLayout.IsFull(LedgerFixtures.Narrow));

        var frame = Draw([LedgerFixtures.Artifact()], size: LedgerFixtures.Narrow);

        Assert.Contains(
            $"terminal is 80x25; the full layout needs {LedgerLayout.MinimumWidth}x{LedgerLayout.MinimumHeight}; resize for it",
            string.Join('\n', frame.Lines),
            StringComparison.Ordinal);

        Assert.All(frame.Lines, row => Assert.Equal(80, row.Length));
    }

    /// <summary>
    /// The study table needs more columns than eighty terminals have, so at the fallback
    /// size the tail is cut inside the pane and marked, and the verdict — the last thing
    /// a reader can afford to lose — is still on the row.
    /// </summary>
    [Fact]
    public void TheFallbackSizeCutsTheTailOfTheStudyTableAndKeepsTheVerdict()
    {
        var artifact = LedgerFixtures.Artifact();
        var frame = Draw([artifact], size: LedgerFixtures.Narrow);
        var pane = Pane(frame, LedgerPaneKind.Studies);
        var row = Slice(frame.Lines[pane.Area.Y + 2], pane.Area.X + 1, pane.Area.Width - 2);

        Assert.Contains(CellText.UnicodeEllipsis.ToString(), row, StringComparison.Ordinal);
        Assert.Equal("Pass", At(row, 47, 10).Trim());
        Assert.DoesNotContain("SAT", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(20, 6)]
    [InlineData(40, 12)]
    public void ATerminalWithNoRoomForAPaneStillDrawsRatherThanThrowing(int width, int height)
    {
        var cells = LedgerLayout.Render(Request(width, height));

        Assert.Equal(width, cells.Width);
        Assert.Equal(height, cells.Height);
        Assert.All(cells.ToLines(), row => Assert.Equal(width, row.Length));
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    /// <summary>One rendered frame and the panes it was rendered into.</summary>
    private readonly record struct Frame(string[] Lines, IReadOnlyList<LedgerPane> Panes, PaneSize Size)
    {
        /// <summary>The row <paramref name="fromContent"/> rows inside the band, cut to the band's inner columns.</summary>
        internal string Content(LedgerPane pane, int fromContent) =>
            LayoutTests.Slice(Lines[pane.Area.Y + 1 + fromContent], pane.Area.X + 1, pane.Area.Width - 2);
    }

    /// <summary>
    /// Renders a frame and computes its panes from the same inputs, so an assertion
    /// about a band can never be aimed at a band the frame did not have.
    /// </summary>
    private static Frame Draw(
        IReadOnlyList<LedgerArtifact> artifacts,
        int artifactIndex = 0,
        int studyIndex = 0,
        int seedIndex = 0,
        bool comparing = false,
        PaneSize? size = null,
        GlyphMode? glyphs = null)
    {
        var at = size ?? LedgerFixtures.Full;
        var artifact = artifacts[Math.Clamp(artifactIndex, 0, artifacts.Count - 1)];

        return new Frame(
            LedgerLayout.Render(new LedgerRequest(
                artifacts,
                artifactIndex,
                studyIndex,
                seedIndex,
                comparing,
                at,
                glyphs ?? GlyphMode.Unicode,
                null)).ToLines(),
            LedgerLayout.Panes(at, artifact.Agent is not null, artifact.Studies.Count, comparing),
            at);
    }

    private static Frame Compare(IReadOnlyList<LedgerArtifact> artifacts, PaneSize? size = null) =>
        Draw(artifacts, comparing: true, size: size);

    private static LedgerRequest Request(
        int width,
        int height,
        IReadOnlyList<LedgerArtifact>? artifacts = null,
        GlyphMode glyphs = GlyphMode.Unicode) =>
        new(
            artifacts ?? [LedgerFixtures.Artifact()],
            ArtifactIndex: 0,
            StudyIndex: 0,
            SeedIndex: 0,
            Comparing: false,
            new PaneSize(width, height),
            glyphs,
            null);

    private static LedgerPane Pane(Frame frame, LedgerPaneKind kind) =>
        frame.Panes.First(pane => pane.Kind == kind);

    /// <summary>A band's rows as one block of text.</summary>
    private static string Text(Frame frame, LedgerPaneKind kind, int firstContentRow = 0)
    {
        var pane = Pane(frame, kind);
        var rows = new List<string>();

        for (var index = 0; index < pane.Area.Height - 2; index++)
        {
            rows.Add(frame.Content(pane, index));
        }

        return string.Join('\n', rows.Skip(firstContentRow));
    }

    /// <summary>A band's inner rows, without its title and borders.</summary>
    private static string[] RowsOf(Frame frame, LedgerPaneKind kind)
    {
        var pane = Pane(frame, kind);
        return [.. Enumerable.Range(0, pane.Area.Height - 2).Select(index => frame.Content(pane, index))];
    }

    private static string At(string row, int offset, int width) => Slice(row, offset, width);

    private static string Slice(string line, int start, int width) =>
        start + width <= line.Length ? line.Substring(start, width) : line[start..].PadRight(width);

    /// <summary>Shared so the frame helper and the assertions cut rows the same way.</summary>
    private static class LayoutTests
    {
        internal static string Slice(string line, int start, int width) =>
            start + width <= line.Length ? line.Substring(start, width) : line[start..].PadRight(width);
    }
}
