namespace Lattice.Tui;

/// <summary>
/// What the playback cursor is doing, as the panes need to say it. A value rather
/// than the cursor itself, so a layout can be drawn from literals and never
/// reaches back into a running replay.
/// </summary>
/// <param name="IsPaused">Whether the cursor is stopped.</param>
/// <param name="StepsPerSecond">The speed the cursor is running at.</param>
public readonly record struct PlaybackState(bool IsPaused, double StepsPerSecond);

/// <summary>
/// Everything one cockpit frame is composed from.
/// </summary>
/// <param name="Document">The replay being shown.</param>
/// <param name="FrameIndex">Which frame of it is on show.</param>
/// <param name="Size">The terminal's size, which the result matches exactly.</param>
/// <param name="Glyphs">Which glyph vocabulary to draw from.</param>
/// <param name="PanelFill">The panel background, or <c>null</c> for the terminal's own.</param>
/// <param name="Phase">How far through the current frame's dwell time this frame is, in [0, 1).</param>
/// <param name="Playback">What the cursor is doing.</param>
/// <param name="Live">
/// The live episode state, or null for a finished recording. Null is the whole of
/// the difference: the world, scoreboard and log are drawn from the same frames
/// either way, and only the timeline and the key hints read this.
/// </param>
/// <param name="HidePanels">
/// Whether the side panes are hidden so the world pane gets the room. Display
/// only: it changes which panes are drawn and nothing about the frames, the
/// cursor or the recording behind them.
/// </param>
public sealed record CockpitRequest(
    ReplayDocument Document,
    int FrameIndex,
    PaneSize Size,
    GlyphMode Glyphs,
    Rgb? PanelFill,
    double Phase,
    PlaybackState Playback,
    LiveState? Live = null,
    bool HidePanels = false);

/// <summary>
/// The cockpit: the world, the scoreboard, the event log, the timeline and the
/// key hints, framed once around the whole terminal.
/// </summary>
/// <remarks>
/// <para>
/// The layout is pure — a request in, a grid the size of the terminal out — so
/// the whole screen can be asserted against its exact cells with no terminal
/// attached. The panes are drawn from the same primitives the rest of the library
/// already has: a rounded border, filled text, and the world renderer, whose grid
/// is copied into the world pane rather than drawn a second time.
/// </para>
/// <para>
/// <b>Below the minimum size the whole cockpit is refused</b>, because four panes
/// and a timeline at 60 columns is four unreadable columns each. A smaller
/// terminal gets the world alone with a line saying what size the cockpit needs.
/// The notice leads with the size it measured so that it survives being clipped.
/// </para>
/// </remarks>
public static class CockpitLayout
{
    /// <summary>The narrowest terminal the whole cockpit is drawn for.</summary>
    public const int MinimumWidth = TerminalCapabilities.MinimumWidth;

    /// <summary>The shortest terminal the whole cockpit is drawn for.</summary>
    public const int MinimumHeight = TerminalCapabilities.MinimumHeight;

    /// <summary>
    /// The key hints are drawn on the screen's own bottom border: the one row no
    /// pane can use, and the one a reader's eye already treats as frame rather than
    /// as content.
    /// </summary>
    public const int KeyHintRowFromBottom = 1;

    /// <summary>
    /// The columns the right-hand column of panes occupies. Sized so that at the
    /// minimum terminal the world pane's inner area is 63x23 — the size the golden
    /// frames are drawn at, so the recorded map looks the same in the cockpit as in
    /// the goldens.
    /// </summary>
    private const int RightColumnWidth = 32;

    /// <summary>The rows the scoreboard occupies when the cockpit is at its minimum height.</summary>
    private const int ScoreboardHeight = 12;

    /// <summary>The smallest inner pane the fallback will draw a map into.</summary>
    private const int FallbackMinimumInnerWidth = 20;

    /// <summary>The smallest inner pane height the fallback will draw a map into.</summary>
    private const int FallbackMinimumInnerHeight = 8;

    /// <summary>How many recorded steps the agent trail reaches back over.</summary>
    public const int TrailFrames = 3;

