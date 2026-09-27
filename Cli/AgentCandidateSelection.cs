namespace Lattice.Cli;

/// <summary>
/// The one rule about <c>evaluate</c>'s candidate side: an external agent and an
/// in-process candidate policy cannot both be asked for (spec §9.5).
/// </summary>
/// <remarks>
/// <para>
/// The external agent is not a fourth policy and not a mode. It takes the seat the
/// in-process candidate takes, against the same baseline, on the same maps, under
/// the same mirrored pairings, and its rows go through the same statistics. So
/// there is exactly one candidate per run, and two selectors on one invocation is
/// ambiguous — worse than ambiguous, because which one won would be a property of
/// flag order rather than of the study, and the artifact would record a number
/// belonging to a policy the reader never chose.
/// </para>
/// <para>
/// <b>Why the list is empty in v3.0.</b> <c>evaluate</c> fixes its target policy
/// and exposes no selector for it, so the rule holds by construction today. It is
/// written out and tested anyway, with the selector set supplied by the caller,
/// because the failure mode this prevents appears the moment somebody adds a
/// <c>--policy</c> flag: a new selector that is not added here would silently be
/// combinable with <c>--agent-cmd</c>, and the resulting study would look entirely
/// ordinary.
/// </para>
/// </remarks>
public static class AgentCandidateSelection
{
    /// <summary>
    /// The <c>evaluate</c> flags that select an in-process candidate policy.
    /// </summary>
    /// <remarks>
    /// Empty in v3.0: the target is the fixed MCTS policy. Deliberately a declared
    /// list rather than a convention, so that adding a selector is an edit to this
    /// property and therefore a reviewed one.
    /// </remarks>
    public static IReadOnlyList<string> InProcessCandidateSelectors { get; } = [];

    /// <summary>
    /// Refuses an invocation that names both an external agent and an in-process
    /// candidate selector.
    /// </summary>
    /// <param name="args">
    /// The raw arguments, before flag parsing. The guard runs here rather than
    /// after parsing so a conflict is reported as a conflict instead of as an
    /// unknown flag — the reader needs to know the two selectors clashed, not that
    /// one of them is not a flag this command has.
    /// </param>
    /// <param name="selectors">
    /// The candidate selectors to look for. Defaults to
    /// <see cref="InProcessCandidateSelectors"/>; the parameter exists so the rule
    /// can be tested against a selector set this version does not have.
    /// </param>
    /// <exception cref="UsageError">
    /// A candidate selector is present. Whether <c>--agent-cmd</c> is also present
    /// is not checked: naming a candidate selector at all is a conflict for a
    /// command whose candidate is being chosen by <c>--agent-cmd</c>, and a
    /// selector with no external agent to conflict with is a different question
    /// that belongs to the flag that introduces it.
    /// </exception>
    public static void GuardAgainstInProcessSelector(
        IReadOnlyList<string> args,
        IReadOnlyList<string>? selectors = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (var selector in selectors ?? InProcessCandidateSelectors)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, selector, StringComparison.Ordinal))
                {
                    throw new UsageError(
                        $"'{selector}' selects an in-process candidate policy and cannot be combined with " +
                        "--agent-cmd, which names the candidate as a process to launch. Use one or the other.");
                }
            }
        }
    }
}
