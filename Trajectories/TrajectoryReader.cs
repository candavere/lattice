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
        TrajectoryHeader? header = null;
        var steps = new List<TrajectoryStep>();
        TrajectoryFinal? final = null;
        var lineNumber = 0;

        string? line;
        while ((line = source.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("Kind", out var kindNode) || kindNode.GetString() is not { } kind)
            {
                throw new InvalidDataException($"Line {lineNumber} has no 'Kind' property.");
            }

            switch (kind)
            {
                case "header":
                    if (lineNumber != 1 || header is not null)
                    {
                        throw new InvalidDataException("The header line must be the first line of the trajectory.");
                    }

                    header = DeserializeOrThrow<TrajectoryWriter.HeaderLine>(line, lineNumber).ToModel();
                    break;

                case "step":
                    if (header is null)
                    {
                        throw new InvalidDataException("A step line appeared before the header.");
                    }

                    if (final is not null)
                    {
                        throw new InvalidDataException("A step line appeared after the final line.");
                    }

                    var step = DeserializeOrThrow<TrajectoryWriter.StepLine>(line, lineNumber).ToModel();
                    if (step.StepNumber != steps.Count + 1)
                    {
                        throw new InvalidDataException(
                            $"Step numbers must be contiguous from 1; encountered {step.StepNumber} after {steps.Count} step(s).");
                    }

                    steps.Add(step);
                    break;

                case "final":
                    if (header is null)
                    {
                        throw new InvalidDataException("The final line appeared before the header.");
                    }

                    if (final is not null)
                    {
                        throw new InvalidDataException("Multiple final lines found.");
                    }

                    var metrics = DeserializeOrThrow<TrajectoryWriter.FinalLine>(line, lineNumber).Metrics;
                    final = new TrajectoryFinal(
                        metrics.Reason,
                        metrics.WinnerAgentId,
                        metrics.TotalSteps,
                        metrics.FinalScores,
                        metrics.ResourcesClaimed,
                        metrics.TotalResources);
                    break;

                default:
                    throw new InvalidDataException($"Line {lineNumber} has unknown kind '{kind}'.");
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("Trajectory has no header line.");
        }

        if (final is null)
        {
            throw new InvalidDataException("Trajectory has no final line.");
        }

        if (final.TotalSteps != steps.Count)
        {
            throw new InvalidDataException(
                $"Final TotalSteps ({final.TotalSteps}) disagrees with the {steps.Count} recorded step line(s).");
        }

        return new TrajectoryRecording(header, steps.ToArray(), final);
    }

    private static T DeserializeOrThrow<T>(string line, int lineNumber) where T : class
    {
        return JsonSerializer.Deserialize<T>(line, Options)
            ?? throw new InvalidDataException($"Line {lineNumber} could not be deserialized ({typeof(T).Name}).");
    }
}