using System.Collections.Immutable;

namespace Lattice.Tui;

/// <summary>
/// One agent's crossing of an edge, as a drawing needs it: which way it is
/// going, and how many ticks of the crossing are left.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TotalTicks"/> is the edge's full crossing time, resolved once from
/// the recording's own map and its own transit speed, so the fraction an agent
/// has covered is <c>(TotalTicks - RemainingTicks) / TotalTicks</c> — a number the
/// recording determines and this type only carries. Interpolating between ticks
/// is a drawing concern and is done by adding a fraction of a tick to
/// <see cref="RemainingTicks"/> at draw time; nothing here is ever rounded into a
/// different tick.
/// </para>
/// </remarks>
/// <param name="FromZoneId">The zone the agent left.</param>
/// <param name="ToZoneId">The zone the agent is heading for.</param>
/// <param name="RemainingTicks">
/// Ticks until arrival, counting the tick in which the agent arrives.
/// </param>
/// <param name="TotalTicks">The whole crossing, as the recording's speed resolves it.</param>
public sealed record WorldTransit(int FromZoneId, int ToZoneId, int RemainingTicks, int TotalTicks)
{
    /// <summary>
    /// The fraction of the crossing already covered, in [0, 1]. Integer
    /// arithmetic on a fixed denominator: a drawing that placed an agent by a
    /// floating-point ratio would put it a cell either side of the same place on a
    /// different runtime.
    /// </summary>
    /// <param name="extraTicks">
    /// A fraction of one further tick to add to the covered distance, which is how
    /// a live redraw slides an agent along its edge between two recorded ticks.
    /// </param>
    public double CoveredFraction(double extraTicks = 0)
    {
        if (TotalTicks < 1)
        {
            return 1.0;
        }

        var covered = TotalTicks - RemainingTicks + extraTicks;
        if (covered <= 0)
        {
            return 0.0;
        }

        return covered >= TotalTicks ? 1.0 : covered / (double)TotalTicks;
    }
}

/// <summary>
/// One agent as a drawing needs it: the slot that fixes its colour, the zone it
/// occupies, the score the recording gives it, and any crossing in progress.
/// </summary>
/// <param name="Slot">The agent's slot, which is the index its accent is derived from.</param>
/// <param name="ZoneId">
/// The zone the agent occupies. While a crossing is in progress this is the
/// departure zone, which is where the recording says the agent still is.
/// </param>
/// <param name="Score">The score the recording carries for this agent at this tick.</param>
/// <param name="Transit">The crossing in progress, or <c>null</c> when the agent is at a zone.</param>
public sealed record WorldAgent(int Slot, int ZoneId, int Score, WorldTransit? Transit = null);

/// <summary>
/// One immutable frame of a replay: the world as the recording states it after a
/// tick, plus everything a pane is allowed to say about that tick.
/// </summary>
/// <remarks>
/// <para>
/// Frames are values, not views: the agent and claim sequences are copied in and
/// copied out, so nothing downstream can reach back and change the recording's
/// own state. Two frames built from the same recording are equal by value, which
/// is what makes stepping back and forth a comparison rather than a hope.
/// </para>
/// <para>
/// Every field is either read from the recording or absent. <see cref="Actions"/>
/// is the recorded action list already formatted, and is <c>null</c> on the start
/// frame because no action produced it; <see cref="StateDigest"/> is the recorded
/// digest or <c>null</c> when the recording carries none, and the panes print
/// "not recorded" for it rather than inventing one.
/// </para>
/// </remarks>
public sealed class ReplayFrame : IEquatable<ReplayFrame>
{
    /// <summary>
    /// Builds one frame. <paramref name="tick"/> is the recorded tick this frame
    /// stands at, and is 0 on the start frame.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tick"/> is negative.</exception>
    public ReplayFrame(
        int tick,
        bool isStart,
        WorldAgent[] agents,
        int[] claims,
        string? actions,
        string? stateDigest,
        bool isTerminal,
        string? terminalReason,
        int? winnerSlot)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentOutOfRangeException.ThrowIfNegative(tick);

