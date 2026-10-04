namespace Lattice.Tui;

/// <summary>
/// What a stepper is doing right now, as the screen is allowed to say it.
/// </summary>
/// <remarks>
/// The two stopped-looking states are not the same claim. <see cref="Stopping"/>
/// says a stop was asked for and the thread has not been joined: an agent may
/// still be deciding, and nothing is known about what it will do.
/// <see cref="Stopped"/> says the thread was joined, which is the only fact that
/// makes "stopped" true. A host that has set a flag and not joined must not
/// print the second word.
/// </remarks>
public enum LiveStepperStatus
{
    /// <summary>The stepper is free to compute the next tick when asked.</summary>
    Running,

    /// <summary>A stop was asked for; the stepper thread has not been joined.</summary>
    Stopping,

    /// <summary>The stepper thread was joined. Nothing can be deciding any more.</summary>
    Stopped,
}

/// <summary>
/// Where a live cursor's frames come from: the simulation side, behind an
/// interface, so this library never names an agent or a simulation state.
/// </summary>
/// <remarks>
/// The seam carries the map, the frames, the budget and two requests, and no
/// more. The map is the episode's own, projected by the same projection a
/// recording uses, so the panes draw the world the simulation is actually
/// playing. Frames arrive in the same projected form a replay viewer draws, oldest
/// first, so the world, scoreboard, log and timeline panes work against a live
/// episode unchanged. The cursor asks for at most one tick beyond what it has
/// shown, so nothing is computed behind the viewer. <see cref="RequestRestart"/>
/// is the only way to throw the episode away, and an implementation that cannot do
/// it yet — because a turn is still deciding — says so through
/// <see cref="Notice"/> instead of replacing anything.
/// </remarks>
public interface ILiveEpisode
{
    /// <summary>
    /// The map every produced frame is drawn against: the episode's own, in the
    /// same projection a recording's map is read into.
    /// </summary>
    WorldMap Map { get; }

    /// <summary>
    /// The frames produced and finished so far, start frame first. Grows by one
    /// per produced tick, and is emptied and refilled with a single start frame on
    /// a restart.
    /// </summary>
    IReadOnlyList<ReplayFrame> Frames { get; }

    /// <summary>The episode's tick budget: the most ticks it can ever produce.</summary>
    int MaximumTicks { get; }

    /// <summary>
    /// The recorded reason the episode ended, or null while it is still running.
    /// The recording's own words; a live episode has none until one exists.
    /// </summary>
    string? FinishedReason { get; }

    /// <summary>What the stepper is doing right now.</summary>
    LiveStepperStatus StepperStatus { get; }

    /// <summary>
    /// Whether the stepper has stopped for good and will produce nothing more — the
    /// episode ended, or the simulation failed. The two are not the same claim and
    /// are not merged: <see cref="FinishedReason"/> says the episode ended and why,
    /// and this says only that there is nothing left to wait for. A failure has no
    /// end reason, so a pane still prints "not recorded" for one.
    /// </summary>
    bool HasStopped { get; }

    /// <summary>
    /// The one line the host must show the reader in preference to the cursor's
    /// own state — why a restart is waiting, or that the stepper is stopping — or
    /// null when there is nothing to say.
    /// </summary>
    string? Notice { get; }

    /// <summary>
    /// Asks for exactly one more tick. Implementations produce at most one tick
    /// beyond what the viewer has consumed, and nothing at all while the viewer is
    /// paused: this is the only way a tick is ever asked for.
    /// </summary>
    void RequestTick();

    /// <summary>
    /// Asks for the episode to be thrown away and started again from the same seed
    /// and arguments, with the history cleared. An implementation that is still
    /// deciding must not replace anything: it reports
    /// <see cref="LivePlayback.RestartWaitingNotice"/> through <see cref="Notice"/>
    /// and waits.
    /// </summary>
    void RequestRestart();
}

