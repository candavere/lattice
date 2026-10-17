using System.Diagnostics;
using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>How to start the current program again as a child: the executable and any leading arguments.</summary>
/// <param name="FileName">The executable to start.</param>
/// <param name="PrefixArguments">Leading arguments before the command's own argv (the dll path under a host).</param>
public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Resolves how the running program re-invokes itself as a child process.
/// <see cref="System.Environment.ProcessPath"/> is the <c>dotnet</c> host when Lattice runs as
/// <c>dotnet lattice.dll</c> or <c>dotnet run</c>, so in that case the child is
/// <c>dotnet &lt;path to lattice.dll&gt;</c>; otherwise it is <c>ProcessPath</c> itself.
/// </summary>
public static class ProcessInvocationResolver
{
    /// <summary>Resolves the invocation for the current process.</summary>
    public static ProcessInvocation ForCurrentProcess() =>
        ForPaths(System.Environment.ProcessPath, typeof(CliApp).Assembly.Location);

    /// <summary>
    /// The testable seam: the same decision for injected paths, so both the host form
    /// (<c>dotnet</c> + dll) and the apphost form (self executable) are asserted on every OS.
    /// Separators are normalised before the file name is read, so a Windows-style path
    /// decides the same way on any host.
    /// </summary>
    public static ProcessInvocation ForPaths(string? processPath, string assemblyLocation)
    {
        ArgumentNullException.ThrowIfNull(assemblyLocation);

        if (string.IsNullOrEmpty(processPath))
        {
            return new ProcessInvocation(assemblyLocation, []);
        }

        var fileName = Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/'));
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessInvocation(processPath, [assemblyLocation]);
        }

        return new ProcessInvocation(processPath, []);
    }
}

/// <summary>One captured command run: the argv, the line it is shown as, and where it runs.</summary>
/// <param name="Invocation">How to start the child; null resolves the current process.</param>
public sealed record CapturedRunRequest(string[] Arguments, string DisplayLine, string WorkingDirectory, ProcessInvocation? Invocation = null);

/// <summary>What a captured run produced.</summary>
/// <param name="ExitCode">The child's exit status (whatever the platform reports after a kill).</param>
/// <param name="Stdout">The child's stdout head, up to the memory cap.</param>
/// <param name="Stderr">The child's stderr head, up to the memory cap.</param>
/// <param name="Cancelled">Whether the run was cancelled through <see cref="ICapturedRun.Cancel"/>.</param>
/// <param name="Elapsed">How long the run took.</param>
/// <param name="Truncated">Whether either stream exceeded its memory cap, hit the disk cap, or lost its spool write.</param>
/// <param name="EffectiveCommand">
/// The command actually passed to the child — executable, invocation prefix and
/// argv — quoted for display. Quoted from the captured argv, never a UI label.
/// </param>
/// <param name="WorkingDirectory">The directory the child ran in.</param>
/// <param name="StdoutDroppedChars">Stdout characters read but kept nowhere.</param>
/// <param name="StderrDroppedChars">Stderr characters read but kept nowhere.</param>
/// <param name="StdoutSpoolPath">The file holding stdout past its memory head, or null.</param>
/// <param name="StderrSpoolPath">The file holding stderr past its memory head, or null.</param>
/// <param name="StdoutMemoryWhole">Whether the stdout head alone is the whole stream (not the projection gate).</param>
/// <param name="StderrMemoryWhole">Whether the stderr head alone is the whole stream (not the projection gate).</param>
/// <param name="StdoutSpoolFailed">Whether the stdout spool write failed (its output is partial).</param>
/// <param name="StderrSpoolFailed">Whether the stderr spool write failed (its output is partial).</param>
/// <param name="CollectionIncomplete">
/// Whether collection ended before both pipes reached EOF, so an unknown tail
/// of output was never read at all — distinct from truncation, where everything
/// read was retained or counted.
/// </param>
/// <remarks>
/// Full raw output is available exactly when a stream is complete, or when its
/// spool path is set with no dropped characters and no spool failure, and the
/// collection itself reached EOF on both pipes; anything else is partial and
/// must never feed a domain projection as if it were whole.
/// </remarks>
public sealed record CapturedRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool Cancelled,
    TimeSpan Elapsed,
    bool Truncated,
    string EffectiveCommand = "",
    string WorkingDirectory = "",
    long StdoutDroppedChars = 0,
    long StderrDroppedChars = 0,
    string? StdoutSpoolPath = null,
    string? StderrSpoolPath = null,
    bool StdoutMemoryWhole = true,
    bool StderrMemoryWhole = true,
    bool StdoutSpoolFailed = false,
    bool StderrSpoolFailed = false,
    bool CollectionIncomplete = false);

