using System.Text;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Saves the complete captured run output (memory head plus spool) to disk. A
/// failure is reported, never thrown. On collision the target name is bumped by
/// inserting a numeric suffix before the extension.
/// </summary>
public static class ResultArtifactWriter
{
    public static ResultArtifactOutcome SaveRaw(
        string directory,
        string fileName,
        string stdoutHead,
        string? stdoutSpoolPath,
        string stderrHead,
        string? stderrSpoolPath,
        IReadOnlyList<string> headerLines)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ResultArtifactOutcome(false, null, "directory required");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new ResultArtifactOutcome(false, null, "filename required");
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = ResolvePath(directory, fileName);
            var sb = new StringBuilder();
            foreach (var h in headerLines ?? Array.Empty<string>())
            {
                sb.Append(h);
                sb.AppendLine();
            }

            if (headerLines is { Count: > 0 })
            {
                sb.AppendLine();
            }

            // stdout: head + spool (complete-raw contract)
            sb.Append(stdoutHead ?? string.Empty);
            if (!string.IsNullOrEmpty(stdoutSpoolPath))
            {
                if (!File.Exists(stdoutSpoolPath) && !Directory.Exists(stdoutSpoolPath))
                {
                    return new ResultArtifactOutcome(false, null, $"stdout spool not found: {stdoutSpoolPath}");
                }

                string stdoutSpool;
                try
                {
                    stdoutSpool = File.ReadAllText(stdoutSpoolPath);
                }
                catch (IOException ex)
                {
                    return new ResultArtifactOutcome(false, null, $"stdout spool unreadable: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return new ResultArtifactOutcome(false, null, $"stdout spool unreadable: {ex.Message}");
                }

                sb.Append(stdoutSpool);
            }

            // stderr: head + spool (complete-raw contract)
            sb.Append(stderrHead ?? string.Empty);
            if (!string.IsNullOrEmpty(stderrSpoolPath))
            {
                if (!File.Exists(stderrSpoolPath) && !Directory.Exists(stderrSpoolPath))
                {
                    return new ResultArtifactOutcome(false, null, $"stderr spool not found: {stderrSpoolPath}");
                }

                string stderrSpool;
                try
                {
                    stderrSpool = File.ReadAllText(stderrSpoolPath);
                }
                catch (IOException ex)
                {
                    return new ResultArtifactOutcome(false, null, $"stderr spool unreadable: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    return new ResultArtifactOutcome(false, null, $"stderr spool unreadable: {ex.Message}");
                }

                sb.Append(stderrSpool);
            }

            // Atomic write to target path
            var temp = Path.Combine(directory, $".lattice-artifact-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, sb.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: false);
            return new ResultArtifactOutcome(true, path, null);
        }
        catch (Exception ex)
        {
            return new ResultArtifactOutcome(false, null, ex.Message);
        }
    }

    private static string ResolvePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var baseDir = Path.GetDirectoryName(candidate) ?? directory;
        var i = 2;
        while (true)
        {
            var newName = $"{name}-{i}{ext}";
            var newPath = Path.Combine(baseDir, newName);
            if (!File.Exists(newPath))
            {
                return newPath;
            }

            i++;
        }
    }
}

/// <summary>Outcome of an attempt to save the raw artifact: success flag, saved path, or failure reason.</summary>
public sealed record ResultArtifactOutcome(bool Success, string? Path, string? Reason);
