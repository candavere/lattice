using System.Collections.Concurrent;

namespace Lattice.Tui;

/// <summary>What a key the reader saw actually was.</summary>
public enum TuiKeyKind
{
    /// <summary>An ordinary character; the glyph carries it.</summary>
    Character,

    /// <summary>The left arrow.</summary>
    Left,

    /// <summary>The right arrow.</summary>
    Right,

    /// <summary>The up arrow.</summary>
    Up,

    /// <summary>The down arrow.</summary>
    Down,

    /// <summary>The home key.</summary>
    Home,

    /// <summary>The end key.</summary>
    End,

    /// <summary>The page-up key.</summary>
    PageUp,

    /// <summary>The page-down key.</summary>
    PageDown,

    /// <summary>
    /// Ctrl-C. Its own kind rather than a character, because no character stands
    /// for it and a host must treat it as a request to leave.
    /// </summary>
    Interrupt,

    /// <summary>
    /// The return key. A named kind because a screen with text fields has to be
    /// able to commit or run on it, and a host that only saw the <c>\r</c> it
    /// used to arrive as would have to guess at which one was meant.
    /// </summary>
    Enter,

    /// <summary>The tab key: the field-to-field key on a form.</summary>
    Tab,

    /// <summary>The backspace key: delete the character before the caret.</summary>
    Backspace,

    /// <summary>The delete key: delete the character at the caret.</summary>
    Delete,

    /// <summary>
    /// The escape key: leave what is in progress without discarding it. Named
    /// rather than carried as <c></c> because the control character it
    /// arrives as is indistinguishable from the start of an escape sequence the
    /// terminal has not finished sending.
    /// </summary>
    Escape,
}

/// <summary>One key press: what it was, and the character when it was one.</summary>
/// <param name="Kind">What the key was.</param>
/// <param name="Glyph">The character, for <see cref="TuiKeyKind.Character"/> only.</param>
public readonly record struct TuiKey(TuiKeyKind Kind, char Glyph = '\0')
{
    /// <summary>Whether this key is one a host treats as a request to quit.</summary>
    public bool IsQuit =>
        Kind == TuiKeyKind.Interrupt || (Kind == TuiKeyKind.Character && (Glyph is 'q' or 'Q'));
}

/// <summary>How a wait for a key ended.</summary>
public enum KeyWait
{
    /// <summary>A key arrived.</summary>
    Key,

    /// <summary>Nothing arrived before the timeout.</summary>
    TimedOut,

    /// <summary>Input has ended and nothing is left: the reader can stop.</summary>
    Closed,
}

/// <summary>
/// Where a host gets its keys. A seam rather than a call into <see cref="Console"/>
/// so the redraw loop can be driven from a test with no terminal, no timer and no
/// waiting.
/// </summary>
public interface IKeySource : IDisposable
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for one key. Returns
    /// <see cref="KeyWait.Closed"/> when input has ended and the queue is empty,
    /// which is how a host learns there is nothing more to read.
    /// </summary>
    KeyWait Wait(TimeSpan timeout, out TuiKey key);
}

/// <summary>
/// Reads keys on a thread of its own and hands them over through a queue, so a
/// blocked read never blocks the redraw loop and the loop never has to know
/// whether a key happens to be ready.
/// </summary>
/// <remarks>
/// <para>
/// The reading thread is a background thread and the queue is unbounded, so a
/// reader holding a key down cannot stall a frame, and a host that quits with a
/// read outstanding does not have to unblock it: the process ends and the thread
/// goes with it. <see cref="Completion"/> is what a test waits on, which is how the
/// reading half is asserted without sleeping.
/// </para>
/// <para>
/// The producer is a function rather than a reader so the queue can be driven
/// either from a real terminal — <see cref="ConsoleKeyReader.FromConsole"/>, which
/// is what a run uses — or from a fixed sequence in a test. Nothing else about the
/// queue differs between the two.
/// </para>
/// </remarks>
public sealed class KeyQueue : IKeySource
{
    private readonly BlockingCollection<TuiKey> _queue = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>
    /// Reads through <paramref name="readOne"/> on a thread of its own. The
    /// producer returns <c>null</c> at end of input, which is what ends the queue.
    /// </summary>
    public KeyQueue(Func<TuiKey?> readOne)
    {
        ArgumentNullException.ThrowIfNull(readOne);

        var reader = new Thread(() => Pump(readOne))
        {
            IsBackground = true,
            Name = "lattice-tui-keys",
        };
        reader.Start();
    }

    /// <summary>
    /// Completes once the reader has reached the end of its input. A host never
    /// waits on it; it exists so a test can wait for the reading half to finish
    /// instead of guessing how long that takes. A task rather than a wait handle
    /// because a queue is routinely disposed while its reader is still blocked —
    /// releasing a handle underneath that reader would fault a background thread,
    /// and a background thread fault takes the process with it.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Waits for one key.</summary>
    public KeyWait Wait(TimeSpan timeout, out TuiKey key)
    {
        var milliseconds = timeout >= TimeSpan.FromMilliseconds(int.MaxValue)
            ? int.MaxValue
            : (int)Math.Max(0.0, timeout.TotalMilliseconds);

        if (_queue.TryTake(out key, milliseconds))
        {
            return KeyWait.Key;
        }

        key = default;
        return _queue.IsAddingCompleted && _queue.Count == 0 ? KeyWait.Closed : KeyWait.TimedOut;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _completion.TrySetResult();
    }

