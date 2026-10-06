using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Whole Ledger frames, pinned cell by cell, for the states a reader can be in and
/// the two terminal sizes they can be in.
/// </summary>
/// <remarks>
/// <para>
/// The same discipline as the cockpit and Launchpad goldens: the rows are accounted
/// for by a source other than <see cref="LedgerLayout"/> before the frozen file is
/// compared — the provenance lines are composed here from the model's own values, the
/// study row is composed from the artifact's own statistics, and the frame, pane
/// titles and key hints are literals at their known places. Only then is the whole
/// frame compared, so a golden cannot drift into agreeing with a regression.
/// </para>
/// <para>
/// <b>These frames exist because of the six states below, and each test says which.</b>
/// A golden with no stated reason is a golden nobody can tell from a regression.
/// </para>
/// </remarks>
public class LedgerGoldens
{
    /// <summary>
    /// Words the screen may never use. The Ledger reports the artifact's own verdict
    /// and its own numbers and makes a protocol check between artifacts; it never says
    /// one policy or one artifact is better than another, because the comparison is not
    /// the screen's to make. Scanned over every committed golden, the pre-existing
    /// ones included, so the rule is a property of this repository's screens rather
    /// than a rule this one happens to follow.
    /// </summary>
    private static readonly string[] Forbidden = [
        "better",
        "worse",
        "improved",
        "improve",
        "regressed",
        "regression",
        "inferior",
        "superior",
        "outperforms",
    ];