        Tick = tick;
        IsStart = isStart;
        Agents = Array.AsReadOnly((WorldAgent[])agents.Clone());
        Claims = Array.AsReadOnly((int[])claims.Clone());
        Actions = actions;
        StateDigest = stateDigest;
        IsTerminal = isTerminal;
        TerminalReason = terminalReason;
        WinnerSlot = winnerSlot;
    }

    /// <summary>The recorded tick this frame stands at; 0 on the start frame.</summary>
    public int Tick { get; }

    /// <summary>
    /// Whether this is the start frame — the state before any recorded step, at
    /// tick 0. A recording of N steps therefore has N + 1 frames.
    /// </summary>
    public bool IsStart { get; }

    /// <summary>The agents as the recording places them at this tick.</summary>
    public IReadOnlyList<WorldAgent> Agents { get; }

    /// <summary>The resource ids the recording lists as claimed at this tick.</summary>
    public IReadOnlyList<int> Claims { get; }

    /// <summary>
    /// The recorded actions of the step this frame is the result of, already
    /// formatted for one row; <c>null</c> on the start frame, which no step
    /// produced.
    /// </summary>
    public string? Actions { get; }

    /// <summary>
    /// The per-tick state digest this step carries, or <c>null</c> when the
    /// recording carries none. Never a substitute: a pane that wants a digest and
    /// has none says the recording did not carry it.
    /// </summary>
    public string? StateDigest { get; }

    /// <summary>Whether the recording marks this tick terminal.</summary>
    public bool IsTerminal { get; }

    /// <summary>The recorded terminal reason, when the tick is terminal.</summary>
    public string? TerminalReason { get; }

    /// <summary>The recorded winning slot, when the tick is terminal and decided.</summary>
    public int? WinnerSlot { get; }

    /// <summary>Value equality across every field, comparing the sequences by content.</summary>
    public bool Equals(ReplayFrame? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Tick != other.Tick
            || IsStart != other.IsStart
            || IsTerminal != other.IsTerminal
            || WinnerSlot != other.WinnerSlot
            || Actions != other.Actions
            || StateDigest != other.StateDigest
            || TerminalReason != other.TerminalReason
            || Agents.Count != other.Agents.Count
            || Claims.Count != other.Claims.Count)
        {
            return false;
        }

        for (var i = 0; i < Agents.Count; i++)
        {
            if (Agents[i] != other.Agents[i])
            {
                return false;
            }
        }

        for (var i = 0; i < Claims.Count; i++)
        {
            if (Claims[i] != other.Claims[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ReplayFrame);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tick);
        hash.Add(IsStart);
        hash.Add(IsTerminal);
        hash.Add(WinnerSlot);
        hash.Add(Actions);
        hash.Add(StateDigest);
        hash.Add(TerminalReason);
        hash.Add(Agents.Count);
        hash.Add(Claims.Count);
        return hash.ToHashCode();
    }
}

/// <summary>
/// What the recording says about itself, as the panes are allowed to show it.
/// Every field is optional because every one of them is optional in the format:
/// a pane that needs one and has none prints "not recorded".
/// </summary>
/// <param name="Seed">The recorded episode seed.</param>
/// <param name="SchemaVersion">The recorded wire-format version.</param>
/// <param name="Scenario">The recorded scenario name, when the recording names one.</param>
/// <param name="AgentRoles">The recorded role per slot, when the recording carries a roster.</param>
/// <param name="RecordedSteps">The step count the recording's own final line states.</param>
/// <param name="ScenarioDigest">The recorded scenario digest, when the recording carries one.</param>
/// <param name="DynamicRuleCount">
/// How many dynamic-topology rules the header declares, or 0 for a static map.
/// The world pane draws the capacities the header's map declares, and a recording
/// with a schedule has per-tick capacities the format does not carry as values —
/// this is what lets a pane say so instead of implying the two are the same.
/// </param>
public sealed record ReplayHeader(
    ulong Seed,
    int SchemaVersion,
    string? Scenario,
    IReadOnlyList<string>? AgentRoles,
    int RecordedSteps,
    string? ScenarioDigest,
    int DynamicRuleCount);

/// <summary>
/// A replayable recording: the map, what the recording says about itself, and
/// one immutable frame per step plus the start frame.
/// </summary>
/// <remarks>
/// Every index move is here and is a move, never a wrap: stepping or scrubbing
/// past either end clamps and reports that nothing moved. The recording's own
/// tick numbers are contiguous from 1, so frame <c>i &gt; 0</c> is the state
/// after step <c>i</c> and its <see cref="ReplayFrame.Tick"/> is that step's
/// recorded tick — the timeline can therefore print the recorded number rather
/// than a count of its own.
/// </remarks>
public sealed class ReplayDocument
{
    /// <summary>
    /// Builds a document from the projected map and its frames.
    /// </summary>
    /// <exception cref="ArgumentException">There are no frames.</exception>
    public ReplayDocument(WorldMap map, ReplayHeader header, IReadOnlyList<ReplayFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0)
        {
            throw new ArgumentException("A replay needs at least the start frame.", nameof(frames));
        }

