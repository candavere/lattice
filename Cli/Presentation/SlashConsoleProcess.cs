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
/// <param name="Stdout">The child's stdout, bounded.</param>
/// <param name="Stderr">The child's stderr, bounded.</param>
/// <param name="Cancelled">Whether the run was cancelled through <see cref="ICapturedRun.Cancel"/>.</param>
/// <param name="Elapsed">How long the run took.</param>
/// <param name="Truncated">Whether either stream exceeded the bound.</param>
public sealed record CapturedRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool Cancelled,
    TimeSpan Elapsed,
    bool Truncated);

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
/// <b>Result buffer bound.</b> Each captured stream is kept up to
/// <see cref="CapturedMaxCharsPerStream"/> characters (32,768); anything past that is
/// dropped and reported through <see cref="CapturedRunResult.Truncated"/>, which the
/// frame shows with its visible truncation marker. <c>--out</c> artifacts are written
/// by the child exactly as on the command line and are never bounded here.
/// </para>
/// </remarks>
public sealed class SlashConsoleProcessRunner : ISlashConsoleRunner
{
    /// <summary>How much of each captured stream is kept, in characters.</summary>
    public const int CapturedMaxCharsPerStream = 32768;

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

        var process = Process.Start(start) ?? throw new InvalidOperationException(
            $"Could not start '{start.FileName}' for '{request.DisplayLine}'.");
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        return new CapturedRun(process, stdout, stderr, Stopwatch.StartNew());
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
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly Stopwatch _stopwatch;
        private bool _cancelled;
        private bool _finished;
        private CapturedRunResult? _result;

        internal CapturedRun(Process process, Task<string> stdout, Task<string> stderr, Stopwatch stopwatch)
        {
            _process = process;
            _stdout = stdout;
            _stderr = stderr;
            _stopwatch = stopwatch;
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
        public CapturedRunResult Finish()
        {
            if (_result is not null)
            {
                return _result;
            }

            try
            {
                _process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }

            var stdout = _stdout.GetAwaiter().GetResult();
            var stderr = _stderr.GetAwaiter().GetResult();
            var elapsed = _stopwatch.Elapsed;

            var (boundedOut, outTruncated) = BoundOutput(stdout);
            var (boundedErr, errTruncated) = BoundOutput(stderr);

            int exit;
            try
            {
                exit = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exit = 1;
            }

            _process.Dispose();
            _finished = true;
            _result = new CapturedRunResult(exit, boundedOut, boundedErr, _cancelled, elapsed, outTruncated || errTruncated);
            return _result;
        }
    }
}