/// <summary>One watch-screen run: the <c>tui</c> argv, the line it is shown as, and where it runs.</summary>
/// <param name="Invocation">How to start the child; null resolves the current process.</param>
public sealed record WatchRunRequest(string[] Arguments, string DisplayLine, string WorkingDirectory, ProcessInvocation? Invocation = null);

/// <summary>What a watch-screen run produced: the exit status and how long it took.</summary>
public readonly record struct WatchRunResult(int ExitCode, TimeSpan Elapsed);

/// <summary>A captured child in flight: poll <see cref="IsDone"/>, end with <see cref="Finish"/>.</summary>
public interface ICapturedRun
{
    /// <summary>Whether the child has exited. Non-blocking.</summary>
    bool IsDone { get; }

    /// <summary>Cancels the run: kills the child's whole process tree, then collects.</summary>
    void Cancel();

    /// <summary>Waits for the exit and collects the bounded output. Idempotent.</summary>
    CapturedRunResult Finish();

    /// <summary>
    /// Takes the collected result if collection has completed, without blocking.
    /// False while the child runs or collection is still in flight — the state a
    /// poll loop shows as still working. Implementations that wrap another
    /// <see cref="ICapturedRun"/> forward this member; it is deliberately not a
    /// default method falling back to <see cref="Finish"/>, because a wrapper
    /// inheriting that default would turn this non-blocking poll back into a
    /// blocking collection — the exact freeze this seam exists to prevent.
    /// </summary>
    bool TryTakeFinished(out CapturedRunResult result);
}

/// <summary>
/// Starts console commands as child processes of the same program. A seam so the host
/// loop is driven with scripted outcomes and the real process behaviour is asserted
/// directly, on all three CI operating systems.
/// </summary>
public interface ISlashConsoleRunner
{
    /// <summary>Starts a captured run: stdout/stderr redirected, stdin closed.</summary>
    ICapturedRun StartCaptured(CapturedRunRequest request);

    /// <summary>
    /// Runs a watch screen with the terminal inherited: no stream redirected, blocking
    /// until the child exits. The caller leaves the alternate screen first and owns
    /// the key handoff, exactly as the Launchpad host does.
    /// </summary>
    WatchRunResult RunWatch(WatchRunRequest request);
}

/// <summary>
/// The one runner: every console command is a child process of the same program, built
/// with <see cref="ProcessStartInfo.ArgumentList"/> — never a joined string — so spaces,
/// quotes, empty strings, Unicode and <c>--flag=value with spaces</c> reach the child
/// unchanged. The child's working directory is the caller's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Result buffer bound.</b> Each captured stream is drained incrementally and
/// kept up to <see cref="CapturedMaxCharsPerStream"/> characters in memory
/// (32,768); overflow is spooled to a per-run file under
/// <see cref="CapturedDiskCapCharsPerStream"/> characters, and anything past
/// that is counted as dropped. Both pipes are always drained to EOF — even
/// after both caps — so the child can never block on a full pipe. A truncated
/// capture reports exactly what was dropped per stream and must never feed a
/// domain projection as if it were complete. <c>--out</c> artifacts are written
/// by the child exactly as on the command line and are never bounded here.
/// Spool files live under the OS temp directory for the session (only runs
/// that overflow memory create them) and are left to OS temp reclamation.
/// </para>
/// <para>
/// <b>Collection is bounded, and exit is not capture.</b> The child's exit, the
/// pipes reaching EOF, and the terminal result are three distinct facts. A
/// worker process the child spawned can inherit a capture pipe and hold it open
/// past the child's own exit, so waiting for EOF after <see cref="ICapturedRun.IsDone"/>
/// can wait forever. Collection therefore runs on its own task under an
/// explicit budget: when a pipe has not reached EOF in time, the whole process
/// tree is killed (which is what actually closes the pipe), a short grace
/// follows, and whatever remains uncollected is reported through
/// <see cref="CapturedRunResult.CollectionIncomplete"/> rather than passed off
/// as complete. The budgets are teardown policy, paid only by runs whose
/// descendants misbehave; an ordinary run collects the moment its pipes close.
/// </para>
/// </remarks>
public sealed class SlashConsoleProcessRunner : ISlashConsoleRunner
{
    /// <summary>How much of each captured stream is kept in memory, in characters.</summary>
    public const int CapturedMaxCharsPerStream = 32768;

    /// <summary>How much overflow per captured stream is spooled to disk, in characters.</summary>
    public const long CapturedDiskCapCharsPerStream = 1048576;

    /// <summary>
    /// How long collection waits for both pipes to reach EOF before the tree is
    /// killed and the capture is marked incomplete. Long enough that no ordinary
    /// command — including a slow but honest one — ever pays it; short enough
    /// that a pipe held open by a descendant cannot hold the console.
    /// </summary>
    public static readonly TimeSpan CollectionEofTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public ICapturedRun StartCaptured(CapturedRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invocation = request.Invocation ?? ProcessInvocationResolver.ForCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
        };

