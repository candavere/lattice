using System.Text;

namespace Lattice.Cli.Presentation;

/// <summary>
/// What one captured child stream retained: the in-memory head, the spool file
/// holding the overflow, and exact counts of everything else.
/// </summary>
/// <param name="MemoryText">The first characters read, up to the memory cap.</param>
/// <param name="TotalChars">Every character read from the pipe, including dropped ones.</param>
/// <param name="DroppedChars">Characters read but kept nowhere: past the disk cap, or unspooled after a spool failure.</param>
/// <param name="SpoolPath">The file holding the characters past the memory head, or null when nothing overflowed.</param>
/// <param name="SpooledChars">Characters in the spool file.</param>
/// <param name="SpoolFailed">A spool write failed; the spool file (if any) is partial and the rest was dropped.</param>
/// <param name="Truncated">The memory head alone is not the whole stream.</param>
public sealed record CapturedStream(
    string MemoryText,
    long TotalChars,
    long DroppedChars,
    string? SpoolPath,
    long SpooledChars,
    bool SpoolFailed,
    bool Truncated)
{
    /// <summary>Whether the memory head alone is the whole stream.</summary>
    /// <remarks>
    /// This is NOT the projection gate: a spooled stream can be fully retained
    /// while its head is partial. Gate domain projections on
    /// <see cref="FullOutputRetained"/>, never on this.
    /// </remarks>
    public bool MemoryWhole => !Truncated && DroppedChars == 0 && !SpoolFailed;

    /// <summary>
    /// Whether the full stream is durably available: the memory head plus the
    /// spool file together hold every character. False exactly when the disk
    /// cap was hit or a spool write failed — the cases where no consumer may
    /// treat the retained bytes as the whole output.
    /// </summary>
    public bool FullOutputRetained =>
        DroppedChars == 0 && !SpoolFailed && (MemoryWhole || SpoolPath is not null);
}

/// <summary>
/// Drains one child stream under a memory cap with overflow spooled to a file
/// under a disk cap. The reader is always consumed to EOF — even after both
/// caps are hit, so the child can never block on a full pipe — while stdout and
/// stderr drain concurrently on the caller's two tasks. Caps are character
/// counts; the spool file is UTF-8 text.
/// </summary>
public static class BoundedStreamCapture
{
    private const int ReadChunkChars = 4096;

    /// <summary>
    /// Reads <paramref name="reader"/> to EOF, keeping the first
    /// <paramref name="memoryCapChars"/> characters in memory and appending the
    /// overflow to <paramref name="spoolPath"/> (created lazily, only when the
    /// memory cap is exceeded) up to <paramref name="diskCapChars"/> characters.
    /// A null <paramref name="spoolPath"/> means the overflow is counted and
    /// dropped. A spool failure is recorded, never thrown: the pipe keeps
    /// draining and the loss is counted exactly.
    /// </summary>
    public static async Task<CapturedStream> CaptureAsync(
        TextReader reader,
        int memoryCapChars,
        string? spoolPath,
        long diskCapChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryCapChars);
        ArgumentOutOfRangeException.ThrowIfNegative(diskCapChars);

        var memory = new StringBuilder(Math.Min(memoryCapChars, ReadChunkChars));
        StreamWriter? spool = null;
        var spoolCreated = false;
        var spoolFailed = false;
        long total = 0;
        long spooled = 0;
        long dropped = 0;
        var buffer = new char[ReadChunkChars];

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var slice = buffer.AsMemory(0, read);
                total += read;

                var memoryRoom = memoryCapChars - memory.Length;
                if (memoryRoom > 0)
                {
                    var take = Math.Min(memoryRoom, read);
                    memory.Append(buffer, 0, take);
                    slice = slice[take..];
                }

                if (slice.Length == 0)
                {
                    continue;
                }

                if (spoolPath is null || spoolFailed)
                {
                    dropped += slice.Length;
                    continue;
                }

                var writer = spool;
                if (writer is null)
                {
                    writer = OpenSpool(spoolPath, ref spoolCreated);
                    spool = writer;
                    if (writer is null)
                    {
                        spoolFailed = true;
                        dropped += slice.Length;
                        continue;
                    }
                }