        Map = map;
        Header = header;
        Frames = frames;
    }

    /// <summary>The map every frame is drawn against.</summary>
    public WorldMap Map { get; }

    /// <summary>What the recording says about itself.</summary>
    public ReplayHeader Header { get; }

    /// <summary>The frames, start frame first, one per recorded step after it.</summary>
    public IReadOnlyList<ReplayFrame> Frames { get; }

    /// <summary>How many frames there are: the recorded steps plus the start frame.</summary>
    public int Count => Frames.Count;

    /// <summary>The index of the start frame.</summary>
    public int FirstIndex => 0;

    /// <summary>The index of the last frame.</summary>
    public int LastIndex => Frames.Count - 1;

    /// <summary>The frame at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the replay.</exception>
    public ReplayFrame this[int index] => Frames[index];

    /// <summary>
    /// An index inside the replay. Any value maps to the nearest end, which is
    /// what keeps a scrub of a fraction past the end from wrapping to the start.
    /// </summary>
    public int Clamp(int index) => index < FirstIndex ? FirstIndex : index > LastIndex ? LastIndex : index;

    /// <summary>The index one step on, clamped at the last frame.</summary>
    public int StepForward(int index) => Clamp(index + 1);

    /// <summary>The index one step back, clamped at the start frame.</summary>
    public int StepBack(int index) => Clamp(index - 1);

    /// <summary>
    /// The up-to-<paramref name="frames"/> earlier frames, oldest first — the
    /// trail the world pane fades. Fewer than asked for when the replay has not
    /// that much history.
    /// </summary>
    public IReadOnlyList<ReplayFrame> TrailBefore(int index, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);

        var taken = Math.Min(frames, index);
        var trail = new ReplayFrame[taken];
        for (var i = 0; i < taken; i++)
        {
            trail[i] = Frames[index - taken + i];
        }

        return trail;
    }
}

/// <summary>
/// The playback cursor over a <see cref="ReplayDocument"/>: which frame is
/// showing, whether it is running, how fast, and how far through the current
/// frame's dwell time it is.
/// </summary>
/// <remarks>
/// <para>
/// The cursor owns no clock of its own: <see cref="Advance"/> takes the elapsed
/// time the caller measured, so a test can place a known duration on the cursor
/// instead of sleeping and hoping. That is the same discipline
/// <see cref="WorkingIndicator"/> follows, and it is what makes "nothing moves
/// while paused" an assertion rather than an observation.
/// </para>
/// <para>
/// Running out of recording stops the cursor at the last frame and pauses it.
/// Stopping rather than wrapping is deliberate: a replay that silently restarted
/// would make the timeline position lie.
/// </para>
/// <para>
/// The key table lives here, beside the state it moves, rather than in the host:
/// a binding and the thing it changes are one fact, and a host that held the
/// bindings would need a second copy of the state to apply them to.
/// </para>
/// </remarks>
public sealed class ReplayPlayback : ICockpitCursor
{
    /// <summary>
    /// The speeds the speed control moves between, in recorded steps per second.
    /// A UI setting, not a fact about any recording, and shown in the pane as it
    /// is.
    /// </summary>
    public static readonly ImmutableArray<double> SpeedsPerSecond = ImmutableArray.Create(
        0.5, 1.0, 2.0, 4.0, 8.0, 16.0);

    /// <summary>Where in <see cref="SpeedsPerSecond"/> a replay starts.</summary>
    private const int DefaultSpeedIndex = 3;

    /// <summary>
    /// How much of the replay one coarse scrub key moves: a tenth of the frames,
    /// and at least one, so a scrub is a visible jump rather than a single tick.
    /// </summary>
    private const int ScrubFraction = 10;

    private readonly ReplayDocument _document;
    private double _accumulatedTicks;
    private int _index;
    private int _speedIndex;
    private bool _paused;

    /// <summary>Opens a cursor on a replay's start frame, paused.</summary>
    public ReplayPlayback(ReplayDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _document = document;
        _index = document.FirstIndex;
        _speedIndex = DefaultSpeedIndex;
        _paused = true;
    }

    /// <summary>The frame currently showing.</summary>
    public int Index => _index;

    /// <summary>The frame currently showing.</summary>
    public ReplayFrame Frame => _document.Frames[_index];

    /// <summary>Whether the cursor is stopped.</summary>
    public bool IsPaused => _paused;

    /// <summary>The replay being played.</summary>
    public ReplayDocument Document => _document;

    /// <summary>
    /// Null: a recording is finished, so there is no live episode state to report.
    /// </summary>
    public LiveState? Live => null;

    /// <summary>The current speed in recorded steps per second.</summary>
    public double StepsPerSecond => SpeedsPerSecond[_speedIndex];

    /// <summary>Whether the speed control can still go faster.</summary>
    public bool CanSpeedUp => _speedIndex < SpeedsPerSecond.Length - 1;

    /// <summary>Whether the speed control can still go slower.</summary>
    public bool CanSlowDown => _speedIndex > 0;

    /// <summary>
    /// How far through the current frame's dwell time the cursor is, in [0, 1).
    /// Frozen at zero while paused, so a paused frame is drawn exactly as the
    /// recording states it rather than at some position between two ticks.
    /// </summary>
    public double Phase => _paused ? 0.0 : _accumulatedTicks;