/// <summary>
/// The playback cursor over a live episode: which produced frame is showing,
/// whether the episode is being asked to compute, how fast, and what one key does
/// to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The viewer is the clock, and the stepper is at most one tick ahead of it.</b>
/// The cursor never indexes a frame the stepper has not finished, and it asks for
/// the next tick only when it has caught up to the frontier. Nothing is precomputed
/// behind the reader: a paused viewer computes nothing at all, and the single
/// exception is the one tick the <c>n</c> key asks for by name.
/// </para>
/// <para>
/// <b>Moving back is free.</b> Every produced frame stays in the list, so <c>p</c>
/// and the scrub keys walk back over the episode's own history at no cost — the
/// frames are values, not a stream.
/// </para>
/// <para>
/// The cursor owns no clock of its own: <see cref="Advance"/> takes the elapsed
/// time the host measured, so a test can place a known duration on it rather than
/// sleeping. How fast a stepper computes is the simulation's business, not the
/// drawing's.
/// </para>
/// </remarks>
public sealed class LivePlayback : ICockpitCursor
{
    /// <summary>Where in <see cref="ReplayPlayback.SpeedsPerSecond"/> an episode starts.</summary>
    private const int DefaultSpeedIndex = 3;

    /// <summary>What the screen says while a stop has been asked for and not joined.</summary>
    public const string StoppingNotice = "stopping";

    /// <summary>What the screen says once the stepper thread has been joined.</summary>
    public const string StoppedNotice = "stopped";

    /// <summary>What the screen says while a restart is waiting on a deciding agent.</summary>
    public const string RestartWaitingNotice = "waiting for the running agent turn to finish";

    private readonly ILiveEpisode _episode;
    private int _index;
    private int _speedIndex = DefaultSpeedIndex;
    private bool _paused = true;
    private double _accumulatedTicks;
    private ReplayDocument? _document;
    private int _documentFrames = -1;

    /// <summary>
    /// Whether the viewer has asked for a tick that has not arrived yet. The stepper
    /// runs on its own thread, so the frame the reader asked for lands on a later
    /// pass; this is what lets that later pass move the view onto it.
    /// </summary>
    private bool _awaitingFrontier;

    /// <summary>Opens a cursor on a live episode's start frame, paused.</summary>
    public LivePlayback(ILiveEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        _episode = episode;
    }

    /// <inheritdoc />
    public ReplayDocument Document
    {
        get
        {
            // Rebuilt only when the episode has grown, so a paused frame costs
            // nothing and a running one costs one list and one record per tick.
            var produced = _episode.Frames.Count;
            if (_document is not null && _documentFrames == produced)
            {
                return _document;
            }

            _documentFrames = produced;
            _document = new ReplayDocument(
                WorldMapOf(_episode.Frames),
                new ReplayHeader(
                    Seed: 0,
                    SchemaVersion: 0,
                    Scenario: "live",
                    AgentRoles: null,
                    RecordedSteps: _episode.MaximumTicks,
                    ScenarioDigest: null,
                    DynamicRuleCount: 0),
                _episode.Frames);

            return _document;
        }
    }

    /// <inheritdoc />
    public int Index => _index;

    /// <inheritdoc />
    public bool IsPaused => _paused;

    /// <inheritdoc />
    public double StepsPerSecond => ReplayPlayback.SpeedsPerSecond[_speedIndex];

    /// <inheritdoc />
    public double Phase => _paused ? 0.0 : _accumulatedTicks;

    /// <inheritdoc />
    /// <remarks>
    /// The stepper's own state is reported only as a notice, never as live episode
    /// state of its own: <see cref="FinishedReason"/> is the recording's word for
    /// how an episode ended, and a stepper that is stopping has not ended one.
    /// </remarks>
    public LiveState? Live => new(
        ProducedTicks: Math.Max(0, _episode.Frames.Count - 1),
        MaximumTicks: _episode.MaximumTicks,
        FinishedReason: _episode.FinishedReason,
        Notice: Notice());

