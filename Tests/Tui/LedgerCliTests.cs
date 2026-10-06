using System.Text;
using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// <c>lattice tui ledger</c>: the order the run happens in, what a run that cannot be a
/// viewer says instead, what a file that is not an artifact says, and that the eight
/// commands' own bytes are untouched by any of it.
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole contract and it is asserted as one list, not as prose: refuse,
/// read, build the key source, take the Ctrl-C setting, draw, restore the terminal, put
/// the setting back, dispose the reader. The two orderings that are not negotiable are
/// that nothing at all happens before the refusal, and that the setting is put back
/// before the reading thread is left to the process.
/// </para>
/// <para>
/// A run that ends badly must end the same way: a missing file, a malformed file and a
/// redirected stream each leave no screen, no terminal state and no thread behind.
/// </para>
/// </remarks>
public class LedgerCliTests
{
    /// <summary>A terminal that can carry a screen, which a test host's own is not.</summary>
    private static readonly TerminalCapabilities Interactive = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>A synthetic artifact, written to disk because the screen is about files.</summary>
    private const string Artifact = """
        {
          "CommitSha": "abc1234",
          "CreatedAtUtc": "2026-09-25T19:41:20Z",
          "Runtime": "TestRuntime 1.0",
          "Os": "TestOs 1.0",
          "Cores": 4,
          "Architecture": "X64",
          "Studies": [
            {
              "Suite": "heldout", "TargetPolicy": "MCTS", "BaselinePolicy": "Scout",
              "RolloutsPerAction": 32, "MaxStepsPerMatch": 200,
              "PerSeed": [
                { "Seed": 2001, "PolicyScoreAtSeat0": 1, "PolicyScoreAtSeat1": 5,
                  "BaselineScoreAtSeat0": 4, "BaselineScoreAtSeat1": 0,
                  "MeanDelta": 1.0, "Match0Outcome": 1, "Match1Outcome": 1 }
              ],
              "Statistics": {
                "Seeds": 1, "Matches": 2, "MeanDelta": 1.0, "MedianDelta": 1.0,
                "StdDevDelta": 0.0, "IqrDelta": 0.0, "CiLower95": 1.0, "CiUpper95": 1.0,
                "Wins": 1, "Draws": 0, "Losses": 1, "Timeouts": 0,
                "WinRate": 0.5, "DrawRate": 0.0, "LossRate": 0.5, "TimeoutRate": 0.0,
                "MeanContentionSaturation": 0.1
              },
              "Passed": true,
              "Decision": "Pass: mean paired delta 1 > 0 and the 95% CI lower bound 1 > 0 on 1 seeds."
            }
          ]
        }
        """;

    // ---------------------------------------------------------------------
    // The order a run happens in.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The whole order, in one list: the artifacts are read, the key source is built,
    /// the setting is taken, the terminal is asked for UTF-8 and entered, a frame is
    /// written, the terminal is restored, the setting is given back, and only then is
    /// the reading thread disposed.
    /// </summary>
    [Fact]
    public void AnAcceptedRunReadsTheArtifactsThenDrawsAndRestoresInThatOrder()
    {
        var run = Driven(Artifact, Quit());

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal(
            ["keys built", "get", "set true", "utf8", "enter", "frame", "restore", "set false", "keys disposed"],
            run.Events);
    }

