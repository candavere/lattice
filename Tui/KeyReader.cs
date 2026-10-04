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
/// </remarks>
public sealed class KeyQueue : IKeySource
{
    private readonly BlockingCollection<TuiKey> _queue = new();
    private readonly ManualResetEventSlim _completion = new(false);
    private readonly Thread _reader;
    private bool _disposed;

    /// <summary>Reads from <paramref name="input"/> on its own thread.</summary>
    public KeyQueue(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);

        _reader = new Thread(() => Pump(input))
        {
            IsBackground = true,
            Name = "lattice-tui-keys",
        };
        _reader.Start();
    }

    /// <summary>
    /// Signalled once the reader has reached the end of its input. A host never
    /// waits on it; it exists so a test can wait for the reading half to finish
    /// instead of guessing how long that takes.
    /// </summary>
    public WaitHandle Completion => _completion.WaitHandle;

    /// <summary>Waits for one key, decoding escape sequences as it goes.</summary>
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
        _completion.Set();
        _completion.Dispose();
    }

    private void Pump(TextReader input)
    {
        try
        {
            while (ReadOne(input, out var key))
            {
                _queue.Add(key);
            }
        }
        finally
        {
            _queue.CompleteAdding();
            _completion.Set();
        }
    }

    /// <summary>
    /// One key from the reader, or false at end of input. Escape sequences are
    /// decoded here rather than in the host, so the host sees a key and not a
    /// terminal's wire format.
    /// </summary>
    private static bool ReadOne(TextReader input, out TuiKey key)
    {
        var read = input.Read();
        if (read < 0)
        {
            key = default;
            return false;
        }

        var glyph = (char)read;
        if (glyph == '\u0003')
        {
            key = new TuiKey(TuiKeyKind.Interrupt);
            return true;
        }

        if (glyph != '\u001b')
        {
            key = new TuiKey(TuiKeyKind.Character, glyph);
            return true;
        }

        // ESC on its own is a key too: a reader who pressed it meant something, and
        // swallowing it makes an unbound key indistinguishable from a hung reader.
        var bracket = input.Read();
        if (bracket < 0 || ((char)bracket is not ('[' or 'O')))
        {
            key = new TuiKey(TuiKeyKind.Character, '\u001b');
            return true;
        }

        var code = input.Read();
        if (code < 0)
        {
            key = new TuiKey(TuiKeyKind.Character, '\u001b');
            return true;
        }

        var final = (char)code;
        if (final is < '0' or > '9')
        {
            key = FromLetter(final);
            return true;
        }

        // The numeric form, ESC [ <n> ~ , which is how most terminals send the
        // navigation keys that have no letter of their own. The terminator is read
        // so the next key is read from the right place.
        input.Read();
        key = FromDigit(final);
        return true;
    }

    private static TuiKey FromLetter(char final) => final switch
    {
        'A' => new TuiKey(TuiKeyKind.Up),
        'B' => new TuiKey(TuiKeyKind.Down),
        'C' => new TuiKey(TuiKeyKind.Right),
        'D' => new TuiKey(TuiKeyKind.Left),
        'H' => new TuiKey(TuiKeyKind.Home),
        'F' => new TuiKey(TuiKeyKind.End),
        _ => new TuiKey(TuiKeyKind.Character, '\u001b'),
    };

    private static TuiKey FromDigit(char digit) => digit switch
    {
        '1' or '7' => new TuiKey(TuiKeyKind.Home),
        '4' or '8' => new TuiKey(TuiKeyKind.End),
        '5' => new TuiKey(TuiKeyKind.PageUp),
        '6' => new TuiKey(TuiKeyKind.PageDown),
        _ => new TuiKey(TuiKeyKind.Character, '\u001b'),
    };
}