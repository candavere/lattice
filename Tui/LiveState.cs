namespace Lattice.Tui;

/// <summary>
/// What a frame being drawn is for: a finished recording, or a live episode the
/// reader is watching being computed. Null on the request is what tells the
/// timeline and the key hints this is a recording, so there is no second flag
/// that can fall out of step with the first.
/// </summary>
/// <param name="ProducedTicks">How many ticks the stepper has produced and finished.</param>
/// <param name="MaximumTicks">
/// The episode's own tick budget: the most ticks it can ever produce. A maximum
/// rather than a count, so it does not change while the episode runs and the
/// reader always has something to compare the produced ticks against.
/// </param>
/// <param name="FinishedReason">
/// The recorded reason the episode ended, or null while it is still running.
/// </param>
/// <param name="Notice">
/// A transient line the host must show the reader in preference to everything
/// else — the stepper is stopping, or a restart is waiting on an agent that is
/// still deciding. Null when there is nothing to say.
/// </param>
/// <param name="Seed">
/// The seed the episode is running at, which it is told rather than asked for: a
/// pane that wants to name where the episode came from names the seed it was
/// given, and says "not recorded" for anything it was not.
/// </param>
/// <param name="AgentRoles">
/// The roster's role per slot, so the scoreboard names the agents rather than
/// falling back to "A0". Null when the roster carries none.
/// </param>
public sealed record LiveState(
    int ProducedTicks,
    int MaximumTicks,
    string? FinishedReason = null,
    string? Notice = null,
    ulong? Seed = null,
    IReadOnlyList<string>? AgentRoles = null)
{
    /// <summary>
    /// Whether the episode has ended. True only once the reason is known: a
    /// stepper that has stopped has an end to report, and a viewer that has not
    /// heard one must not claim the episode is over.
    /// </summary>
    public bool IsFinished => CockpitEpisodes.HasReason(FinishedReason);
}

/// <summary>
/// The episode-wide numbers the panes read, from whichever of the two sources
/// carries them. Every value here is either recorded or computed; none is
/// invented, and a recording needs no live state at all because its own header
/// states the step count it recorded.
/// </summary>
public static class CockpitEpisodes
{
    /// <summary>
    /// What a source records when it records nothing. A pane that wants a fact the
    /// source does not carry says this, which is a statement about the recording
    /// rather than a guess.
    /// </summary>
    public const string NotRecorded = "not recorded";

    /// <summary>
    /// Whether a source states a reason at all. A missing or blank reason is not a
    /// reason: claiming an episode ended on the strength of whitespace would be the
    /// viewer inventing a verdict.
    /// </summary>
    public static bool HasReason(string? reason) => !string.IsNullOrWhiteSpace(reason);

    /// <summary>
    /// The seed a frame's episode is running at, as the event log names it: the
    /// value a live episode was given, or a recording's own header. A live episode
    /// that was given no seed says so rather than printing a zero.
    /// </summary>
    public static string Seed(ReplayDocument document, LiveState? live) =>
        live is null
            ? Invariant((int)(document.Header.Seed & 0xFFFFFFFF))
            : live.Seed is { } seed ? seed.ToString(System.Globalization.CultureInfo.InvariantCulture) : NotRecorded;

    /// <summary>
    /// The wire format a frame's episode was recorded in. A live episode is being
    /// computed and has not been recorded at all, so it has no schema version and
    /// says so.
    /// </summary>
    public static string SchemaVersion(ReplayDocument document, LiveState? live) =>
        live is null
            ? "v" + Invariant(document.Header.SchemaVersion)
            : NotRecorded;

    /// <summary>
    /// The step count the episode is measured against: a recording's own final
    /// line, or a live episode's tick budget.
    /// </summary>
    public static int MaximumTicks(ReplayDocument document, LiveState? live) =>
        live?.MaximumTicks ?? document.Header.RecordedSteps;

    /// <summary>
    /// How many ticks the episode has produced. A recording has all of its steps
    /// already, so it has produced every one of them; a live episode has produced
    /// only what the stepper has finished.
    /// </summary>
    public static int ProducedTicks(ReplayDocument document, LiveState? live) =>
        live?.ProducedTicks ?? Math.Max(0, document.Count - 1);

    /// <summary>
    /// Why the episode ended, for a pane that asks. "not recorded" when the
    /// source carries no reason, which is a statement about the recording rather
    /// than a guess.
    /// </summary>
    public static string FinishedReason(ReplayFrame frame, LiveState? live) =>
        live is not null
            ? live.IsFinished ? live.FinishedReason! : NotRecorded
            : HasReason(frame.TerminalReason) ? frame.TerminalReason! : NotRecorded;

    /// <summary>
    /// The role per slot the scoreboard names agents by: a live episode's own
    /// roster, or a recording's header.
    /// </summary>
    public static IReadOnlyList<string>? AgentRoles(ReplayDocument document, LiveState? live) =>
        live?.AgentRoles ?? document.Header.AgentRoles;

    private static string Invariant(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}