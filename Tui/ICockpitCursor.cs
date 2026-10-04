namespace Lattice.Tui;

/// <summary>
/// What a host run moves: which frame is showing, whether it is running, how fast,
/// and what one key does to it.
/// </summary>
/// <remarks>
/// <para>
/// The host draws frames and reads keys; it does not know whether they come from
/// a finished recording or from an episode still being computed. Both are this:
/// the recording's cursor plays frames that already exist, and the live cursor
/// plays frames the stepper has produced so far and asks it for more. Keeping the
/// difference behind this one interface is what lets a single host own the
/// alternate screen for both — one place that asks for UTF-8, enters, writes
/// frames, reads keys and puts the terminal back, in that order, once.
/// </para>
/// <para>
/// A cursor owns no clock: <see cref="Advance"/> takes the elapsed time the host
/// measured, so a test can place a known duration on it instead of sleeping. The
/// host's own frame ceiling is separate and applies to both.
/// </para>
/// </remarks>
public interface ICockpitCursor
{
    /// <summary>The frames this cursor can show, as they stand right now.</summary>
    ReplayDocument Document { get; }

    /// <summary>The index of the frame on show.</summary>
    int Index { get; }

    /// <summary>Whether the cursor is stopped.</summary>
    bool IsPaused { get; }

    /// <summary>The current speed in steps per second.</summary>
    double StepsPerSecond { get; }

    /// <summary>
    /// How far through the current frame's dwell time the cursor is, in [0, 1).
    /// Zero while paused, so a paused frame is drawn as the source states it rather
    /// than at some position between two ticks.
    /// </summary>
    double Phase { get; }

    /// <summary>
    /// The live episode state, or null when the frames are a finished recording.
    /// The panes read this and nothing else to tell the two apart.
    /// </summary>
    LiveState? Live { get; }

    /// <summary>
    /// Applies one key. Returns whether the frame on screen would differ
    /// afterwards, so a key with no binding costs no write.
    /// </summary>
    bool Apply(TuiKey key);

    /// <summary>
    /// Adds the elapsed time the host measured and returns whether the frame on
    /// screen would differ afterwards. A stopped cursor reports no change however
    /// long the elapsed time is, which is what stops a paused screen animating.
    /// </summary>
    bool Advance(TimeSpan elapsed);

    /// <summary>
    /// Whether the source has nothing left to give: there will be no further frames
    /// and no further state changes, so the host puts the terminal back rather than
    /// sitting on a last frame looking live. A recording is never finished this way
    /// — a replay ends when the reader says so.
    /// </summary>
    bool IsFinished { get; }
}