using System.Text.Json;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// Reads the JSONL produced by <see cref="TrajectoryWriter"/> back into the
/// in-memory <see cref="TrajectoryRecording"/>. The format is strict about
/// structure — exactly one header first, one final line last, contiguous
/// step numbers — so a truncated or hand-corrupted file is rejected loudly
/// instead of silently replaying wrong data.
/// </summary>
public static class TrajectoryReader
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Parses every non-blank line of <paramref name="source"/>, validating
    /// structure and step numbering, and returns the reconstructed
    /// trajectory. Throws <see cref="InvalidDataException"/> on structural
    /// violations and <see cref="JsonException"/> on malformed JSON.
    /// </summary>
    public static TrajectoryRecording Read(TextReader source)
    {
        var header = ReadHeader(source);
        TrajectoryFinal? final = null;
        var steps = ReadSteps(source, value => final = value).ToArray();
        var recording = new TrajectoryRecording(header, steps, final!);

        if (recording.Final.FinalScores is null)
        {
            throw new InvalidDataException("The final line has no 'FinalScores'.");
        }

        if (recording.Final.FinalScores.Length != recording.Header.SimulationConfig.AgentCount)
        {
            throw new InvalidDataException(
                $"Final 'FinalScores' has {recording.Final.FinalScores.Length} entries, " +
                $"but the simulation config declares {recording.Header.SimulationConfig.AgentCount} agent(s).");
        }

        return recording;
    }

    public static TrajectoryHeader ReadHeader(TextReader source)
    {
        var line = source.ReadLine();
        if (line is null)
        {
            throw new InvalidDataException("Trajectory has no header line.");
        }

        if (string.IsNullOrWhiteSpace(line) || ReadKind(line, 1) != "header")
        {
            throw new InvalidDataException("The header line must be the first line of the trajectory.");
        }

        var header = DeserializeOrThrow<TrajectoryWriter.HeaderLine>(line, 1).ToModel();
        if (header.SimulationConfig is null)
        {
            throw new InvalidDataException("The header line has no 'SimulationConfig'.");
        }

        if (header.Map is null)
        {
            throw new InvalidDataException("The header line has no 'Map'.");
        }

        if (header.Map.Zones is null || header.Map.Resources is null || header.Map.ChokePoints is null)
        {
            throw new InvalidDataException(
                "The header line's 'Map' is missing its 'Zones', 'Resources', or 'ChokePoints' topology.");
        }

        ValidateMapElements(header.Map);

        if (header.SchemaVersion > TrajectorySchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Trajectory header schema version {header.SchemaVersion} is newer than the supported version {TrajectorySchema.CurrentVersion}.");
        }

        return header;
    }

    public static IEnumerable<TrajectoryStep> StreamSteps(TextReader source)
    {
        return ReadSteps(source, null);
    }

    private static IEnumerable<TrajectoryStep> ReadSteps(TextReader source, Action<TrajectoryFinal>? onFinal)
    {
        var stepCount = 0;
        TrajectoryFinal? final = null;
        var lineNumber = 1;

        string? line;
        while ((line = source.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var kind = ReadKind(line, lineNumber);
            switch (kind)
            {
                case "header":
                    throw new InvalidDataException("The header line must be the first line of the trajectory.");

                case "step":
                    if (final is not null)
                    {
                        throw new InvalidDataException("A step line appeared after the final line.");
                    }

                    var step = DeserializeOrThrow<TrajectoryWriter.StepLine>(line, lineNumber).ToModel();
                    if (step.Actions is null)
                    {
                        throw new InvalidDataException($"Line {lineNumber} has no 'Actions'.");
                    }

                    if (step.Result is null)
                    {
                        throw new InvalidDataException($"Line {lineNumber} has no 'Result'.");
                    }

                    if (step.StateHash is { } stateHash && !IsSha256Hex(stateHash))
                    {
                        throw new InvalidDataException(
                            $"Line {lineNumber} has a 'StateHash' that is not 64 lowercase hex characters: '{stateHash}'.");
                    }

                    if (step.StepNumber != stepCount + 1)
                    {
                        throw new InvalidDataException(
                            $"Step numbers must be contiguous from 1; encountered {step.StepNumber} after {stepCount} step(s).");
                    }

                    stepCount++;
                    yield return step;
                    break;

                case "final":
                    if (final is not null)
                    {
                        throw new InvalidDataException("Multiple final lines found.");
                    }

                    final = DeserializeOrThrow<TrajectoryWriter.FinalLine>(line, lineNumber).Metrics
                        ?? throw new InvalidDataException($"Line {lineNumber} has no final metrics.");
                    break;

                default:
                    throw new InvalidDataException($"Line {lineNumber} has unknown kind '{kind}'.");
            }
        }

        if (final is null)
        {
            throw new InvalidDataException("Trajectory has no final line.");
        }

        if (final.TotalSteps != stepCount)
        {
            throw new InvalidDataException(
                $"Final TotalSteps ({final.TotalSteps}) disagrees with the {stepCount} recorded step line(s).");
        }

        onFinal?.Invoke(final);
    }

    /// <summary>
    /// Rejects a map whose array elements are null or whose zone/resource
    /// positions are absent. The arrays themselves being present is not enough:
    /// a hand-edited file can carry <c>"Zones":[null, ...]</c> or
    /// <c>"Position":null</c>, and those reach far past this reader —
    /// <c>Simulation.TransitTicks</c> reads <c>Zone.Position</c> for the
    /// kinematic edge length and the perception filter reads both positions to
    /// build observations, and the per-step state digest reads all of them. A
    /// null position is not caught by the simulation itself, because
    /// <see cref="SimulationConfig.InstantTransit"/> returns before any
    /// distance is computed, so an episode that never leaves a zone can carry a
    /// null position all the way to a fault inside verification. Rejecting it
    /// here names the offending field instead.
    /// </summary>
    private static void ValidateMapElements(MapGraph map)
    {
        for (var i = 0; i < map.Zones.Length; i++)
        {
            var zone = map.Zones[i];
            if (zone is null)
            {
                throw new InvalidDataException($"The header line's 'Map' has a null entry at 'Zones[{i}]'.");
            }

            if (zone.Position is null)
            {
                throw new InvalidDataException($"The header line's 'Map' zone {zone.Id} at 'Zones[{i}]' has no 'Position'.");
            }
        }

        for (var i = 0; i < map.Resources.Length; i++)
        {
            var resource = map.Resources[i];
            if (resource is null)
            {
                throw new InvalidDataException($"The header line's 'Map' has a null entry at 'Resources[{i}]'.");
            }

            if (resource.Position is null)
            {
                throw new InvalidDataException(
                    $"The header line's 'Map' resource {resource.Id} at 'Resources[{i}]' has no 'Position'.");
            }
        }

        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            if (map.ChokePoints[i] is null)
            {
                throw new InvalidDataException($"The header line's 'Map' has a null entry at 'ChokePoints[{i}]'.");
            }
        }
    }

    private static bool IsSha256Hex(string value)    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadKind(string line, int lineNumber)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Kind", out var kindNode) ||
            kindNode.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Line {lineNumber} has no valid 'Kind' property.");
        }

        return kindNode.GetString()!;
    }

    private static T DeserializeOrThrow<T>(string line, int lineNumber) where T : class
    {
        return JsonSerializer.Deserialize<T>(line, Options)
            ?? throw new InvalidDataException($"Line {lineNumber} could not be deserialized ({typeof(T).Name}).");
    }
}