    /// <inheritdoc />
    /// <remarks>
    /// A finished episode ends the host run, and so does a stepper that has failed:
    /// there is nothing left to compute and nothing left to show, so the terminal
    /// goes back rather than sitting on a last frame looking live. A failure says no
    /// reason — <see cref="LiveState.IsFinished"/> stays false and the panes print
    /// "not recorded" — because the viewer does not get to invent an ending.
    /// </remarks>
    public bool IsFinished => CockpitEpisodes.HasReason(_episode.FinishedReason) || _episode.HasStopped;

    /// <summary>The frame on show.</summary>
    public ReplayFrame Frame => Document.Frames[Clamped(_index)];

    /// <summary>The most frames the episode has produced.</summary>
    public int ProducedFrames => Math.Max(0, _episode.Frames.Count - 1);

    /// <summary>Moves the cursor onto a produced frame, clamped to the frontier.</summary>
    public int ScrubTo(int index)
    {
        _index = Math.Clamp(index, 0, ProducedFrames);
        return _index;
    }

    /// <inheritdoc />
    public bool Apply(TuiKey key)
    {
        SnapToTheFrontier();
        var before = _index;
        var notice = Notice();
        var changed = ApplyCore(key);

        // A key can end the episode or throw it away, which moves the frontier
        // under the view. Clamping after as well as before is what keeps the cursor
        // from standing on a frame that no longer exists.
        SnapToTheFrontier();

        // The episode's own notice is part of the frame too. Without this a restart
        // that had to wait for a running turn would change nothing the cursor could
        // see, and the reason it did nothing would never reach the reader.
        return changed || _index != before || !Same(notice, Notice());
    }

    private bool ApplyCore(TuiKey key)
    {
        var before = _index;
        var wasPaused = _paused;
        var speed = StepsPerSecond;

        switch (key.Kind)
        {
            case TuiKeyKind.Left:
            case TuiKeyKind.PageUp:
                ScrubTo(_index - 1);
                break;

            case TuiKeyKind.Right:
            case TuiKeyKind.PageDown:
                ScrubTo(_index + 1);
                break;

            case TuiKeyKind.Home:
                ScrubTo(0);
                break;

            case TuiKeyKind.End:
                RequestToTheEnd();
                break;

            case TuiKeyKind.Up:
                Faster();
                break;

            case TuiKeyKind.Down:
                Slower();
                break;

            case TuiKeyKind.Character:
                return ApplyCharacter(key.Glyph, before, wasPaused, speed);

            default:
                return false;
        }

        return Changed(before, wasPaused, speed);
    }

    /// <inheritdoc />
    public bool Advance(TimeSpan elapsed)
    {
        SnapToTheFrontier();
        var before = _index;
        var notice = Notice();

        if (_paused || elapsed <= TimeSpan.Zero)
        {
            return _index != before || !Same(notice, Notice());
        }

        _accumulatedTicks += elapsed.TotalSeconds * StepsPerSecond;

        while (_accumulatedTicks >= 1.0)
        {
            if (_index >= ProducedFrames)
            {
                // At the frontier: ask for the next tick. The stepper produces it
                // on its own thread, so the viewer shows the frame it has and the
                // new one appears on the next pass — never more than one tick ahead.
                if (!CanProduce())
                {
                    break;
                }

                _episode.RequestTick();
                _awaitingFrontier = true;
                _accumulatedTicks -= 1.0;
                break;
            }

            _accumulatedTicks -= 1.0;
            _index++;
        }

        SnapToTheFrontier();
        return _index != before || !Same(notice, Notice());
    }

    /// <summary>
    /// Moves the view onto a tick the reader asked for as soon as the stepper has
    /// finished it. Without this the frame would arrive on the stepper's thread and
    /// be drawn only when something else moved the view, so pressing the one-tick
    /// key would appear to do nothing until the reader pressed it again.
    /// </summary>
    private void SnapToTheFrontier()
    {
        if (ProducedFrames < _index)
        {
            // The frontier fell below the view, which only a restart does: the old
            // episode's frames are gone. The cursor follows the frontier down and
            // stops, because a new episode must not begin running behind a reader
            // who did not ask it to.
            _index = ProducedFrames;
            _paused = true;
            _awaitingFrontier = false;
            _accumulatedTicks = 0.0;
            return;
        }

        if (_awaitingFrontier && _index < ProducedFrames)
        {
            _index = ProducedFrames;
            _awaitingFrontier = false;
        }
    }