    private void Pump(Func<TuiKey?> readOne)
    {
        try
        {
            while (readOne() is { } key)
            {
                _queue.Add(key);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // A reader that cannot read — a closed stream, a host with no console —
            // ends the queue rather than taking the process down on a thread nobody
            // is watching.
        }
        finally
        {
            _queue.CompleteAdding();
            _completion.TrySetResult();
        }
    }
}

/// <summary>
/// The production key producer: the console's own key reader, which puts the
/// terminal into the mode a key-at-a-time program needs.
/// </summary>
/// <remarks>
/// Reading the console's keys rather than its characters is what makes single
/// keystrokes arrive at all. A terminal in its ordinary line mode hands a reader
/// nothing until a newline is typed, so a viewer whose controls are single
/// characters would wait for return after every one of them. The console's reader
/// also decodes the escape sequences the navigation keys arrive as, which is why
/// this type maps a reported key rather than parsing bytes.
/// </remarks>
public static class ConsoleKeyReader
{
    /// <summary>The character a terminal sends for Ctrl-C.</summary>
    private const char ControlC = '\u0003';

    /// <summary>
    /// A producer over the process's own console. End of input — which is what a
    /// host treats as "nothing more will arrive" — is reported when the console
    /// cannot produce keys at all, as it cannot when standard input is redirected.
    /// </summary>
    /// <remarks>
    /// This asks the console for nothing and changes nothing: it builds a reader
    /// and hands back a way to ask it for the next key. That is deliberate, because
    /// on Windows asking a redirected console for its input mode throws, and a run
    /// that is going to be refused must not throw on its way to saying so. The
    /// setting that makes Ctrl-C arrive as a key is
    /// <see cref="ControlCAsInputScope"/>'s, taken by the caller after the refusal
    /// check and given back when the run ends.
    /// </remarks>
    public static Func<TuiKey?> FromConsole()
    {
        return () =>
        {
            try
            {
                return Map(Console.ReadKey(intercept: true));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                return null;
            }
        };
    }

    /// <summary>
    /// One reported key as this library names it. A named key keeps its identity;
    /// anything else arrives as the character it typed, which is how the space bar
    /// and the letters reach a host without a table of key codes.
    /// </summary>
    public static TuiKey Map(ConsoleKeyInfo key)
    {
        if (key.KeyChar == ControlC)
        {
            return new TuiKey(TuiKeyKind.Interrupt);
        }

        // The control characters a text-entry screen binds are recognised from the
        // character as well as from the key name. A Unix console hands them over as
        // the bare control character with no key name at all, so a mapping that only
        // asked <see cref="ConsoleKey"/> would let Escape through as a character,
        // where it is not printable and is dropped — a form with no way to leave a
        // field. Matched before the key-name switch so both paths agree.
        if (key.KeyChar is '\u001b')
        {
            return new TuiKey(TuiKeyKind.Escape);
        }

        if (key.KeyChar is '\t')
        {
            return new TuiKey(TuiKeyKind.Tab);
        }

        if (key.KeyChar is '\r' or '\n')
        {
            return new TuiKey(TuiKeyKind.Enter);
        }

        if (key.KeyChar is '\b' or '\u007f')
        {
            return new TuiKey(TuiKeyKind.Backspace);
        }

        return key.Key switch
        {
            ConsoleKey.LeftArrow => new TuiKey(TuiKeyKind.Left),
            ConsoleKey.RightArrow => new TuiKey(TuiKeyKind.Right),
            ConsoleKey.UpArrow => new TuiKey(TuiKeyKind.Up),
            ConsoleKey.DownArrow => new TuiKey(TuiKeyKind.Down),
            ConsoleKey.Home => new TuiKey(TuiKeyKind.Home),
            ConsoleKey.End => new TuiKey(TuiKeyKind.End),
            ConsoleKey.PageUp => new TuiKey(TuiKeyKind.PageUp),
            ConsoleKey.PageDown => new TuiKey(TuiKeyKind.PageDown),
            ConsoleKey.Enter => new TuiKey(TuiKeyKind.Enter),
            ConsoleKey.Tab => new TuiKey(TuiKeyKind.Tab),
            ConsoleKey.Backspace => new TuiKey(TuiKeyKind.Backspace),
            ConsoleKey.Delete => new TuiKey(TuiKeyKind.Delete),
            ConsoleKey.Escape => new TuiKey(TuiKeyKind.Escape),
            _ => new TuiKey(TuiKeyKind.Character, key.KeyChar),
        };
    }
}