    /// <summary>Starts and stops the cursor.</summary>
    /// <returns>Whether the cursor is now paused.</returns>
    public bool TogglePause()
    {
        // Resuming at the end has nowhere to go, so it moves back to the start
        // rather than sitting still looking as though it were running.
        if (_paused && _index == _document.LastIndex)
        {
            _index = _document.FirstIndex;
            _accumulatedTicks = 0.0;
        }

        _paused = !_paused;
        return _paused;
    }

    /// <summary>One recorded step on, clamped at the last frame.</summary>
    public int StepForward() => _index = _document.StepForward(_index);

    /// <summary>One recorded step back, clamped at the start frame.</summary>
    public int StepBack() => _index = _document.StepBack(_index);

    /// <summary>Jumps to the first frame.</summary>
    public int JumpToStart() => _index = _document.FirstIndex;

    /// <summary>Jumps to the last frame.</summary>
    public int JumpToEnd() => _index = _document.LastIndex;

    /// <summary>One speed step faster, clamped at the fastest.</summary>
    public double SpeedUp()
    {
        if (_speedIndex < SpeedsPerSecond.Length - 1)
        {
            _speedIndex++;
        }

        return StepsPerSecond;
    }

    /// <summary>One speed step slower, clamped at the slowest.</summary>
    public double SlowDown()
    {
        if (_speedIndex > 0)
        {
            _speedIndex--;
        }

        return StepsPerSecond;
    }

    /// <summary>
    /// Moves the cursor onto <paramref name="index"/>, clamped to the replay's
    /// ends. Scrubbing is an index move like any other, so the same clamping
    /// rules apply and nothing wraps.
    /// </summary>
    public int ScrubTo(int index) => _index = _document.Clamp(index);

    /// <summary>
    /// Adds the elapsed time the caller measured and reports whether the frame on
    /// screen would differ afterwards: the index moved, or the within-frame phase
    /// advanced. A paused cursor reports no change however long the elapsed time
    /// is, which is what stops a paused replay animating.
    /// </summary>
    public bool Advance(TimeSpan elapsed)
    {
        if (_paused || elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        var before = _index;
        var stopped = false;
        _accumulatedTicks += elapsed.TotalSeconds * StepsPerSecond;

        while (_accumulatedTicks >= 1.0)
        {
            if (_index >= _document.LastIndex)
            {
                _index = _document.LastIndex;
                _accumulatedTicks = 0.0;
                _paused = true;
                stopped = true;
                break;
            }

            _accumulatedTicks -= 1.0;
            _index++;
        }

        // The frame on screen changes when the index moved or the within-frame phase
        // moved on — and separately when the cursor stopped at the end of the
        // recording, which moves neither. That last one is reported in its own right
        // because a pane that says whether the replay is running would otherwise go
        // on saying "playing" over the last frame for ever.
        return _index != before || !_paused || stopped;
    }

    /// <summary>
    /// What one key does to the cursor, and whether it changed anything a reader
    /// can see. A key with no binding changes nothing and says so, which is what
    /// keeps a stray keypress from costing a frame.
    /// </summary>
    public bool Apply(TuiKey key)
    {
        var before = _index;
        var wasPaused = _paused;
        var speed = StepsPerSecond;
        var jump = Math.Max(1, _document.Count / ScrubFraction);

        switch (key.Kind)
        {
            case TuiKeyKind.Left:
            case TuiKeyKind.PageUp:
                StepBack();
                break;

            case TuiKeyKind.Right:
            case TuiKeyKind.PageDown:
                StepForward();
                break;

            case TuiKeyKind.Home:
                JumpToStart();
                break;

            case TuiKeyKind.End:
                JumpToEnd();
                break;

            case TuiKeyKind.Up:
                SpeedUp();
                break;

            case TuiKeyKind.Down:
                SlowDown();
                break;

            case TuiKeyKind.Character:
                return ApplyCharacter(key.Glyph, jump, before, wasPaused, speed);

            default:
                return false;
        }

        return Changed(before, wasPaused, speed);
    }

    private bool ApplyCharacter(char glyph, int jump, int before, bool wasPaused, double speed)
    {
        switch (glyph)
        {
            case ' ':
                TogglePause();
                break;

            case 'n':
                StepForward();
                break;

            case 'p':
                StepBack();
                break;

            case '>':
            case '+':
                SpeedUp();
                break;

            case '<':
            case '-':
                SlowDown();
                break;

            case '[':
                ScrubTo(_index - jump);
                break;

            case ']':
                ScrubTo(_index + jump);
                break;

            default:
                return false;
        }

        return Changed(before, wasPaused, speed);
    }

    private bool Changed(int index, bool paused, double speed) =>
        _index != index || _paused != paused || StepsPerSecond != speed;
}