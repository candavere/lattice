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
/// Where the recording carries per-step decision-time
/// <see cref="PartialObservation"/>s (schema 4), each is likewise recomputed —
/// reprojected through a <see cref="PerceptionFilter"/> under the header's
/// declared per-agent vision — and compared, so verification attests to the fog
/// each agent actually faced and not merely to the results. All steps or none:
/// a partial block is a discrepancy, and a recording new enough to be expected
/// to carry perceptions that carries none reports
/// <see cref="NoPerceptionNotice"/> instead of passing in silence.
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
    /// The notice emitted when a recording that declares
    /// <see cref="TrajectorySchema.DecisionTimePerceptionVersion"/> or later
    /// carries neither recorded perceptions nor a declared per-agent vision, so
    /// the pass silently degrades to step-level verification and says so. The
    /// mirror of <see cref="NoStateHashNotice"/> for the decision-time fog: a
    /// reader that did not check the fog must not be able to report a pass that
    /// reads as though it had. A recording from before that schema version
    /// predates the fields and is not nagged about them.
    /// </summary>
    public const string NoPerceptionNotice =
        "no recorded perception: decision-time visibility not verified";

    /// <summary>
    /// The notice emitted when a recording carries no
    /// <see cref="TrajectoryHeader.ScenarioSha256"/>, so the pass cannot say
    /// which scenario descriptor produced the episode. The mirror of
    /// <see cref="NoStateHashNotice"/> for provenance: a reader that did not
    /// check the digest must not report a pass that reads as though it had.
    /// A recording made before schema 5 predates the field and is not nagged
    /// about it — the notice is for a schema-5 recording whose digest was
    /// stripped, which is the case worth reporting.
    /// </summary>
    public const string NoScenarioDigestNotice =
        "no scenario digest: the recording does not name the descriptor that produced it";

    /// <summary>
    /// The first schema version in which a recording is expected to carry a
    /// <see cref="TrajectoryHeader.ScenarioSha256"/>. This is an
    /// informational, not an adjudicating, version: a schema-5 recording
    /// without the field verifies on everything else and says so, because the
    /// digest is provenance about a file that replay never reopens (the
    /// recording stays self-contained). There is nothing for replay to check
    /// the digest against, and it does not pretend otherwise.
    /// </summary>
    public const int ScenarioDigestVersion = 5;

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
    /// recomputed), the state each step was decided from (so the recorded
    /// decision-time perceptions can be reprojected), and the last replayed
    /// step's <see cref="Info"/> (why the episode ended, who won). The final
    /// state is what the final summary line's aggregates are recomputed from.
    /// </summary>
    private static (
        List<StepResult> Results,
        List<SimulationState> States,
        List<SimulationState> PreStepStates,
        SimulationState FinalState,
        Info? LastInfo) ReplayFull(TrajectoryRecording recording)
    {
        var state = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);
        var results = new List<StepResult>();
        var states = new List<SimulationState>();
        // The world each step was decided FROM, kept alongside the world it
        // produced: an agent's perception is projected from the pre-step
        // observation, so recomputing one needs this list and the states alone
        // are off by a tick.
        var preStepStates = new List<SimulationState> { state };
        Info? lastInfo = null;

        foreach (var step in recording.Steps)
        {
            var outcome = Simulation.Step(state, step.Actions, recording.Header.SimulationConfig);
            results.Add(outcome.Result);
            states.Add(outcome.NextState);
            state = outcome.NextState;
            lastInfo = outcome.Result.Info;
            preStepStates.Add(state);

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        return (results, states, preStepStates, state, lastInfo);
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
    /// each tick produced and not only the step results. Where it carries
    /// decision-time <see cref="PartialObservation"/>s, each is reprojected and
    /// compared agent by agent, so the pass also attests to what each agent saw.
    /// </summary>
    public static TrajectoryVerification VerifyDetailed(TrajectoryRecording recording)
    {
        var problems = new List<string>();
        var notices = new List<string>();
        var (replayed, states, preStepStates, finalState, lastInfo) = ReplayFull(recording);

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

        AppendPerceptionProblems(problems, notices, recording, preStepStates, turns);

        AppendScenarioDigestNotice(notices, recording);

        AppendFinalProblems(problems, recording.Final, TrajectoryWriter.BuildFinal(finalState, lastInfo));

        return new TrajectoryVerification(problems, notices);
    }

    /// <summary>
    /// The decision-time half of the pass. A recording that carries
    /// <see cref="TrajectoryStep.Perceptions"/> has each one reprojected
    /// through a <see cref="PerceptionFilter"/> built from the header's
    /// declared per-agent vision — the world the agent decided from is
    /// available, the filter is deterministic, and the stale memory it
    /// accumulates is the same memory the agent's own filter accumulated — and
    /// every recorded entry is compared to what that projection produced. A
    /// single edited zone status, last-seen tick or rival sighting therefore
    /// fails, and the offending tick and agent slot are named.
    /// <para>
    /// The gate is the same shape as the state hash's. All the steps or none:
    /// a partial block is a discrepancy, because a recording that can be made
    /// to stop claiming its fog is not one whose fog was checked. A header
    /// that declares a vision no step backs is a discrepancy for the mirror
    /// reason. Neither present on a recording that declares a version new
    /// enough to be expected to have them is a notice, and neither present on
    /// an older recording is silence, because that recording never claimed to
    /// carry any.
    /// </para>
    /// </summary>
    private static void AppendPerceptionProblems(
        List<string> problems,
        List<string> notices,
        TrajectoryRecording recording,
        IReadOnlyList<SimulationState> preStepStates,
        int turns)
    {
        var recorded = recording.Steps.Count(step => step.Perceptions is not null);
        if (recorded == 0)
        {
            if (recording.Header.AgentVision is not null)
            {
                problems.Add(
                    $"The header declares an 'AgentVision', but none of the {recording.Steps.Length} " +
                    "step line(s) carry a 'Perceptions' array. Every step must carry one, or none may.");
            }
            else if (recording.Header.SchemaVersion >= TrajectorySchema.DecisionTimePerceptionVersion
                && recording.Steps.Length > 0)
            {
                notices.Add(NoPerceptionNotice);
            }

            return;
        }

        if (recorded != recording.Steps.Length)
        {
            problems.Add(
                $"Recording is inconsistent: {recorded} of {recording.Steps.Length} step line(s) carry " +
                "'Perceptions'. Every step must carry one, or none may.");
            return;
        }

        if (recording.Header.AgentVision is null)
        {
            problems.Add(
                $"{recorded} step line(s) carry 'Perceptions', but the header declares no 'AgentVision', " +
                "so the perception cones that produced them cannot be rebuilt.");
            return;
        }

        PartialObservation[][] projected;
        try
        {
            // The header's own declaration is checked by the same rule the
            // reader applies, so an in-memory recording cannot smuggle past
            // verify a cone that does not match its roster.
            var vision = PerceptionProjector.ReadVision(
                recording.Header.SimulationConfig.AgentCount, recording.Header.AgentVision, "header line")
                ?? throw new InvalidDataException("The header declares no 'AgentVision'.");

            projected = PerceptionProjector.Project(
                recording.Header.Map, vision, preStepStates.Take(turns + 1).ToList());
        }
        catch (InvalidDataException ex)
        {
            problems.Add(ex.Message);
            return;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            problems.Add(
                $"The header's 'AgentVision' cannot be projected through: {ex.Message}");
            return;
        }

        for (var i = 0; i < turns; i++)
        {
            var step = recording.Steps[i];
            try
            {
                PerceptionProjector.CheckStep(
                    recording.Header.SimulationConfig.AgentCount, step.Perceptions, $"Step {step.StepNumber}");
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
                continue;
            }

            for (var agentId = 0; agentId < step.Perceptions!.Length; agentId++)
            {
                if (JsonSerializer.Serialize(step.Perceptions[agentId], Options) !=
                    JsonSerializer.Serialize(projected[i][agentId], Options))
                {
                    problems.Add(
                        $"Step {step.StepNumber} perception diverges from replay for agent {agentId}.");
                }
            }
        }
    }

    /// <summary>
    /// Reports, rather than adjudicates, the absence of a scenario digest.
    /// <para>
    /// This is a notice and never a problem, and the asymmetry with the state
    /// hash is deliberate. The state hash is something replay <em>recomputes</em>
    /// and compares, so its absence from a schema-3-or-newer recording is a
    /// self-inconsistency worth failing. The scenario digest is something replay
    /// cannot recompute: it identifies a file that a self-contained recording
    /// deliberately does not reopen, so there is nothing to compare it against
    /// and no way for stripping it to have hidden a tampered state. A
    /// schema-5 recording without one therefore still verifies on every tick,
    /// and says out loud that it could not name its descriptor.
    /// </para>
    /// </summary>
    private static void AppendScenarioDigestNotice(List<string> notices, TrajectoryRecording recording)
    {
        if (recording.Header.ScenarioSha256 is null
            && recording.Header.SchemaVersion >= ScenarioDigestVersion)
        {
            notices.Add(NoScenarioDigestNotice);
        }
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