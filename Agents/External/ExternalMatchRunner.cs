using Lattice.Environment;
using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// One match between an external agent and an in-process baseline, as a result.
/// </summary>
/// <param name="Seed">The run seed both sides saw.</param>
/// <param name="ExternalSlot">The agent slot the external process played.</param>
/// <param name="Match">
/// The per-match row, in the existing <see cref="MatchResult"/> shape, with the
/// reason code in <c>TerminationReason</c> — or <see langword="null"/> for a
/// <b>void</b> run, which produces no match row at all (§9.3).
/// </param>
/// <param name="ExternalOutcome">
/// The external agent's own policy outcome: always a <b>loss</b> for an
/// agent-attributable failure, and <see langword="null"/> for a void run, which
/// is not scored (§9.1).
/// </param>
/// <param name="Fault">
/// The failure, or <see langword="null"/> when the match completed normally. A
/// match either has a fault or an episode, never both and never neither.
/// </param>
/// <param name="Episode">
/// The <see cref="ScenarioResult"/> exactly as <see cref="ScenarioRunner"/>
/// produced it, carrying the external agent's actions in the same per-step turn
/// array as an in-process agent's (§10.2). <see langword="null"/> on failure.
/// </param>
/// <param name="ChildProcessId">
/// The OS process id the external agent ran as, or <see langword="null"/> when no
/// process was started — which is the case for a <c>host_limit</c> refusal, since
/// that match is refused before a process exists (§8.4). Carried so a caller can
/// assert no child outlived the match.
/// </param>
/// <param name="ChildExited">
/// True when the child was observed to have exited before the runner returned.
/// Always true, because the runner disposes the child on every path; the
/// distinction between <c>true</c> here and a leaked process is what the tests
/// check against the OS rather than against this flag.
/// </param>
/// <param name="StderrBytesObserved">
/// How many stderr bytes the host actually read over the life of the match.
/// This is the unbounded, monotonically increasing total, not the ring's
/// contents, so it is the only witness to output that no longer fits in the
/// 64 KiB ring -- and therefore the evidence that the drain kept up rather than
/// that the agent happened to stay quiet.
/// </param>
public sealed record ExternalMatchResult(
    ulong Seed,
    int ExternalSlot,
    MatchResult? Match,
    MatchOutcome? ExternalOutcome,
    ExternalAgentFault? Fault,
    ScenarioResult? Episode,
    int? ChildProcessId = null,
    bool ChildExited = true,
    long StderrBytesObserved = 0)
{
    /// <summary>True when the match played to a normal ending, with no protocol failure.</summary>
    public bool Completed => Fault is null;

    /// <summary>
    /// True when Lattice refused the match on its own limits: a <b>void</b> run,
    /// reported as a count and excluded from the paired statistics, and
    /// explicitly <b>not</b> a loss for the external agent (§9.1, §9.3).
    /// </summary>
    public bool IsVoid => Fault is { IsHostFault: true };

    /// <summary>
    /// True when the external agent's own plumbing failed. Counted in
    /// <c>AgentFailures</c> by reason code, and always a loss (§9.3).
    /// </summary>
    public bool IsAgentFailure => Fault is { IsAgentFault: true };
}

/// <summary>
/// The aggregate of a set of matches: everything stage 4 needs to report, and
/// nothing that changes a score.
/// </summary>
/// <param name="Matches">The per-match rows, in the order they were played.</param>
/// <param name="AgentFailuresByCode">
/// Agent-attributable failures counted <b>by §8 reason code</b>, so a run in which
/// every failure is <c>timeout_handshake</c> is visibly distinguishable from one
/// in which every failure is <c>agent_crashed</c> (§9.3). Report-only: it changes
/// no outcome, no delta, no confidence interval, and no decision rule.
/// </param>
/// <param name="VoidRuns">
/// Host-limit refusals. Reported as a count so a match that did not happen is
/// visible rather than inferred from a total that is short, and excluded from the
/// paired statistics because neither agent played (§9.3).
/// </param>
public sealed record ExternalMatchReport(
    ExternalMatchResult[] Matches,
    IReadOnlyDictionary<ProtocolReason, int> AgentFailuresByCode,
    int VoidRuns)
{
    /// <summary>Every agent-attributable failure, across all codes.</summary>
    public int AgentFailureCount => AgentFailuresByCode.Values.Sum();

    /// <summary>Matches that played to a normal ending.</summary>
    public int CompletedCount => Matches.Count(m => m.Completed);
}

