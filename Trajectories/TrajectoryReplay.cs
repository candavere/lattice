using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Verifies a recorded trajectory by re-running it: recorded actions are
/// fed into a fresh simulation built from the recorded header (seed map +
/// simulation config) and each replayed <see cref="StepResult"/>'s JSON
/// serialization must equal the recorded one — per-step serialized StepResult
/// equivalence. This is a serialized-result comparison, never a state digest:
/// no canonical simulation-state hash tree currently exists. The replay gate
/// catches any future rule change that breaks determinism or format
/// compatibility.
/// </summary>
public static class TrajectoryReplay
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Replays the recorded actions from a fresh initial state and returns
    /// the resulting StepResults in order (stopping at the recorded terminal
    /// tick, like <see cref="SimulationDriver"/>). Purely for inspection;
    /// use <see cref="Verify"/> for the assertion.
    /// </summary>
    public static List<StepResult> Replay(TrajectoryRecording recording)
    {
        var state = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);
        var results = new List<StepResult>();

        foreach (var step in recording.Steps)
        {
            var outcome = Simulation.Step(state, step.Actions, recording.Header.SimulationConfig);
            results.Add(outcome.Result);
            state = outcome.NextState;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Replays <paramref name="recording"/> and returns every discrepancy
    /// found (empty = the recording verifies). Checks action-space validity
    /// of every recorded turn, step-count agreement, and per-step serialized
    /// StepResult equivalence against the recorded ones. Equivalence is
    /// serialized-result equality, never a state digest — no canonical
    /// simulation-state hash tree currently exists.
    /// </summary>
    public static IReadOnlyList<string> Verify(TrajectoryRecording recording)
    {
        var problems = new List<string>();
        var replayed = Replay(recording);

        if (replayed.Count != recording.Steps.Length)
        {
            problems.Add($"Replay produced {replayed.Count} step(s), but the recording has {recording.Steps.Length}.");
        }

        var turns = Math.Min(replayed.Count, recording.Steps.Length);
        for (var i = 0; i < turns; i++)
        {
            var actionProblems = new List<string>();
            foreach (var action in recording.Steps[i].Actions)
            {
                actionProblems.AddRange(ActionSpace.Validate(action, recording.Header.Map));
            }

            if (actionProblems.Count > 0)
            {
                problems.Add($"Step {recording.Steps[i].StepNumber} has out-of-space actions: {string.Join(" ", actionProblems)}");
            }

            var recordedJson = JsonSerializer.Serialize(recording.Steps[i].Result, Options);
            var replayedJson = JsonSerializer.Serialize(replayed[i], Options);
            if (recordedJson != replayedJson)
            {
                problems.Add($"Step {recording.Steps[i].StepNumber} result diverges from replay.");
            }
        }

        return problems;
    }
}