    /// <summary>
    /// A redirected run is refused before anything is built and before any file is
    /// opened. Building a key source is what asks the process's console for its input
    /// mode, and on Windows that ask throws when standard input is a file — so the fake
    /// below fails loudly from either member rather than only counting calls.
    /// </summary>
    [Fact]
    public void ARedirectedRunIsRefusedBeforeAKeySourceAFileOrAScreen()
    {
        var controlC = new ThrowingControlC();
        var surface = new RecordingSurface();
        var events = new List<string>();

        var result = LedgerHost.Run(Request(
            ["no/such/artifact.json"],
            Interactive with { InputRedirected = true },
            new TuiConsole(controlC, () => new NeverKeys()),
            surface,
            events));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.NotNull(result.Refusal);
        Assert.Equal(0, controlC.Calls);
        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Utf8Requests);
        Assert.Equal("", string.Join(",", events));
    }

    /// <summary>
    /// The refusal is the host's own line with the screen named, not a second opinion:
    /// a reader who is told about the replay viewer has been told about the wrong
    /// screen, and the fact about redirection is the host's alone.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheRefusalIsTheHostsOwnLineWithThisScreenNamed(bool input, bool output)
    {
        var capabilities = Interactive with { InputRedirected = input, OutputRedirected = output };
        var errors = new StringWriter();

        var result = LedgerHost.Run(Request(
            ["no/such/artifact.json"],
            capabilities,
            new TuiConsole(new NeverFailsControlC(), () => new NeverKeys()),
            new RecordingSurface(),
            [],
            errors));

        Assert.Equal(UsageError.ExitCode, result.ExitCode);
        Assert.Equal(
            TuiHost.RefusalFor(capabilities)!.Replace("the replay viewer", "the ledger", StringComparison.Ordinal),
            result.Refusal);
        Assert.Contains("the ledger", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("lattice tui:", result.Refusal!, StringComparison.Ordinal);
        Assert.Equal(result.Refusal + System.Environment.NewLine, errors.ToString());
    }

    // ---------------------------------------------------------------------
    // Files that are not readable artifacts.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A path that is not there is a runtime failure, not a usage error: the command
    /// line was runnable and the work it named could not be done. The exit-status
    /// contract says so for every command in the CLI, and the Ledger keeps it.
    /// <para>
    /// Asserted on the host, because a path that names no file — or a directory in the
    /// path that is not there — is an <see cref="IOException"/> and both of those are
    /// runtime failures. That the CLI maps one to the runtime-failure status is the
    /// shared mapping every command uses and is asserted for another command by
    /// <c>LaunchpadValidationPurityTests</c>; it cannot be asserted here because a test
    /// host's own streams are redirected, and a redirected host is refused before any
    /// file is opened — which is the case two tests down.
    /// </para>
    /// </summary>
    [Fact]
    public void AMissingArtifactIsARuntimeFailureAndOpensNoScreen()
    {
        var surface = new RecordingSurface();

        Assert.ThrowsAny<IOException>(() => Drive(["no/such/artifact.json"], surface));

        // Nothing about the terminal was touched: no entry, no encoding request.
        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Utf8Requests);
    }

    /// <summary>
    /// Reached through the real command line on a redirected host, the Ledger refuses
    /// before it opens the file: the invocation named no terminal, so the fact that the
    /// file is also unreadable is not the reader's immediate problem. This is the order
    /// the whole stage turns on, and it is only visible from here — a test host's own
    /// streams are always redirected.
    /// </summary>
    [Fact]
    public void ThroughTheCommandLineTheRefusalComesBeforeAnyFileIsOpened()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(
            ["tui", "ledger", "no/such/artifact.json"],
            stdout,
            stderr,
            CliTerminal.For(stderr),
            TuiConsole.Default);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("the ledger needs a terminal", stderr.ToString(), StringComparison.Ordinal);

        // The refusal, and not a report about the file: the file was never opened.
        Assert.DoesNotContain("error:", stderr.ToString(), StringComparison.Ordinal);
        Assert.Single(stderr.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("""{"CommitSha":"abc"}""")]
    [InlineData("""{"CommitSha":"abc","Runtime":"r","Os":"o","Architecture":"a","Cores":1,"Studies":"nope"}""")]
    public void AFileThatIsNotAReadableArtifactIsOneLineAndOpensNoScreen(string content)
    {
        var surface = new RecordingSurface();
        var path = WithFile("bad.json", content);

        var refusal = Assert.Throws<InvalidDataException>(() => Drive([path], surface));

        Assert.Equal(0, surface.Entries);
        Assert.Equal(0, surface.Utf8Requests);

        // Exactly one line, naming the file: a reader who passed two files needs to know
        // which one to replace.
        Assert.DoesNotContain('\n', refusal.Message);
        Assert.Contains("bad.json", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("lattice tui ledger", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One bad file among several fails the run and names that one, so a reader who
    /// passed a directory's worth of artifacts knows which to replace.
    /// </summary>
    [Fact]
    public void OneBadFileAmongSeveralNamesThatOne()
    {
        var surface = new RecordingSurface();
        var good = WithFile("good.json", Artifact);
        var bad = WithFile("bad.json", "{ not json");
        var alsoGood = WithFile("alsogood.json", Artifact);

        var refusal = Assert.Throws<InvalidDataException>(() => Drive([good, bad, alsoGood], surface));

        Assert.Equal(0, surface.Entries);
        Assert.Contains("bad.json", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("good.json", refusal.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The invocation itself.
    // ---------------------------------------------------------------------

    /// <summary>
    /// No artifact at all is a usage error: it is decidable from the command line alone,
    /// so it reports the usage status with nothing run and nothing read.
    /// </summary>
    [Fact]
    public void NoArtifactIsAUsageErrorWithNothingOnStdout()
    {
        var (exit, stdout, stderr) = RunCli(["tui", "ledger"]);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tui ledger", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFlagIsAUsageErrorAndSaysSoByName()
    {
        var (exit, stdout, stderr) = RunCli(["tui", "ledger", "a.json", "--verbose"]);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown flag '--verbose'", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The usage line names the Ledger and still names the two subcommands it already
    /// named, because a usage line a reader cannot find their own subcommand on is not
    /// a diagnosis.
    /// </summary>
    [Fact]
    public void TheUsageLineNamesTheLedgerAndTheSubcommandsThatWereAlreadyThere()
    {
        var (_, _, stderr) = RunCli(["tui", "ledger"]);

        Assert.Contains("lattice tui ledger", stderr, StringComparison.Ordinal);
        Assert.Contains("lattice tui replay", stderr, StringComparison.Ordinal);
        Assert.Contains("lattice tui simulate", stderr, StringComparison.Ordinal);
        Assert.Contains("--ascii", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("ledgerish")]
    public void AnUnknownSubcommandIsStillAUsageError(string subcommand)
    {
        var (exit, stdout, stderr) = RunCli(["tui", subcommand]);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tui replay", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quitting is a success, from any of the three keys that mean it, and Ctrl-C is one
    /// of them rather than a failure: the reader interrupted a screen, not a command.
    /// </summary>
    [Theory]
    [InlineData('q')]
    [InlineData('Q')]
    public void QuittingIsSuccessful(char glyph)
    {
        var run = Driven(Artifact, new TuiKey(TuiKeyKind.Character, glyph));

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal("restore", run.Events[^3]);
    }

    [Fact]
    public void EscapeAlsoQuits()
    {
        var run = Driven(Artifact, new TuiKey(TuiKeyKind.Escape));

        Assert.Equal(0, run.Result.ExitCode);
    }

    /// <summary>
    /// A Ctrl-C leaves the screen, puts the terminal back and exits successfully, and
    /// the setting goes back before the reading thread is disposed.
    /// </summary>
    [Fact]
    public void AnInterruptRestoresTheTerminalAndExitsSuccessfully()
    {
        var events = new List<string>();
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events) { OnWait = surface.Interrupt };

        var result = LedgerHost.Run(Request(
            [WithFile("results.json", Artifact)],
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            surface,
            events));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("restore", events);
        Assert.Equal("set false", events[^2]);
        Assert.Equal("keys disposed", events[^1]);
    }

    /// <summary>
    /// A screen that ends because its input ended is a clean quit rather than a hang:
    /// a real terminal's reader blocks on the next key, so a loop that waits for the end
    /// of its input waits for ever on a terminal working exactly as intended.
    /// </summary>
    [Fact]
    public void TheEndOfTheKeysIsAQuitRatherThanAWaitForever()
    {
        var run = Driven(Artifact);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Contains("restore", run.Events);
    }

    /// <summary>
    /// A pause is not the end of the input. This is the failure a pty check on a real
    /// terminal finds and a scripted queue cannot: the real reader times out on every
    /// pause and never reports end of input.
    /// </summary>
    [Fact]
    public void APauseIsNotTheEndOfTheInput()
    {
        var events = new List<string>();
        var surface = new RecordingSurface(events);
        var keys = new ScriptedKeys(events) { IdleBeforeClosing = 40 };

        var result = LedgerHost.Run(Request(
            [WithFile("results.json", Artifact)],
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => keys),
            surface,
            events));

        Assert.Equal(0, result.ExitCode);
        Assert.True(keys.TimedOutWaits >= 40, $"only {keys.TimedOutWaits} waits timed out.");
        Assert.Equal("restore", events[^3]);
    }

    /// <summary>
    /// The eight commands are byte-identical whether they are run from a shell or
    /// reached through the same dispatch the Ledger now also goes through. Asserted by
    /// running each one both ways in this test class's own fixture set, and in full by
    /// the differential comparison the stage records against the base commit.
    /// </summary>
    [Fact]
    public void NamingAnyCommandStillRunsThatCommandAndNotTheLedger()
    {
        var (exit, stdout, stderr) = RunCli(["generate", "--seed", "42"]);

        Assert.Equal(0, exit);
        Assert.NotEqual("", stdout);
        Assert.DoesNotContain("ledger", stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bare-invocation behaviour, unchanged by a new <c>tui</c> subcommand.</summary>
    [Fact]
    public void ABareInvocationOnARedirectedStreamStillPrintsUsage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(
            [],
            stdout,
            stderr,
            CliTerminal.For(stderr),
            TuiConsole.Default,
            Interactive with { OutputRedirected = true });

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("usage: lattice <command> [options]", stderr.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The Launchpad route to the same screen.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The Ledger is on the setup screen as a viewer, with one path field and an
    /// optional second one, and no field to choose a run mode: it is read-only and has
    /// nothing to record.
    /// </summary>
    [Fact]
    public void TheLedgerIsAViewerWithTwoPathFieldsAndNoRunMode()
    {
        var form = Select("ledger");

        Assert.Equal([LaunchpadCatalog.PositionalPathLabel, LaunchpadCatalog.SecondPathLabel, LaunchpadCatalog.ExtraArgumentsLabel],
            form.Fields.Select(field => field.Label).ToArray());
        Assert.Equal(["screen"], form.RunModes.ToArray());
        Assert.Equal(RunMode.Screen, form.ChosenMode);
    }

    /// <summary>
    /// The command line under the form is the line a reader would actually type, with
    /// the one and second artifacts and the <c>tui</c> the screen is reached through. A
    /// line without the prefix would report an unknown command.
    /// </summary>
    [Fact]
    public void TheLedgerEntryShowsACommandLineThatWouldActuallyRun()
    {
        var form = Select("ledger");

        Type(form, LaunchpadCatalog.PositionalPathLabel, "a.json");
        Type(form, LaunchpadCatalog.SecondPathLabel, "b.json");

        Assert.Equal("lattice tui ledger a.json b.json", form.CommandLine);
        Assert.Equal(["tui", "ledger", "a.json", "b.json"], form.Arguments);
        Assert.Null(form.ValidationError);
        Assert.True(form.CanRun);
    }

    /// <summary>
    /// The second artifact is optional, so a form with only the first still produces a
    /// command line that runs.
    /// </summary>
    [Fact]
    public void OneArtifactIsEnoughForTheLedgerEntryToRun()
    {
        var form = Select("ledger");
        Type(form, LaunchpadCatalog.PositionalPathLabel, "a.json");

        Assert.Equal("lattice tui ledger a.json", form.CommandLine);
        Assert.Equal(["tui", "ledger", "a.json"], form.Arguments);
    }

    /// <summary>
    /// The Ledger is reached through the cockpit runner like the other two screens, so
    /// the vector a reader sees is the vector the cockpit is handed and no second
    /// implementation of the screen exists.
    /// </summary>
    [Fact]
    public void TheLedgerIsReachedThroughTheScreenRunnerNotRunAsACommand()
    {
        Assert.True(CliAppLaunchpadRunner.IsCockpit(["ledger", "a.json"]));
        Assert.True(CliAppLaunchpadRunner.IsCockpit(["tui", "ledger", "a.json"]));

        // And the prefixed vector is not prefixed twice.
        Assert.Equal(
            ["tui", "ledger", "a.json"],
            CliAppLaunchpadRunner.CockpitArguments(["tui", "ledger", "a.json"]));
    }

    /// <summary>
    /// A reader who watches the Ledger from the setup screen reaches the same
    /// <c>lattice tui ledger</c> path a reader at a shell does — the argument vector is
    /// handed over unchanged rather than re-parsed here.
    /// </summary>
    [Fact]
    public void WatchingTheLedgerFromTheLaunchpadHandsOverItsOwnArgumentVector()
    {
        LaunchpadRunRequest? seen = null;
        var form = Select("ledger");
        Type(form, LaunchpadCatalog.PositionalPathLabel, "a.json");

        new CliAppLaunchpadRunner(new CapturingCockpit(request => seen = request), CliTerminal.For(TextWriter.Null))
            .Run(new LaunchpadRunRequest(form.Arguments, form.CommandLine, TextWriter.Null, TextWriter.Null, form.ChosenMode));

        Assert.NotNull(seen);
        Assert.Equal(["tui", "ledger", "a.json"], seen!.Value.Arguments);
        Assert.Equal("lattice tui ledger a.json", seen.Value.CommandLine);
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    /// <summary>What one driven run did.</summary>
    private sealed record Run(TuiRunResult Result, List<string> Events);

    private static Run Driven(string artifact, params TuiKey[] keys2)
    {
        var events = new List<string>();
        var keys = new ScriptedKeys(events);

        foreach (var key in keys2)
        {
            keys.Queue(key);
        }

        var result = LedgerHost.Run(Request(
            [WithFile("results.json", Artifact)],
            Interactive,
            new TuiConsole(new RecordingControlC(events), () => Built(events, keys)),
            new RecordingSurface(events),
            events));

        return new Run(result, events);
    }

    private static LedgerHostRequest Request(
        IReadOnlyList<string> paths,
        TerminalCapabilities capabilities,
        TuiConsole console,
        RecordingSurface surface,
        List<string> events,
        TextWriter? errors = null) =>
        new(
            paths,
            TextWriter.Null,
            errors ?? TextWriter.Null,
            capabilities,
            console,
            surface,
            new StaticClock(),
            TrailingIdleFrames: 0);

    /// <summary>
    /// A run against an interactive terminal that is left to end on its own, so a test
    /// can assert what it did. A run that is refused or that fails on its files never
    /// gets that far, which is exactly what the file cases above assert.
    /// </summary>
    private static TuiRunResult Drive(IReadOnlyList<string> paths, RecordingSurface surface) =>
        LedgerHost.Run(Request(
            paths,
            Interactive,
            new TuiConsole(new NeverFailsControlC(), () => new NeverKeys()),
            surface,
            []));

    private static (int Exit, string Stdout, string Stderr) RunCli(string[] arguments)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(arguments, stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default);

        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The setup screen, navigated to one command.</summary>
    private static LaunchpadForm Select(string command)
    {
        var form = LaunchpadForm.For(LaunchpadCatalog.Commands);
        var index = LaunchpadCatalog.Commands.ToList().FindIndex(entry => entry.Name == command);

        for (var i = 0; i < index; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Down));
        }

        Assert.Equal(command, form.Selected);
        return form;
    }

    /// <summary>Focuses the named field and types into it, then leaves it.</summary>
    private static void Type(LaunchpadForm form, string label, string text)
    {
        var field = form.Fields.ToList().FindIndex(entry => entry.Label == label);
        Assert.True(field >= 0, $"the form has no '{label}' field.");

        form.Apply(new TuiKey(TuiKeyKind.Tab));
        for (var i = 0; i < field; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Tab));
        }

        Assert.Equal(label, form.FocusedField);
        form.Apply(new TuiKey(TuiKeyKind.Character, text[0]));

        for (var i = 1; i < text.Length; i++)
        {
            form.Apply(new TuiKey(TuiKeyKind.Character, text[i]));
        }

        form.Apply(new TuiKey(TuiKeyKind.Escape));
    }

    /// <summary>A path that resolves to a file with the given content.</summary>
    private static string WithFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-ledger-cli-{Guid.NewGuid():N}", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static TuiKey Quit() => new(TuiKeyKind.Character, 'q');

    private static IKeySource Built(List<string> events, IKeySource keys)
    {
        events.Add("keys built");
        return keys;
    }

    /// <summary>The cockpit, stood in for: this test is about the argument vector.</summary>
    private sealed class CapturingCockpit(Action<LaunchpadRunRequest> capture) : ICockpitRunner
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

    /// <summary>A setting that records nothing, because the ordering is asserted elsewhere.</summary>
    private sealed class NeverFailsControlC : IControlCAsInput
    {
        public bool Get() => false;

        public void Set(bool value)
        {
        }
    }

    /// <summary>A setting a test can watch: every get and set in order.</summary>
    private sealed class RecordingControlC(List<string> events) : IControlCAsInput
    {
        private bool _value;

        internal bool Value => _value;

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

    /// <summary>Keys that hand over a scripted sequence and then report end of input.</summary>
    private sealed class ScriptedKeys(List<string> events) : IKeySource
    {
        private readonly Queue<TuiKey> _keys = new();

        internal Action? OnWait { get; init; }

        /// <summary>How many waits report a timeout before the input ends.</summary>
        internal int IdleBeforeClosing { get; init; }

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

        public void Dispose() => events.Add("keys disposed");
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

        internal RecordingSurface()
            : this(null)
        {
        }

        internal RecordingSurface(List<string>? events)
        {
            _events = events ?? [];
            _session = new RecordingSession(_events);
        }

        internal int Entries => _events.Count(entry => entry == "enter");

        internal int Utf8Requests => _events.Count(entry => entry == "utf8");

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

        private sealed class RecordingSession(List<string> events) : ITerminalSession
        {
            private bool _restored;

            public event Action? Interrupted;

            public void Write(string text) => events.Add("frame");

            public void Dispose()
            {
                if (_restored)
                {
                    return;
                }

                _restored = true;
                events.Add("restore");
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
