using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// The outcome of a verification pass: the discrepancies found (empty = the
/// recording verifies) and any notices that are not failures. A notice is how
/// a recording that predates per-step state hashes reports that it was checked
/// on step results alone.
/// </summary>
/// <param name="Problems">Discrepancies; any entry means the recording does not verify.</param>
/// <param name="Notices">Non-fatal statements about what the pass could and could not check.</param>
public sealed record TrajectoryVerification(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notices);

/// <summary>
/// Verifies a recorded trajectory by re-running it: recorded actions are
/// fed into a fresh simulation built from the recorded header (seed map +
/// simulation config) and each replayed <see cref="StepResult"/>'s JSON
/// serialization must equal the recorded one — per-step serialized StepResult
/// equivalence. Where the recording carries a per-step
/// <see cref="SimulationStateHash"/> digest (schema 3 and later), the digest of
/// the replayed world is recomputed at every tick and compared as well, so
/// verification attests to the state each tick produced and not merely the
/// results; a mismatch names the first offending tick. The recorded final
/// summary line is authenticated too: every aggregate field is recomputed from
/// the re-simulated run and compared field by field, so a same-length tampered
/// final line fails. A recording from before schema 3 carries no state hash
/// anywhere; that still verifies on step results and the final line, and
/// reports <see cref="NoStateHashNotice"/> saying so. A recording that claims
/// schema 3 or later but carries no state hash is a PROBLEM, not a notice —
/// stripping the hashes must never be a way to make a tampered file pass.
/// </summary>
public static class TrajectoryReplay
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// The notice emitted when a recording carries no per-step state hash, so
    /// the pass silently degrades to step-level verification. Wording is part
    /// of the contract: tooling and humans both key off it.
    /// </summary>
    public const string NoStateHashNotice = "no state hash: step-level verification only";

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
    /// <see cref="StepResult"/>, the <see cref="SimulationState"/> the replayed
    /// run ends in after each step (in order, so per-step state digests can be
    /// recomputed), and the last replayed step's <see cref="Info"/> (why the
    /// episode ended, who won). The final state is what the final summary
    /// line's aggregates are recomputed from.
    /// </summary>
    private static (List<StepResult> Results, List<SimulationState> States, SimulationState FinalState, Info? LastInfo) ReplayFull(
        TrajectoryRecording recording)
    {
        var state = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);
        var results = new List<StepResult>();
        var states = new List<SimulationState>();
        Info? lastInfo = null;

        foreach (var step in recording.Steps)
        {
            var outcome = Simulation.Step(state, step.Actions, recording.Header.SimulationConfig);
            results.Add(outcome.Result);
            states.Add(outcome.NextState);
            state = outcome.NextState;
            lastInfo = outcome.Result.Info;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return (results, states, state, lastInfo);
    }

    /// <summary>
    /// Replays <paramref name="recording"/> and returns every discrepancy
    /// found (empty = the recording verifies). Equivalent to
    /// <see cref="VerifyDetailed"/>(recording).Problems; prefer the detailed
    /// form when the notices matter, since a recording without state hashes
    /// verifies with no problems at all.
    /// </summary>
    public static IReadOnlyList<string> Verify(TrajectoryRecording recording) =>
        VerifyDetailed(recording).Problems;

    /// <summary>
    /// Replays <paramref name="recording"/> and reports both the discrepancies
    /// found and any notices. Checks action-space validity of every recorded
    /// turn, step-count agreement, and per-step serialized StepResult
    /// equivalence against the recorded ones, then authenticates the final
    /// summary line field by field. Where the recording carries per-step
    /// <see cref="SimulationStateHash"/> digests, each digest is also recomputed
    /// from the replayed state and compared, so the pass attests to the state
    /// each tick produced and not only the step results.
    /// </summary>
    public static TrajectoryVerification VerifyDetailed(TrajectoryRecording recording)
    {
        var problems = new List<string>();
        var notices = new List<string>();
        var (replayed, states, finalState, lastInfo) = ReplayFull(recording);

        if (replayed.Count != recording.Steps.Length)
        {
            problems.Add($"Replay produced {replayed.Count} step(s), but the recording has {recording.Steps.Length}.");
        }

        // A recording that carries no state hash at all only gets the pass
        // with a notice when it was WRITTEN before schema 3 existed. A
        // schema-3-or-newer recording whose every hash is missing is
        // self-inconsistent: it promises a digest per step and carries none,
        // which is indistinguishable from someone having stripped them off a
        // genuine recording to hide a tampered state. That is a problem, not a
        // notice — otherwise deleting the hashes from a tampered file would
        // turn a red verify green.
        var hashed = recording.Steps.Count(step => step.StateHash is not null);
        if (hashed == 0)
        {
            if (recording.Header.SchemaVersion >= TrajectorySchema.StateHashRequiredVersion && recording.Steps.Length > 0)
            {
                problems.Add(
                    $"Recording declares schema version {recording.Header.SchemaVersion}, which requires a StateHash on " +
                    $"every step, but none of the {recording.Steps.Length} step line(s) carry one.");
            }
            else
            {
                notices.Add(NoStateHashNotice);
            }
        }
        else if (hashed != recording.Steps.Length)
        {
            problems.Add(
                $"Recording is inconsistent: {hashed} of {recording.Steps.Length} step line(s) carry a StateHash. " +
                "Every step must carry one, or none may.");
        }

        var turns = Math.Min(replayed.Count, recording.Steps.Length);
        for (var i = 0; i < turns; i++)
        {
            var step = recording.Steps[i];
            var actionProblems = new List<string>();
            foreach (var action in step.Actions)
            {
                actionProblems.AddRange(ActionSpace.Validate(action, recording.Header.Map));
            }

            if (actionProblems.Count > 0)
            {
                problems.Add($"Step {step.StepNumber} has out-of-space actions: {string.Join(" ", actionProblems)}");
            }

            var recordedJson = JsonSerializer.Serialize(step.Result, Options);
            var replayedJson = JsonSerializer.Serialize(replayed[i], Options);
            if (recordedJson != replayedJson)
            {
                problems.Add($"Step {step.StepNumber} result diverges from replay.");
            }

            if (step.StateHash is { } recordedHash)
            {
                var replayedHash = SimulationStateHash.Compute(states[i], recording.Header.Seed);
                if (recordedHash != replayedHash)
                {
                    problems.Add(
                        $"Step {step.StepNumber} state hash diverges from replay: " +
                        $"expected {replayedHash}, actual {recordedHash}.");
                }
            }
        }

        AppendFinalProblems(problems, recording.Final, TrajectoryWriter.BuildFinal(finalState, lastInfo));

        return new TrajectoryVerification(problems, notices);
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