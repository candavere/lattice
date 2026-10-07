using System.Globalization;
using System.Text;

namespace Lattice.Tui;

/// <summary>
/// The Ledger: an artifact's own statistics, provenance and verdict, its per-seed
/// rows, and — when the reader asks for it — two or more artifacts beside each other
/// with the protocol they were run under.
/// </summary>
/// <remarks>
/// <para>
/// Pure — a request in, a grid the size of the terminal out — so the whole screen can
/// be asserted against its exact cells with no terminal attached, exactly as
/// <see cref="CockpitLayout"/> and <see cref="LaunchpadLayout"/> are.
/// </para>
/// <para>
/// <b>Stacked, one pane per band.</b> These tables carry more columns than half a
/// terminal has room for, so every pane spans the full width and the panes stack down
/// the screen in reading order: provenance, then the external agent when there is one,
/// then the study table, then whichever detail the reader asked for. Below the
/// designed size the screen states the size it measured, so a cut column is never
/// mistaken for a short one.
/// </para>
/// <para>
/// <b>Nothing overflows a pane, and nothing overflows an artifact.</b> Every string is
/// cut to the pane it is drawn in and marked when it was cut, and a column cut by the
/// pane's edge takes no column after it with it. The study table's columns are
/// ordered by how much a reader needs them, so a terminal too narrow for all of them
/// loses the outcome rates rather than the suite name, the interval or the verdict.
/// </para>
/// <para>
/// <b>No column compares two artifacts.</b> The compare band puts their numbers side
/// by side and states whether their protocols matched, and stops there: a difference
/// in protocol is a fact about the runs, not about either of them.
/// </para>
/// </remarks>
public static class LedgerLayout
{
    /// <summary>The narrowest terminal the layout is designed for.</summary>
    public const int MinimumWidth = 100;

    /// <summary>The shortest terminal the layout is designed for.</summary>
    public const int MinimumHeight = 30;

    /// <summary>The row the key hints are on, counted from the bottom.</summary>
    public const int KeyHintRowFromBottom = 1;

    /// <summary>The controls, on one row, each naming what it moves.</summary>
    public const string NavigationHints =
        "tab artifact  up/down suite  pgup/pgdn/home/end seed  c compare  q quit";

    /// <summary>Provenance: a top border, two rows of facts, a bottom border.</summary>
    private const int SummaryHeight = 4;

    /// <summary>The external agent: three rows of facts between two borders.</summary>
    private const int AgentHeight = 5;

    /// <summary>
    /// The study table at its smallest: two borders, a header, and room for one suite
    /// row. A pane one row shorter is legal on a terminal too small for anything else —
    /// it then shows the header alone — which is why the row count is a separate floor.
    /// </summary>
    private const int StudiesMinimum = 4;

    /// <summary>The study table with its header and no suite rows at all.</summary>
    private const int StudiesHeaderOnly = 3;

    /// <summary>The detail band at its smallest: two borders and two rows.</summary>
    private const int DetailMinimum = 4;

    /// <summary>Blank rows between panes, so one box never touches another.</summary>
    private const int Gap = 1;

    /// <summary>
    /// The row a narrower terminal's size notice takes, above every band. Zero rows
    /// tall when the terminal is at least the designed size.
    /// </summary>
    private static readonly int Notice = 1;

    /// <summary>
    /// The blank column between two table columns. Without it a left-aligned suite name
    /// runs straight into a right-aligned seed count and the row reads as one word.
    /// </summary>
    private const int ColumnGap = 1;

    /// <summary>A delta, three decimals at most, with no trailing zeros to pad.</summary>
    private const string DeltaFormat = "0.###";

    /// <summary>A rate or a saturation: one decimal, which is all a table can carry.</summary>
    private const string RateFormat = "0.0";

    /// <summary>When an artifact was written, to the second, in UTC.</summary>
    private const string WhenFormat = "yyyy-MM-dd HH:mm:ss'Z'";

    /// <summary>Below this a figure is zero as far as a reader is concerned.</summary>
    private const double NothingToShow = 0.0005;

