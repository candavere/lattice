using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Analytics;

/// <summary>
/// The divergence between a recorded trajectory's outcome and the outcome of a
/// counterfactual branch (T9.1). Reported against the same axis the recording
/// uses: per-agent final scores, the winner, and the ticks-to-completion — so
/// a delta is directly interpretable ("agent 0 would have scored +2 and won").
/// </summary>
public sealed record CounterfactualResult(
    int BranchTick,
    int RecordedSteps,
    int ForkedSteps,
    int[] RecordedScores,
    int[] ForkedScores,
    int? RecordedWinner,
    int? ForkedWinner,
    int[] ScoreDeltas,
    bool WinnerChanged);

/// <summary>
/// Re-rolls a completed trajectory from branch tick K under an alternative
/// action sequence (T9.1). The recorded prefix (turns strictly before K) is
/// replayed exactly; the alternative turns replace the recorded turn at K and
/// drive the episode to completion; the recorded suffix is discarded. Any
/// remaining ticks after the alternative sequence is exhausted are played as
/// Wait so the fork always reaches a terminal tick (bounded by MaxTicks) and a
/// steps-to-complete divergence is well-defined. All stepping happens on a
/// detached <see cref="SimulationFork"/>, so the recording and its header's
/// map/config are never touched.
/// </summary>
public static class CounterfactualEvaluator
{
    /// <summary>
    /// Evaluates the branch at <paramref name="branchTick"/> (1-based) on
    /// <paramref name="recording"/> under <paramref name="alternativeTurns"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a branch tick
    /// outside 1..recorded-step-count and
    /// <see cref="InvalidOperationException"/> if the recorded prefix is already
    /// terminal (nothing left to branch on). Deterministic: identical inputs
    /// yield an identical <see cref="CounterfactualResult"/>.
    /// </summary>
    public static CounterfactualResult Evaluate(
        TrajectoryRecording recording,
        int branchTick,
        AgentAction[][] alternativeTurns)
    {
        if (branchTick < 1 || branchTick > recording.Steps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(branchTick),
                branchTick,
                $"Branch tick must be within 1..{recording.Steps.Length} (recorded step count).");
        }

        if (alternativeTurns is null)
        {
            throw new ArgumentNullException(nameof(alternativeTurns));
        }

        var config = recording.Header.SimulationConfig;
        var fork = SimulationFork.Create(Simulation.CreateInitial(recording.Header.Map, config), config);
        var forkedSteps = 0;
        StepResult lastResult = default!;

        // Replay the recorded prefix (turns with StepNumber < branchTick).
        foreach (var step in recording.Steps.Where(step => step.StepNumber < branchTick))
        {
            lastResult = fork.Step(step.Actions);
            forkedSteps++;
            if (lastResult.Info.IsTerminal)
            {
                throw new InvalidOperationException(
                    $"Trajectory is terminal at tick {lastResult.Info.StepNumber}; nothing can branch at tick {branchTick}.");
            }
        }

        // Drive the branch to completion under the alternative sequence.
        foreach (var turn in alternativeTurns.Concat(WaitTurns(config.AgentCount)))
        {
            if (fork.IsTerminal || forkedSteps >= config.MaxTicks)
            {
                break;
            }

            lastResult = fork.Step(turn);
            forkedSteps++;
            if (lastResult.Info.IsTerminal)
            {
                break;
            }
        }

        var forkedScores = fork.Snapshot.Agents.Select(agent => agent.Score).ToArray();
        var forkedWinner = lastResult.Info.WinnerAgentId;
        var recordedScores = recording.Final.FinalScores;

        return new CounterfactualResult(
            BranchTick: branchTick,
            RecordedSteps: recording.Final.TotalSteps,
            ForkedSteps: forkedSteps,
            RecordedScores: recordedScores,
            ForkedScores: forkedScores,
            RecordedWinner: recording.Final.WinnerAgentId,
            ForkedWinner: forkedWinner,
            ScoreDeltas: forkedScores.Select((score, i) => score - recordedScores[i]).ToArray(),
            WinnerChanged: recording.Final.WinnerAgentId != forkedWinner);
    }

    /// <summary>
    /// An infinite Wait-turn sequence (lazily evaluated) so the branch
    /// completion loop always has a legal, deterministic filler action.
    /// </summary>
    private static IEnumerable<AgentAction[]> WaitTurns(int agentCount)
    {
        var waitTurn = Enumerable.Range(0, agentCount)
            .Select(_ => new AgentAction(ActionKind.Wait))
            .ToArray();
        while (true)
        {
            yield return waitTurn;
        }
    }
}