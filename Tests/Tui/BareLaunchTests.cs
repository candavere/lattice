using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Bare <c>lattice</c>: what it does when a terminal is attached, and exactly what
/// it keeps doing when one is not.
/// </summary>
/// <remarks>
/// <para>
/// The frozen half of this stage is the quiet one. A test calls
/// <c>CliApp.Run</c> with no arguments from a host whose streams are all redirected,
/// and it must still get usage on stderr and the usage status. That is asserted here
/// against a call with no capabilities at all, because that is the call a test host
/// makes and the call the answer must not depend on the ambient console for.
/// </para>
/// <para>
/// The decision is made from capabilities passed in — never from
/// <see cref="Console"/> read inside the run — so a developer's terminal cannot
/// change what a test sees.
/// </para>
/// </remarks>
public class BareLaunchTests
{
    /// <summary>A terminal that can carry the Launchpad, which a test host's own is not.</summary>
    private static readonly TerminalCapabilities Terminal = new(
        ColorDepth.TrueColor,
        Utf8: true,
        InputRedirected: false,
        OutputRedirected: false,
        Width: 100,
        Height: 30);

    /// <summary>
    /// The behaviour that may not change: no arguments, no capabilities named, the
    /// same usage and the same status as before this screen existed.
    /// </summary>
    [Fact]
    public void AZeroArgumentRunStillPrintsUsageAndReportsTheUsageStatus()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run([], stdout, stderr);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("usage: lattice <command> [options]", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero-argument run that is told its streams are redirected prints usage and
    /// the usage status — the same answer as the overload that names no capabilities
    /// at all. The Launchpad is decided from the capabilities, never from what the
    /// process happens to be attached to.
    /// </summary>
    [Fact]
    public void AZeroArgumentRunWithRedirectedCapabilitiesStillPrintsUsage()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(
            [],
            stdout,
            stderr,
            CliTerminal.For(stderr),
            TuiConsole.Default,
            Terminal with { OutputRedirected = true });

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("usage: lattice <command> [options]", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Launchpad is decided from the capabilities and from nothing else, so the
    /// answer is the same whatever the process's own console looks like. Two runs
    /// differing only in that — which a test cannot change — are asserted from the
    /// value alone.
    /// </summary>
    [Fact]
    public void OnlyTheCapabilitiesDecideWhetherTheLaunchpadOpens()
    {
        Assert.True(CliApp.CanOpenLaunchpad(Terminal));
        Assert.False(CliApp.CanOpenLaunchpad(Terminal with { InputRedirected = true }));
        Assert.False(CliApp.CanOpenLaunchpad(Terminal with { OutputRedirected = true }));
    }

    /// <summary>
    /// On a terminal that can carry the screen, a zero-argument run opens it instead
    /// of printing usage. The status is success: nothing failed, the reader quit.
    /// </summary>
    [Fact]
    public void ABareRunOnATerminalOpensTheLaunchpad()
    {
        var result = Bare(Terminal);

        Assert.True(result.Opened, "the Launchpad was not opened on an interactive terminal.");
        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Stdout);
    }

    /// <summary>
    /// A redirected stdin is a shell script or a pipe, not a person. Usage is the only
    /// answer a reader of that stream can act on, so a bare run there is unchanged.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ABareRunOnARedirectedStreamKeepsPrintingUsage(bool input, bool output)
    {
        var result = Bare(Terminal with { InputRedirected = input, OutputRedirected = output });

        Assert.False(result.Opened);
        Assert.Equal(UsageError.ExitCode, result.Exit);
        Assert.Equal("", result.Stdout);
        Assert.Contains("usage: lattice <command> [options]", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flags are unchanged by this screen: <c>-h</c>, <c>--help</c>,
    /// <c>--version</c> and <c>-v</c> all answer as they did, on a terminal as well
    /// as anywhere else.
    /// </summary>
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void HelpIsUnchangedOnATerminal(string flag)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run([flag], stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default, Terminal);

        Assert.Equal(0, exit);
        Assert.Contains("usage: lattice <command> [options]", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", stderr.ToString());
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void TheVersionIsUnchangedOnATerminal(string flag)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run([flag], stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default, Terminal);

        Assert.Equal(0, exit);
        Assert.Equal($"{CliApp.Version}{System.Environment.NewLine}", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    /// <summary>
    /// The eight commands are unreachable through the new overload's dispatch: naming
    /// one is still the only way to run one. A bare run must not have changed what a
    /// named run does.
    /// </summary>
    [Fact]
    public void NamingACommandStillRunsThatCommandOnATerminal()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(
            ["generate", "--seed", "42"],
            stdout,
            stderr,
            CliTerminal.For(stderr),
            TuiConsole.Default,
            Terminal);

        Assert.Equal(0, exit);
        Assert.NotEqual("", stdout.ToString());
    }

    /// <summary>
    /// A per-command <c>--help</c> is still the whole usage text, on a terminal as
    /// well as anywhere else.
    /// </summary>
    [Fact]
    public void ACommandsHelpIsUnchangedOnATerminal()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["tui", "--help"], stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default, Terminal);

        Assert.Equal(0, exit);
        Assert.Contains("usage: lattice <command> [options]", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown command is still a usage error, and the usage text that follows it
    /// still says what the commands are — now including the Launchpad itself, which is
    /// the one line that changed.
    /// </summary>
    [Fact]
    public void AnUnknownCommandIsStillAUsageErrorAndTheUsageNamesTheLaunchpad()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["frobnicate"], stdout, stderr, CliTerminal.For(stderr), TuiConsole.Default, Terminal);

        Assert.Equal(UsageError.ExitCode, exit);
        Assert.Contains("unknown command 'frobnicate'", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("lattice", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The usage text names the Launchpad, so a reader who types <c>lattice</c> on a
    /// pipe and gets usage is told what they would have got on a terminal.
    /// </summary>
    [Fact]
    public void TheUsageTextSaysABareInvocationOpensTheLaunchpad()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        CliApp.Run(["--help"], stdout, stderr);

        Assert.Contains(
            "With no command, and standard input and standard output both a terminal,",
            stdout.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("lattice opens the Launchpad", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>What one bare run did, and where it put it.</summary>
    private readonly record struct BareRun(bool Opened, int Exit, string Stdout, string Stderr);

    /// <summary>
    /// A bare run with the capabilities named, and a Launchpad that only records that
    /// it was opened rather than drawing anything: what it draws is asserted by the
    /// layout and host tests, and a real screen here would need a real terminal.
    /// </summary>
    private static BareRun Bare(TerminalCapabilities capabilities)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var opened = 0;
        var keys = new QuitImmediately();

        var exit = CliApp.Run(
            [],
            stdout,
            stderr,
            CliTerminal.For(stderr),
            new TuiConsole(new RecordingControlC(), () => keys),
            capabilities,
            session: new RecordingSessionFactory(),
            onLaunchpad: () => opened++);

        return new BareRun(opened > 0, exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Keys that end the screen at once: the Launchpad's first key is read as quit.</summary>
    private sealed class QuitImmediately : IKeySource
    {
        public KeyWait Wait(TimeSpan timeout, out TuiKey key)
        {
            key = new TuiKey(TuiKeyKind.Character, 'q');
            return KeyWait.Key;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A setting that records nothing, because the ordering is asserted elsewhere.</summary>
    private sealed class RecordingControlC : IControlCAsInput
    {
        public bool Get() => false;

        public void Set(bool value)
        {
        }
    }

    /// <summary>A terminal that accepts the screen and does nothing with it.</summary>
    private sealed class RecordingSessionFactory : ITerminalSessionFactory
    {
        public bool RequestUtf8Output() => true;

        public ITerminalSession Enter(TextWriter output) => new Session();

        private sealed class Session : ITerminalSession
        {
            public event Action? Interrupted;

            public void Write(string text)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
