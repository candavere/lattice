using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// What a failed match's record says: the scores as they stood when the agent
/// stopped, and a diagnostic that names the budget which actually expired.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are about a human reading a failed run. A row that says
/// <c>0-0</c> for a match that ran to step 12 tells the reader nothing they can
/// act on, and a timeout whose detail names the wrong budget sends them to tune
/// the wrong number — the step budget when the match budget ran out is a
/// plausible-looking fix that changes nothing.
/// </para>
/// <para>
/// Three real child processes, and no more: the scores can only come from a match
/// that really played, and the two timeout branches are the only two the §7
/// constraint can produce.
/// </para>
/// </remarks>
public class ExternalMatchFailureRecordTests
{
    private const ulong Seed = 1001;

    private const int MaxTicks = 6;

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Failed_Match_Records_The_Scores_At_The_Moment_Of_Failure()
    {
        // The stub answers steps 0 and 1, then stops. The baseline has collected
        // one of the map's two resources on step 0, so at the moment of the
        // failure the score is 0 for the external agent and 1 for the baseline —
        // which is exactly what the row used to erase by recording 0-0.
        //
        // Two resources, not one: a map with a single resource would reach
        // `resources-exhausted` on step 0, the match would end there normally, and
        // there would be no failure to record a score for.
        const int stallAtStep = 2;
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            TwoResourceMap(),
            ExternalAgentTestHost.Config(MaxTicks),
            ExternalAgentTestHost.Launch("hang-at-step", stallAtStep.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            externalSlot: 0,
            baseline: new CollectOnceAgent(1),
            Seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 400, MaxTicks, slackMs: 1_000)));

        Assert.Equal(ProtocolReason.TimeoutStep, result.Fault!.Reason);

        var match = Assert.IsType<MatchResult>(result.Match);
        Assert.Equal(0, match.ScoreA);
        Assert.Equal(1, match.ScoreB);
        Assert.Equal(stallAtStep, match.TotalSteps);

        // Still a loss, whatever the scores say: a protocol failure is a loss for
        // the external side (§9.1), and the row says so in the same field an
        // environment termination uses.
        Assert.Equal("timeout_step", match.TerminationReason);
        Assert.Equal(MatchOutcome.TeamBWin, match.Outcome);
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Handshake_Timeout_Names_The_Step_Budget_That_Expired()
    {
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            ExternalAgentTestHost.TwoZoneMap(),
            ExternalAgentTestHost.Config(MaxTicks),
            ExternalAgentTestHost.Launch("silent-handshake"),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 400, MaxTicks, slackMs: 1_000)));

        var fault = result.Fault!;
        Assert.Equal(ProtocolReason.TimeoutHandshake, fault.Reason);

        // The step budget, by its own name and value. The match budget is
        // deliberately not named: under §7 it cannot be the one that expired
        // here, and a detail that listed both would tell the reader nothing about
        // which knob to turn.
        Assert.Contains("step_timeout_ms 400", fault.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("match_timeout_ms", fault.Detail, StringComparison.Ordinal);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Match_Timeout_Names_The_Match_Budget_That_Expired()
    {
        // The one configuration in which the match budget binds: one tick and no
        // slack make the two budgets equal, so the match budget is the smaller one
        // from the first wait onward. The stub then stalls three times the entire
        // budget, so no child startup cost can decide the outcome.
        const int stepMs = 2_000;
        const int stallMs = 6_000;

        var result = await Task.Run(() => ExternalMatchRunner.Run(
            ExternalAgentTestHost.TwoZoneMap(),
            ExternalAgentTestHost.Config(maxTicks: 1),
            ExternalAgentTestHost.Launch("stall-match", stallMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: 1,
            limits: ExternalAgentTestHost.Budget(stepMs, maxTicks: 1, slackMs: 0)));

        var fault = result.Fault!;
        Assert.Equal(ProtocolReason.TimeoutMatch, fault.Reason);

        // The match budget, by its own name and value — the mirror of the
        // handshake case above, and the reason both texts come from one place
        // rather than from two hard-coded strings.
        Assert.Contains($"match_timeout_ms {stepMs}", fault.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("step_timeout_ms", fault.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two zones with a resource in each, so whichever seat the baseline is given
    /// it can score, and so the episode cannot end by exhaustion after one
    /// collect — a single resource would end the match on step 0 and there would
    /// be no step left to fail at.
    /// </summary>
    private static MapGraph TwoResourceMap() =>
        new(
            [
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(3, 0)),
            ],
            [
                new ResourceNode(0, 0, new GridPoint(0, 0)),
                new ResourceNode(1, 1, new GridPoint(3, 0)),
            ],
            [new ChokePoint(0, 0, 1, MaxOccupancy: 1)]);

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Played_Match_Reports_The_External_Agents_Own_Outcome_From_Either_Seat()
    {
        // The baseline collects and the external stub only waits, so the external
        // agent loses — from seat 1 this time, which is the seating that used to
        // report the opposite. The row's own Outcome is in slot terms (seat 0 won);
        // ExternalOutcome is documented as the policy's own view, and reading it
        // from the wrong seat turns a loss into a reported win.
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            TwoResourceMap(),
            ExternalAgentTestHost.Config(MaxTicks),
            ExternalAgentTestHost.Launch("conform"),
            externalSlot: 1,
            baseline: new CollectOnceAgent(0),
            Seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 2_000, MaxTicks, slackMs: 2_000)));

        Assert.Null(result.Fault);

        // Slot 0 is the baseline and it scored, so the row says seat 0 won.
        var match = Assert.IsType<MatchResult>(result.Match);
        Assert.Equal(1, match.ScoreA);
        Assert.Equal(0, match.ScoreB);
        Assert.Equal(MatchOutcome.TeamAWin, match.Outcome);

        // The policy sat at seat 1, so its own outcome is the loss.
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task A_Failed_Match_Is_A_Loss_For_The_External_Agent_From_Either_Seat()
    {
        // A protocol failure is a loss for the external side whatever the seat and
        // whatever the scores (§9.1), so this must not be derived from the row's
        // slot-relative outcome at all: at seat 1 that outcome says seat 0 won,
        // which would report a win.
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            TwoResourceMap(),
            ExternalAgentTestHost.Config(MaxTicks),
            ExternalAgentTestHost.Launch("hang-at-step", "2"),
            externalSlot: 1,
            baseline: new AlwaysWaitAgent(0),
            Seed,
            maxSteps: MaxTicks,
            limits: ExternalAgentTestHost.Budget(stepTimeoutMs: 400, MaxTicks, slackMs: 1_000)));

        Assert.Equal(ProtocolReason.TimeoutStep, result.Fault!.Reason);
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);
    }
}

/// <summary>
/// An in-process baseline that collects the lowest-numbered unclaimed resource in
/// its own zone and then waits, so a match has a non-zero score to lose.
/// </summary>
/// <remarks>
/// It has to be a real scoring agent rather than a constant: the point of the
/// partial-score test is that the number on the row came out of the simulation
/// rather than out of the test. Reading the resource out of the observation also
/// keeps it honest when the two seats start in different zones, which is the
/// whole point of the mirrored-seating tests.
/// </remarks>
internal sealed class CollectOnceAgent : IAgent
{
    public CollectOnceAgent(int agentId) => AgentId = agentId;

    public int AgentId { get; }

    public AgentAction Decide(Observation observation)
    {
        if (observation.AgentStates[AgentId].Score > 0)
        {
            return new AgentAction(ActionKind.Wait);
        }

        var here = observation.AgentStates[AgentId].ZoneId;
        var claimed = observation.Claims;
        var target = observation.Map.Resources
            .Where(resource => resource.ZoneId == here && !claimed.Contains(resource.Id))
            .OrderBy(resource => resource.Id)
            .FirstOrDefault();

        return target is null
            ? new AgentAction(ActionKind.Wait)
            : new AgentAction(ActionKind.Collect, ResourceId: target.Id);
    }
}