    /// <summary>
    /// The study table's columns, in reading order: what identifies the row and lets a
    /// reader judge it, then what a reader checking the rates needs to confirm them.
    /// The four outcome counts share a column and the four rates share another, which is
    /// what keeps the whole row inside a hundred columns with a blank between each; the
    /// rates come after the verdict deliberately, because on a narrower terminal the
    /// tail is what is lost and the rates are recoverable from the counts while the
    /// verdict is not recoverable at all.
    /// </summary>
    private static readonly Column[] StudyColumns =
    [
        new("", 2, Alignment.Left),
        new("SUITE", 9, Alignment.Left),
        new("SEEDS", 5, Alignment.Right),
        new("MATCH", 5, Alignment.Right),
        new("MEAN", 7, Alignment.Right),
        new("95% CI", 13, Alignment.Right),
        new("VERDICT", 10, Alignment.Left),
        new("W/D/L/T", 9, Alignment.Right),
        new("WIN%/D%/L%/T%", 19, Alignment.Right),
        new("SAT", 5, Alignment.Right),
    ];

    /// <summary>
    /// The per-seed table's columns: the seed, the four seat-scores the paired delta is
    /// built from, the delta, and the two decoded outcomes. The scores come before the
    /// outcomes so a reader can check a letter against the numbers rather than the
    /// other way round.
    /// </summary>
    private static readonly Column[] SeedColumns =
    [
        new("", 2, Alignment.Left),
        new("SEED", 9, Alignment.Right),
        new("POL0", 5, Alignment.Right),
        new("BASE0", 5, Alignment.Right),
        new("POL1", 5, Alignment.Right),
        new("BASE1", 5, Alignment.Right),
        new("DELTA", 7, Alignment.Right),
        new("M0", 4, Alignment.Right),
        new("M1", 4, Alignment.Right),
    ];

    /// <summary>The compare band's suite column.</summary>
    private const int CompareSuiteWidth = 9;

    /// <summary>
    /// The protocol column, wide enough for the words "protocols differ" so the state
    /// of the check is legible without reading the line under it.
    /// </summary>
    private const int CompareProtocolWidth = 16;

    /// <summary>Columns before the first artifact's group starts, gaps included.</summary>
    private const int CompareGroupLeft = 2 + ColumnGap + CompareSuiteWidth + ColumnGap + CompareProtocolWidth + ColumnGap;

    /// <summary>One artifact's columns, as the group the next artifact follows.</summary>
    private const int CompareGroupWidth = 8 + ColumnGap + 13 + ColumnGap + 8;

    /// <summary>
    /// The columns one artifact contributes to the compare band. The seed count is not
    /// among them: the study table above the band already shows it for every artifact,
    /// and the protocol line below the row carries it whenever the artifacts disagree
    /// about it — which is the only time a reader needs to compare it.
    /// </summary>
    private static readonly Column[] GroupColumns =
    [
        new("", 8, Alignment.Right),
        new("", 13, Alignment.Right),
        new("", 8, Alignment.Left),
    ];

    /// <summary>The protocol facts a reader must be able to compare directly.</summary>
    private static readonly string[] ProtocolFacts = ["rollouts", "max steps", "seeds"];

    /// <summary>
    /// Every glyph a closed box can be drawn from, in both vocabularies. A test checks
    /// each pane's four corners against this set, which is what "the pane is closed on
    /// all four sides" means without hard-coding either glyph set.
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

    /// <summary>Whether this terminal gets the layout every column was designed for.</summary>
    public static bool IsFull(PaneSize size) =>
        size.Width >= MinimumWidth && size.Height >= MinimumHeight;

    /// <summary>The line a narrower terminal is shown above its panes.</summary>
    public static string ResizeNotice(PaneSize size) =>
        $"terminal is {Invariant(size.Width)}x{Invariant(size.Height)}; " +
        $"the full layout needs {MinimumWidth}x{MinimumHeight}; resize for it";