        foreach (var argument in invocation.PrefixArguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var effectiveCommand = ArgvDisplay.QuoteCommand(
            [start.FileName, .. invocation.PrefixArguments, .. request.Arguments]);
        var spoolDirectory = Path.Combine(Path.GetTempPath(), "lattice-console-" + Guid.NewGuid().ToString("N"));

        var process = Process.Start(start) ?? throw new InvalidOperationException(
            $"Could not start '{start.FileName}' for '{request.DisplayLine}'.");
        process.StandardInput.Close();

        // Both streams drain concurrently from here: each task reads its pipe
        // to EOF under the caps, so a chatty child can never block.
        var stdoutSnapshot = new CaptureSnapshot();
        var stderrSnapshot = new CaptureSnapshot();
        var stdout = BoundedStreamCapture.CaptureAsync(
            process.StandardOutput,
            CapturedMaxCharsPerStream,
            Path.Combine(spoolDirectory, "stdout.log"),
            CapturedDiskCapCharsPerStream,
            progress: stdoutSnapshot.Update);
        var stderr = BoundedStreamCapture.CaptureAsync(
            process.StandardError,
            CapturedMaxCharsPerStream,
            Path.Combine(spoolDirectory, "stderr.log"),
            CapturedDiskCapCharsPerStream,
            progress: stderrSnapshot.Update);
        return new CapturedRun(
            process,
            stdout,
            stderr,
            stdoutSnapshot,
            stderrSnapshot,
            Stopwatch.StartNew(),
            effectiveCommand,
            request.WorkingDirectory);

    }

    /// <summary>The latest snapshot of one in-flight capture, safe to read from any thread.</summary>
    private sealed class CaptureSnapshot
    {
        private volatile CapturedStream? _current;

        internal CapturedStream? Current => _current;

        internal void Update(CapturedStream snapshot) => _current = snapshot;
    }

    /// <inheritdoc />
    public WatchRunResult RunWatch(WatchRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invocation = request.Invocation ?? ProcessInvocationResolver.ForCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory,
        };

        foreach (var argument in invocation.PrefixArguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException(
            $"Could not start '{start.FileName}' for '{request.DisplayLine}'.");
        process.WaitForExit();
        return new WatchRunResult(process.ExitCode, stopwatch.Elapsed);
    }