    /// <summary>How many characters of a recorded digest the scoreboard shows.</summary>
    public const int DigestCharacters = 12;

    /// <summary>
    /// The columns each scoreboard column occupies, named so the headings and the
    /// rows cannot drift apart. The role column is cut to its own width before the
    /// row is composed, which is what keeps a long recorded role from pushing the
    /// score off the end of the pane.
    /// </summary>
    private const int SlotColumnWidth = 5;

    /// <summary>The columns a role name is cut to, before the row is composed.</summary>
    private const int RoleColumnWidth = 12;

    /// <summary>The columns the zone column occupies.</summary>
    private const int ZoneColumnWidth = 6;

    /// <summary>The columns the score column occupies.</summary>
    private const int ScoreColumnWidth = 5;

    /// <summary>The columns before the slot column: the agent glyph and a space.</summary>
    private const int RowIndentWidth = 2;

    /// <summary>What a pane's title looks like in its top border.</summary>
    public static string PaneTitle(string title) => $" {title} ";

    /// <summary>The row the key hints are on for a terminal of this height.</summary>
    public static int KeyHintRow(PaneSize size) => size.Height - KeyHintRowFromBottom;

    /// <summary>Whether this terminal gets the whole cockpit.</summary>
    public static bool IsCockpit(PaneSize size) =>
        size.Width >= MinimumWidth && size.Height >= MinimumHeight;

    /// <summary>The line a smaller terminal is shown instead of the cockpit.</summary>
    public static string ResizeNotice(PaneSize size) =>
        $"terminal is {Invariant(size.Width)}x{Invariant(size.Height)}; " +
        $"the cockpit needs {MinimumWidth}x{MinimumHeight}; resize for the full layout";

    /// <summary>
    /// The title row: the product, what kind of run this is, and the recording's own
    /// name.
    /// </summary>
    /// <remarks>
    /// The mode word comes from the same one fact the timeline's LIVE label and the
    /// live key hints read — whether the frame carries live state. A title calling a
    /// live run a replay is contradicted by the row directly underneath it, and a
    /// second flag passed in alongside <see cref="LiveState"/> could fall out of step
    /// with it. Lower case to match the rest of the row, and the same word the LIVE
    /// label uses.
    /// </remarks>
    private static string TitleRow(LiveState? live, string recordingTitle) =>
        $"LATTICE TUI  {(live is not null ? "live" : "replay")}  {recordingTitle}";

    /// <summary>Draws one cockpit frame.</summary>
    /// <remarks>
    /// A terminal too small to frame — one cell, or a shape with no room for a box —
    /// is not an error: the grid comes back at exactly the size asked for with
    /// whatever fits in it drawn, because a resize nobody asked for must not take
    /// the replay down.
    /// </remarks>
    public static CellBuffer Render(CockpitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var size = request.Size;
        var cells = new CellBuffer(size.Width, size.Height);
        var frame = Border(request.Glyphs);
        var palette = new PaletteRoles();

        if (request.PanelFill is { } fill)
        {
            cells.Fill(0, 0, size.Width, size.Height, new Cell(' ', null, fill));
        }

        if (size.Width < 2 || size.Height < 2)
        {
            return cells;
        }

        cells.DrawBorder(0, 0, size.Width, size.Height, frame, new Cell(' ', palette.Sage, request.PanelFill));
        cells.DrawText(
            2,
            0,
            Clip(request, TitleRow(request.Live, Title(request.Document)), Math.Max(0, size.Width - 4)),
            new Cell(' ', palette.Accent, request.PanelFill, CellAttributes.Bold));

        if (IsCockpit(size))
        {
            DrawCockpit(request, cells, frame, palette);
        }
        else
        {
            DrawFallback(request, cells, frame, palette);
        }

        return cells;
    }