    /// <summary>
    /// The bands of a frame of this size, top to bottom. Named rather than computed
    /// inside the drawing, so a test can check that every one of them is closed on all
    /// four sides and that none has fallen off the screen.
    /// </summary>
    /// <param name="size">The terminal's size.</param>
    /// <param name="hasAgent">Whether the selected artifact records an external agent.</param>
    /// <param name="studyCount">How many suites the selected artifact has.</param>
    /// <param name="comparing">Whether the detail band is showing the comparison.</param>
    public static IReadOnlyList<LedgerPane> Panes(
        PaneSize size,
        bool hasAgent,
        int studyCount,
        bool comparing = false)
    {
        var panes = new List<LedgerPane>();
        var notice = IsFull(size) ? 0 : Notice;
        var available = size.Height - 3 - notice;
        var width = size.Width - 2;

        if (available <= 0 || width < 4)
        {
            return panes;
        }

        // The three bands that are always wanted, at their smallest, plus the gaps
        // between them. The agent's band is the only one that can be dropped: a reader
        // on a short terminal still needs to see which suite is on show.
        var minimums = SummaryHeight + Gap + StudiesMinimum + Gap + DetailMinimum;
        var agentRows = hasAgent && available >= minimums + AgentHeight + Gap ? AgentHeight + Gap : 0;

        if (available < minimums)
        {
            return Degraded(width, available, hasAgent, comparing);
        }

        var spare = available - minimums - agentRows;

        // Spare rows go to the study table first, because a suite the reader cannot see
        // is a suite they cannot select, and then to the detail band, which scrolls.
        var studies = StudiesMinimum + Math.Min(Math.Max(0, spare), Math.Max(0, studyCount - 1));
        var detail = available - SummaryHeight - Gap - agentRows - studies - Gap;

        var top = 1 + notice;
        panes.Add(new LedgerPane(LedgerPaneKind.Summary, new Rect(1, top, width, SummaryHeight), "ARTIFACT"));
        top += SummaryHeight + Gap;

        if (agentRows > 0)
        {
            panes.Add(new LedgerPane(LedgerPaneKind.Agent, new Rect(1, top, width, AgentHeight), "EXTERNAL AGENT"));
            top += AgentHeight + Gap;
        }

        panes.Add(new LedgerPane(LedgerPaneKind.Studies, new Rect(1, top, width, studies), "STUDIES"));
        top += studies + Gap;

        panes.Add(new LedgerPane(
            LedgerPaneKind.Detail,
            new Rect(1, top, width, detail),
            comparing ? "COMPARE" : "PER-SEED"));

        return panes;
    }

    /// <summary>
    /// A terminal with no room for the three bands at their smallest. Each band is given
    /// what is left in reading order and the rest is left out, so a reader sees the
    /// bands that fit rather than bands squashed into rows too short to hold their own
    /// content.
    /// <para>
    /// A band is only given a height it can actually draw into. Handing one fewer rows
    /// than its own content needs would put a content row on top of the band below it,
    /// which is how a pane ends up with its own bottom border running through the middle
    /// of its text.
    /// </para>
    /// </summary>
    private static IReadOnlyList<LedgerPane> Degraded(
        int width,
        int available,
        bool hasAgent,
        bool comparing)
    {
        var panes = new List<LedgerPane>();
        var budget = available;
        var top = 1 + Notice;
        var bands = new List<(LedgerPaneKind Kind, int Wanted, int Needed, string Title)>
        {
            (LedgerPaneKind.Summary, SummaryHeight, SummaryHeight, "ARTIFACT"),
            (LedgerPaneKind.Agent, AgentHeight, AgentHeight, "EXTERNAL AGENT"),
            (LedgerPaneKind.Studies, StudiesMinimum + 1, StudiesHeaderOnly, "STUDIES"),
            (LedgerPaneKind.Detail, DetailMinimum, DetailMinimum, comparing ? "COMPARE" : "PER-SEED"),
        };

        foreach (var (kind, wanted, needed, title) in bands)
        {
            if (kind == LedgerPaneKind.Agent && !hasAgent)
            {
                continue;
            }

            if (budget < needed)
            {
                continue;
            }

            var height = Math.Min(wanted, budget);
            panes.Add(new LedgerPane(kind, new Rect(1, top, width, height), title));
            top += height + Gap;
            budget -= height + Gap;
        }

        return panes;
    }

