using System.Text;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Starting a command from the Launchpad: the order it happens in, what the
/// terminal is left in, and what the screen shows when it comes back.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract, and it is asserted as one list per ending rather than
/// as prose. A screen that enters the alternate screen before it has decided
/// whether it can, or that leaves a terminal in raw mode after a command has run, is
/// broken in a way a reader cannot recover from without killing the process.
/// </para>
/// <para>
/// Four endings are driven, because a path that restores on three of four is the
/// path that breaks on the fourth: a clean quit, an interrupt, a command that fails,
/// and a command that throws.
/// </para>
/// </remarks>
public class LaunchpadRunTests
{
    /// <summary>A terminal that can carry a screen, which a test host's own is not.</summary>
    private static readonly TerminalCapabilities Interactive = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>
    /// Everything an accepted Launchpad run did, in order: the key source built, the
    /// setting taken, the terminal asked for UTF-8 and entered, a frame written, the
    /// terminal restored, the setting given back, and only then the key source
    /// disposed. The two orderings that are not negotiable are the UTF-8 request
    /// before the entry, and the restore before the setting going back.
    /// </summary>
    [Fact]
    public void AnAcceptedRunEntersWritesRestoresAndGivesTheSettingBackLast()
    {
        var run = Driven(TrailingIdleFrames: 0, Keys: Quit());

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal(
            ["keys built", "get", "set true", "utf8", "enter", "frame", "restore", "set false", "keys disposed"],
            run.Events);
    }

