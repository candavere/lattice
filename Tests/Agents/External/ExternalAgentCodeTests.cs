using Lattice.Agents;
using Lattice.Agents.External;
using Lattice.Environment;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Agents.External;

/// <summary>
/// End-to-end coverage of every §8 reason code a real child process can provoke,
/// through the actual stub process over an actual pipe.
/// </summary>
/// <remarks>
/// <para>
/// Each test asserts three things, not one: the <b>exact</b> reason code, that the
/// match was scored as a <b>loss</b> for the external side, and — for the codes
/// the spec attaches a stderr tail to — that the tail is really on the record. A
/// test that asserted only "the match failed" would pass for the wrong reason
/// eleven different ways.
/// </para>
/// <para>
/// Every match runs through <see cref="Match"/>, which also asserts that the
/// child process is gone once the match returns, so no test here can leave an
/// orphan behind or pass without the kill-on-dispose having actually worked. All
/// the budgets are test-only and far below the spec defaults, and each is still
/// built through the spec's own formula, so §7's constraint holds in every one.
/// </para>
/// <para>
/// The tests are <c>async</c> over <see cref="Task.Run(Task{T})"/> purely so
/// xunit's per-test timeout can bound a call that is necessarily blocking: the
/// runner is synchronous because <see cref="IAgent.Decide"/> is, and the timeout
/// is worth more than the tidiness of a direct call.
/// </para>
/// </remarks>
public class ExternalAgentCodeTests
{
    /// <summary>The seed every test plays; fixed so a failure is reproducible.</summary>
    private const ulong Seed = 1001;

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Timeout_Handshake_When_The_Agent_Never_Acks()
    {
        var result = await Match(mode: "silent-handshake", stepMs: 400, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.TimeoutHandshake, Fault(result).Reason);
        AssertScoredAsLoss(result, "timeout_handshake");

        // A timing failure carries the tail, so a human can see the agent was
        // alive and simply silent rather than gone.
        AssertStderrTailAttached(result);

        // The handshake is detected before step 0 exists.
        Assert.Equal(-1, Fault(result).Step);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Timeout_Step_When_The_Agent_Stops_Answering_Mid_Match()
    {
        // Hangs at step 1: one exchange completes, so this is a stall rather
        // than a failure to start.
        var result = await Match(mode: "hang-at-step", argument: "1", stepMs: 400, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.TimeoutStep, Fault(result).Reason);
        AssertScoredAsLoss(result, "timeout_step");
        AssertStderrTailAttached(result);
        Assert.Equal(1, Fault(result).Step);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Timeout_Match_When_The_Whole_Match_Budget_Expires()
    {
        // The only way the match budget can bind, and it is worth spelling out
        // because it is a consequence of the §7 constraint rather than a free
        // choice. match_timeout_ms >= step_timeout_ms x max_ticks, and the step
        // loop runs exactly max_ticks steps, so the step waits alone can never
        // reach the match budget. The budget the handshake consumes is what tips
        // it over -- so the stub answers the handshake immediately and then spends
        // most of each step budget before answering. With 3 ticks, a 1500 ms step
        // budget and a 1300 ms per-step delay, the budget is gone partway through
        // the last step, where the step budget no longer fits inside what is left
        // and the match limit is what fires.
        // One tick, no slack, and a delay 50 ms under the step budget. That is the
        // whole configuration, and each number is load-bearing:
        //
        //   * match_timeout_ms = step_timeout_ms x 1, so the budget the handshake
        //     consumed is the whole of what is left when step 0 begins. Whatever
        //     the child's startup cost, less than a full step budget remains.
        //   * the stub then delays 1950 ms, which is more than the 2000 - startup
        //     that remains, so the match limit is what fires. More ticks would
        //     make the budget *harder* to exhaust, since each extra tick adds
        //     budget without adding a delay.
        //   * the 50 ms gap is the only margin protecting the *step* budget, and
        //     it is enough because Thread.Sleep never returns early and the stub's
        //     own parse-and-encode is sub-millisecond.
        //
        // So the test passes for any child startup between a few milliseconds and
        // the full step budget, which is the entire realistic range.
        const int stepMs = 2_000;
        var result = await Match(mode: "slow", argument: "1950", stepMs: stepMs, maxTicks: 1, slackMs: 0);

        Assert.Equal(ProtocolReason.TimeoutMatch, Fault(result).Reason);
        AssertScoredAsLoss(result, "timeout_match");
        AssertStderrTailAttached(result);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Agent_Exited_When_The_Process_Leaves_Cleanly_Mid_Match()
    {
        // Exit code 0 is a clean exit, which is agent_exited and not a crash.
        var result = await Match(mode: "exit-at-step", argument: "1", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.AgentExited, Fault(result).Reason);
        AssertScoredAsLoss(result, "agent_exited");
        AssertStderrTailAttached(result);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Agent_Crashed_When_The_Process_Exits_Non_Zero()
    {
        var result = await Match(mode: "crash", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.AgentCrashed, Fault(result).Reason);
        AssertScoredAsLoss(result, "agent_crashed");
        AssertStderrTailAttached(result);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Malformed_Json_When_The_Agent_Sends_Truncated_Output()
    {
        var result = await Match(mode: "bad-json", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.MalformedJson, Fault(result).Reason);
        AssertScoredAsLoss(result, "malformed_json");
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Unknown_Field_When_The_Agent_Sends_A_Field_Outside_The_Schema()
    {
        var result = await Match(mode: "unknown-field", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.UnknownField, Fault(result).Reason);
        AssertScoredAsLoss(result, "unknown_field");
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Step_Mismatch_When_The_Agent_Echoes_The_Wrong_Step()
    {
        var result = await Match(mode: "wrong-step", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.StepMismatch, Fault(result).Reason);
        AssertScoredAsLoss(result, "step_mismatch");
        Assert.Equal(0, Fault(result).Step);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Protocol_Mismatch_When_The_Agent_Acks_A_Different_Version()
    {
        // No step is ever reached: the handshake itself is refused, so the reason
        // is the version and not whatever the exit would have been.
        var result = await Match(mode: "wrong-protocol", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.ProtocolMismatch, Fault(result).Reason);
        AssertScoredAsLoss(result, "protocol_mismatch");
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Line_Too_Long_When_The_Agent_Sends_An_Over_Length_Line()
    {
        // The stub then exits 0, so this also pins §8.4's precedence: a detected
        // violation outranks the clean exit that followed it.
        var result = await Match(mode: "oversize-line", stepMs: 5_000, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.LineTooLong, Fault(result).Reason);
        AssertScoredAsLoss(result, "line_too_long");
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Illegal_Action_When_A_Valid_Action_Is_Outside_The_Map()
    {
        // Well formed and in shape, but a Move to a zone no map has.
        var result = await Match(mode: "illegal-action", stepMs: 800, maxTicks: 4, slackMs: 1_000);

        Assert.Equal(ProtocolReason.IllegalAction, Fault(result).Reason);
        AssertScoredAsLoss(result, "illegal_action");
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Host_Limit_Voids_The_Match_And_Blames_No_Process()
    {
        // A map whose serialized observation cannot fit in one line. This is
        // Lattice's own limit, so the run is void: not a loss, no match row, and
        // no process started at all to blame.
        var oversized = ExternalAgentTestHost.OversizedObservationMap();

        var result = await Task.Run(() => ExternalMatchRunner.Run(
            oversized,
            ExternalAgentTestHost.Config(maxTicks: 4),
            ExternalAgentTestHost.Launch("conform"),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: 4));

        var fault = Fault(result);
        Assert.Equal(ProtocolReason.HostLimit, fault.Reason);
        Assert.True(fault.IsHostFault);
        Assert.False(fault.IsAgentFault);

        // Void, not a loss: no match row, no scored outcome, and nothing counted
        // as an agent failure.
        Assert.Null(result.Match);
        Assert.Null(result.ExternalOutcome);
        Assert.False(result.IsAgentFailure);

        var report = ExternalMatchRunner.Report([result]);
        Assert.Equal(1, report.VoidRuns);
        Assert.Equal(0, report.AgentFailureCount);
        Assert.Empty(report.AgentFailuresByCode);

        // The refusal happens before the exchange exists, so there is no child:
        // this is the "no process blamed" claim in its strongest form, since the
        // process is never created rather than created and then excused.
        Assert.Null(result.ChildProcessId);
    }

    [Fact(Timeout = ExternalAgentTestHost.TestTimeoutMs)]
    public async Task Stderr_Flood_Completes_Normally_And_Does_Not_Deadlock()
    {
        // The stub writes strictly more than 1 MiB to stderr while playing
        // correctly, which is more than the 64 KiB ring and more than the OS pipe
        // buffer can hold. If the drain were deferred or bounded, the child would
        // block mid-match and this would report timeout_step instead.
        const int maxTicks = 6;
        var result = await Match(mode: "stderr-flood", stepMs: 2_000, maxTicks: maxTicks, slackMs: 2_000);

        Assert.Null(result.Fault);
        Assert.True(result.Completed);
        Assert.Equal(maxTicks, result.Episode!.Metrics.TotalSteps);

        // The drain kept up with far more than the ring holds. Asserting only
        // that the match completed would pass for a stub that stayed under the
        // cap; the byte total is what shows the flood really happened and really
        // was drained rather than truncated.
        Assert.True(
            result.StderrBytesObserved > 1024 * 1024,
            $"the flood wrote only {result.StderrBytesObserved} stderr byte(s); the test did not flood.");
    }

    /// <summary>
    /// Plays one match against the stub and asserts the child is gone afterwards,
    /// so every spawning test carries the orphan check whether or not it thought
    /// to.
    /// </summary>
    private static async Task<ExternalMatchResult> Match(
        string mode,
        int stepMs,
        int maxTicks,
        int slackMs,
        string? argument = null)
    {
        var result = await Task.Run(() => ExternalMatchRunner.Run(
            ExternalAgentTestHost.TwoZoneMap(),
            ExternalAgentTestHost.Config(maxTicks),
            ExternalAgentTestHost.Launch(mode, argument),
            externalSlot: 0,
            baseline: new AlwaysWaitAgent(1),
            Seed,
            maxSteps: maxTicks,
            limits: ExternalAgentTestHost.Budget(stepMs, maxTicks, slackMs)));

        Assert.True(result.ChildExited, $"the {mode} child was not observed to have exited.");
        if (result.ChildProcessId is { } childId)
        {
            ExternalAgentTestHost.AssertNoProcessLeft(childId);
        }

        return result;
    }

    /// <summary>The single failure on a result that must have exactly one.</summary>
    private static ExternalAgentFault Fault(ExternalMatchResult result)
    {
        Assert.NotNull(result.Fault);
        return result.Fault;
    }

    /// <summary>
    /// Asserts the failure was recorded as a loss for the external side: a match
    /// row exists, its outcome is a win for the baseline, the external policy
    /// outcome is the loss, and the reason code is in the one nullable
    /// <c>TerminationReason</c> field an environment termination also uses.
    /// </summary>
    private static void AssertScoredAsLoss(ExternalMatchResult result, string expectedReason)
    {
        Assert.True(result.IsAgentFailure);
        Assert.False(result.IsVoid);
        Assert.False(result.Completed);

        var match = Assert.IsType<MatchResult>(result.Match);
        Assert.Equal(expectedReason, match.TerminationReason);
        Assert.Equal(MatchOutcome.TeamBWin, match.Outcome);
        Assert.Equal(MatchOutcome.TeamBWin, result.ExternalOutcome);

        var report = ExternalMatchRunner.Report([result]);
        Assert.Equal(0, report.VoidRuns);
        Assert.Equal(1, report.AgentFailureCount);
        Assert.Equal(1, report.AgentFailuresByCode[result.Fault!.Reason]);
    }

    private static void AssertStderrTailAttached(ExternalMatchResult result)
    {
        var tail = Fault(result).StderrTail;
        Assert.NotNull(tail);

        // The stub announces its mode on stderr at startup, so a tail that
        // reached the record is provably the agent's own output and not a
        // placeholder. Bounded by the ring, which is the other half of the claim.
        Assert.Contains("stub: mode=", tail, StringComparison.Ordinal);
        Assert.True(
            tail.Length <= StderrRing.CapacityBytes,
            $"the attached stderr tail was {tail.Length} chars, over the 64 KiB ring.");
    }
}

/// <summary>
/// The in-process twin of the stub's <c>conform</c> mode: a legal
/// <see cref="ActionKind.Wait"/> every step.
/// </summary>
/// <remarks>
/// It exists so the determinism test can compare a wire trajectory against an
/// in-process one <em>byte for byte</em>. A "close enough" comparison would not
/// show a dropped field or a reordered member, which is exactly the class of bug
/// the §5.2 projection rule exists to prevent.
/// </remarks>
internal sealed class AlwaysWaitAgent : IAgent
{
    public AlwaysWaitAgent(int agentId) => AgentId = agentId;

    public int AgentId { get; }

    public AgentAction Decide(Observation observation) => new(ActionKind.Wait);
}