/// <summary>
/// Plays one match between an external agent process and an in-process baseline,
/// and turns every outcome — including every protocol failure — into a result.
/// </summary>
/// <remarks>
/// <para>
/// The episode is driven by the existing <see cref="ScenarioRunner"/> with the
/// <see cref="ExternalAgent"/> in the <see cref="IAgent"/> slot, so the step loop,
/// the ascending-slot poll order, the contention accounting, and the terminal-tick
/// handling are the same code an in-process match uses. That is what makes the
/// recorded trajectory byte-identical to an in-process one rather than merely
/// similar (§10.2), and it is why nothing in <see cref="ScenarioRunner"/> or
/// <see cref="IAgent"/> had to change.
/// </para>
/// <para>
/// <b>Why this exists rather than a factory plugged into
/// <see cref="EvaluationHarness"/>.</b> The harness has no failure path: it calls
/// <see cref="ScenarioRunner.Run"/> and builds a row from the result, so an agent
/// that throws would abort the whole suite rather than produce the row §9.1
/// requires, and the process would never be disposed. This runner closes that by
/// catching the fault, building the row itself, and disposing the child in a
/// <c>finally</c> — one process per match, always cleaned up.
/// </para>
/// <para>
/// <b>Scoring.</b> Every agent-attributable code becomes a recorded row whose
/// outcome is a win for the baseline side, so the external agent's own outcome is
/// a loss. Nothing is retried and no default action is ever substituted (§9.1,
/// §9.2). A <c>host_limit</c> refusal becomes a void run: no row, not a loss, and
/// counted separately.
/// </para>
/// </remarks>
public static class ExternalMatchRunner
{
    /// <summary>
    /// Plays one match. The child process is started here, used, and disposed
    /// before this returns, on every path including every failure.
    /// </summary>
    /// <param name="map">The map both sides play.</param>
    /// <param name="config">The simulation config; <c>AgentCount</c> must be 2, as evaluation pairings are.</param>
    /// <param name="launch">The §3.2 launch contract for the external agent.</param>
    /// <param name="externalSlot">The slot the external process plays.</param>
    /// <param name="baseline">
    /// The in-process agent in the other slot. Every match is external versus a
    /// baseline, which is what makes the failure a loss <em>for the external
    /// side</em> rather than an unattributable gap.
    /// </param>
    /// <param name="seed">The run seed, sent in <c>hello</c> and recorded in the trajectory header.</param>
    /// <param name="maxSteps">The match's tick budget, sent as <c>hello.max_ticks</c>.</param>
    /// <param name="scenario">The scenario family for <c>hello.scenario</c>.</param>
    /// <param name="limits">
    /// The two named time limits. Defaults to the spec §7 values for
    /// <paramref name="maxSteps"/> when omitted.
    /// </param>
    public static ExternalMatchResult Run(
        MapGraph map,
        SimulationConfig config,
        ExternalAgentLaunch launch,
        int externalSlot,
        IAgent baseline,
        ulong seed,
        int maxSteps,
        string scenario = ExternalAgent.StandardScenario,
        ExternalTimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentOutOfRangeException.ThrowIfNegative(externalSlot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

        if (config.AgentCount != 2)
        {
            throw new ArgumentException(
                "An external match is head-to-head; SimulationConfig.AgentCount must be 2.",
                nameof(config));
        }

        if (externalSlot > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(externalSlot),
                externalSlot,
                "An external match is head-to-head; the external slot must be 0 or 1.");
        }

        var effectiveLimits = limits ?? ExternalTimeLimits.Default(maxSteps);
        var baselineSlot = 1 - externalSlot;

        // §7's host gate, run *before* the process is started, so a match Lattice
        // refuses on its own limits never spawns an agent to blame (§8.4).
        var refused = CheckOpeningObservation(map, config, externalSlot);
        if (refused is not null)
        {
            return VoidResult(seed, externalSlot, refused);
        }

        ExternalAgent? agent = null;
        ExternalMatchResult result = default!;
        try
        {
            // One process per match: created here, disposed below on every path,
            // and never reused across matches, seeds, or mirrored seatings (§3, U-8).
            agent = new ExternalAgent(launch, externalSlot, seed, scenario, maxSteps, config.AgentCount, effectiveLimits);

            var agents = new IAgent[2];
            agents[externalSlot] = agent;
            agents[baselineSlot] = baseline;

            result = Completed(seed, externalSlot, ScenarioRunner.Run(map, config, agents, maxSteps), agent.ProcessId);
        }
        catch (ExternalAgentFaultException faulted)
        {
            result = Failed(seed, externalSlot, faulted.Fault, agent?.ProcessId, agent?.LastKnownScores);
        }
        finally
        {
            // Kill-on-dispose on every path, including every failure: a match can
            // never leave a process behind. The observed exit state is folded in
            // after the dispose, so the result reports what actually happened rather
            // than what was true while the match was still running.
            agent?.Dispose();

            if (agent is not null)
            {
                result = result with
                {
                    ChildProcessId = result.ChildProcessId ?? agent.ProcessId,
                    ChildExited = agent.ProcessExited,

                    // Read after the dispose, so the total includes whatever the
                    // child emitted on its way out.
                    StderrBytesObserved = agent.Diagnostics.TotalBytes,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Aggregates match results into the report stage 4 renders: failures by
    /// reason code, and the void-run count. Pure, and it changes no outcome.
    /// </summary>
    public static ExternalMatchReport Report(IEnumerable<ExternalMatchResult> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var rows = matches.ToArray();
        var byCode = new Dictionary<ProtocolReason, int>();

        foreach (var row in rows.Where(r => r.IsAgentFailure))
        {
            var reason = row.Fault!.Reason;
            byCode[reason] = byCode.TryGetValue(reason, out var count) ? count + 1 : 1;
        }

        return new ExternalMatchReport(rows, byCode, rows.Count(r => r.IsVoid));
    }

    /// <summary>
    /// The step-0 observation this match would open with, so the §7 outbound gate
    /// can run before a process exists. One line, mirroring
    /// <see cref="ScenarioRunner"/>'s own observation construction, and evaluated
    /// once per match.
    /// </summary>
    private static ExternalAgentFault? CheckOpeningObservation(
        MapGraph map,
        SimulationConfig config,
        int externalSlot)
    {
        var state = Simulation.CreateInitial(map, config, DynamicMapRuleSet.None);
        var observation = new Observation(
            externalSlot,
            state.Map,
            state.Agents,
            state.Claims,
            state.StepCount);

        return ExternalAgent.CheckOutbound(step: 0, observation);
    }

    private static ExternalMatchResult Completed(ulong seed, int externalSlot, ScenarioResult episode, int? childId)
    {
        var metrics = episode.Metrics;
        var scoreA = metrics.Agents[0].Score;
        var scoreB = metrics.Agents[1].Score;

        // The external side is recorded as Team A and the seat it actually played
        // is carried separately in ExternalSlot, so this row means exactly what an
        // in-process row means and the seat is never guessed from a team label.
        var outcome = Classify(metrics, scoreA, scoreB);
        var row = new MatchResult(
            seed,
            TeamAIndex: 0,
            TeamBIndex: 1,
            TeamA: "external",
            TeamB: "baseline",
            outcome,
            scoreA,
            scoreB,
            metrics.TotalSteps,
            metrics.TerminationReason,
            metrics.ContentionRate);

        return new ExternalMatchResult(
            seed,
            externalSlot,
            row,
            PolicyOutcome(outcome, policyAtSeat: externalSlot),
            Fault: null,
            episode,
            childId);
    }

    /// <summary>
    /// The row for a match the external agent lost, carrying the reason code and
    /// the scores as they stood when it stopped.
    /// </summary>
    /// <param name="seed">The run seed.</param>
    /// <param name="externalSlot">The seat the external process played.</param>
    /// <param name="fault">The failure, which is agent-attributable by construction here.</param>
    /// <param name="childId">The child's process id, when one was started.</param>
    /// <param name="scoresAtFailure">
    /// Both slots' scores at the start of the step that failed, or
    /// <see langword="null"/> when no step ever began — a handshake failure, where
    /// 0-0 is the true score rather than a missing one.
    /// </param>
    /// <remarks>
    /// The scores on the row are the ones the match had reached, not 0-0. They come
    /// from the last observation the exchange was driven with, so they are the state
    /// the failing step started from, and a reader comparing two seeds can see that
    /// one agent got further than the other before its plumbing broke. They are
    /// indexed by slot, like every other row this runner writes, so the seat is
    /// never guessed from the numbers.
    /// <para>
    /// <b>What they do to the score.</b> They are a report of how far the match
    /// got, and the paired delta is computed from scores, so a failed match's
    /// partial scores do enter the delta even though its outcome counts as a loss.
    /// That is deliberate and it is the price of reporting a real number instead of
    /// a fabricated 0-0: an agent that plays well for a while and then crashes can
    /// therefore contribute a positive delta. The outcome accounting is what stops
    /// a crash from buying a <em>win</em>, and §9.3's <c>AgentFailures</c> count is
    /// what makes the crashes visible next to the delta rather than inside it. A
    /// study that wants the delta to exclude failed matches has to say so in the
    /// analyzer, not here.
    /// </para>
    /// </remarks>
    private static ExternalMatchResult Failed(
        ulong seed,
        int externalSlot,
        ExternalAgentFault fault,
        int? childId,
        (int Slot0Score, int Slot1Score)? scoresAtFailure)
    {
        if (fault.IsHostFault)
        {
            return VoidResult(seed, externalSlot, fault, childId);
        }

        var (scoreA, scoreB) = scoresAtFailure ?? (0, 0);

        // Every agent-attributable code is a win for the baseline side, which is
        // the same shape as any other loss for the external agent, and the reason
        // code travels in the same nullable TerminationReason an environment
        // termination uses (section 9.1).
        //
        // The row's outcome is in SLOT terms, and so it is not what the external
        // agent's own outcome is derived from: at seat 1 a slot-relative "seat 0
        // won" is a win for the baseline, and routing it through
        // PolicyOutcome would report the external agent as the winner of a match
        // it lost. A protocol failure is a loss for the external side whatever the
        // seat and whatever the scores (§9.1), so it is stated rather than
        // computed.
        var outcome = MatchOutcome.TeamBWin;
        var row = new MatchResult(
            seed,
            TeamAIndex: 0,
            TeamBIndex: 1,
            "external",
            "baseline",
            outcome,
            ScoreA: scoreA,
            ScoreB: scoreB,
            TotalSteps: Math.Max(0, fault.Step),
            TerminationReason: fault.TerminationReason,
            ContentionRate: 0.0);

        return new ExternalMatchResult(
            seed,
            externalSlot,
            row,
            MatchOutcome.TeamBWin,
            fault,
            Episode: null,
            childId);
    }

    private static ExternalMatchResult VoidResult(ulong seed, int externalSlot, ExternalAgentFault fault, int? childId = null) =>
        new(seed, externalSlot, Match: null, ExternalOutcome: null, fault, Episode: null, childId);

    private static MatchOutcome Classify(ScenarioMetrics metrics, int scoreA, int scoreB)
    {
        if (!metrics.Terminated)
        {
            return MatchOutcome.Timeout;
        }

        if (scoreA == scoreB)
        {
            return MatchOutcome.Draw;
        }

        return scoreA > scoreB ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;
    }

    /// <summary>
    /// The outcome from the external agent's own point of view, mirroring
    /// <c>PairedEvaluation.PolicyOutcome</c> exactly: <see cref="MatchOutcome.TeamAWin"/>
    /// means the policy at <paramref name="policyAtSeat"/> won and
    /// <see cref="MatchOutcome.TeamBWin"/> means it lost, with
    /// <see cref="MatchOutcome.Timeout"/> and <see cref="MatchOutcome.Draw"/>
    /// passing through. For an agent-attributable failure this is always
    /// <see cref="MatchOutcome.TeamBWin"/> -- a loss (section 9.1).
    /// </summary>
    private static MatchOutcome PolicyOutcome(MatchOutcome match, int policyAtSeat)
    {
        if (match is MatchOutcome.Timeout or MatchOutcome.Draw)
        {
            return match;
        }

        var policyWon = policyAtSeat == 0 ? match == MatchOutcome.TeamAWin : match == MatchOutcome.TeamBWin;
        return policyWon ? MatchOutcome.TeamAWin : MatchOutcome.TeamBWin;
    }
}
