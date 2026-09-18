using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// Competitive statistics for one agent after a scenario run. Score is the
/// number of resources collected (each collect grants exactly +1), Moves is
/// the number of Move-kind actions it issued across the episode, and
/// Efficiency is Score per move tick — resources earned per step actually
/// spent travelling. Efficiency floors the move count at 1 so agents that
/// never move (or that spawned onto their first claim) still produce a
/// defined, comparable number rather than a division-by-zero artifact.
/// </summary>
public sealed record AgentMetrics(int AgentId, int Score, int Moves, double Efficiency);

/// <summary>
/// Episode-level competitive metrics for one scenario run. TerminationReason
/// mirrors <see cref="Info.Reason"/> ("resources-exhausted" or "tick-limit"),
/// or null when the run was cut short by the caller's step budget (a
/// "truncated" run that never reached a terminal tick). Contention is defined
/// tick-by-tick: a tick is "contended" when at least two agents attempted a
/// Collect of the SAME resource id in that tick (a claim race), OR when at
/// least two agents requested a Move across the SAME capacity-1 choke edge in
/// that tick (a transit denial on a single-lane gate) — regardless of who won
/// the claim or passage; ContentionRate is the fraction of recorded ticks that
/// were contended. Counting attempts (not outcomes) is deliberate — it
/// measures how often agents race for the same prize or gate, which is exactly
/// the pressure a competitive match is supposed to surface.
/// </summary>
public sealed record ScenarioMetrics(
    string? TerminationReason,
    bool Terminated,
    int TotalSteps,
    int MaxSteps,
    int ContendedTicks,
    double ContentionRate,
    AgentMetrics[] Agents);

/// <summary>
/// The complete outcome of a scenario run: the aggregated
/// <see cref="ScenarioMetrics"/> plus the raw turns and results, kept so
/// callers can replay, verify, or re-render the exact episode that produced
/// the numbers. All three views describe the same events, so they can never
/// drift apart.
/// </summary>
public sealed record ScenarioResult(
    ScenarioMetrics Metrics,
    AgentAction[][] Turns,
    StepResult[] Results);