    private bool ApplyCharacter(char glyph, int before, bool wasPaused, double speed)
    {
        switch (glyph)
        {
            case ' ':
                // Resuming at the frontier with nothing produced would otherwise sit
                // still looking as though it were running.
                if (_paused && _index >= ProducedFrames && ProducedFrames > 0)
                {
                    _index = 0;
                }

                _paused = !_paused;
                break;

            case 'n':
                // One more tick, but only at the frontier: off it, the next frame is
                // one the stepper already finished and asking again would compute
                // ahead of the reader.
                if (_index < ProducedFrames)
                {
                    _index++;
                    break;
                }

                if (CanProduce())
                {
                    _episode.RequestTick();
                    _awaitingFrontier = true;
                }

                SnapToTheFrontier();
                break;

            case 'p':
                ScrubTo(_index - 1);
                break;

            case '>':
            case ']':
            case '+':
                Faster();
                break;

            case '<':
            case '[':
            case '-':
                Slower();
                break;

            case 'r':
                _episode.RequestRestart();
                break;

            default:
                return false;
        }

        return Changed(before, wasPaused, speed) || Produced();
    }

    /// <summary>
    /// The end key asks for the end of the episode. A live episode has no end yet,
    /// so the cursor asks for what it can — the frames produced so far — and stops
    /// there: it may never stand on a frame that does not exist.
    /// </summary>
    private void RequestToTheEnd()
    {
        if (CanProduce())
        {
            _episode.RequestTick();
            _awaitingFrontier = true;
        }

        SnapToTheFrontier();
        ScrubTo(int.MaxValue);
    }

    /// <summary>
    /// Whether a tick may be asked for: the stepper is still able to produce one and
    /// the budget has not been spent. A stopped stepper computes nothing more,
    /// however many times the reader asks.
    /// </summary>
    private bool CanProduce() =>
        !_episode.HasStopped && ProducedFrames < _episode.MaximumTicks;

    private void Faster()
    {
        if (_speedIndex < ReplayPlayback.SpeedsPerSecond.Length - 1)
        {
            _speedIndex++;
        }
    }

    private void Slower()
    {
        if (_speedIndex > 0)
        {
            _speedIndex--;
        }
    }

    /// <summary>
    /// Whether the episode has produced a frame since the cursor last looked, which
    /// is a change the reader can see even though no key moved anything: the tick
    /// they asked for has arrived.
    /// </summary>
    private bool Produced() => _index < ProducedFrames;

    private bool Changed(int index, bool paused, double speed) =>
        _index != index || _paused != paused || StepsPerSecond != speed;

    /// <summary>
    /// The one line the frame must carry, from the episode if it has one and from
    /// the stepper's own state otherwise.
    /// </summary>
    private string? Notice() => _episode.Notice ?? NoticeState();

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// The stepper's own state, when the episode offers no notice of its own. Only
    /// "stopped" is ever printed, and only when the episode says its thread was
    /// joined.
    /// </summary>
    private string? NoticeState() =>
        _episode.StepperStatus switch
        {
            LiveStepperStatus.Stopping => StoppingNotice,
            LiveStepperStatus.Stopped => StoppedNotice,
            _ => null,
        };

    private int Clamped(int index) => Math.Clamp(index, 0, Math.Max(0, ProducedFrames));

    /// <summary>
    /// The world the panes draw the produced frames against: the episode's own map,
    /// projected by the one projection a recording uses. Nothing here invents a
    /// zone, a resource or an edge.
    /// </summary>
    private WorldMap WorldMapOf(IReadOnlyList<ReplayFrame> frames) => frames.Count == 0
        ? new WorldMap(Array.Empty<WorldZone>(), Array.Empty<WorldResource>(), Array.Empty<WorldEdge>())
        : _episode.Map;
}