                var diskRoom = diskCapChars - spooled;
                if (diskRoom > 0)
                {
                    var take = (int)Math.Min(diskRoom, slice.Length);
                    try
                    {
                        await writer.WriteAsync(slice[..take], cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        FailSpool(ref spool, spoolPath, spoolCreated);
                        spoolFailed = true;
                        dropped += slice.Length;
                        continue;
                    }

                    spooled += take;
                    slice = slice[take..];
                }

                dropped += slice.Length;
            }
        }
        finally
        {
            var writer = spool;
            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    FailSpool(ref spool, spoolPath, spoolCreated);
                    spoolFailed = true;
                    dropped += spooled;
                    spooled = 0;
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        var memoryText = memory.ToString();
        return new CapturedStream(
            memoryText,
            total,
            dropped,
            spoolFailed ? null : (spooled > 0 ? spoolPath : null),
            spoolFailed ? 0 : spooled,
            spoolFailed,
            total > memoryText.Length);
    }

    private static StreamWriter? OpenSpool(string spoolPath, ref bool created)
    {
        try
        {
            var directory = Path.GetDirectoryName(spoolPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var writer = new StreamWriter(
                new FileStream(spoolPath, FileMode.Create, FileAccess.Write, FileShare.Read, ReadChunkChars, useAsync: true),
                Encoding.UTF8);
            created = true;
            return writer;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private static void FailSpool(ref StreamWriter? spool, string? spoolPath, bool created)
    {
        try
        {
            spool?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
        }
        finally
        {
            spool = null;
        }

        // A partial spool file must never be mistaken for the full output.
        if (created && spoolPath is not null)
        {
            try
            {
                File.Delete(spoolPath);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
            }
        }
    }
}

/// <summary>
/// The honest limit line for one stream: null when nothing was lost, otherwise
/// exactly what was dropped and from which stream. Pure, so the wording the
/// results workspace shows is asserted without a process.
/// </summary>
public static class CaptureLimits
{
    /// <summary>
    /// Describes what happened to <paramref name="stream"/> beyond its caps, or
    /// null when the stream is whole.
    /// </summary>
    public static string? Describe(string streamName, CapturedStream stream, int memoryCapChars, long diskCapChars)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.MemoryWhole)
        {
            return null;
        }

        if (stream.SpoolFailed)
        {
            return $"{streamName} spool write failed; kept {stream.MemoryText.Length} characters in memory; " +
                $"dropped {stream.DroppedChars} characters; output is partial.";
        }

        if (stream.SpoolPath is null)
        {
            return $"{streamName} exceeded the memory cap ({memoryCapChars} characters); spooling off; " +
                $"dropped {stream.DroppedChars} characters.";
        }

        if (stream.DroppedChars > 0)
        {
            return $"{streamName} exceeded the memory cap ({memoryCapChars} characters) and the disk cap " +
                $"({diskCapChars} characters); kept {stream.MemoryText.Length} in memory and {stream.SpooledChars} " +
                $"spooled in {stream.SpoolPath}; dropped {stream.DroppedChars} characters.";
        }

        return $"{streamName} exceeded the memory cap ({memoryCapChars} characters); full output in {stream.SpoolPath}.";
    }
}

/// <summary>
/// Quotes an argv for display exactly as passed: no reordering, no elision, no
/// defaults filled in. POSIX single-quote style; display only, never re-parsed.
/// </summary>
public static class ArgvDisplay
{
    /// <summary>Quotes <paramref name="argv"/> for the results workspace's command line.</summary>
    public static string QuoteCommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return string.Join(' ', argv.Select(QuoteOne));
    }

    private static string QuoteOne(string argument)
    {
        if (argument.Length == 0)
        {
            return "''";
        }

        var safe = argument.All(IsSafeChar);
        if (safe)
        {
            return argument;
        }

        return "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsSafeChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '%' or '+' or '=' or ':' or ',' or '.' or '/' or '-';
}
