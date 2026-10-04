using Lattice.Environment;
using Lattice.Trajectories;
using Lattice.Tui;
using Lattice.Visualization;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Reads a recorded trajectory into the read-only projection the TUI draws: the
/// map, and one immutable frame per recorded step plus the start frame.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the recording format and the drawing library, and it
/// is deliberately the only place the two meet. <c>Lattice.Tui</c> holds no
/// reference to the trajectory or environment assemblies — it is the same
/// zero-dependency library the command-lifecycle lines are drawn from — so a
/// recording is read and projected here, once, and everything downstream works
/// against plain values.
/// </para>
/// <para>
/// Nothing is re-derived and nothing is invented. Every frame is the state the
/// recording's own step result states: its <see cref="Observation"/> for the
/// agents and claims, its <see cref="Info"/> for the tick and the terminal facts,
/// its <see cref="TrajectoryStep.StateHash"/> for the digest, and the step's own
/// action list formatted by the same formatter the plain playback already uses.
/// The engine is called for exactly two things, both read-only and both
/// deterministic: the start frame, which no step line records, comes from
/// <see cref="Simulation.CreateInitial"/> over the recorded header exactly as the
/// ASCII playback derives its initial frame; and an edge's full crossing time
/// comes from <see cref="Simulation.TransitTicks"/> so an agent in transit can be
/// placed a fraction of the way along from the countdown the recording carries.
/// </para>
/// </remarks>
public static class ReplaySource
{
    /// <summary>
    /// The most zones a world pane can name, one character each: ten digits,
    /// then twenty-six capitals, then twenty-six lower-case capitals. A map with
    /// more is refused here rather than aliased into two ids sharing a character,
    /// which would make the drawing quietly wrong.
    /// </summary>
    private const int MaxNameableZones = 62;

    /// <summary>Reads a trajectory from <paramref name="source"/>.</summary>
    /// <exception cref="InvalidDataException">The trajectory is structurally invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">A line is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">The map has more zones than a pane can name.</exception>
    public static ReplayDocument Read(TextReader source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var recording = TrajectoryReader.Read(source);
        var map = Project(recording.Header.Map);
        var frames = new List<ReplayFrame>(recording.Steps.Length + 1)
        {
            StartFrame(recording, map),
        };

        foreach (var step in recording.Steps)
        {
            frames.Add(StepFrame(recording, step, map));
        }

        var header = new ReplayHeader(
            recording.Header.Seed,
            recording.Header.SchemaVersion,
            recording.Header.Scenario,
            recording.Header.AgentRoles,
            recording.Final.TotalSteps,
            recording.Header.ScenarioSha256,
            recording.Steps.Length > 0 && recording.Steps.All(step => step.Perceptions is not null));

        return new ReplayDocument(map, header, frames);
    }

    /// <summary>Reads a trajectory from a file, read-only.</summary>
    /// <exception cref="System.IO.FileNotFoundException">There is no such file.</exception>
    /// <exception cref="System.IO.IOException">The file could not be read.</exception>
    public static ReplayDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var reader = new StreamReader(path);
        return Read(reader);
    }

    /// <summary>
    /// The frame before any recorded step: the state the recorded header starts
    /// from, at tick 0, with nothing claimed and no digest, because the recording
    /// carries none for a tick no step line describes.
    /// </summary>
    private static ReplayFrame StartFrame(TrajectoryRecording recording, WorldMap map)
    {
        var initial = Simulation.CreateInitial(
            recording.Header.Map,
            recording.Header.SimulationConfig,
            recording.Header.DynamicRules ?? DynamicMapRuleSet.None);

        return new ReplayFrame(
            tick: 0,
            isStart: true,
            initial.Agents.Select(agent => Project(agent, recording, map)).ToArray(),
            initial.Claims,
            actions: null,
            stateDigest: null,
            isTerminal: false,
            terminalReason: null,
            winnerSlot: null);
    }

    private static ReplayFrame StepFrame(TrajectoryRecording recording, TrajectoryStep step, WorldMap map)
    {
        var info = step.Result.Info;

        // Full observability is the step contract's default, so every observation
        // of a step states the same world; the first is the one the existing
        // playback reads for exactly this reason.
        var observation = step.Result.Observations[0];

        return new ReplayFrame(
            info.StepNumber,
            isStart: false,
            observation.AgentStates.Select(agent => Project(agent, recording, map)).ToArray(),
            observation.Claims,
            TrajectoryPlayback.FormatActions(step.Actions),
            step.StateHash,
            info.IsTerminal,
            info.Reason,
            info.WinnerAgentId);
    }

    private static WorldAgent Project(AgentState agent, TrajectoryRecording recording, WorldMap map)
    {
        var transit = agent.Transit is { } crossing
            ? new WorldTransit(
                crossing.FromZoneId,
                crossing.ToZoneId,
                crossing.RemainingTicks,
                TotalTicks(recording, crossing.FromZoneId, crossing.ToZoneId))
            : null;

        return new WorldAgent(agent.AgentId, agent.ZoneId, agent.Score, transit);
    }

    /// <summary>
    /// The whole crossing time of an edge, as the recording's own transit speed
    /// resolves it. A recording that ever puts an agent in transit has a non-zero
    /// speed, so the value is a real crossing length and not a stand-in.
    /// </summary>
    private static int TotalTicks(TrajectoryRecording recording, int fromZoneId, int toZoneId) =>
        Simulation.TransitTicks(
            recording.Header.Map,
            fromZoneId,
            toZoneId,
            recording.Header.SimulationConfig.TransitSpeed);

    private static WorldMap Project(MapGraph map)
    {
        var zones = new WorldZone[map.Zones.Length];
        for (var i = 0; i < map.Zones.Length; i++)
        {
            var zone = map.Zones[i];
            zones[i] = new WorldZone(
                zone.Id,
                Label(zone.Id),
                zone.Position.X,
                zone.Position.Y,
                zone.MaxOccupancy,
                zone.Role);
        }

        var resources = new WorldResource[map.Resources.Length];
        for (var i = 0; i < map.Resources.Length; i++)
        {
            var resource = map.Resources[i];
            resources[i] = new WorldResource(
                resource.Id,
                resource.ZoneId,
                resource.Position.X,
                resource.Position.Y,
                resource.Role);
        }

        var edges = new WorldEdge[map.ChokePoints.Length];
        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            var choke = map.ChokePoints[i];
            edges[i] = new WorldEdge(
                choke.Id,
                choke.FromZoneId,
                choke.ToZoneId,
                choke.MaxOccupancy,
                choke.Role);
        }

        return new WorldMap(zones, resources, edges);
    }

    /// <summary>
    /// The one character a zone is named by, or a refusal: ten digits, then
    /// twenty-six capitals, then twenty-six lower-case capitals, in that order, so
    /// the same map always draws the same names.
    /// </summary>
    private static string Label(int zoneId)
    {
        if (zoneId < 0 || zoneId >= MaxNameableZones)
        {
            throw new InvalidOperationException(
                $"A map with zone id {zoneId} cannot be drawn: a zone is one character and only " +
                $"{MaxNameableZones} ids have one.");
        }

        return zoneId < 10
            ? ((char)('0' + zoneId)).ToString()
            : zoneId < 36
                ? ((char)('A' + zoneId - 10)).ToString()
                : ((char)('a' + zoneId - 36)).ToString();
    }
}