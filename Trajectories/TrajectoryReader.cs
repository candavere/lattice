using System.Text.Json;

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
        return new TrajectoryRecording(header, steps, final!);
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