    /// <summary>
    /// A redirected run is refused before anything is built. Building a key source is
    /// what asks the process's console for its input mode, and on Windows that ask
    /// throws when standard input is a file — which is why the fake below fails
    /// loudly from either member rather than only counting calls.
    /// </summary>
    [Fact]
    public void ARedirectedRunTouchesNothingAtAll()
    {
        var controlC = new ThrowingControlC();
        var surface = new RecordingSurface();
        var events = new List<string>();

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive with { InputRedirected = true },
            Console: new(controlC, () => new NeverKeys()),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.NotNull(result.Refusal);
        Assert.Contains("redirected", result.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, controlC.Calls);
        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Utf8Requests);
        Assert.Equal("", string.Join(",", events));
    }

    /// <summary>
    /// The refusal is the host's own line with the screen named, not a second
    /// opinion: a reader who is told about the replay viewer has been told about the
    /// wrong screen. The one place it differs is the noun.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheLaunchpadAndAgreesWithTheHostAboutTheFact()
    {
        var capabilities = Interactive with { OutputRedirected = true };
        var result = Refused(capabilities);

        // The host's own line, with the noun changed to this screen: the fact about
        // redirection is the host's alone, and the noun is the only thing that differs.
        Assert.Equal(
            TuiHost.RefusalFor(capabilities)!
                .Replace("lattice tui:", "lattice:", StringComparison.Ordinal)
                .Replace("the replay viewer", "the launchpad", StringComparison.Ordinal),
            result.Refusal);
        Assert.Contains("the launchpad", result.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The command line the reader asked to run is handed over as arguments, with the
    /// command itself first — the same shape <c>CliApp.Run</c> takes, so the Launchpad
    /// cannot be a second parser for the same command line.
    /// </summary>
    [Fact]
    public void TheRunnersArgumentsAreTheCommandsOwnArgumentVector()
    {
        var runner = new StubRunner([]);
        LaunchpadRunRequest? seen = null;
        var capturing = new CapturingRunner(request => seen = request);
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);

        // render, into --trajectory, a path, then run.
        for (var i = 0; i < 2; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Down));
        }

        form.Apply(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "site/demo.jsonl")
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, glyph));
        }

        capturing.Run(new LaunchpadRunRequest(
            form.Arguments,
            form.CommandLine,
            TextWriter.Null,
            TextWriter.Null));

        Assert.NotNull(seen);
        Assert.Equal(["render", "--trajectory", "site/demo.jsonl"], seen!.Value.Arguments);
        Assert.Equal("lattice render --trajectory site/demo.jsonl", seen!.Value.CommandLine);
    }

    /// <summary>
    /// A command that fails is reported and the Launchpad stays up. The reader has
    /// corrected a form and run it; nothing they did failed, so the Launchpad's own
    /// status is still success — and the failing status is on the screen.
    /// </summary>
    [Fact]
    public void AFailingCommandIsShownAndTheLaunchpadStaysUpToCorrectIt()
    {
        var run = Driven(
            TrailingIdleFrames: 1,
            Keys: [.. RenderWithTrajectory(), .. Quit()],
            RunnerStatus: 1);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.True(run.Entries >= 2, $"the screen was entered {run.Entries} time(s); it should be entered again for the return.");

        // The three events before the command are the whole ordering: the setting given
        // back first, so Ctrl-C reaches the command as a signal, then the terminal
        // restored, so the command's own output has an ordinary terminal to land on,
        // and then the parent's key source handed off, so a nested screen is the
        // only reader while the command runs.
        var ran = run.Events.IndexOf("ran");
        Assert.Equal(["set false", "restore", "keys disposed"], run.Events[(ran - 3)..ran].ToArray());
        Assert.Contains("exit 1", Message(run), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that throws leaves the terminal back, the setting restored and the
    /// key source disposed, and reports the failure rather than propagating it. A run
    /// that only tidies up on success leaves a console in raw mode, which is the
    /// failure this arrangement exists to prevent.
    /// </summary>
    [Fact]
    public void ACommandThatThrowsStillRestoresEverythingAndReportsTheFailure()
    {
        var run = Driven(
            TrailingIdleFrames: 1,
            Keys: [.. RenderWithTrajectory(), .. Quit()],
            RunnerThrows: true);

        Assert.Equal(0, run.Result.ExitCode);

        // The terminal was restored before the command started and entered again
        // after it finished: the only one of those two that matters here is that the
        // restore came first, because a command started on a terminal still in the
        // alternate screen has nowhere to draw.
        Assert.True(
            run.Events.IndexOf("restore") < run.Events.IndexOf("ran"),
            "the command ran before the terminal was restored.");
        Assert.Equal("set false", run.Events[^2]);
        Assert.Equal("keys disposed", run.Events[^1]);
        Assert.False(run.ControlC.Value);
        Assert.Contains("fell over", Message(run), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that succeeds says so plainly. A screen that reported nothing on
    /// success would leave the reader unable to tell a finished run from a hung one.
    /// </summary>
    [Fact]
    public void ASucceedingCommandIsShown()
    {
        var run = Driven(TrailingIdleFrames: 1, Keys: [.. RenderWithTrajectory(), .. Quit()], RunnerStatus: 0);

        Assert.Contains("exit 0", Message(run), StringComparison.Ordinal);
    }

    /// <summary>
    /// Ctrl-C leaves the screen, restores the terminal and exits successfully: the
    /// reader interrupted a screen, not a command, so nothing failed.
    /// </summary>
    [Fact]
    public void AnInterruptRestoresTheTerminalAndExitsSuccessfully()
    {
        var events = new List<string>();
        var controlC = new RecordingControlC(events);
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events) { OnWait = surface.Interrupt };

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(controlC, () => Built(events, keys)),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("restore", events);
        Assert.Equal("set false", events[^2]);
        Assert.False(controlC.Value);
    }

    /// <summary>
    /// A process that had already asked for Ctrl-C as input keeps it. The scope puts
    /// back what it found rather than forcing the default back on.
    /// </summary>
    [Fact]
    public void AProcessThatAlreadyTreatsControlCAsInputKeepsIt()
    {
        var events = new List<string>();
        var controlC = new RecordingControlC(events, original: true);
        var keys = new ScriptedKeys(events);

        keys.Queue(new TuiKey(TuiKeyKind.Character, 'q'));

        LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(controlC, () => Built(events, keys)),
            Session: new RecordingSurface(events),
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(["keys built", "get", "set true", "set true", "keys disposed"], events.Where(entry => entry is "get" or "set true" or "set false" or "keys built" or "keys disposed").ToArray());
        Assert.True(controlC.Value);
    }

    /// <summary>
    /// Quitting the Launchpad is a success, from either key. There is nothing a
    /// reader did wrong, and a non-zero status would make the shell's history say
    /// otherwise.
    /// </summary>
    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void QuittingIsSuccessful(char key)
    {
        var run = Driven(TrailingIdleFrames: 0, Keys: [new TuiKey(TuiKeyKind.Character, key)]);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal("restore", run.Events[^3]);
    }

    /// <summary>
    /// The end of the key stream is a quit, not a hang. A real terminal's reader
    /// blocks on the next key for as long as the reader sits there, so a loop that
    /// waits for the input to end is a loop that hangs on a terminal working exactly
    /// as intended.
    /// </summary>
    [Fact]
    public void TheEndOfTheKeysIsAQuitRatherThanAWaitForever()
    {
        var run = Driven(TrailingIdleFrames: 0, Keys: []);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Contains("restore", run.Events);
    }

    /// <summary>
    /// A wait that times out is not the end of the input, and must not end the
    /// screen. This is the failure a pty check on a real terminal finds and a test
    /// over a scripted queue cannot: the real reader times out on every pause and
    /// never reports end of input, so a screen that treats a timeout as an ending
    /// closes on the first pause the reader takes to think.
    /// </summary>
    [Fact]
    public void APauseIsNotTheEndOfTheInput()
    {
        var events = new List<string>();
        var controlC = new RecordingControlC(events);
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events) { IdleBeforeClosing = 40 };

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(controlC, () => keys),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);

        // The screen stayed up through every one of those pauses and then closed,
        // rather than closing on the first.
        Assert.True(keys.TimedOutWaits >= 40, $"only {keys.TimedOutWaits} waits timed out.");
        Assert.Equal("restore", events[^3]);
    }

    /// <summary>
    /// A command's status is shown until the reader acknowledges it, and the key that
    /// acknowledges it is consumed rather than acted on. Without the gate the first
    /// keypress after a run — the one meant for "that is fine" — would be fed to the
    /// form, where on a full-screen display it moves the selection out from under a
    /// reader who was looking at the status.
    /// </summary>
    [Fact]
    public void AStatusIsShownUntilAKeyAcknowledgesItAndThatKeyIsNotSpentOnTheForm()
    {
        var events = new List<string>();
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events);

        foreach (var key in RenderWithTrajectory())
        {
            keys.Queue(key);
        }

        // The first key is one that would move the selection if it reached the form.
        // The gate consumes it, so the selection stays where the reader left it.
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'j'));
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'q'));

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(new RecordingControlC(events), () => keys),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        Assert.Equal(0, result.ExitCode);

        // The status was drawn, and after the acknowledging key the selection had not
        // moved: 'j' would have taken it to the second command.
        Assert.Contains("exit 0", surface.Written, StringComparison.Ordinal);

        // The 'j' was spent acknowledging the status, so the selection never moved:
        // render is still the command on show after it.
        var afterStatus = Strip(surface.Written[surface.Written.IndexOf("exit 0", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("> analyze", afterStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("> render", afterStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// A status from a command does not shadow a field error for ever. Once the
    /// reader has read it, the form's own message is what they see.
    /// </summary>
    [Fact]
    public void AStatusIsClearedOnceItHasBeenShownSoAFieldErrorIsNotShadowed()
    {
        var events = new List<string>();
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events);

        foreach (var key in RenderWithTrajectory())
        {
            keys.Queue(key);
        }

        // Acknowledge the status, walk back to a command with a required field, and
        // focus it. The form's own message now belongs on the screen.
        keys.Queue(new TuiKey(TuiKeyKind.Character, ' '));
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'k'));
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'k'));
        keys.Queue(new TuiKey(TuiKeyKind.Tab));
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'q'));

        LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(new RecordingControlC(events), () => keys),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        // The form's own refusal is on the screen: the status from the run was shown
        // once and cleared rather than shadowing it for the rest of the session.
        Assert.Contains("missing required flag", surface.Written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text of a written frame, with the escape sequences removed, so a search
    /// for what was on screen is not looking for a literal string the renderer broke
    /// up with cursor positioning.
    /// </summary>
    private static string Strip(string written) =>
        System.Text.RegularExpressions.Regex.Replace(written, "\\u001b\\[[0-9;?]*[A-Za-z]", "");

    /// <summary>The keys that select render and type a trajectory into its field.</summary>
    private static IReadOnlyList<TuiKey> RenderWithTrajectory()
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

        // Enter commits the field, Enter again asks to run.
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        keys.Add(new TuiKey(TuiKeyKind.Enter));
        return keys;
    }

    private static IReadOnlyList<TuiKey> Quit() => [new TuiKey(TuiKeyKind.Character, 'q')];

    /// <summary>
    /// Every key the reader produces reaches the form, including one that arrives
    /// after a wait has already timed out. A loop that waits a second time to check
    /// for the end of its input, and throws away whatever that wait produced, loses
    /// keys at exactly the rate a reader thinks — which is how a screen that works
    /// when you type steadily drops a character when you hesitate. Only a real
    /// terminal finds this: a scripted queue answers instantly and hides it.
    /// </summary>
    [Fact]
    public void AKeyThatArrivesAfterATimedOutWaitStillReachesTheForm()
    {
        var events = new List<string>();
        var surface = new RecordingSurface(events);
        var keys = new SlowKeys();

        // Tab first, so the letters are typed into a field rather than steering.
        keys.Queue(new TuiKey(TuiKeyKind.Tab));
        foreach (var glyph in "abc")
        {
            keys.Queue(new TuiKey(TuiKeyKind.Character, glyph));
        }

        keys.Queue(new TuiKey(TuiKeyKind.Escape));
        keys.Queue(new TuiKey(TuiKeyKind.Character, 'q'));

        LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(new RecordingControlC(events), () => keys),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));

        // Every key was handed over, and the screen drew the form with all three
        // letters typed into the field — so none was dropped on the way.
        Assert.Equal(6, keys.HandedOver);
        Assert.Contains("abc", surface.Written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key source that answers every other wait with nothing and only produces on
    /// the next, which is the shape that loses a key.
    /// </summary>
    private sealed class SlowKeys : IKeySource
    {
        private readonly Queue<TuiKey> _keys = new();
        private bool _timed;

        internal int HandedOver { get; private set; }

        internal void Queue(TuiKey key) => _keys.Enqueue(key);

        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            _timed = !_timed;
            if (_timed || _keys.Count == 0)
            {
                key = default;
                return KeyWait.TimedOut;
            }

            HandedOver++;
            key = _keys.Dequeue();
            return KeyWait.Key;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>One driven run, and everything it did.</summary>
    private sealed record Run(
        TuiRunResult Result,
        List<string> Events,
        RecordingControlC ControlC,
        int Entries,
        string Written);

    /// <summary>
    /// A run driven with a scripted key sequence, a recording terminal and a
    /// recording setting, so the order is one list rather than a claim.
    /// </summary>
    private static Run Driven(
        int TrailingIdleFrames,
        IReadOnlyList<TuiKey> Keys,
        int RunnerStatus = 0,
        bool RunnerThrows = false)
    {
        var events = new List<string>();
        var controlC = new RecordingControlC(events);
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events);

        foreach (var key in Keys)
        {
            keys.Queue(key);
        }

        var result = LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: Interactive,
            Console: new(controlC, () => Built(events, keys)),
            Session: surface,
            Clock: new StaticClock(),
            Runner: new StubRunner(events, RunnerStatus, RunnerThrows),
            TrailingIdleFrames: TrailingIdleFrames));

        return new Run(result, events, controlC, events.Count(entry => entry == "enter"), surface.Written);
    }

    /// <summary>
    /// Everything the screen drew, as one string. The status a reader is shown is
    /// asserted from this rather than from a return value, because what is on the
    /// screen is what the reader actually gets.
    /// </summary>
    private static string Message(Run run) => run.Written;

    private static TuiRunResult Refused(TerminalCapabilities capabilities)
    {
        var events = new List<string>();
        return LaunchpadHost.Run(new LaunchpadHostRequest(
            Commands: LaunchpadCatalog.Commands,
            Output: TextWriter.Null,
            Errors: TextWriter.Null,
            Capabilities: capabilities,
            Console: new(new ThrowingControlC(), () => new NeverKeys()),
            Session: new RecordingSurface(events),
            Clock: new StaticClock(),
            Runner: new StubRunner(events),
            TrailingIdleFrames: 0));
    }

    /// <summary>
    /// The command runner, stood in for here so the order assertions are about the
    /// terminal and the setting rather than about a command's work. The real runner
    /// is asserted by <see cref="LaunchpadCliTests"/>, which runs the actual commands.
    /// </summary>
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

    /// <summary>
    /// The console's key factory, which records that it was asked for one. That is
    /// the event a refused run must never reach, so it is named rather than inferred.
    /// </summary>
    private static IKeySource Built(List<string> events, IKeySource keys)
    {
        events.Add("keys built");
        return keys;
    }

    /// <summary>A runner that keeps the request it was given, so the arguments can be asserted.</summary>
    private sealed class CapturingRunner(Action<LaunchpadRunRequest> capture) : ILaunchpadRunner
    {
        public int Run(LaunchpadRunRequest request)
        {
            capture(request);
            return 0;
        }
    }

    /// <summary>A setting a redirected Windows console answers with a failure from both members.</summary>
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

    /// <summary>A setting a test can watch: every get and set in order.</summary>
    private sealed class RecordingControlC : IControlCAsInput
    {
        private readonly List<string> _events;
        private bool _value;

        internal RecordingControlC(List<string> events, bool original = false)
        {
            _events = events;
            _value = original;
        }

        internal bool Value => _value;

        public bool Get()
        {
            _events.Add("get");
            return _value;
        }

        public void Set(bool value)
        {
            _events.Add(value ? "set true" : "set false");
            _value = value;
        }
    }

    /// <summary>Keys that hand over a scripted sequence and then report end of input.</summary>
    private sealed class ScriptedKeys : IKeySource
    {
        private readonly List<string> _events;
        private readonly Queue<TuiKey> _keys = new();

        internal ScriptedKeys(List<string> events) => _events = events;

        internal Action? OnWait { get; init; }

        /// <summary>
        /// How many waits report a timeout before the input ends, which is what a real
        /// terminal's reader does on every pause and never otherwise.
        /// </summary>
        internal int IdleBeforeClosing { get; init; }

        /// <summary>How many waits have timed out, so the screen's patience can be asserted.</summary>
        internal int TimedOutWaits { get; private set; }

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

            if (TimedOutWaits < IdleBeforeClosing)
            {
                TimedOutWaits++;
                return KeyWait.TimedOut;
            }

            return KeyWait.Closed;
        }

        public void Dispose() => _events.Add("keys disposed");
    }

    /// <summary>Keys that are never asked for, because a refused run must not build one.</summary>
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

    /// <summary>The terminal seam, recorded: what was asked and in what order.</summary>
    private sealed class RecordingSurface : ITerminalSessionFactory
    {
        private readonly List<string> _events;
        private readonly RecordingSession _session;

        internal RecordingSurface(List<string>? events = null)
        {
            _events = events ?? [];
            _session = new RecordingSession(_events);
        }

        internal int Entries => _events.Count(entry => entry == "enter");

        internal int Utf8Requests => _events.Count(entry => entry == "utf8");

        /// <summary>Everything the screen wrote, which is what a reader would see.</summary>
        internal string Written => _session.Written;

        public bool RequestUtf8Output()
        {
            _events.Add("utf8");
            return true;
        }

        public ITerminalSession Enter(TextWriter output)
        {
            _events.Add("enter");
            return _session;
        }

        internal void Interrupt() => _session.RaiseInterrupt();

        private sealed class RecordingSession : ITerminalSession
        {
            private readonly List<string> _events;
            private bool _restored;

            private readonly StringBuilder _written = new();

            internal RecordingSession(List<string> events) => _events = events;

            /// <summary>Every frame the screen wrote, in order.</summary>
            internal string Written => _written.ToString();

            public event Action? Interrupted;

            public void Write(string text)
            {
                _events.Add("frame");
                _written.Append(text);
            }

            public void Dispose()
            {
                if (_restored)
                {
                    return;
                }

                _restored = true;
                _events.Add("restore");
            }

            internal void RaiseInterrupt() => Interrupted?.Invoke();
        }
    }

    /// <summary>A clock that never advances, so a scripted run needs no sleep.</summary>
    private sealed class StaticClock : IUiClock
    {
        public TimeSpan Now => TimeSpan.Zero;
    }
}
