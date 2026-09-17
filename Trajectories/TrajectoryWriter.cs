using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Records full episodes to inspectable JSONL: a header line (seed + map +
/// sim config), one line per tick (actions + complete StepResult), and a
/// final metrics line. Each line is independently parseable JSON; simulation
/// output is byte-stable so re-recording or replaying yields identical lines.
/// Newlines are emitted as a bare <c>\n</c> on every platform so written
/// files are byte-identical across Windows, Linux, and macOS.
/// </summary>
public static class TrajectoryWriter
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Plays <paramref name="actions"/> as one episode on
    /// <paramref name="map"/> and writes the trajectory to
    /// <paramref name="sink"/>, returning the same recording in memory.
    /// Every submitted action is checked against
    /// <see cref="ActionSpace"/> first so a malformed episode never lands on
    /// disk; stepping stops at the first terminal tick (extra turns are
    /// ignored, mirroring <see cref="SimulationDriver"/>).
    /// </summary>
    public static TrajectoryRecording Record(
        MapGraph map,
        SimulationConfig simulationConfig,
        ulong seed,
        AgentAction[][] actions,
        TextWriter sink,
        string? scenario = null,
        string[]? agentRoles = null)
    {
        var state = Simulation.CreateInitial(map, simulationConfig);

        sink.Write(Serialize(new HeaderLine("header", seed, map, simulationConfig, scenario, agentRoles)) + "\n");

        var steps = new List<TrajectoryStep>();
        Info? lastInfo = null;
        foreach (var turn in actions)
        {
            ValidateTurn(map, steps.Count + 1, turn);

            var outcome = Simulation.Step(state, turn, simulationConfig);
            var step = new TrajectoryStep(outcome.Result.Info.StepNumber, turn, outcome.Result);
            steps.Add(step);
            sink.Write(Serialize(new StepLine("step", step.StepNumber, turn, outcome.Result)) + "\n");
            state = outcome.NextState;
            lastInfo = outcome.Result.Info;

            if (outcome.Result.Info.IsTerminal)
            {
                break;
            }
        }

        var final = BuildFinal(state, lastInfo);
        sink.Write(Serialize(new FinalLine("final", final)) + "\n");

        return new TrajectoryRecording(
            new TrajectoryHeader(seed, map, simulationConfig),
            steps.ToArray(),
            final);
    }

    /// <summary>
    /// Writes an already-built <paramref name="recording"/> to
    /// <paramref name="sink"/> using the same line format as
    /// <see cref="Record"/>. Byte-identical to the original recording's own
    /// output, which is how a read-back is verified.
    /// </summary>
    public static void Write(
        TrajectoryRecording recording,
        TextWriter sink,
        string? scenario = null,
        string[]? agentRoles = null)
    {
        sink.Write(Serialize(new HeaderLine(
            "header",
            recording.Header.Seed,
            recording.Header.Map,
            recording.Header.SimulationConfig,
            scenario ?? recording.Header.Scenario,
            agentRoles ?? recording.Header.AgentRoles)) + "\n");

        foreach (var step in recording.Steps)
        {
            sink.Write(Serialize(new StepLine("step", step.StepNumber, step.Actions, step.Result)) + "\n");
        }

        sink.Write(Serialize(new FinalLine("final", recording.Final)) + "\n");
    }

    private static void ValidateTurn(MapGraph map, int stepNumber, AgentAction[] turn)
    {
        for (var i = 0; i < turn.Length; i++)
        {
            var problems = ActionSpace.Validate(turn[i], map);
            if (problems.Count > 0)
            {
                var detail = string.Join(" ", problems);
                throw new InvalidOperationException(
                    $"Agent {i}'s action at step {stepNumber} is outside the action space: {detail}");
            }
        }
    }

    private static TrajectoryFinal BuildFinal(SimulationState state, Info? info)
    {
        return new TrajectoryFinal(
            Reason: info?.Reason,
            WinnerAgentId: info?.WinnerAgentId,
            TotalSteps: state.StepCount,
            FinalScores: state.Agents.Select(a => a.Score).ToArray(),
            ResourcesClaimed: state.Claims.Length,
            TotalResources: state.Map.Resources.Length);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal sealed record HeaderLine(
        string Kind,
        ulong Seed,
        MapGraph Map,
        SimulationConfig SimulationConfig,
        string? Scenario = null,
        string[]? AgentRoles = null)
    {
        public TrajectoryHeader ToModel() => new(Seed, Map, SimulationConfig, Scenario, AgentRoles);
    }

    internal sealed record StepLine(string Kind, int StepNumber, AgentAction[] Actions, StepResult Result)
    {
        public TrajectoryStep ToModel() => new(StepNumber, Actions, Result);
    }

    internal sealed record FinalLine(string Kind, TrajectoryFinal Metrics);
}