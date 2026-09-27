namespace Lattice.Agents.External;

/// <summary>
/// The two named per-match time limits of spec §7 (U-2), and the defaults the
/// spec fixes for them.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are <see cref="DefaultStepTimeoutMs"/> for
/// <c>step_timeout_ms</c> and a <b>computed</b> <c>match_timeout_ms</c> of
/// <c>step_timeout_ms × max_ticks + <see cref="MatchTimeoutSlackMs"/></c>.
/// Deriving the match budget rather than choosing a second constant is the
/// whole point: §7 requires <c>match_timeout_ms ≥ step_timeout_ms × MaxTicks</c>,
/// and two independently chosen numbers make that a relationship a future editor
/// can silently break by changing only one of them. Computed, the constraint
/// holds by construction.
/// </para>
/// <para>
/// The slack covers the per-step bookkeeping and the final exchange, so a match
/// that legitimately uses its entire step budget does not trip the whole-match
/// limit on the way out. The constructor <b>refuses</b> any combination that
/// violates §7, which is §7's stated requirement for an overridden value, and it
/// refuses it before the match starts rather than during it.
/// </para>
/// <para>
/// Both values are per-match, not protocol constants (§12.6): they travel in
/// <c>hello.limits</c>, and the runner records them in the run's output metadata.
/// Neither <see cref="StepTimeoutMs"/> nor <see cref="MatchTimeoutMs"/> is ever
/// compared against a wall clock; the runner measures them on a
/// <see cref="System.Diagnostics.Stopwatch"/>, so a system clock adjustment
/// cannot manufacture or suppress a timeout mid-match.
/// </para>
/// </remarks>
public sealed record ExternalTimeLimits
{
    /// <summary>
    /// The spec §7 default <c>step_timeout_ms</c> (U-2): 5000 monotonic
    /// milliseconds, used for one <c>action</c> after one <c>observation</c> and
    /// for one <c>hello_ack</c> after one <c>hello</c>.
    /// </summary>
    public const int DefaultStepTimeoutMs = 5_000;

    /// <summary>
    /// The additive slack in the §7 <c>match_timeout_ms</c> formula, in
    /// milliseconds. It exists so a match that consumes its whole step budget on
    /// every step still terminates normally rather than on its last step.
    /// </summary>
    public const int MatchTimeoutSlackMs = 30_000;

    /// <summary>
    /// The spec §7 formula: the whole-match budget for a given step budget and
    /// tick budget, as <c>step_timeout_ms × max_ticks + <paramref name="slackMs"/>
    /// </c>. Computed in 64-bit arithmetic because a large
    /// <paramref name="maxTicks"/> times a large step budget overflows
    /// <see cref="int"/>.
    /// </summary>
    public static int ComputeMatchTimeoutMs(int stepTimeoutMs, int maxTicks, int slackMs = MatchTimeoutSlackMs) =>
        checked((int)((long)stepTimeoutMs * maxTicks + slackMs));

    /// <summary>
    /// The spec's defaults for a match of <paramref name="maxTicks"/> ticks:
    /// <c>step_timeout_ms = 5000</c> and the <c>match_timeout_ms</c> computed
    /// from it.
    /// </summary>
    public static ExternalTimeLimits Default(int maxTicks) =>
        new(DefaultStepTimeoutMs, ComputeMatchTimeoutMs(DefaultStepTimeoutMs, maxTicks), maxTicks);

    /// <summary>Initializes a validated pair of limits.</summary>
    /// <param name="stepTimeoutMs">The per-step (and per-handshake) budget, ≥ 1.</param>
    /// <param name="matchTimeoutMs">The whole-match budget, ≥ 1.</param>
    /// <param name="maxTicks">
    /// The tick budget the §7 constraint is checked against. Pass the same
    /// <c>max_ticks</c> the match will put on the wire.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either value is below 1, or <paramref name="matchTimeoutMs"/> is below
    /// <c>stepTimeoutMs × maxTicks</c> — the §7 constraint, which Lattice MUST
    /// refuse to mis-set rather than score an external agent a loss for.
    /// </exception>
    public ExternalTimeLimits(int stepTimeoutMs, int matchTimeoutMs, int maxTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stepTimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(matchTimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

        var required = (long)stepTimeoutMs * maxTicks;
        if (matchTimeoutMs < required)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchTimeoutMs),
                matchTimeoutMs,
                $"match_timeout_ms must be at least step_timeout_ms x max_ticks = {stepTimeoutMs} x {maxTicks} = " +
                $"{required} (spec §7). Lattice mis-setting this would score every external agent a loss for a " +
                "host-side limit.");
        }

        StepTimeoutMs = stepTimeoutMs;
        MatchTimeoutMs = matchTimeoutMs;
    }

    /// <summary>Monotonic milliseconds allowed for one <c>action</c>, and for the <c>hello_ack</c>.</summary>
    public int StepTimeoutMs { get; }

    /// <summary>Monotonic milliseconds allowed for the whole match, measured from <c>hello</c> written.</summary>
    public int MatchTimeoutMs { get; }

    /// <summary>As <see cref="System.TimeSpan"/>, for the monotonic clock.</summary>
    public TimeSpan StepTimeout => TimeSpan.FromMilliseconds(StepTimeoutMs);

    /// <summary>As <see cref="System.TimeSpan"/>, for the monotonic clock.</summary>
    public TimeSpan MatchTimeout => TimeSpan.FromMilliseconds(MatchTimeoutMs);
}