    /// <summary>
    /// Bounds one captured stream to <see cref="CapturedMaxCharsPerStream"/>, reporting
    /// whether anything was dropped. Pure, so the bound is asserted without a process.
    /// </summary>
    public static (string Text, bool Truncated) BoundOutput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length > CapturedMaxCharsPerStream
            ? (text[..CapturedMaxCharsPerStream], true)
            : (text, false);
    }

    private sealed class CapturedRun : ICapturedRun
    {
        private readonly Process _process;
        private readonly Task<CapturedStream> _stdout;
        private readonly Task<CapturedStream> _stderr;
        private readonly CaptureSnapshot _stdoutSnapshot;
        private readonly CaptureSnapshot _stderrSnapshot;
        private readonly Stopwatch _stopwatch;
        private readonly string _effectiveCommand;
        private readonly string _workingDirectory;
        private readonly Task<CapturedRunResult> _collection;
        private bool _cancelled;
        private bool _finished;
        private CapturedRunResult? _result;

        internal CapturedRun(
            Process process,
            Task<CapturedStream> stdout,
            Task<CapturedStream> stderr,
            CaptureSnapshot stdoutSnapshot,
            CaptureSnapshot stderrSnapshot,
            Stopwatch stopwatch,
            string effectiveCommand,
            string workingDirectory)
        {
            _process = process;
            _stdout = stdout;
            _stderr = stderr;
            _stdoutSnapshot = stdoutSnapshot;
            _stderrSnapshot = stderrSnapshot;
            _stopwatch = stopwatch;
            _effectiveCommand = effectiveCommand;
            _workingDirectory = workingDirectory;

            // Collection starts now, off the caller's thread, and is bounded by
            // its own policy: nothing in Finish ever waits unboundedly for a
            // pipe a descendant may be holding open.
            _collection = Task.Run(CollectAsync);
        }

        /// <inheritdoc />
        public bool IsDone
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <inheritdoc />
        public void Cancel()
        {
            _cancelled = true;

            try
            {
                // The whole tree: an evaluate run's external agent is a grandchild,
                // and leaving it behind would leave a live agent with no console.
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited: there is nothing left to kill.
            }
            catch (NotSupportedException)
            {
                try
                {
                    _process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        /// <inheritdoc />
        public bool TryTakeFinished(out CapturedRunResult result)
        {
            if (!_collection.IsCompleted)
            {
                result = default!;
                return false;
            }

            result = _collection.GetAwaiter().GetResult();
            return true;
        }

        /// <inheritdoc />
        public CapturedRunResult Finish()
        {
            if (_result is not null)
            {
                return _result;
            }

            // Bounded by the collection's own policy, never by an unbounded
            // EOF wait: the collection task kills the tree when a descendant
            // holds a pipe, so this cannot wait forever.
            var collected = _collection.GetAwaiter().GetResult();
            _finished = true;
            _result = collected;
            return _result;
        }

        /// <summary>
        /// The one bounded collection every caller reads from: wait for the
        /// direct child, wait for both pipes to reach EOF under
        /// <see cref="SlashConsoleProcessRunner.CollectionEofTimeout"/>, kill
        /// the tree when a descendant holds a pipe past that, allow a short
        /// grace for the kill to take effect, and report honestly what remains
        /// uncollected.
        /// </summary>
        private async Task<CapturedRunResult> CollectAsync()
        {
            try
            {
                _process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }

            bool bothClosed;
            try
            {
                bothClosed = await WaitForBothClosed().WaitAsync(CollectionEofTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                bothClosed = false;
            }

            var incomplete = !bothClosed;
            if (incomplete)
            {
                // A descendant of the child is holding a pipe open. Killing the
                // tree is what closes it: without the kill there is no EOF, only
                // an unbounded wait. This is the path where the direct child's
                // exit and safe capture are distinct facts. The kill is a
                // collection policy, not a user cancellation, so it must not
                // set the run's Cancelled flag — that flag is the user's Esc
                // and Ctrl-C, and reporting a policy kill as one would misname
                // what happened. The incompleteness itself is what the result
                // reports.
                KillTree();
                try
                {
                    bothClosed = await WaitForBothClosed().WaitAsync(GraceForKill).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    bothClosed = false;
                }

                incomplete = !bothClosed;
            }

            var stdout = await ValueOf(_stdout, _stdoutSnapshot).ConfigureAwait(false);
            var stderr = await ValueOf(_stderr, _stderrSnapshot).ConfigureAwait(false);
            var elapsed = _stopwatch.Elapsed;

            int exit;
            try
            {
                exit = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exit = 1;
            }

            try
            {
                _process.Dispose();
            }
            catch (InvalidOperationException)
            {
            }

            var truncated = incomplete || !stdout.MemoryWhole || !stderr.MemoryWhole;
            return new CapturedRunResult(
                exit,
                stdout.MemoryText,
                stderr.MemoryText,
                _cancelled,
                elapsed,
                truncated,
                _effectiveCommand,
                _workingDirectory,
                stdout.DroppedChars,
                stderr.DroppedChars,
                stdout.SpoolPath,
                stderr.SpoolPath,
                stdout.MemoryWhole,
                stderr.MemoryWhole,
                stdout.SpoolFailed,
                stderr.SpoolFailed,
                incomplete);
        }

        /// <summary>
        /// Kills the whole tree from the collector, without touching the user
        /// cancellation flag — <see cref="Cancel"/> is the user's Esc and
        /// Ctrl-C, and a collection-policy kill is a different fact. The
        /// direct child is usually already gone here, and the primitive probe
        /// behind these tests shows <c>Kill(entireProcessTree: true)</c> on an
        /// exited parent is accepted but does not reliably reap an orphaned
        /// descendant on every OS — so the honest guarantee is bounded
        /// collection with the gap reported, not a promise that every
        /// descendant is reaped.
        /// </summary>
        private void KillTree()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited: the descendants are the collector's only
                // remaining concern, and the platform gap above applies.
            }
            catch (NotSupportedException)
            {
                try
                {
                    _process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        /// <summary>
        /// True when both capture tasks reached EOF. Where the kill does not
        /// close the pipes, the honest outcome is the incomplete flag, not a
        /// promise that every descendant was reaped.
        /// </summary>
        private async Task<bool> WaitForBothClosed()
        {
            try
            {
                await _stdout.ConfigureAwait(false);
                await _stderr.ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A capture that faulted will never produce more output; treat
                // it as closed so collection settles. The fault's own stream
                // falls back to its snapshot below.
                return true;
            }
        }

        /// <summary>Resolves a capture task, falling back to its latest snapshot when EOF never came.</summary>
        private static async Task<CapturedStream> ValueOf(Task<CapturedStream> capture, CaptureSnapshot snapshot)
        {
            if (capture.IsCompleted)
            {
                try
                {
                    return await capture.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                }
            }

            return snapshot.Current
                ?? new CapturedStream(string.Empty, 0, 0, null, 0, SpoolFailed: false, Truncated: false);
        }

        /// <summary>How long the kill gets to close the pipes before the capture is reported incomplete.</summary>
        private static readonly TimeSpan GraceForKill = TimeSpan.FromSeconds(3);
    }
}