    /// <summary>Draws one Ledger frame.</summary>
    /// <remarks>
    /// A terminal too small to frame — one cell, or a shape with no room for a box — is
    /// not an error: the grid comes back at exactly the size asked for with whatever
    /// fits in it drawn.
    /// </remarks>
    public static CellBuffer Render(LedgerRequest request)
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
            Clip(request, Title(request), Math.Max(0, size.Width - 4)),
            new Cell(' ', Palette.Accent, request.PanelFill, CellAttributes.Bold));

        if (size.Height >= 4)
        {
            // Clipped like every other row: the hints sit on the screen's own bottom
            // border by the convention the cockpit already uses, so an unclipped line
            // long enough to reach the edge would overwrite the corner and the screen
            // would stop looking closed.
            cells.DrawText(
                2,
                size.Height - KeyHintRowFromBottom,
                Clip(request, NavigationHints, Math.Max(0, size.Width - 4)),
                new Cell(' ', Theme.KeyHint, request.PanelFill));

            if (!IsFull(size))
            {
                cells.DrawText(
                    2,
                    1,
                    Clip(request, ResizeNotice(size), Math.Max(0, size.Width - 4)),
                    new Cell(' ', Palette.Warning, request.PanelFill));
            }
        }

        DrawPanes(request, cells, frame);
        return cells;
    }

    private static void DrawPanes(LedgerRequest request, CellBuffer cells, BorderGlyphs frame)
    {
        if (Selected(request) is not { } artifact)
        {
            return;
        }

        var panes = Panes(request.Size, artifact.Agent is not null, artifact.Studies.Count, request.Comparing);

        foreach (var pane in panes)
        {
            DrawPane(request, cells, frame, pane, Title(pane, request));
        }

        foreach (var pane in panes)
        {
            var inner = pane.Area.Width - 2;

            // A pane is only drawn into when it can hold its own content. This is the
            // invariant the geometry arithmetic exists to keep: a pane that is handed
            // fewer rows than it writes would put its last content row on top of the
            // band below it, which is a pane with a border running through its text.
            if (inner <= 0 || pane.Area.Height < Content(pane.Kind))
            {
                continue;
            }

            switch (pane.Kind)
            {
                case LedgerPaneKind.Summary:
                    DrawSummary(request, cells, pane, inner, artifact);
                    break;

                case LedgerPaneKind.Agent when artifact.Agent is { } agent:
                    DrawAgent(request, cells, pane, inner, agent);
                    break;

                case LedgerPaneKind.Studies:
                    DrawStudies(request, cells, pane, inner, artifact);
                    break;

                default:
                    DrawDetail(request, cells, pane, inner);
                    break;
            }
        }
    }

    // ---- Provenance. ----------------------------------------------------

    private static void DrawSummary(
        LedgerRequest request,
        CellBuffer cells,
        LedgerPane pane,
        int inner,
        LedgerArtifact artifact)
    {
        var row = pane.Area.Y + 1;

        Draw(request, cells, pane.Area.X + 1, row, inner,
            $"commit {Or(artifact.CommitSha, "none")}   created {When(artifact.CreatedAtUtc)}   runtime {artifact.Runtime}",
            Palette.TextPrimary);

        Draw(request, cells, pane.Area.X + 1, row + 1, inner,
            $"os {artifact.Os}   cores {Invariant(artifact.Cores)}   arch {artifact.Architecture}   " +
            $"external agent {(artifact.Agent is null ? "none" : "scored")}",
            Palette.TextPrimary);
    }

    // ---- The external agent. --------------------------------------------

    private static void DrawAgent(
        LedgerRequest request,
        CellBuffer cells,
        LedgerPane pane,
        int inner,
        LedgerAgent agent)
    {
        var row = pane.Area.Y + 1;

        Draw(request, cells, pane.Area.X + 1, row, inner,
            $"command {string.Join(' ', agent.Command)}   step {Invariant(agent.Limits.StepTimeoutMs)}ms   " +
            $"match {Invariant(agent.Limits.MatchTimeoutMs)}ms   void runs {Invariant(agent.VoidRuns)}",
            Palette.TextPrimary);

        Draw(request, cells, pane.Area.X + 1, row + 1, inner,
            "failures " + (agent.Failures.Count == 0
                ? "none recorded"
                : string.Join(", ", agent.Failures.Select(failure => $"{failure.Reason} {Invariant(failure.Count)}"))),
            Palette.TextPrimary);

        Draw(request, cells, pane.Area.X + 1, row + 2, inner, "forfeits " + Forfeits(agent), Palette.TextPrimary);
    }

    /// <summary>
    /// The forfeit count and, when there are any, each forfeited match's seed, seat and
    /// reason. The artifact reports the partial scoreboard as well and this pane leaves
    /// it out: it is per-match diagnostic detail, and a band three rows tall cannot
    /// carry it without the count itself being the thing that falls off the end.
    /// </summary>
    private static string Forfeits(LedgerAgent agent) =>
        agent.Forfeits.Count == 0
            ? "0"
            : $"{Invariant(agent.Forfeits.Count)}: " + string.Join(
                "; ",
                agent.Forfeits.Select(forfeit =>
                    $"seed {forfeit.Seed} seat {Invariant(forfeit.ExternalSeat)} ({forfeit.Reason})"));

    // ---- The study table. ----------------------------------------------

    private static void DrawStudies(
        LedgerRequest request,
        CellBuffer cells,
        LedgerPane pane,
        int inner,
        LedgerArtifact artifact)
    {
        var capacity = pane.Area.Height - 3;
        if (capacity < 0)
        {
            return;
        }

        DrawTable(request, cells, pane, pane.Area.Y + 1, inner, StudyColumns, Headers(StudyColumns), Theme.TableHeader);

        if (capacity == 0)
        {
            // Room for the header and no suite row. The header alone still says which
            // columns this band carries, which is more use than an empty box.
            return;
        }

        var first = Math.Clamp(request.StudyIndex - (capacity - 1), 0, Math.Max(0, artifact.Studies.Count - capacity));
        var row = pane.Area.Y + 2;

        for (var index = first; index < artifact.Studies.Count && row < pane.Area.Y + pane.Area.Height - 1; index++)
        {
            var selected = index == request.StudyIndex;

            if (selected)
            {
                cells.Fill(
                    pane.Area.X + 1,
                    row,
                    Math.Min(inner, RowWidth(StudyColumns)),
                    1,
                    new Cell(' ', Theme.SelectionForeground, Theme.SelectionBackground));
            }

            DrawTable(
                request,
                cells,
                pane,
                row,
                inner,
                StudyColumns,
                StudyRow(artifact.Studies[index], selected),
                selected ? Theme.SelectionForeground : Palette.TextPrimary);

            row++;
        }
    }

    /// <summary>
    /// One study's row, in the artifact's own numbers. The verdict is the label the
    /// artifact's own decision sentence begins with — its own word for it — so a study
    /// the decision rule declined to grade is not drawn as a failure.
    /// </summary>
    private static string[] StudyRow(LedgerStudy study, bool selected) =>
    [
        selected ? "> " : "  ",
        study.Suite,
        Invariant(study.Statistics.Seeds),
        Invariant(study.Statistics.Matches),
        Delta(study.Statistics.MeanDelta),
        Interval(study.Statistics.CiLower95, study.Statistics.CiUpper95),
        Verdict(study.Decision),
        $"{Invariant(study.Statistics.Wins)}/{Invariant(study.Statistics.Draws)}/" +
            $"{Invariant(study.Statistics.Losses)}/{Invariant(study.Statistics.Timeouts)}",
        $"{Rate(study.Statistics.WinRate)}/{Rate(study.Statistics.DrawRate)}/" +
            $"{Rate(study.Statistics.LossRate)}/{Rate(study.Statistics.TimeoutRate)}",
        Rate(study.Statistics.MeanContentionSaturation),
    ];

    /// <summary>
    /// The verdict label an artifact's own decision sentence begins with: "Pass",
    /// "Fail", or "Not graded". Read from the sentence rather than from the boolean,
    /// because the boolean alone cannot tell a failure from a study that was never
    /// graded.
    /// </summary>
    private static string Verdict(string decision)
    {
        var colon = decision.IndexOf(':', StringComparison.Ordinal);
        return (colon > 0 ? decision[..colon] : decision).Trim();
    }

    // ---- The detail band: per-seed rows or the comparison. ---------------

    private static void DrawDetail(LedgerRequest request, CellBuffer cells, LedgerPane pane, int inner)
    {
        var comparing = request.Comparing && request.Artifacts.Count >= 2;
        var row = pane.Area.Y + 1;
        var capacity = pane.Area.Height - 3;

        if (request.Comparing && !comparing)
        {
            // One artifact has nothing to sit beside. Said here rather than by refusing
            // the key, because a reader who pressed it is owed the reason nothing
            // changed, and the per-seed rows are the more useful thing to show anyway.
            Draw(request, cells, pane.Area.X + 1, row, inner,
                "compare needs 2 artifacts; tab adds another file", Palette.Warning);
            row++;
            capacity--;
        }

        if (comparing)
        {
            DrawCompare(request, cells, pane, inner, row);
            return;
        }

        if (CurrentStudy(request) is not { } study || capacity <= 0)
        {
            return;
        }

        DrawTable(request, cells, pane, row, inner, SeedColumns, Headers(SeedColumns), Theme.TableHeader);
        row++;

        var seeds = study.PerSeed;
        capacity = pane.Area.Height - 3;

        // A row is held back for the "N more" line whenever there is more than one
        // screenful, so a reader is never told the pane is short by scrolling and
        // finding nothing said about it.
        var visible = seeds.Count > capacity ? Math.Max(0, capacity - 1) : capacity;
        var first = Math.Clamp(request.SeedIndex - (visible - 1), 0, Math.Max(0, seeds.Count - visible));
        var shown = 0;

        // The loop is bounded by `visible` as well as by the pane, because the pane is
        // one row taller than the rows a full screen of seeds needs — the extra row is
        // the one reserved for the notice below.
        for (var index = first; index < seeds.Count && shown < visible && row < pane.Area.Y + pane.Area.Height - 1; index++)
        {
            var selected = index == request.SeedIndex;

            if (selected)
            {
                cells.Fill(
                    pane.Area.X + 1,
                    row,
                    Math.Min(inner, RowWidth(SeedColumns)),
                    1,
                    new Cell(' ', Theme.SelectionForeground, Theme.SelectionBackground));
            }

            DrawTable(request, cells, pane, row, inner, SeedColumns, SeedRow(seeds[index], selected),
                selected ? Theme.SelectionForeground : Palette.TextPrimary);

            row++;
            shown++;
        }

        var dropped = seeds.Count - first - shown;
        if (dropped > 0 && row < pane.Area.Y + pane.Area.Height - 1)
        {
            Draw(request, cells, pane.Area.X + 1, row, inner,
                $"+{Invariant(dropped)} more; pgdn reaches them", Theme.SecondaryText);
        }
    }

    private static string[] SeedRow(LedgerSeed seed, bool selected) =>
    [
        selected ? "> " : "  ",
        Invariant(seed.Seed),
        Invariant(seed.PolicyScoreAtSeat0),
        Invariant(seed.BaselineScoreAtSeat0),
        Invariant(seed.PolicyScoreAtSeat1),
        Invariant(seed.BaselineScoreAtSeat1),
        Delta(seed.MeanDelta),
        Letter(seed.Match0),
        Letter(seed.Match1),
    ];

    /// <summary>
    /// A decoded outcome as the single letter a table can carry. The pane's title is
    /// where the letter's meaning is stated, so it is never an undefined mark.
    /// </summary>
    private static string Letter(LedgerOutcome outcome) => outcome switch
    {
        LedgerOutcome.PolicyWin => "W",
        LedgerOutcome.PolicyLoss => "L",
        LedgerOutcome.Draw => "D",
        _ => "T",
    };

    // ---- The compare band. ---------------------------------------------

    /// <summary>
    /// One row per suite name, the same name in the same column for every artifact, and
    /// a protocol line that says whether the runs were under the same protocol and, when
    /// they were not, which parts of it were not, with each one's values side by side.
    /// <para>
    /// Nothing here ranks anything. Two artifacts whose protocols differ are not
    /// therefore better and worse than each other; the band shows the numbers and the
    /// protocol and stops there.
    /// </para>
    /// </summary>
    private static void DrawCompare(LedgerRequest request, CellBuffer cells, LedgerPane pane, int inner, int firstRow)
    {
        var artifacts = request.Artifacts;
        var row = firstRow;
        var bottom = pane.Area.Y + pane.Area.Height - 1;

        var header = new StringBuilder();
        header.Append("  ");
        header.Append("SUITE".PadRight(CompareSuiteWidth));
        header.Append(' ');
        header.Append("PROTOCOL".PadRight(CompareProtocolWidth));
        header.Append(' ');
        foreach (var artifact in artifacts)
        {
            header.Append(Pad(CellText.Clip(artifact.Label, CompareGroupWidth, request.Glyphs), CompareGroupWidth));
            header.Append(' ');
        }

        Draw(request, cells, pane.Area.X + 1, row, inner, header.ToString(), Theme.TableHeader);
        row++;

        foreach (var suite in Suites(artifacts))
        {
            if (row >= bottom)
            {
                break;
            }

            var studies = artifacts.Select(artifact => Study(artifact, suite)).ToArray();
            var differences = ProtocolDifferences(artifacts, suite);

            var line = new StringBuilder();
            line.Append("> ");
            line.Append(Pad(CellText.Clip(suite, CompareSuiteWidth, request.Glyphs), CompareSuiteWidth));
            line.Append(' ');
            line.Append(Pad(
                differences.Count == 0 ? "same" : "protocols differ",
                CompareProtocolWidth));
            line.Append(' ');

            foreach (var study in studies)
            {
                line.Append(Group(request, study));
                line.Append(' ');
            }

            Draw(request, cells, pane.Area.X + 1, row, inner, line.ToString(), Palette.TextPrimary);
            row++;

            if (differences.Count > 0 && row < bottom)
            {
                Draw(request, cells, pane.Area.X + 1, row, inner,
                    "  " + string.Join("  ", differences), Palette.Warning);
                row++;
            }
        }
    }

    /// <summary>
    /// One artifact's columns: the mean and its interval, and the artifact's own verdict
    /// label — in the order the study table above names them, each cut to its own column
    /// so one artifact's long label can never push another's numbers out of place.
    /// </summary>
    private static string Group(LedgerRequest request, LedgerStudy? study)
    {
        var values = study is null
            ? new[] { "absent", string.Empty, string.Empty }
            : [Delta(study.Statistics.MeanDelta), Interval(study.Statistics.CiLower95, study.Statistics.CiUpper95), Verdict(study.Decision)];

        var line = new StringBuilder();
        for (var index = 0; index < GroupColumns.Length; index++)
        {
            var column = GroupColumns[index];
            var clipped = CellText.Clip(values[index], column.Width, request.Glyphs);

            line.Append(column.Alignment == Alignment.Right ? clipped.PadLeft(column.Width) : clipped.PadRight(column.Width));

            if (index < GroupColumns.Length - 1)
            {
                line.Append(' ');
            }
        }

        return line.ToString();
    }

    /// <summary>
    /// Which parts of the protocol the artifacts disagree on. A suite one artifact does
    /// not have is named as such rather than left as a blank column, and no other fact
    /// is reported once that is true — there is nothing to compare about a suite only
    /// one of them ran.
    /// </summary>
    private static IReadOnlyList<string> ProtocolDifferences(IReadOnlyList<LedgerArtifact> artifacts, string suite)
    {
        var differences = new List<string>();
        var studies = artifacts.Select(artifact => Study(artifact, suite)).ToArray();

        for (var index = 0; index < artifacts.Count; index++)
        {
            if (studies[index] is null)
            {
                differences.Add($"only in {artifacts[index].Label}");
            }
        }

        if (differences.Count > 0)
        {
            return differences;
        }

        for (var fact = 0; fact < ProtocolFacts.Length; fact++)
        {
            var values = studies.Select(study => Value(study!, fact)).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() > 1)
            {
                differences.Add($"{ProtocolFacts[fact]} {string.Join('/', values)}");
            }
        }

        return differences;
    }

    /// <summary>The three protocol facts, in the order they are named.</summary>
    private static string Value(LedgerStudy study, int fact) => fact switch
    {
        0 => Invariant(study.RolloutsPerAction),
        1 => Invariant(study.MaxStepsPerMatch),
        _ => Invariant(study.Statistics.Seeds),
    };

    /// <summary>
    /// Every suite name across the artifacts: the first artifact's order, then any name
    /// only a later one has. The union is what makes the rows align by name, so a suite
    /// is never read against a different one's numbers.
    /// </summary>
    private static IReadOnlyList<string> Suites(IReadOnlyList<LedgerArtifact> artifacts)
    {
        var names = new List<string>();

        foreach (var artifact in artifacts)
        {
            foreach (var study in artifact.Studies)
            {
                if (!names.Contains(study.Suite, StringComparer.Ordinal))
                {
                    names.Add(study.Suite);
                }
            }
        }

        return names;
    }

    private static LedgerStudy? Study(LedgerArtifact artifact, string suite) =>
        artifact.Studies.FirstOrDefault(study => study.Suite == suite);

    // ---- Rows, panes, titles and formatting. ---------------------------

    /// <summary>
    /// The rows a band needs before it can be drawn into at all: its own borders plus
    /// the content rows its table needs to say anything.
    /// </summary>
    private static int Content(LedgerPaneKind kind) => kind switch
    {
        LedgerPaneKind.Summary => SummaryHeight,
        LedgerPaneKind.Agent => AgentHeight,
        LedgerPaneKind.Studies => StudiesHeaderOnly,
        _ => DetailMinimum,
    };

    /// <summary>
    /// Draws one table row from its columns, with a blank column between each. A column
    /// that does not fit the pane's remaining columns is cut and marked, and no column
    /// after it is drawn — which is what stops a narrow terminal showing a number whose
    /// heading was cut away.
    /// </summary>
    private static void DrawTable(
        LedgerRequest request,
        CellBuffer cells,
        LedgerPane pane,
        int row,
        int inner,
        IReadOnlyList<Column> columns,
        IReadOnlyList<string> values,
        Rgb? foreground)
    {
        var builder = new StringBuilder();
        var used = 0;

        for (var index = 0; index < columns.Count && index < values.Count; index++)
        {
            var column = columns[index];
            var take = Math.Min(column.Width, inner - used);

            if (take <= 0)
            {
                break;
            }

            var clipped = CellText.Clip(values[index], take, request.Glyphs);
            builder.Append(take < column.Width ? clipped : Align(clipped, column));
            used += take;

            if (take < column.Width)
            {
                break;
            }

            // The blank between columns, taken from the pane and never from the value,
            // so two columns can never run together into one word.
            if (used < inner)
            {
                builder.Append(' ');
                used++;
            }
        }

        cells.DrawText(pane.Area.X + 1, row, builder.ToString(), new Cell(' ', foreground, request.PanelFill));
    }

    private static string Align(string text, Column column) =>
        column.Alignment == Alignment.Right ? text.PadLeft(column.Width) : text.PadRight(column.Width);

    /// <summary>
    /// The columns of a whole row, gaps included — the width a selected row is filled
    /// across, so the selection covers its values and not the pane beyond them.
    /// </summary>
    private static int RowWidth(IReadOnlyList<Column> columns) =>
        columns.Sum(column => column.Width) + (Math.Max(0, columns.Count - 1) * ColumnGap);

    private static string Pad(string text, int width) => text.Length >= width ? text : text.PadRight(width);

    private static void Draw(LedgerRequest request, CellBuffer cells, int x, int row, int inner, string text, Rgb? foreground) =>
        cells.DrawText(x, row, Clip(request, text, inner), new Cell(' ', foreground, request.PanelFill));

    private static string[] Headers(IReadOnlyList<Column> columns) =>
        [.. columns.Select(column => column.Header)];

    private static void DrawPane(
        LedgerRequest request,
        CellBuffer cells,
        BorderGlyphs frame,
        LedgerPane pane,
        string title)
    {
        cells.Fill(pane.Area.X, pane.Area.Y, pane.Area.Width, pane.Area.Height, new Cell(' ', null, request.PanelFill));
        cells.DrawBorder(pane.Area.X, pane.Area.Y, pane.Area.Width, pane.Area.Height, frame, new Cell(' ', Theme.Sage, request.PanelFill));
        cells.DrawText(
            pane.Area.X + 2,
            pane.Area.Y,
            Clip(request, $" {title} ", Math.Max(0, pane.Area.Width - 4)),
            new Cell(' ', Palette.Accent, request.PanelFill, CellAttributes.Bold));
    }

    /// <summary>
    /// A pane's title. The per-seed band names its suite, the two policies it compares,
    /// and what a W or an L in it means — so a letter on screen is never undefined.
    /// </summary>
    private static string Title(LedgerPane pane, LedgerRequest request)
    {
        if (pane.Kind != LedgerPaneKind.Detail)
        {
            return pane.Title;
        }

        if (pane.Title == "COMPARE")
        {
            return "COMPARE  suites aligned by name; protocols checked";
        }

        var study = CurrentStudy(request);
        return study is null
            ? "PER-SEED"
            : $"PER-SEED {study.Suite}  {study.TargetPolicy} vs {study.BaselinePolicy}  W/L = policy's seat";
    }

    private static string Title(LedgerRequest request)
    {
        var position = request.Artifacts.Count > 1
            ? $"  {Invariant(request.ArtifactIndex + 1)}/{Invariant(request.Artifacts.Count)}"
            : string.Empty;

        return $"LATTICE TUI  ledger{position}  {Selected(request)?.Label ?? string.Empty}";
    }

    private static LedgerArtifact? Selected(LedgerRequest request) =>
        request.Artifacts.Count == 0
            ? null
            : request.Artifacts[Math.Clamp(request.ArtifactIndex, 0, request.Artifacts.Count - 1)];

    private static LedgerStudy? CurrentStudy(LedgerRequest request)
    {
        if (Selected(request) is not { } artifact || artifact.Studies.Count == 0)
        {
            return null;
        }

        return artifact.Studies[Math.Clamp(request.StudyIndex, 0, artifact.Studies.Count - 1)];
    }

    private static string Clip(LedgerRequest request, string text, int width) =>
        CellText.Clip(text, Math.Max(0, width), request.Glyphs);

    private static BorderGlyphs Border(GlyphMode glyphs) =>
        glyphs == GlyphMode.Unicode ? BorderGlyphs.Rounded : BorderGlyphs.Ascii;

    private static string When(DateTime value) =>
        value.ToUniversalTime().ToString(WhenFormat, CultureInfo.InvariantCulture);

    private static string Delta(double value) =>
        (Math.Abs(value) < NothingToShow ? 0.0 : value).ToString(DeltaFormat, CultureInfo.InvariantCulture);

    private static string Rate(double value) =>
        (Math.Abs(value) < NothingToShow ? 0.0 : value).ToString(RateFormat, CultureInfo.InvariantCulture);

    private static string Interval(double lower, double upper) => $"[{Delta(lower)},{Delta(upper)}]";

    private static string Or(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private enum Alignment
    {
        Left,
        Right,
    }

    private readonly record struct Column(string Header, int Width, Alignment Alignment);

    /// <summary>A pane's rectangle, in cells.</summary>
    public readonly record struct Rect(int X, int Y, int Width, int Height);
}