    private static void DrawCockpit(CockpitRequest request, CellBuffer cells, BorderGlyphs frame, PaletteRoles palette)
    {
        if (request.HidePanels)
        {
            DrawHiddenCockpit(request, cells, frame, palette);
            return;
        }

        var size = request.Size;
        var world = new Rect(1, 1, size.Width - 2 - RightColumnWidth - 1, size.Height - 5);
        var scoreboard = new Rect(world.X + world.Width + 1, 1, RightColumnWidth, ScoreboardHeight);
        var log = new Rect(scoreboard.X, scoreboard.Y + scoreboard.Height, RightColumnWidth, world.Height - ScoreboardHeight);
        var timeline = new Rect(1, size.Height - 4, size.Width - 2, 3);

        DrawPane(request, cells, frame, palette, world, "WORLD");
        DrawPane(request, cells, frame, palette, scoreboard, "SCOREBOARD");
        DrawPane(request, cells, frame, palette, log, "EVENT LOG");
        DrawPane(request, cells, frame, palette, timeline, "TIMELINE");

        DrawWorld(request, cells, world);

        DrawScoreboard(request, cells, palette, scoreboard);
        DrawEventLog(request, cells, palette, log);
        DrawTimeline(request, cells, palette, timeline);
        DrawKeyHints(request, cells, palette, hidden: false);
    }

    /// <summary>
    /// The hidden-panels cockpit: the world pane alone, over the whole terminal
    /// minus the screen's own frame, with the key hints still on the bottom
    /// border. The scoreboard, the event log and the timeline are not drawn —
    /// and nothing else changes: the same world at a bigger size, the same
    /// frame, the same title, and a hint row that names the way back.
    /// </summary>
    private static void DrawHiddenCockpit(CockpitRequest request, CellBuffer cells, BorderGlyphs frame, PaletteRoles palette)
    {
        var size = request.Size;
        var world = new Rect(1, 1, size.Width - 2, size.Height - 2);

        DrawPane(request, cells, frame, palette, world, "WORLD");
        DrawWorld(request, cells, world);
        DrawKeyHints(request, cells, palette, hidden: true);
    }

    private static void DrawFallback(CockpitRequest request, CellBuffer cells, BorderGlyphs frame, PaletteRoles palette)
    {
        var size = request.Size;
        if (size.Height >= 2)
        {
            cells.DrawText(2, 1, Clip(request, ResizeNotice(size), Math.Max(0, size.Width - 4)), new Cell(' ', palette.Accent, request.PanelFill));
        }

        var innerWidth = size.Width - 4;
        var innerHeight = size.Height - 8;
        if (innerWidth < FallbackMinimumInnerWidth || innerHeight < FallbackMinimumInnerHeight)
        {
            return;
        }

        var pane = new Rect(1, 3, size.Width - 2, size.Height - 6);
        DrawPane(request, cells, frame, palette, pane, "WORLD");

        DrawWorld(request, cells, pane);

        // The controls stay on screen at this size too. A reader who cannot see
        // them cannot use them, and the hint row costs one line of a pane that is
        // not there anyway.
        DrawKeyHints(request, cells, palette, hidden: false);
    }

    /// <summary>
    /// The world pane's contents: the renderer asked for at exactly the pane's
    /// inner size, then copied in. One path for the cockpit and the fallback, so
    /// the single-pane screen is the same world at a different size rather than a
    /// second drawing of it.
    /// </summary>
    private static void DrawWorld(CockpitRequest request, CellBuffer cells, Rect pane)
    {
        var world = WorldRenderer.Render(new WorldRenderRequest(
            request.Document.Map,
            request.Frame(),
            request.Document.TrailBefore(request.FrameIndex, TrailFrames),
            new PaneSize(pane.Width - 2, pane.Height - 2),
            request.Glyphs,
            request.PanelFill,
            request.Phase));

        Blit(world.Cells, cells, pane.X + 1, pane.Y + 1);
    }

