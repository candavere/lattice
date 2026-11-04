using System.Text.Json;
using Lattice.Analytics;
using Lattice.Environment;
using Lattice.Trajectories;
using Xunit;

namespace Lattice.Tests.Analytics;

/// <summary>
/// <see cref="CounterfactualEvaluator"/>: re-roll a completed
/// trajectory from branch tick K under an alternative action sequence and read
/// off the outcome divergence (per-agent score delta, winner change, steps to
/// complete) against the recorded outcome — with the recording provably left
/// untouched and the branch fully deterministic.
/// </summary>
public class CounterfactualEvaluatorTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void AlternativeBranch_FlipsTheWinner_Deterministically()
    {
        // Fixture-5: A1 wins 2-1 at tick 6 after A0 takes the contested R0 at
        // tick 3. Branching at tick 4 to send BOTH agents to zone 2 early lets
        // A0 win both contested zone-2 resources instead.
        var recording = AnalyticsFixtures.Fixture5();
        var recordingJson = Json(recording);

        var first = CounterfactualEvaluator.Evaluate(
            recording,
            branchTick: 4,
            alternativeTurns: new[]
            {
                new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Move, ZoneId: 2) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Collect, ResourceId: 1) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Collect, ResourceId: 2) },
            });

        var second = CounterfactualEvaluator.Evaluate(
            recording,
            4,
            new[]
            {
                new[] { new AgentAction(ActionKind.Move, ZoneId: 2), new AgentAction(ActionKind.Move, ZoneId: 2) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 1), new AgentAction(ActionKind.Collect, ResourceId: 1) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 2), new AgentAction(ActionKind.Collect, ResourceId: 2) },
            });

        Assert.Equal([1, 2], first.RecordedScores);
        Assert.Equal([2, 1], first.ForkedScores);
        Assert.Equal([1, -1], first.ScoreDeltas);
        Assert.Equal(1, first.RecordedWinner);
        Assert.Equal(0, first.ForkedWinner);
        Assert.True(first.WinnerChanged);
        Assert.Equal(first.RecordedSteps, first.ForkedSteps);

        Assert.Equal(Json(first), Json(second)); // identical branch -> identical outcome
        Assert.Equal(recordingJson, Json(recording)); // recording provably unmutated
    }

    [Fact]
    public void AlternativeBranch_WithIncompleteSequence_RunsToTickLimit()
    {
        // Fixture-6: three agents race for a single resource (A0 wins at tick 2).
        // Branching tick 2 to have everyone Wait leaves the resource unclaimed,
        // so the Wait filler continues the fork to the MaxTicks=100 limit.
        var recording = AnalyticsFixtures.Fixture6();

        var result = CounterfactualEvaluator.Evaluate(
            recording,
            branchTick: 2,
            alternativeTurns: new[]
            {
                new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
            });

        Assert.Equal(2, result.RecordedSteps);
        Assert.Equal(100, result.ForkedSteps);
        Assert.Equal([0, 0, 0], result.ForkedScores);
        Assert.Equal([0, 0, -1], result.ScoreDeltas);
        Assert.Equal(0, result.ForkedWinner);
    }

    [Fact]
    public void AlternativeBranch_ThatSkipsTheFinalCollect_ChangesStepsToComplete()
    {
        // Fixture-1's last tick collects A0's third resource. Replacing it with
        // Wait leaves 3 of 4 resources claimed, so completion shifts from tick 6
        // (recorded resource-exhaustion) to the 100-tick limit with a 1-point
        // score delta for A0.
        var recording = AnalyticsFixtures.Fixture1();

        var result = CounterfactualEvaluator.Evaluate(
            recording,
            branchTick: 6,
            alternativeTurns: new[]
            {
                new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
            });

        Assert.Equal(6, result.RecordedSteps);
        Assert.True(result.ForkedSteps > result.RecordedSteps, "Skipping the final collect must extend the episode.");
        Assert.Equal([2, 1], result.ForkedScores);
        Assert.Equal([-1, 0], result.ScoreDeltas);
        Assert.False(result.WinnerChanged);
    }

    [Fact]
    public void EmptyAlternativeSequence_DefaultsToWaitFiller_ToTickLimit()
    {
        // Branch tick 1 with no alternative turns: every later tick is a Wait
        // filler, so the fork runs to the 100-tick limit — still deterministic.
        var recording = AnalyticsFixtures.Fixture2();
        Assert.Equal(4, recording.Final.TotalSteps);

        var result = CounterfactualEvaluator.Evaluate(recording, branchTick: 1, Array.Empty<AgentAction[]>());

        Assert.Equal([0, 0], result.ForkedScores);
        Assert.Equal(96, result.ForkedSteps - result.RecordedSteps);
        Assert.Equal(100, result.ForkedSteps);
    }

    [Fact]
    public void BranchingRecordingWithDynamicRules_HonorsTheRecordedTopology()
    {
        // A recording whose header carries a TimedPortcullisRule (choke 0 shut
        // for ticks 1-5, open at tick 6, on the three-zone line). Branching at
        // tick 2 with a Move across the choke must be refused in the fork —
        // the portcullis is closed at fork tick 2 — so the follow-up collect
        // fails and the fork runs to the 20-tick limit scoreless. If the fork
        // ignored the header's rules (treated them as absent), the tick-2 Move
        // would cross immediately and the tick-3 Collect would land, ending
        // the fork at tick 3 with agent 0 the winner.
        const ulong seed = 0xD3D3UL;
        var config = new SimulationConfig(2, 20, TransitSpeed: 4);
        var map = new MapGraph(
            new[]
            {
                new Zone(0, new GridPoint(0, 0)),
                new Zone(1, new GridPoint(0, 8)),
                new Zone(2, new GridPoint(0, 16)),
            },
            new[] { new ResourceNode(0, 1, new GridPoint(0, 8)) },
            new[]
            {
                new ChokePoint(0, 0, 1, MaxOccupancy: 1),
                new ChokePoint(1, 1, 2),
            });
        var rules = new DynamicMapRuleSet(new IDynamicMapRule[]
        {
            new TimedPortcullisRule(ChokeId: 0, OpenTicks: 1, ClosedTicks: 5),
        });

        var recording = TrajectoryWriter.Record(
            map, config, seed,
            new[]
            {
                new[] { new AgentAction(ActionKind.Wait), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
            },
            new StringWriter(),
            rules: rules);
        Assert.NotNull(recording.Header.DynamicRules);
        Assert.True(recording.Steps.Length >= 3, "fixture episode must have at least three recorded steps");

        var result = CounterfactualEvaluator.Evaluate(
            recording,
            branchTick: 2,
            alternativeTurns: new[]
            {
                new[] { new AgentAction(ActionKind.Move, ZoneId: 1), new AgentAction(ActionKind.Wait) },
                new[] { new AgentAction(ActionKind.Collect, ResourceId: 0), new AgentAction(ActionKind.Wait) },
            });

        // The closed portcullis refused the tick-2 Move in the fork, so the
        // collect never lands and the Wait filler runs the fork to MaxTicks.
        Assert.Equal([0, 0], result.ForkedScores);
        Assert.Equal([0, 0], result.ScoreDeltas);
        Assert.Equal(20, result.ForkedSteps);
        Assert.Equal(0, result.ForkedWinner);
    }

    [Fact]
    public void BranchTick_OutOfRange_Throws()
    {
        var recording = AnalyticsFixtures.Fixture1();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CounterfactualEvaluator.Evaluate(recording, 0, Array.Empty<AgentAction[]>()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CounterfactualEvaluator.Evaluate(recording, recording.Steps.Length + 1, Array.Empty<AgentAction[]>()));
    }

    [Fact]
    public void BranchingFromATerminalPrefix_Throws()
    {
        var recording = AnalyticsFixtures.Fixture1();

        // Branch tick 8 is beyond the 6 recorded steps, so it throws for range
        // rather than attempting to branch after the episode already ended.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CounterfactualEvaluator.Evaluate(recording, 8, Array.Empty<AgentAction[]>()));
    }
}