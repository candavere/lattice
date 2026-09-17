using System.Globalization;
using System.Text;
using Lattice.Environment;
using Lattice.Trajectories;

namespace Lattice.Visualization;

/// <summary>
/// Exports a full trajectory (JSONL recording) as one self-contained,
/// CSS-animated multi-frame SVG. Each recorded tick becomes a hidden
/// &lt;g&gt; layer; an embedded @keyframes stylesheet cycles the layers in
/// sequence so the file animates a step-by-step replay in any browser, in the
/// spirit of Settle's scrubbable CSS-driven visualization — with no script,
/// no images, and no dependencies beyond the SVG itself. The exporter is a
/// pure function of the recording: every frame is built solely from the
/// recorded <see cref="Observation"/>s (never re-derived), and the output is
/// byte-identical for identical recordings.
/// </summary>
public static class SvgTrajectoryExporter
{
    /// <summary>
    /// Converts an in-memory <paramref name="recording"/> to a standalone SVG
    /// document. Frame 0 is the initial state reconstructed from the header
    /// (the only non-recorded data present, and it requires no game logic —
    /// placement is defined by the environment contract); each subsequent
    /// frame renders one recorded step's first observation.
    /// </summary>
    public static string Export(TrajectoryRecording recording)
    {
        var map = recording.Header.Map;
        var viewport = SvgRenderer.CreateViewport(map);
        var frames = BuildFrames(recording);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"")
            .Append(SvgRenderer.Num(viewport.Width)).Append("\" height=\"")
            .Append(SvgRenderer.Num(viewport.Height)).Append("\" viewBox=\"")
            .Append(viewport.ToViewBox()).Append("\">\n");
        sb.Append("<rect width=\"").Append(SvgRenderer.Num(viewport.Width)).Append("\" height=\"")
            .Append(SvgRenderer.Num(viewport.Height)).Append("\" fill=\"")
            .Append(SvgRenderer.BackgroundFill).Append("\"/>\n");

        AppendStyle(sb, frames.Count);

        for (var i = 0; i < frames.Count; i++)
        {
            sb.Append("<g id=\"frame-").Append(i).Append("\" class=\"frame\" data-step=\"")
                .Append(frames[i].Label).Append("\">\n");
            sb.Append(SvgRenderer.RenderScene(map, frames[i].Agents, frames[i].Claims, recording.Header.AgentRoles));
            sb.Append("</g>\n");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Convenience overload mirroring <see cref="TrajectoryPlayback.Playback"/>:
    /// reads the JSONL stream via <see cref="TrajectoryReader.Read"/> and
    /// exports the resulting recording. Throws the same structural exceptions
    /// on a malformed trajectory.
    /// </summary>
    public static string Export(TextReader trajectorySource) => Export(TrajectoryReader.Read(trajectorySource));

    private readonly record struct Frame(string Label, AgentState[] Agents, int[] Claims);

    private static List<Frame> BuildFrames(TrajectoryRecording recording)
    {
        var frames = new List<Frame>();
        var initial = Simulation.CreateInitial(recording.Header.Map, recording.Header.SimulationConfig);
        frames.Add(new Frame("initial", initial.Agents, initial.Claims));
        foreach (var step in recording.Steps)
        {
            var observation = step.Result.Observations[0];
            frames.Add(new Frame(
                step.StepNumber.ToString(CultureInfo.InvariantCulture),
                observation.AgentStates,
                observation.Claims));
        }

        return frames;
    }

    /// <summary>
    /// Emits one animation rule and one @keyframes block per frame. Frame i is
    /// fully opaque only for the i/count..(i+1)/count slice of the total
    /// duration (one second per frame), giving a sequential loop; with
    /// steps(1) the transition is a hard cut, so each frame "holds" like a
    /// scrubbed timeline rather than cross-fading.
    /// </summary>
    private static void AppendStyle(StringBuilder sb, int frameCount)
    {
        sb.Append("<style>\n");
        for (var i = 0; i < frameCount; i++)
        {
            sb.Append("  #frame-").Append(i)
                .Append(" { animation: frame-").Append(i).Append(' ')
                .Append(frameCount).Append("s steps(1) infinite; }\n");
        }

        for (var i = 0; i < frameCount; i++)
        {
            sb.Append("  @keyframes frame-").Append(i).Append(" { ");
            sb.Append("0%, ").Append(Percent(i, frameCount)).Append("% { opacity: 0; } ");
            sb.Append(Percent(i, frameCount)).Append("%, ").Append(Percent(i + 1, frameCount)).Append("% { opacity: 1; } ");
            sb.Append(Percent(i + 1, frameCount)).Append("%, 100% { opacity: 0; } }\n");
        }

        sb.Append("</style>\n");
    }

    private static string Percent(int frame, int frameCount) =>
        (100.0 * frame / frameCount).ToString("0.##", CultureInfo.InvariantCulture);
}