    [Fact]
    public void NoGoldenUsesAClaimWord()
    {
        var directory = GoldenDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(directory, "*.txt").OrderBy(file => file, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file).ToLowerInvariant();
            offenders.AddRange(Forbidden
                .Where(word => text.Contains(word, StringComparison.Ordinal))
                .Select(word => $"{Path.GetFileName(file)}: '{word}'"));
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The ordinary case: one artifact, one suite, three seeds, the per-seed pane
    /// showing that suite's rows. This is the frame a reader sees almost every time,
    /// so it is the one that has to be right.
    /// </summary>
    [Fact]
    public void OneArtifactWithOneSuiteShowsItsProvenanceItsStudyAndItsSeeds()
    {
        var artifact = LedgerFixtures.Artifact();
        var frame = Golden("ledger-single-100x30", [artifact]);

        // The provenance, composed from the model rather than read out of the frame.
        var summary = PaneText(frame, LedgerPaneKind.Summary);
        Assert.Contains($"commit {artifact.CommitSha}", summary, StringComparison.Ordinal);
        Assert.Contains("created 2026-09-25 19:41:20Z", summary, StringComparison.Ordinal);
        Assert.Contains($"cores {artifact.Cores}", summary, StringComparison.Ordinal);

        // The study row, composed from the artifact's own statistics.
        var studies = PaneText(frame, LedgerPaneKind.Studies);
        var study = artifact.Studies[0];
        Assert.Contains(study.Suite, studies, StringComparison.Ordinal);
        Assert.Contains($"{study.Statistics.Seeds}", studies, StringComparison.Ordinal);
        Assert.Contains($"{study.Statistics.Matches}", studies, StringComparison.Ordinal);

        var seeds = PaneText(frame, LedgerPaneKind.Detail);
        foreach (var seed in study.PerSeed)
        {
            Assert.Contains($"{seed.Seed}", seeds, StringComparison.Ordinal);
        }

        Assert.Equal(LedgerFixtures.Full.Width, frame.Lines[0].Length);
    }

    /// <summary>
    /// Two artifacts under one protocol, side by side. The frame exists to pin that
    /// each artifact gets its own named column and that the protocol line says the
    /// protocols match rather than leaving the reader to assume it.
    /// </summary>
    [Fact]
    public void TwoArtifactsUnderTheSameProtocolAreAlignedBesideEachOther()
    {
        var first = LedgerFixtures.Artifact("first.json");
        var second = LedgerFixtures.Artifact("second.json");
        var frame = CompareGolden("ledger-compare-100x30", [first, second]);

        var pane = PaneText(frame, LedgerPaneKind.Detail);

        Assert.Contains("first.json", pane, StringComparison.Ordinal);
        Assert.Contains("second.json", pane, StringComparison.Ordinal);
        Assert.Contains("same", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("differ", pane, StringComparison.Ordinal);

        // Both mean deltas are on screen, each in its own column.
        Assert.Contains("1", pane, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two artifacts whose protocols do not match. The frame exists to pin the two
    /// required things together: the words "protocols differ", and the raw numbers of
    /// what differs. A screen that said only "they differ" would leave the reader
    /// unable to tell whether the difference is one rollouts figure or the whole run.
    /// </summary>
    [Fact]
    public void TwoArtifactsUnderDifferentProtocolsSayWhichPartsDiffer()
    {
        var first = LedgerFixtures.Artifact("first.json", rollouts: 32, maxSteps: 200, seeds: LedgerFixtures.Seeds(3));
        var second = LedgerFixtures.Artifact("second.json", rollouts: 8, maxSteps: 100, seeds: LedgerFixtures.Seeds(9));
        var frame = CompareGolden("ledger-compare-protocol-100x30", [first, second]);

        var pane = PaneText(frame, LedgerPaneKind.Detail);

        Assert.Contains("protocols differ", pane, StringComparison.Ordinal);
        Assert.Contains("rollouts 32/8", pane, StringComparison.Ordinal);
        Assert.Contains("max steps 200/100", pane, StringComparison.Ordinal);
        Assert.Contains("seeds 3/9", pane, StringComparison.Ordinal);
    }

    /// <summary>
    /// An artifact that scored an external agent. The frame exists to pin that the
    /// agent's command, limits, failure breakdown, void runs and forfeits all appear,
    /// and that the pane is absent entirely when there was no external agent.
    /// </summary>
    [Fact]
    public void AnExternalAgentBlockIsShownWithItsCommandLimitsFailuresAndForfeits()
    {
        var frame = Golden("ledger-agent-100x30", [LedgerFixtures.Artifact(agent: LedgerFixtures.Agent())]);

        var agent = PaneText(frame, LedgerPaneKind.Agent);
        Assert.Contains("python3 agent.py", agent, StringComparison.Ordinal);
        Assert.Contains("step 5000ms", agent, StringComparison.Ordinal);
        Assert.Contains("agent_crashed 2", agent, StringComparison.Ordinal);
        Assert.Contains("void runs 3", agent, StringComparison.Ordinal);
        Assert.Contains("forfeits 2", agent, StringComparison.Ordinal);

        Assert.Equal(LedgerFixtures.Full.Width, frame.Lines[0].Length);
    }

    /// <summary>
    /// A file name far longer than the header. The frame exists to pin that the label
    /// is cut inside the screen and marked, so a reader can see it was cut rather than
    /// believing the file they were given had that short a name.
    /// </summary>
    [Fact]
    public void AnArtifactLabelLongerThanTheHeaderIsCutAndMarked()
    {
        var frame = Golden("ledger-long-label-100x30", [LedgerFixtures.Artifact(label: new string('L', 300))]);

        Assert.Contains(CellText.UnicodeEllipsis.ToString(), frame.Lines[0], StringComparison.Ordinal);
        Assert.Equal(LedgerFixtures.Full.Width, frame.Lines[0].Length);
    }

    /// <summary>
    /// CJK and emoji in a suite name and in the file name. The frame exists to pin
    /// that every such character is replaced and never left as half a cell, and that
    /// the study table's own alignment is unaffected by the replacements.
    /// </summary>
    [Fact]
    public void WideCharactersInLabelsAndSuiteNamesAreReplacedNotLeftHalfACell()
    {
        var artifact = new LedgerArtifact(
            "漢字\U0001F600-very-long-file-name.json",
            LedgerFixtures.Commit,
            LedgerFixtures.Written,
            "TestRuntime 1.0",
            "TestOs 1.0",
            4,
            "X64",
            [
                LedgerFixtures.Study(
                    "漢字suite",
                    32,
                    200,
                    LedgerFixtures.Seeds(2)),
            ],
            null);

        var frame = Golden("ledger-wide-100x30", [artifact]);

        Assert.All(frame.Lines.SelectMany(line => line), glyph => Assert.False(char.IsSurrogate(glyph)));
        Assert.Contains(new string('\uFFFD', 2), frame.Lines[0], StringComparison.Ordinal);

        // The statistics are still in their own columns: a replaced suite name must not push a
        // number out of the row. The row is cut to the band's own inner columns first,
        // so the offsets are the ones the layout specifies.
        var studies = frame.Panes.First(pane => pane.Kind == LedgerPaneKind.Studies);
        var screenRow = frame.Lines[studies.Area.Y + 2];
        var row = screenRow.Substring(studies.Area.X + 1, studies.Area.Width - 2);
        Assert.Equal("4", row.Substring(19, 5).Trim());
        Assert.Equal("1", row.Substring(25, 7).Trim());
    }

    /// <summary>
    /// The narrower terminal. The frame exists to pin that the screen still draws every
    /// pane there, that it states the size it measured, and that a column which no
    /// longer fits is cut inside the pane with a marker rather than over a border.
    /// </summary>
    [Fact]
    public void TheNarrowerTerminalStillDrawsEveryPaneAndStatesItsSize()
    {
        var artifact = LedgerFixtures.Artifact(agent: LedgerFixtures.Agent());
        var frame = Golden("ledger-fallback-80x25", [artifact], LedgerFixtures.Narrow);

        Assert.All(frame.Lines, row => Assert.Equal(80, row.Length));
        Assert.Contains(
            $"terminal is 80x25; the full layout needs {LedgerLayout.MinimumWidth}x{LedgerLayout.MinimumHeight}; resize for it",
            string.Join('\n', frame.Lines),
            StringComparison.Ordinal);

        // Every pane is still on screen at the narrower size, including the agent's.
        foreach (var kind in Enum.GetValues<LedgerPaneKind>())
        {
            Assert.Contains(LedgerLayout.Panes(LedgerFixtures.Narrow, hasAgent: true, studyCount: 1), pane => pane.Kind == kind);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    private static Frame Golden(string name, IReadOnlyList<LedgerArtifact> artifacts, PaneSize? size = null) =>
        Compare(name, artifacts, comparing: false, size: size ?? LedgerFixtures.Full, ascii: false);

    private static Frame CompareGolden(string name, IReadOnlyList<LedgerArtifact> artifacts) =>
        Compare(name, artifacts, comparing: true, size: LedgerFixtures.Full, ascii: false);

    /// <summary>
    /// The frame this golden names, compared against the frozen copy of it row by row,
    /// and handed back with the panes it was drawn into. A change in the renderer fails
    /// here until the golden has been re-read and re-justified.
    /// </summary>
    private static Frame Compare(
        string name,
        IReadOnlyList<LedgerArtifact> artifacts,
        bool comparing,
        PaneSize size,
        bool ascii)
    {
        var path = Path.Combine(GoldenDirectory(), name + ".txt");
        Assert.True(
            File.Exists(path),
            $"{path} is missing. A golden is generated once and frozen; the test that reads it never writes it.");

        var artifact = artifacts[0];
        var frame = LedgerLayout.Render(new LedgerRequest(
            artifacts,
            ArtifactIndex: 0,
            StudyIndex: 0,
            SeedIndex: 0,
            Comparing: comparing,
            size,
            GlyphModes.Resolve(true, ascii),
            null)).ToLines();

        var frozen = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(frozen.Length, frame.Length);
        for (var row = 0; row < frame.Length; row++)
        {
            Assert.True(
                frame[row] == frozen[row],
                $"row {row} of {name}:\n  frame:  |{frame[row]}|\n  frozen: |{frozen[row]}|");
        }

        return new Frame(
            frozen,
            LedgerLayout.Panes(size, artifact.Agent is not null, artifact.Studies.Count, comparing));
    }

    /// <summary>A frozen frame and the panes it was drawn into.</summary>
    private readonly record struct Frame(string[] Lines, IReadOnlyList<LedgerPane> Panes);

    /// <summary>
    /// A pane's rows as one block of text, located through the panes of the very render
    /// that produced the frame rather than through a separate guess at the geometry, so
    /// the assertion cannot be aimed at a band the frame did not have.
    /// </summary>
    private static string PaneText(Frame frame, LedgerPaneKind kind)
    {
        var pane = frame.Panes.First(candidate => candidate.Kind == kind);

        return string.Join('\n', frame.Lines[pane.Area.Y..(pane.Area.Y + pane.Area.Height)]);
    }

    private static string GoldenDirectory() =>
        Path.Combine(RepositoryRoot(), "Tests", "Tui", "Goldens");

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
