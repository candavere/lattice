namespace Lattice.Tui;

/// <summary>
/// The process-wide console setting that decides whether Ctrl-C arrives as a key
/// or only as a signal. A seam rather than a call into <see cref="Console"/>, for
/// two reasons that are the same reason.
/// </summary>
/// <remarks>
/// <para>
/// It is a setting rather than a mode the reader owns: it belongs to the process,
/// so a run that changes it has to give back what it found. And on Windows the
/// setter needs a real console input handle — it calls <c>GetConsoleMode</c> on
/// standard input and throws when standard input is a file or a pipe. A run that
/// is going to be refused anyway must therefore never reach it.
/// </para>
/// </remarks>
public interface IControlCAsInput
{
    /// <summary>
    /// Reads the setting the process is running with. Safe to ask on any console,
    /// but only ever worth asking after the redirected-stream refusal: it is a
    /// console input property, and on Windows it is the same call the setter makes.
    /// </summary>
    bool Get();

    /// <summary>
    /// Makes the process-wide setting so. On Windows this needs a real console
    /// input handle: it calls <c>GetConsoleMode</c> on standard input and then
    /// <c>SetConsoleMode</c>, throwing <see cref="System.ComponentModel.Win32Exception"/>
    /// when standard input is a file or a pipe. Nothing may call this on the way to
    /// refusing a run.
    /// </summary>
    void Set(bool value);
}

/// <summary>
/// <see cref="IControlCAsInput"/> over the process's own console. The only
/// implementation a real run uses.
/// </summary>
public sealed class ConsoleControlCAsInput : IControlCAsInput
{
    /// <inheritdoc />
    public bool Get() => Console.TreatControlCAsInput;

    /// <inheritdoc />
    public void Set(bool value) => Console.TreatControlCAsInput = value;
}

/// <summary>
/// One interactive viewer's claim on the Ctrl-C-as-input setting: taken for the
/// length of the run, and given back the value it found.
/// </summary>
/// <remarks>
/// <para>
/// The scope exists so the setting cannot outlive the run that wanted it. Every
/// ending restores it — a quit key, a Ctrl-C, the end of the episode, a failed
/// stepper, an exception during start-up — because every one of those paths
/// disposes the scope, and the restore does not wait on the reading thread.
/// </para>
/// <para>
/// The restore puts back what <see cref="Enter"/> read, so a process that had
/// already asked for Ctrl-C as input keeps it. The restore also swallows a
/// console that has gone away: a failure to tidy up must not replace the status
/// the run actually ended with.
/// </para>
/// <para>
/// <b>Call this only after the redirected-stream refusal.</b> Reading the prior
/// value is itself a console input property, and on a redirected console it
/// throws exactly as setting it does.
/// </para>
/// </remarks>
public sealed class ControlCAsInputScope : IDisposable
{
    private readonly IControlCAsInput _controlC;
    private readonly bool _prior;
    private bool _restored;
    private bool _suspended;

    private ControlCAsInputScope(IControlCAsInput controlC, bool prior)
    {
        _controlC = controlC;
        _prior = prior;
    }

    /// <summary>
    /// Reads the setting the process currently has, asks for Ctrl-C as input, and
    /// hands back a scope that will return the value it found.
    /// </summary>
    /// <remarks>
    /// Ctrl-C has to arrive as a key, not only as a signal. The terminal guard puts
    /// the console in raw mode, which clears ISIG, so the console generates no
    /// interrupt from the control character — and .NET's
    /// <see cref="Console.ReadKey(bool)"/> deliberately never returns Ctrl-C as a key
    /// unless this is set. Without it a Ctrl-C on a real terminal is swallowed: the
    /// viewer neither quits nor puts the terminal back. The
    /// <see cref="Console.CancelKeyPress"/> path still covers an interrupt raised
    /// from anywhere else.
    /// </remarks>
    public static ControlCAsInputScope Enter(IControlCAsInput controlC)
    {
        ArgumentNullException.ThrowIfNull(controlC);

        var prior = controlC.Get();
        controlC.Set(true);
        return new ControlCAsInputScope(controlC, prior);
    }

    /// <summary>
    /// Gives the setting back while the scope is still alive, and takes it again on
    /// <see cref="Resume"/>.
    /// <para>
    /// This is for a screen that also starts plain commands. A command is not a
    /// viewer: it must run under the process's own prior setting, so Ctrl-C reaches
    /// it as a signal and interrupts it, rather than arriving as a key the command
    /// would ignore. Resuming re-reads nothing — it re-applies the value this scope
    /// took — so a command that changed the setting does not get to keep it.
    /// </para>
    /// </summary>
    public void Suspend()
    {
        if (_suspended)
        {
            return;
        }

        _suspended = true;
        Restore();
    }

    /// <summary>Takes the setting again after a <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        Try(() => _controlC.Set(true));
    }

    /// <summary>
    /// Puts the prior value back, exactly once, whether or not the reading thread
    /// has finished. A console that is no longer there is a console the viewer is
    /// already leaving, so the failure is swallowed rather than raised over the
    /// status the run ended with.
    /// </summary>
    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        Restore();
    }

    /// <summary>
    /// Puts the prior value back, swallowing a console that has gone away. The
    /// restore's only contract is that it does not throw: it runs while the run is
    /// unwinding, so anything it raised would travel out in place of the status the
    /// run actually ended with — and a console that is gone reports that in more ways
    /// than there are exception types to enumerate. The Windows setter alone throws
    /// Win32Exception, IOException or InvalidOperationException depending on which
    /// handle went away first.
    /// </summary>
    private void Restore() => Try(() => _controlC.Set(_prior));

    /// <summary>Runs one console write, swallowing every failure for the reason above.</summary>
    private static void Try(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Nothing to report: the process is leaving the terminal, and a failure
            // to tidy up is not a failure of the run.
        }
    }
}
