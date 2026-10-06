using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Who owns console input while the Launchpad runs a command, and who owns
/// each entered screen: the parent hands its key source off before the run
/// and takes a fresh one afterwards, and every entered session is restored
/// exactly once — including the screen the run returns to.
/// </summary>
/// <remarks>
/// A shared scripted queue cannot show the production defect (two readers
/// splitting one real stdin), so these tests assert the structure that makes
/// the defect impossible: the handoff order around the runner, and the
/// disposal of every entered session. The real-reader evidence is the PTY
/// record; these tests pin the discipline that fixes it.
/// </remarks>
public class LaunchpadInputOwnershipTests
{
    private static readonly TerminalCapabilities Interactive = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>
    /// The parent hands its key source off before the command runs and builds a
    /// fresh one for the returned screen: disposed, ran, built — in that order.
    /// A parent that keeps reading while the command runs splits real input
    /// with it, so the handoff is the fix and not bookkeeping.
    /// </summary>
    [Fact]
    public void ParentHandsOffItsKeySourceAroundACommandRun()
    {
        var events = new List<string>();
        var keys = new ScriptedKeys(events);
        foreach (var key in RunRenderKeys())
        {
            keys.Queue(key);
        }

        keys.Queue(Character(' '));
        keys.Queue(Character('q'));

        LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            new MultiSession(events),
            new StaticClock(),
            new StubRunner(events),
            TrailingIdleFrames: 0));

        var ran = events.IndexOf("ran");
        Assert.True(ran > 0, "the command never ran.");
        Assert.Equal("keys disposed", events[ran - 1]);
        Assert.Contains("keys built", events[(ran + 1)..]);
    }

    /// <summary>
    /// While the runner runs, the parent's source is handed off: a runner that
    /// looks finds it disposed rather than live. A live parent source is a
    /// second reader on the same stdin.
    /// </summary>
    [Fact]
    public void RunnerSeesNoLiveParentSourceWhileItRuns()
    {
        var events = new List<string>();
        var built = new List<RecordingKeys>();
        var inner = new ScriptedKeys(events);
        foreach (var key in RunRenderKeys())
        {
            inner.Queue(key);
        }

        inner.Queue(Character(' '));
        inner.Queue(Character('q'));

        var parentDisposedDuringRun = false;
        var runner = new ObservingRunner(
            events,
            () => parentDisposedDuringRun = built.Count > 0 && built[^1].Disposed);

        LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(
                new RecordingControlC(events),
                () =>
                {
                    events.Add("keys built");
                    var recording = new RecordingKeys(inner);
                    built.Add(recording);
                    return recording;
                }),
            new MultiSession(events),
            new StaticClock(),
            runner,
            TrailingIdleFrames: 0));

        Assert.True(runner.Ran, "the command never ran.");
        Assert.True(parentDisposedDuringRun, "the parent source was still live while the command ran.");
    }

    /// <summary>
    /// Two enters, two restores, one each: the screen the run returns to is a
    /// session of its own and is restored like the first. A quit after
    /// returning that leaves the current screen up leaves the reader's
    /// terminal in the alternate screen.
    /// </summary>
    [Fact]
    public void EveryEnteredSessionIsDisposedExactlyOnce()
    {
        var events = new List<string>();
        var sessions = new MultiSession(events);
        var keys = new ScriptedKeys(events);
        foreach (var key in RunRenderKeys())
        {
            keys.Queue(key);
        }

        keys.Queue(Character(' '));
        keys.Queue(Character('q'));

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            sessions,
            new StaticClock(),
            new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, sessions.Entries);
        Assert.All(sessions.Sessions, session => Assert.Equal(1, session.Disposes));
        Assert.DoesNotContain(events, entry => entry.StartsWith("write-after-dispose", StringComparison.Ordinal));
        Assert.Equal(2, events.Count(entry => entry.StartsWith("restore", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A nonzero command status still restores both sessions: the failure is on
    /// the screen, and the terminal is back either way.
    /// </summary>
    [Fact]
    public void NonzeroStatusStillRestoresBothSessions()
    {
        var events = new List<string>();
        var sessions = new MultiSession(events);

        var result = Driven(sessions, events, RunnerStatus: 1);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, sessions.Entries);
        Assert.All(sessions.Sessions, session => Assert.Equal(1, session.Disposes));
        Assert.Contains("exit 1", sessions.Written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A throwing command still restores both sessions and reports the failure.
    /// </summary>
    [Fact]
    public void ThrowingCommandStillRestoresBothSessions()
    {
        var events = new List<string>();
        var sessions = new MultiSession(events);

        var result = Driven(sessions, events, RunnerThrows: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, sessions.Entries);
        Assert.All(sessions.Sessions, session => Assert.Equal(1, session.Disposes));
        Assert.Contains("fell over", sessions.Written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ctrl-C with no command running restores the one entered session.
    /// </summary>
    [Fact]
    public void InterruptRestoresTheSingleSession()
    {
        var events = new List<string>();
        var sessions = new MultiSession(events);
        var keys = new ScriptedKeys(events) { OnWait = sessions.InterruptLatest };

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            sessions,
            new StaticClock(),
            new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, sessions.Entries);
        Assert.Equal(1, sessions.Sessions[0].Disposes);
    }

    /// <summary>
    /// The end of the keys restores the one entered session.
    /// </summary>
    [Fact]
    public void InputEndRestoresTheSingleSession()
    {
        var events = new List<string>();
        var sessions = new MultiSession(events);

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, new ScriptedKeys(events))),
            sessions,
            new StaticClock(),
            new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, sessions.Entries);
        Assert.Equal(1, sessions.Sessions[0].Disposes);
    }

    /// <summary>
    /// A terminal that dies while the command runs ends the run cleanly: no
    /// exception escapes, nothing is written to the dead screen, and the first
    /// session is still restored exactly once.
    /// </summary>
    [Fact]
    public void FailedReentryStopsCleanly()
    {
        var events = new List<string>();
        var sessions = new FailingReentrySession(events);
        var keys = new ScriptedKeys(events);
        foreach (var key in RunRenderKeys())
        {
            keys.Queue(key);
        }

        keys.Queue(Character(' '));
        keys.Queue(Character('q'));

        var exception = Record.Exception(() => LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            sessions,
            new StaticClock(),
            new StubRunner(events),
            TrailingIdleFrames: 0)));

        Assert.Null(exception);
        Assert.Equal(1, sessions.Entries);
        Assert.Equal(1, sessions.First.Disposes);
        Assert.DoesNotContain(events, entry => entry.StartsWith("write-after-dispose", StringComparison.Ordinal));
    }

    /// <summary>
    /// A redirected run builds no key source at all.
    /// </summary>
    [Fact]
    public void RedirectedLaunchpadBuildsNoKeySource()
    {
        var builds = 0;
        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive with { InputRedirected = true },
            new TuiConsole(
                new ThrowingControlC(),
                () =>
                {
                    builds++;
                    return new NeverKeys();
                }),
            new MultiSession([]),
            new StaticClock(),
            new StubRunner([]),
            TrailingIdleFrames: 0));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.Equal(0, builds);
    }

    /// <summary>
    /// A redirected Ledger run opens no artifact file and builds no key source.
    /// The paths name nothing, so any file access would throw rather than refuse.
    /// </summary>
    [Fact]
    public void RedirectedLedgerOpensNoFilesAndBuildsNoKeySource()
    {
        var builds = 0;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = LedgerHost.Run(new LedgerHostRequest(
            [MissingArtifact(), MissingArtifact()],
            stdout,
            stderr,
            Interactive with { InputRedirected = true, OutputRedirected = true },
            new TuiConsole(new ThrowingControlC(), () =>
            {
                builds++;
                return new NeverKeys();
            }),
            new MultiSessionFactory(),
            new StaticClock()));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, builds);
    }

    private static string MissingArtifact() =>
        Path.Combine(Path.GetTempPath(), $"lattice-absent-{Guid.NewGuid():N}.json");

    /// <summary>A run that selects render, types a trajectory, and runs it.</summary>
    private static IReadOnlyList<TuiKey> RunRenderKeys()
    {
        var keys = new List<TuiKey>();
        for (var i = 0; i < 2; i++)
        {
            keys.Add(new TuiKey(TuiKeyKind.Down));
        }

        keys.Add(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "site/demo.jsonl")
        {
            keys.Add(new TuiKey(TuiKeyKind.Character, glyph));
        }

        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        return keys;
    }

    private static TuiRunResult Driven(
        MultiSession sessions,
        List<string> events,
        int RunnerStatus = 0,
        bool RunnerThrows = false)
    {
        var keys = new ScriptedKeys(events);
        foreach (var key in RunRenderKeys())
        {
            keys.Queue(key);
        }

        keys.Queue(Character(' '));
        keys.Queue(Character('q'));

        return LaunchpadHost.Run(new LaunchpadHostRequest(
            LaunchpadCatalog.Commands,
            TextWriter.Null,
            TextWriter.Null,
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            sessions,
            new StaticClock(),
            new StubRunner(events, RunnerStatus, RunnerThrows),
            TrailingIdleFrames: 0));
    }

    private static TuiKey Character(char glyph) => new(TuiKeyKind.Character, glyph);

    private static IKeySource Built(List<string> events, IKeySource keys)
    {
        events.Add("keys built");
        return keys;
    }

    /// <summary>A runner that reports whether the parent source was handed off while it ran.</summary>
    private sealed class ObservingRunner(List<string> events, Func<bool> parentDisposed) : ILaunchpadRunner
    {
        internal bool Ran { get; private set; }

        public int Run(LaunchpadRunRequest request)
        {
            Ran = true;
            if (!parentDisposed())
            {
                events.Add("parent still live");
            }

            return 0;
        }
    }

    private sealed class StubRunner : ILaunchpadRunner
    {
        private readonly List<string> _events;
        private readonly int _status;
        private readonly bool _throws;

        internal StubRunner(List<string> events, int status = 0, bool throws = false)
        {
            _events = events;
            _status = status;
            _throws = throws;
        }

        public int Run(LaunchpadRunRequest request)
        {
            _events.Add("ran");

            if (_throws)
            {
                throw new InvalidOperationException("the command fell over");
            }

            return _status;
        }
    }

    /// <summary>A key source that records its disposal and delegates the waits.</summary>
    private sealed class RecordingKeys(IKeySource inner) : IKeySource
    {
        internal bool Disposed { get; private set; }

        public KeyWait Wait(TimeSpan timeout, out TuiKey key) => inner.Wait(timeout, out key);

        public void Dispose() => Disposed = true;
    }

    private sealed class NeverKeys : IKeySource
    {
        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            key = default;
            return KeyWait.Closed;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingControlC : IControlCAsInput
    {
        internal int Calls { get; private set; }

        public bool Get()
        {
            Calls++;
            throw new IOException("The handle is invalid.");
        }

        public void Set(bool value)
        {
            Calls++;
            throw new IOException("The handle is invalid.");
        }
    }

    private sealed class RecordingControlC(List<string> events) : IControlCAsInput
    {
        private bool _value;

        public bool Get()
        {
            events.Add("get");
            return _value;
        }

        public void Set(bool value)
        {
            events.Add(value ? "set true" : "set false");
            _value = value;
        }
    }

    private sealed class ScriptedKeys(List<string> events) : IKeySource
    {
        private readonly Queue<TuiKey> _keys = new();

        internal Action? OnWait { get; init; }

        internal void Queue(params TuiKey[] keys)
        {
            foreach (var key in keys)
            {
                _keys.Enqueue(key);
            }
        }

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            OnWait?.Invoke();

            if (_keys.Count > 0)
            {
                key = _keys.Dequeue();
                return KeyWait.Key;
            }

            key = default;
            return KeyWait.Closed;
        }

        public void Dispose() => events.Add("keys disposed");
    }

    /// <summary>A terminal factory that mints a distinct session per entry.</summary>
    private class MultiSession(List<string> events) : ITerminalSessionFactory
    {
        private readonly List<TrackedSession> _sessions = new();

        internal IReadOnlyList<TrackedSession> Sessions => _sessions;

        internal int Entries => _sessions.Count;

        internal string Written => string.Concat(_sessions.Select(session => session.Written));

        internal void InterruptLatest() => _sessions[^1].RaiseInterrupt();

        public virtual bool RequestUtf8Output()
        {
            events.Add("utf8");
            return true;
        }

        public virtual ITerminalSession Enter(TextWriter output)
        {
            events.Add("enter");
            var session = new TrackedSession(_sessions.Count, events);
            _sessions.Add(session);
            return session;
        }

        internal sealed class TrackedSession(int id, List<string> events) : ITerminalSession
        {
            private readonly System.Text.StringBuilder _written = new();
            private bool _disposed;

            internal int Disposes { get; private set; }

            internal string Written => _written.ToString();

            public event Action? Interrupted;

            public void Write(string text)
            {
                if (_disposed)
                {
                    events.Add($"write-after-dispose{id}");
                    return;
                }

                _written.Append(text);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Disposes++;
                events.Add($"restore{id}");
            }

            internal void RaiseInterrupt() => Interrupted?.Invoke();
        }
    }

    /// <summary>A terminal factory whose first entry succeeds and whose second throws.</summary>
    private sealed class FailingReentrySession(List<string> events) : MultiSession(events)
    {
        internal TrackedSession First => Sessions[0];

        public override ITerminalSession Enter(TextWriter output)
        {
            if (Entries > 0)
            {
                throw new InvalidOperationException("the terminal went away mid-run");
            }

            return base.Enter(output);
        }
    }

    private sealed class MultiSessionFactory : ITerminalSessionFactory
    {
        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output) => new NullSession();

        private sealed class NullSession : ITerminalSession
        {
            public event Action? Interrupted { add { } remove { } }

            public void Write(string text)
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class StaticClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.Zero;
    }
}