    /// <summary>
    /// The scoreboard: one row per agent in its slot's accent, and the recorded
    /// facts about the tick — its number, the claims it lists, and the digest it
    /// carries or the fact that it carries none.
    /// </summary>
    private static void DrawScoreboard(CockpitRequest request, CellBuffer cells, PaletteRoles palette, Rect pane)
    {
        var frame = request.Document[request.FrameIndex];
        var rows = new List<(string Text, Rgb? Accent)>();
        var roles = CockpitEpisodes.AgentRoles(request.Document, request.Live);

        foreach (var agent in frame.Agents)
        {
            var recorded = roles is not null && agent.Slot < roles.Count && !string.IsNullOrEmpty(roles[agent.Slot])
                ? roles[agent.Slot]
                : "A" + agent.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var glyph = GlyphModes.Glyph(
                agent.Transit is null ? Glyphs.FilledDiamond : Glyphs.HollowDiamond,
                request.Glyphs);
            // Where the agent is: the zone it occupies, or the zone it is heading
            // for while its recorded countdown runs.
            var where = agent.Transit is null
                ? Invariant(agent.ZoneId)
                : "->" + Invariant(agent.Transit.ToZoneId);

            rows.Add((
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{glyph} {Invariant(agent.Slot),-5}{Role(request, recorded),-RoleColumnWidth}" +
                    $"{where,-ZoneColumnWidth}{Invariant(agent.Score),ScoreColumnWidth}"),
                Theme.AgentSlot(agent.Slot)));
        }

        var width = pane.Width - 2;
        var row = pane.Y + 1;

