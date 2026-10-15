using System.Diagnostics;
using System.Text;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;
using Xunit.Abstractions;

namespace Lattice.Tests.Cli;

/// <summary>
/// Run-control repair, bug B (red phase): the direct child's exit is mistaken
/// for safe capture completion.
///
/// The fixture is a real process tree: a Python direct child prints one line
/// and exits while a worker it spawned keeps the capture pipes open and sleeps.
/// <see cref="ICapturedRun.IsDone"/> reads true (the direct child is gone) but
/// collection has no EOF coming. Every wait here is bounded so a regression
/// fails instead of hanging the host, and cleanup kills only the PIDs the
/// fixture itself recorded in its own pidfile. No broad-name kill, no scan,
/// no stale PID: the pidfile is created fresh per test and read back by the
/// test that owns it.
/// </summary>
public class SlashConsoleDescendantTests
{
    private readonly ITestOutputHelper _output;

    public SlashConsoleDescendantTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The direct child exits while an owned worker keeps both pipes open.
    /// Collection must finish within an explicit bound. Pre-fix: Finish blocks
    /// past the bound (the EOF it waits for is held by a live worker) and the
    /// test fails on the bound instead of hanging the run.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData("both")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task CollectionFinishesWithinBoundWhileWorkerHoldsPipesOpen(string mode)
    {
        var python = RequirePython();
        using var temp = new TempDir();
        var pidfile = Path.Combine(temp.Path, "worker-pids.txt");
        File.WriteAllText(
            Path.Combine(temp.Path, "descendant.py"),
            DescendantScript());

        var runner = new SlashConsoleProcessRunner();
        var run = runner.StartCaptured(new CapturedRunRequest(
            ["descendant.py", mode, pidfile],
            "fixture descendant",
            temp.Path,
            new ProcessInvocation(python, [])));

        try
        {
            Assert.True(
                await WaitForWorkerPid(pidfile, TimeSpan.FromSeconds(15)),
                "the worker never recorded its PID; the fixture did not establish the held-pipe premise.");
            Assert.True(
                await WaitForExit(run, TimeSpan.FromSeconds(15)),
                "the direct child never exited; the fixture did not establish the exited-parent premise.");

            // IsDone reads the direct child: true while collection is blocked.
            Assert.True(run.IsDone);

            var finished = await Task.Run(() => run.Finish()).WaitAsync(TimeSpan.FromSeconds(20));
            _output.WriteLine($"collected exit={finished.ExitCode} stdout={finished.Stdout.Length} chars");
            Assert.Contains("child-line", finished.Stdout, StringComparison.Ordinal);
        }
        catch (TimeoutException)
        {
            KillOwnedWorkers(pidfile);
            Assert.Fail(
                $"Finish did not return within 20 s although the direct child had exited (mode={mode}); " +
                "collection waited unboundedly for EOF held open by a live worker.");
        }
        finally
        {
            KillOwnedWorkers(pidfile);
        }
    }

    /// <summary>
    /// Which .NET/OS primitives actually work after the direct parent exits.
    /// Starts the same fixture as a raw owned process (no runner), lets the
    /// parent exit, tries <c>Kill(entireProcessTree: true)</c> on the exited
    /// parent handle, and records whether the worker died. The outcome is
    /// evidence for the fix's ownership mechanism, not an assertion about the
    /// primitive: the test asserts the fixture premise (worker alive after
    /// parent exit) and the guaranteed cleanup (worker dead at the end by an
    /// owned-PID kill), and writes the primitive outcome to the log.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task TreeKillPrimitiveAfterParentExitIsRecorded()
    {
        var python = RequirePython();
        using var temp = new TempDir();
        var pidfile = Path.Combine(temp.Path, "worker-pids.txt");
        var script = Path.Combine(temp.Path, "descendant.py");
        File.WriteAllText(script, DescendantScript());

        using var owned = StartFixture(python, script, pidfile, temp.Path);
        owned.StandardInput.Close();
        Assert.True(
            await WaitForWorkerPid(pidfile, TimeSpan.FromSeconds(15)),
            "the worker never recorded its PID.");
        Assert.True(
            owned.WaitForExit((int)TimeSpan.FromSeconds(15).TotalMilliseconds),
            "the direct child never exited.");

        var workerPids = ReadOwnedPids(pidfile);
        Assert.NotEmpty(workerPids);
        var workerAliveAfterExit = workerPids.Any(IsAlive);
        Assert.True(
            workerAliveAfterExit,
            "the worker was already gone when the parent exited; the held-pipe premise was not established.");

        string primitive;
        try
        {
            owned.Kill(entireProcessTree: true);
            primitive = "Kill(true) accepted on the exited parent handle";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            primitive = $"Kill(true) refused on the exited parent handle: {exception.GetType().Name}";
        }

        var deadline = Stopwatch.StartNew();
        while (workerPids.Any(IsAlive) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);
        }

