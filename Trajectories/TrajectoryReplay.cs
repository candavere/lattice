using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Verifies a recorded trajectory by re-running it: recorded actions are
/// fed into a fresh simulation built from the recorded header (seed map +
/// simulation config) and each replayed <see cref="StepResult"/>'s JSON
/// serialization must equal the recorded one — per-step serialized StepResult
/// equivalence. The recorded final summary line is authenticated too: every
/// aggregate field is recomputed from the re-simulated run and compared field
/// by field, so a same-length tampered final line fails. This is a
/// serialized-result comparison, never a state digest: no canonical
/// simulation-state hash tree currently exists. The replay gate
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
        return ReplayFull(recording).Results;
    }

    /// <summary>
    /// The full re-simulation behind <see cref="Replay"/>: every replayed
    /// <see cref="StepResult"/>, the final <see cref="SimulationState"/> the
    /// replayed run ends in, and the last replayed step's <see cref="Info"/>
    /// (why the episode ended, who won). The final state is what the final
    /// summary line's aggregates are recomputed from.
    /// </summary>
    private static (List<StepResult> Results, SimulationState FinalState, Info? LastInfo) ReplayFull(
        TrajectoryRecording recording)
    {
        var state = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);
        var results = new List<StepResult>();
        Info? lastInfo = null;

        foreach (var step in recording.Steps)
        {
            var outcome = Simulation.Step(state, step.Actions, recording.Header.SimulationConfig);
            results.Add(outcome.Result);
            state = outcome.NextState;
            lastInfo = outcome.Result.Info;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return (results, state, lastInfo);
    }

    /// <summary>
    /// Replays <paramref name="recording"/> and returns every discrepancy
    /// found (empty = the recording verifies). Checks action-space validity
    /// of every recorded turn, step-count agreement, and per-step serialized
    /// StepResult equivalence against the recorded ones, then authenticates
    /// the final summary line: every aggregate field (Reason, WinnerAgentId,
    /// TotalSteps, FinalScores, ResourcesClaimed, TotalResources) is
    /// recomputed from the re-simulated run and compared field by field, so a
    /// same-length tampered final line fails. Equivalence is serialized-result
    /// equality, never a state digest — no canonical simulation-state hash
    /// tree currently exists.
    /// </summary>
    public static IReadOnlyList<string> Verify(TrajectoryRecording recording)
    {
        var problems = new List<string>();
        var (replayed, finalState, lastInfo) = ReplayFull(recording);

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

        AppendFinalProblems(problems, recording.Final, TrajectoryWriter.BuildFinal(finalState, lastInfo));

        return problems;
    }

    /// <summary>
    /// Compares the recorded final summary line against the one recomputed
    /// from the re-simulated run, field by field. Expected is the replay's
    /// value; actual is what the file claims. Every mismatch is reported with
    /// the field name so a tampered aggregate is named, not just counted.
    /// </summary>
    private static void AppendFinalProblems(List<string> problems, TrajectoryFinal recorded, TrajectoryFinal replayed)
    {
        if (recorded.Reason != replayed.Reason)
        {
            problems.Add(
                $"Final line 'Reason' diverges from replay: " +
                $"expected {FormatNullable(replayed.Reason)}, actual {FormatNullable(recorded.Reason)}.");
        }

        if (recorded.WinnerAgentId != replayed.WinnerAgentId)
        {
            problems.Add(
                $"Final line 'WinnerAgentId' diverges from replay: " +
                $"expected {FormatNullable(replayed.WinnerAgentId)}, actual {FormatNullable(recorded.WinnerAgentId)}.");
        }

        if (recorded.TotalSteps != replayed.TotalSteps)
        {
            problems.Add(
                $"Final line 'TotalSteps' diverges from replay: " +
                $"expected {replayed.TotalSteps}, actual {recorded.TotalSteps}.");
        }

        if (recorded.FinalScores is null || !recorded.FinalScores.SequenceEqual(replayed.FinalScores))
        {
            problems.Add(
                $"Final line 'FinalScores' diverges from replay: " +
                $"expected {FormatScores(replayed.FinalScores)}, actual {FormatScores(recorded.FinalScores)}.");
        }

        if (recorded.ResourcesClaimed != replayed.ResourcesClaimed)
        {
            problems.Add(
                $"Final line 'ResourcesClaimed' diverges from replay: " +
                $"expected {replayed.ResourcesClaimed}, actual {recorded.ResourcesClaimed}.");
        }

        if (recorded.TotalResources != replayed.TotalResources)
        {
            problems.Add(
                $"Final line 'TotalResources' diverges from replay: " +
                $"expected {replayed.TotalResources}, actual {recorded.TotalResources}.");
        }
    }

    private static string FormatNullable(int? value) => value?.ToString() ?? "null";

    private static string FormatNullable(string? value) => value ?? "null";

    private static string FormatScores(int[]? scores) =>
        scores is null ? "null" : "[" + string.Join(",", scores) + "]";
}