        // The column headings: a scoreboard whose columns are only
        // discoverable by counting is a scoreboard nobody reads. They stand under
        // the same indent the rows use, so each heading is over its own column.
        cells.DrawText(
            pane.X + 1,
            row++,
            Clip(
                request,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{new string(' ', RowIndentWidth)}{"slot",-SlotColumnWidth}{"role",-RoleColumnWidth}" +
                    $"{"zone",-ZoneColumnWidth}{"score",ScoreColumnWidth}"),
                width),
            new Cell(' ', palette.TableHeader, request.PanelFill, CellAttributes.Bold));

        foreach (var (text, accent) in rows)
        {
            if (row >= pane.Y + pane.Height - 1)
            {
                break;
            }

            cells.DrawText(
                pane.X + 1,
                row++,
                Clip(request, text, width),
                new Cell(' ', accent ?? palette.TextPrimary, request.PanelFill));
        }

        var claims = frame.Claims.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var resources = request.Document.Map.Resources.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var facts = new List<string>
        {
            $"tick    {Invariant(frame.Tick)}/{Invariant(request.Document.Header.RecordedSteps)}",
            $"claims  {claims}/{resources}",
            "digest  " + (frame.StateDigest is { Length: > 0 } digest
                ? Digest(request, digest)
                : "not recorded"),
        };

        if (frame.IsTerminal)
        {
            // The recorded end of the episode, in the recording's own words — and,
            // for a live episode, the same value the timeline shows, read through the
            // one helper both panes use. A blank reason is not a reason, and a live
            // episode's reason is the episode's rather than the frame's, so the two
            // panes cannot end up saying different things about the same ending.
            facts.Add("end     " + EndReason(request, frame));
            facts.Add("winner  " + (frame.WinnerSlot is { } winner ? Invariant(winner) : "none"));
        }

        if (request.Document.Header.DynamicRuleCount > 0)
        {
            // The honesty line: this pane shows the capacities the recorded map
            // declares, and a recording with a topology schedule has per-tick
            // capacities the format does not carry as values.
            facts.Add("chokes  map capacities; per-tick overrides not shown");
        }

        foreach (var fact in facts)
        {
            if (row >= pane.Y + pane.Height - 1)
            {
                break;
            }

            cells.DrawText(
                pane.X + 1,
                row++,
                Clip(request, fact, width),
                new Cell(' ', palette.TextPrimary, request.PanelFill));
        }
    }

    /// <summary>
    /// The end value the scoreboard prints: the one helper both panes read. A live
    /// episode's reason is the episode's own, so this row and the timeline cannot
    /// disagree about how the episode ended; a recording's is the frame's, and
    /// whitespace is not a reason — <see cref="CockpitEpisodes.FinishedReason"/> is
    /// the only test either pane applies.
    /// </summary>
    private static string EndReason(CockpitRequest request, ReplayFrame frame) =>
        CockpitEpisodes.FinishedReason(frame, request.Live);

    /// <summary>
    /// A recorded role in its own column: made column-safe and cut to exactly the
    /// column's width, marked when it was cut. Every surviving character is one
    /// column, so padding to the width afterwards is padding in columns too, and
    /// the zone and score columns that follow land where they always land.
    /// </summary>
    private static string Role(CockpitRequest request, string recorded) =>
        CellText.Clip(recorded, RoleColumnWidth, request.Glyphs).PadRight(RoleColumnWidth);

    /// <summary>
    /// The leading characters of a recorded digest, made column-safe first so a cut
    /// through it cannot leave half a surrogate in a cell.
    /// </summary>
    private static string Digest(CockpitRequest request, string digest) =>
        CellText.Sanitize(digest, request.Glyphs)[..DigestCharacters] + CellText.Ellipsis(request.Glyphs);

    /// <summary>
    /// The event log: one compact row per recorded step, oldest at the top of the
    /// pane, the step on show picked out. A start frame has no step behind it, and
    /// says so rather than showing a blank pane.
    /// </summary>
    private static void DrawEventLog(CockpitRequest request, CellBuffer cells, PaletteRoles palette, Rect pane)
    {
        var capacity = pane.Height - 2;
        if (capacity <= 0)
        {
            return;
        }

        var document = request.Document;
        var first = System.Math.Max(1, request.FrameIndex - capacity + 1);
        var row = pane.Y + 1;

        if (request.FrameIndex == 0)
        {
            cells.DrawText(
                pane.X + 1,
                row,
                // A live episode has produced nothing yet and has not been
                // recorded; calling it a recording would be a claim about its
                // provenance that nothing supports.
                Clip(
                    request,
                    request.Live is null ? "start of recording" : "start of episode",
                    pane.Width - 2),
                new Cell(' ', palette.Secondary, request.PanelFill));
            row++;
        }

        for (var index = first; index <= request.FrameIndex && row < pane.Y + pane.Height - 1; index++)
        {
            var frame = document[index];
            var current = index == request.FrameIndex;
            var line = $"{Invariant(frame.Tick),3}  {frame.Actions}";
            var style = current
                ? new Cell(' ', Theme.SelectionForeground, Theme.SelectionBackground, CellAttributes.Bold)
                : new Cell(' ', palette.TextPrimary, request.PanelFill);

            cells.DrawText(pane.X + 1, row++, Clip(request, line, pane.Width - 2), style);
        }

        // Where the recording came from, in the recording's own terms: the seed and
        // wire format it declares, and the descriptor digest when it carries one.
        if (row < pane.Y + pane.Height - 1)
        {
            var header = request.Document.Header;
            var provenance = $"seed {CockpitEpisodes.Seed(document, request.Live)}" +
                $"  schema {CockpitEpisodes.SchemaVersion(document, request.Live)}  " +
                "sha256 " + (request.Live is not null
                    ? CockpitEpisodes.NotRecorded
                    : header.ScenarioDigest is { Length: > 0 } digest
                        ? Digest(request, digest)
                        : CockpitEpisodes.NotRecorded);
            cells.DrawText(
                pane.X + 1,
                row,
                Clip(request, provenance, pane.Width - 2),
                new Cell(' ', palette.Secondary, request.PanelFill));
        }
    }

    /// <summary>
    /// The timeline: the recorded tick over the recorded step count, a bar filled
    /// in proportion, and the cursor's own state. Every number on it is either read
    /// from the recording or a setting the reader can change and see.
    /// </summary>
    private static void DrawTimeline(CockpitRequest request, CellBuffer cells, PaletteRoles palette, Rect pane)
    {
        var document = request.Document;
        var live = request.Live;
        var maximum = CockpitEpisodes.MaximumTicks(document, live);
        var produced = CockpitEpisodes.ProducedTicks(document, live);
        var tick = request.Frame().Tick;
        var row = pane.Y + 1;

        var state = new Cell(' ', palette.TextPrimary, request.PanelFill);

        // A live episode names itself, and says how much of it exists: the tick on
        // show, the ticks produced so far, and the budget it is measured against.
        // A recording has already produced all of its steps, so it states the two
        // numbers as one fraction and says no such thing.
        var label = live is null
            ? $"tick {Invariant(tick)}/{Invariant(maximum)}  "
            : $"LIVE tick {Invariant(tick)}/{Invariant(produced)} of {Invariant(maximum)}  ";
        label = Clip(request, label, pane.Width - 2);
        cells.DrawText(pane.X + 1, row, label, state);

        // The bar's position is the produced count over the budget for a live
        // episode, so a reader sees how much is left as well as how much has run.
        var position = live is null ? tick : produced;
        var suffix = TimelineSuffix(request);
        var track = pane.Width - 2 - label.Length - suffix.Length;

        if (track > 0)
        {
            // One cell per recorded tick while the recording is short enough to
            // show at that resolution, so the bar is a ruler the reader can count
            // rather than a smear; longer recordings fall back to a proportional
            // fill of the same track.
            var cellsWide = Math.Min(maximum, track);
            var filled = maximum <= 0 || cellsWide <= 0
                ? 0
                : Math.Min(cellsWide, ((position * cellsWide * 2) + maximum) / (maximum * 2));

            for (var column = 0; column < track; column++)
            {
                var played = column < filled;
                var style = played
                    ? new Cell(' ', column == filled - 1 ? palette.AccentBright : palette.Accent, request.PanelFill)
                    : new Cell(' ', palette.TextDim, request.PanelFill);
                cells[pane.X + 1 + label.Length + column, row] = style with
                {
                    Glyph = GlyphModes.Glyph(played ? Glyphs.FullBlock : Glyphs.LightShade, request.Glyphs),
                };
            }
        }

        cells.DrawText(
            pane.X + 1 + label.Length + Math.Max(track, 0),
            row,
            Clip(request, suffix, Math.Max(0, pane.Width - 2 - label.Length - Math.Max(track, 0))),
            state);
    }

    /// <summary>
    /// What the timeline says after the bar. A transient notice comes first: a
    /// viewer that is stopping, or a restart waiting on an agent, is telling the
    /// reader something they cannot infer from a paused cursor. Then the recorded
    /// reason the episode ended, in the recording's own words. Then the cursor's
    /// own state, which is all a frame that has neither has to say.
    /// </summary>
    private static string TimelineSuffix(CockpitRequest request)
    {
        if (request.Live is { Notice: { Length: > 0 } notice })
        {
            return "  " + notice;
        }

        if (request.Live is { IsFinished: true } finished)
        {
            return "  finished: " + finished.FinishedReason;
        }

        var stateWord = request.Playback.IsPaused ? "paused" : "playing";
        var speed = request.Playback.StepsPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"  {stateWord} {speed} steps/s";
    }

    /// <summary>
    /// The key hints: plain ASCII, every control the reader can press, in one row.
    /// ASCII unconditionally, so the one row that explains the keys is never the
    /// row a terminal cannot encode.
    /// </summary>
    private static void DrawKeyHints(CockpitRequest request, CellBuffer cells, PaletteRoles palette, bool hidden)
    {
        cells.DrawText(
            2,
            KeyHintRow(request.Size),
            KeyHintsFor(request.Live is not null, hidden),
            new Cell(' ', palette.KeyHint, request.PanelFill));
    }

    /// <summary>
    /// The controls, as the hint row states them. Kept beside the drawing so the
    /// two cannot drift: a control added to one has to be named in the other.
    /// </summary>
    public const string KeyHints = "space pause  n/p step  < > speed  [ ] scrub  home/end jump  q quit";

    /// <summary>
    /// The controls a live episode has, which are not the recording's: there is no
    /// recording to scrub to the end of, and there is a restart to name. Says LIVE
    /// so the reader knows the episode in front of them is still being computed.
    /// </summary>
    public const string LiveKeyHints =
        "LIVE  space pause  n tick  p back  < > speed  [ ] speed  home/end jump  r restart  q quit";

    /// <summary>
    /// The controls while the side panes are hidden: the way back first, then the
    /// core controls. Short by design, so the row fits the smallest cockpit with
    /// room to spare; the full control list is one keypress away in normal mode.
    /// </summary>
    public const string HiddenKeyHints =
        "h show panels  space pause  n/p step  < > speed  [ ] scrub  q quit";

    /// <summary>
    /// The controls a live episode has while the side panes are hidden: the way
    /// back first, then the core live controls. Says LIVE, as the normal live row
    /// does, so the reader knows the episode is still being computed.
    /// </summary>
    public const string HiddenLiveKeyHints =
        "LIVE  h show panels  space pause  n tick  p back  < > [ ] speed  r restart  q quit";

    /// <summary>
    /// The hint row this frame draws, from the one place that decides, so a caller
    /// and the renderer can never disagree about which row is on screen.
    /// </summary>
    public static string KeyHintsFor(bool live) => KeyHintsFor(live, hidden: false);

    /// <summary>
    /// The hint row this frame draws when the side panes may be hidden. A hidden
    /// frame names the way back; a normal frame draws exactly what it always drew.
    /// </summary>
    public static string KeyHintsFor(bool live, bool hidden) => (live, hidden) switch
    {
        (false, false) => KeyHints,
        (true, false) => LiveKeyHints,
        (false, true) => HiddenKeyHints,
        (true, true) => HiddenLiveKeyHints,
    };

    private static void DrawPane(
        CockpitRequest request,
        CellBuffer cells,
        BorderGlyphs frame,
        PaletteRoles palette,
        Rect pane,
        string title)
    {
        cells.Fill(pane.X, pane.Y, pane.Width, pane.Height, new Cell(' ', null, request.PanelFill));
        cells.DrawBorder(pane.X, pane.Y, pane.Width, pane.Height, frame, new Cell(' ', palette.Sage, request.PanelFill));
        cells.DrawText(
            pane.X + 2,
            pane.Y,
            Clip(request, PaneTitle(title), Math.Max(0, pane.Width - 4)),
            new Cell(' ', palette.Accent, request.PanelFill, CellAttributes.Bold));
    }

    /// <summary>
    /// Copies a grid into another at an offset. A plain copy rather than a
    /// redraw: the world renderer already decided what every cell says, and
    /// drawing it twice is a second answer to the same question.
    /// </summary>
    private static void Blit(CellBuffer source, CellBuffer target, int left, int top)
    {
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                target[left + x, top + y] = source[x, y];
            }
        }
    }

    /// <summary>
    /// A pane's text, made column-safe and cut to what the pane can actually hold.
    /// <para>
    /// <see cref="CellBuffer.DrawText"/> clips at the edge of the buffer, not at
    /// the edge of a pane, so a row longer than its pane would be written straight
    /// over the pane's border and the screen's own frame — a long recorded action
    /// list does exactly that. Cutting the string here is what keeps a pane's
    /// contents inside the pane, and <see cref="CellText"/> marks the cut so a
    /// clipped row cannot be mistaken for a short one.
    /// </para>
    /// </summary>
    private static string Clip(CockpitRequest request, string text, int width) =>
        CellText.Clip(text, Math.Max(0, width), request.Glyphs);

    private static BorderGlyphs Border(GlyphMode glyphs) =>
        glyphs == GlyphMode.Unicode ? BorderGlyphs.Rounded : BorderGlyphs.Ascii;

    private static string Title(ReplayDocument document) =>
        document.Header.Scenario is { Length: > 0 } scenario
            ? scenario
            : "recorded episode";

    private static string Invariant(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The colour roles a frame is painted in, taken from the one theme.</summary>
    private readonly record struct PaletteRoles
    {
        internal Rgb Sage => Theme.Sage;

        internal Rgb Accent => Palette.Accent;

        internal Rgb AccentBright => Palette.AccentBright;

        internal Rgb TextPrimary => Palette.TextPrimary;

        internal Rgb TextDim => Palette.TextDim;

        internal Rgb Secondary => Theme.SecondaryText;

        internal Rgb TableHeader => Theme.TableHeader;

        internal Rgb KeyHint => Theme.KeyHint;
    }

    /// <summary>A pane's rectangle, in cells.</summary>
    private readonly record struct Rect(int X, int Y, int Width, int Height);
}

internal static class CockpitRequestExtensions
{
    /// <summary>The frame this request is showing, clamped to the replay.</summary>
    internal static ReplayFrame Frame(this CockpitRequest request) =>
        request.Document[request.Document.Clamp(request.FrameIndex)];
}