        var workerDeadByPrimitive = !workerPids.Any(IsAlive);
        _output.WriteLine($"{primitive}; worker dead within 5 s: {workerDeadByPrimitive}");
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        KillOwnedWorkers(pidfile);
        Assert.False(workerPids.Any(IsAlive), "owned cleanup did not reap the worker.");
    }

    /// <summary>
    /// The host stays usable while collection is blocked: driven through the
    /// real host loop with a runner that delegates to the real process runner
    /// on the fixture, Esc delivered <b>after</b> the direct child has exited
    /// (an observed <c>IsDone</c>, plus a settle so the loop's poll cadence has
    /// already entered collection) must cancel the tree and settle. Merely
    /// timing Finish is not this proof: the proof is that the key/render loop
    /// observed the key at all. Pre-fix, PollRun enters the blocking Finish on
    /// its 50 ms poll before the gate releases, Esc is never read, and the
    /// outer bound fires.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task HostProcessesCancelWhileCollectionIsBlocked()
    {
        var python = RequirePython();
        using var temp = new TempDir();
        var pidfile = Path.Combine(temp.Path, "worker-pids.txt");
        File.WriteAllText(Path.Combine(temp.Path, "descendant.py"), DescendantScript());
        var fixture = new FixtureRunner(python, temp.Path, pidfile);

        var keys = new List<TuiKey>();
        foreach (var c in "/generate --seed 42")
        {
            keys.Add(new TuiKey(TuiKeyKind.Character, c));
        }

        keys.Add(new TuiKey(TuiKeyKind.Enter));

        try
        {
            var host = await Task.Run(() => Drive(keys, fixture, extraIdleFrames: 60))
                .WaitAsync(TimeSpan.FromSeconds(45));

            // The contract under test: the host loop stayed live enough to
            // deliver the cancel while collection was still in flight, and
            // returned. Whether the "cancelled" title reaches the screen
            // depends on collection finishing before the loop's idle exit —
            // which it does not where an orphaned descendant survives the tree
            // kill and holds the pipes (the documented platform gap).
            Assert.True(fixture.CancelSeen, "the host never delivered cancel to the run; the loop was blocked in collection.");
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "the host loop never returned after the direct child exited: collection blocked the input/render " +
                "thread and Esc could not be processed.");
        }
        finally
        {
            KillOwnedWorkers(pidfile);
        }
    }

    /// <summary>
    /// The normal path still works through the same runner: a clean child is
    /// collected whole with no truncation. Passes before and after the fix;
    /// retained so the bounded collection cannot regress the ordinary case.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NormalCleanExitStillCollectsWhole()
    {
        using var temp = new TempDir();
        var runner = new SlashConsoleProcessRunner();
        var invocation = ProcessInvocationResolver.ForPaths("dotnet", LatticeDll());
        var run = runner.StartCaptured(new CapturedRunRequest(
            ["--version"], "lattice --version", temp.Path, invocation));
        var finished = await Task.Run(() => run.Finish()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, finished.ExitCode);
        Assert.False(finished.Cancelled);
        Assert.False(finished.Truncated);
        Assert.NotEmpty(finished.Stdout);
    }

    // ---- Fixture ----

    /// <summary>
    /// A direct child that prints one line and exits without waiting, while a
    /// worker it spawned keeps the chosen capture pipes open for 300 s. The
    /// worker records its own PID in the test-owned pidfile first, which is
    /// the explicit ownership the test cleans up by.
    /// </summary>
    private static string DescendantScript() =>
        "import os, subprocess, sys, time\n" +
        "mode = sys.argv[1]\n" +
        "pidfile = sys.argv[2]\n" +
        "wcode = (\"import os,sys,time\\n\"\n" +
        "         \"open(sys.argv[1],'a').write(str(os.getpid())+'\\\\n')\\n\"\n" +
        "         \"sys.stdout.write('worker-line\\\\n')\\n\"\n" +
        "         \"sys.stdout.flush()\\n\"\n" +
        "         \"time.sleep(300)\\n\")\n" +
        "out = sys.stdout if mode in ('both', 'stdout') else subprocess.DEVNULL\n" +
        "err = sys.stderr if mode in ('both', 'stderr') else subprocess.DEVNULL\n" +
        "w = subprocess.Popen([sys.executable, '-c', wcode, pidfile], stdout=out, stderr=err)\n" +
        "print('child-line')\n" +
        "sys.stdout.flush()\n";

    private static async Task<bool> WaitForWorkerPid(string pidfile, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < budget)
        {
            if (File.Exists(pidfile) && new FileInfo(pidfile).Length > 0)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<bool> WaitForExit(ICapturedRun run, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < budget)
        {
            if (run.IsDone)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return run.IsDone;
    }

    private static List<int> ReadOwnedPids(string pidfile)
    {
        if (!File.Exists(pidfile))
        {
            return [];
        }

        return File.ReadAllLines(pidfile)
            .Select(line => int.TryParse(line.Trim(), out var pid) ? pid : -1)
            .Where(pid => pid > 0)
            .Distinct()
            .ToList();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reaps only the PIDs the fixture recorded in the test-owned pidfile.
    /// Never a broad name kill, never a scan: a PID that is not in this file
    /// is not this test's to touch. Waits, bounded, so a caller asserting
    /// "dead" afterwards is asserting an observed fact rather than a hope.
    /// </summary>
    private void KillOwnedWorkers(string pidfile)
    {
        var owned = ReadOwnedPids(pidfile);
        foreach (var pid in owned)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    _output.WriteLine($"reaping owned worker {pid}");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        var deadline = Stopwatch.StartNew();
        while (owned.Any(IsAlive) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(100);
        }
    }

    private static string RequirePython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }

                    if (OperatingSystem.IsWindows())
                    {
                        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                        {
                            if (File.Exists(full + ext))
                            {
                                return full + ext;
                            }
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        throw new InvalidOperationException("no python3/python on PATH; the descendant fixture cannot run on this host.");
    }

    private static string LatticeDll()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "lattice.dll");
        Assert.True(File.Exists(dll), $"the CLI was not found at '{dll}'.");
        return dll;
    }

    // ---- Host seam scaffolding (minimal; mirrors SlashConsoleHostTests) ----

    private sealed class FixtureRunner(string python, string directory, string pidfile) : ISlashConsoleRunner
    {
        private readonly SlashConsoleProcessRunner _real = new();
        private volatile ICapturedRun? _current;
        internal bool CancelSeen { get; private set; }

        /// <summary>Whether the current run's direct child has exited, or null when none was started.</summary>
        internal bool? CurrentIsDone => _current?.IsDone;

        public ICapturedRun StartCaptured(CapturedRunRequest request)
        {
            var inner = _real.StartCaptured(new CapturedRunRequest(
                ["descendant.py", "both", pidfile],
                "fixture descendant",
                directory,
                new ProcessInvocation(python, [])));
            _current = inner;
            return new CancelSpy(inner, () => CancelSeen = true);
        }

        public WatchRunResult RunWatch(WatchRunRequest request) => _real.RunWatch(request);

        private sealed class CancelSpy(ICapturedRun inner, Action onCancel) : ICapturedRun
        {
            public bool IsDone => inner.IsDone;
            public void Cancel()
            {
                onCancel();
                inner.Cancel();
            }

            public CapturedRunResult Finish() => inner.Finish();

            // Forwarded, not defaulted: a decorator inheriting a Finish-based
            // default would block this poll on the wrapped collection.
            public bool TryTakeFinished(out CapturedRunResult result) => inner.TryTakeFinished(out result);
        }
    }

    private static (int Exit, string Screen) Drive(
        List<TuiKey> keys, FixtureRunner runner, int extraIdleFrames)
    {
        var index = 0;
        var escDelivered = false;
        var scripted = new KeyQueue(() =>
        {
            if (index < keys.Count)
            {
                return keys[index++];
            }

            if (!escDelivered)
            {
                // Readiness gate, not a proof-sleep: release Esc only once the
                // direct child has exited, then let the loop's own poll cadence
                // run so a blocking collector is entered before the key arrives.
                while (runner.CurrentIsDone is null or false)
                {
                    Thread.Sleep(25);
                }

                Thread.Sleep(150);
                escDelivered = true;
                return new TuiKey(TuiKeyKind.Escape);
            }

            return null;
        });

        var session = new RecordingSession();
        var request = new SlashConsoleHostRequest(
            new StringWriter(),
            new StringWriter(),
            new TerminalCapabilities(ColorDepth.TrueColor, Utf8: true, InputRedirected: false, OutputRedirected: false, Width: 100, Height: 30),
            new TuiConsole(new RecordingControlC(), () => scripted),
            session,
            new FixedClock(),
            runner,
            Path.GetTempPath(),
            TrailingIdleFrames: extraIdleFrames);

        var exit = SlashConsoleHost.Run(request).ExitCode;
        return (exit, session.Written);
    }

    private sealed class FixedClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.FromSeconds(5);
    }

    private sealed class RecordingControlC : IControlCAsInput
    {
        public bool Get() => false;
        public void Set(bool value)
        {
        }
    }

    private sealed class RecordingSession : ITerminalSessionFactory
    {
        private readonly StringBuilder _written = new();
        internal string Written
        {
            get
            {
                lock (_written)
                {
                    return _written.ToString();
                }
            }
        }

        public bool RequestUtf8Output() => true;
        public ITerminalSession Enter(TextWriter output) => new Inner(_written);

        private sealed class Inner(StringBuilder written) : ITerminalSession
        {
            public event Action? Interrupted
            {
                add { }
                remove { }
            }

            public void Write(string text)
            {
                lock (written)
                {
                    written.Append(text);
                }
            }

            public void Dispose()
            {
            }
        }
    }

    private static Process StartFixture(string python, string script, string pidfile, string directory)
    {
        var start = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("both");
        start.ArgumentList.Add(pidfile);
        return Process.Start(start) ?? throw new InvalidOperationException("could not start the fixture parent.");
    }

    private sealed class TempDir : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "lattice-desc-" + Guid.NewGuid().ToString("N"));

        internal TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
