using System.Text;
using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Visualization;

/// <summary>
/// Streams a recorded trajectory to a terminal as printable frames.
/// an initial-state frame, then one frame per recorded tick. It consumes the
/// JSONL produced by <see cref="TrajectoryWriter"/> through
/// <see cref="TrajectoryReader"/> and renders ONLY what the recorded
/// <see cref="StepResult"/>s already contain — the initial frame is derived
/// from the seed/map/config header (there is no generative logic in the
/// visualizer), so nothing about game rules is re-executed or re-derived.
/// </summary>
public static class TrajectoryPlayback
{
/// <summary>
/// Reads a trajectory from <paramref name="trajectorySource"/> and writes a
/// human-readable sequence of frames to <paramref name="sink"/>. Throws the
/// same structural exceptions as <see cref="TrajectoryReader.Read"/> on a
/// malformed file. Deterministic: same JSONL, byte-identical output, with
/// newlines emitted as a bare <c>\n</c> on every platform.
/// </summary>
public static void Playback(TextReader trajectorySource, TextWriter sink)
{
    var recording = TrajectoryReader.Read(trajectorySource);
    var header = recording.Header;
    var initial = Simulation.CreateInitial(header.Map, header.SimulationConfig);

    sink.Write("== initial state ==\n");
    WriteFrame(sink, header.Map, initial.Agents, initial.Claims);

    foreach (var step in recording.Steps)
    {
        var heading = $"== step {step.StepNumber}";
        var info = step.Result.Info;
        if (info.IsTerminal)
        {
            heading += $" - terminal ({info.Reason}), winner: agent {info.WinnerAgentId}";
        }

        heading += " ==";
        sink.Write(heading + "\n");
        sink.Write($"actions: {FormatActions(step.Actions)}\n");
        WriteFrame(sink, header.Map, step.Result.Observations[0]);
    }
}

    /// <summary>
    /// One-line summary of a turn, agent-by-agent in slot order, e.g.
    /// "agent0: Move(2); agent1: Collect(0)". Used in the frame header so a
    /// step is readable without cross-referencing the raw actions.
    /// </summary>
    public static string FormatActions(AgentAction[] actions)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < actions.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("; ");
            }

            builder.Append("agent").Append(i).Append(": ");
            switch (actions[i].Kind)
            {
                case ActionKind.Move:
                    builder.Append("Move(").Append(actions[i].ZoneId).Append(')');
                    break;
                case ActionKind.Collect:
                    builder.Append("Collect(").Append(actions[i].ResourceId).Append(')');
                    break;
                default:
                    builder.Append("Wait");
                    break;
            }
        }

        return builder.ToString();
    }

    private static void WriteFrame(TextWriter sink, MapGraph map, AgentState[] agents, int[] claims)
    {
        sink.Write(AsciiRenderer.RenderFrame(map, agents, claims) + "\n\n");
    }

    private static void WriteFrame(TextWriter sink, MapGraph map, Observation observation)
    {
        sink.Write(AsciiRenderer.RenderFrame(map, observation) + "\n\n");
    }
}