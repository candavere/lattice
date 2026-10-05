using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The input path: what the queue hands a host, in order, and how it reports the
/// end of its input. The queue is driven through a scripted producer and through
/// the console's own key mapping, so nothing here needs a terminal, a timer or a
/// thread to be scheduled before it can pass.
/// </summary>
public class KeyReaderTests
{
    /// <summary>Long enough that a loaded machine does not lose the race, short enough to fail.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task EveryKeyIsHandedOverInTheOrderItWasRead()
    {
        var scripted = new[] { Key('n'), Key(' '), Key(TuiKeyKind.Right), Key(TuiKeyKind.End) };
        using var queue = new KeyQueue(Producer(scripted));

        await queue.Completion.WaitAsync(Generous);

        Assert.Equal(scripted, Drain(queue));
    }

    [Fact]
    public async Task AnEmptyProducerReportsEndOfInput()
    {
        using var queue = new KeyQueue(Producer([]));

        await queue.Completion.WaitAsync(Generous);

        Assert.Equal(KeyWait.Closed, queue.Wait(TimeSpan.Zero, out _));
    }

    [Fact]
    public async Task AProducerThatHasNotFinishedReportsATimeoutRatherThanEndOfInput()
    {
        var release = new ManualResetEventSlim(false);
        using var queue = new KeyQueue(() =>
        {
            release.Wait();
            return null;
        });

        Assert.Equal(KeyWait.TimedOut, queue.Wait(TimeSpan.FromMilliseconds(20), out _));

        release.Set();
        await queue.Completion.WaitAsync(Generous);
        Assert.Equal(KeyWait.Closed, queue.Wait(TimeSpan.FromMilliseconds(20), out _));
    }

    [Fact]
    public async Task AProducerThatThrowsEndsTheQueueRatherThanTakingTheProcessDown()
    {
        using var queue = new KeyQueue(() => throw new IOException("the terminal went away"));

        await queue.Completion.WaitAsync(Generous);

        Assert.Equal(KeyWait.Closed, queue.Wait(TimeSpan.Zero, out _));
    }

    [Fact]
    public void TheConsoleKeyNamesArriveAsTheKindsAHostBinds()
    {
        var cases = new (ConsoleKey Key, TuiKeyKind Expected)[]
        {
            (ConsoleKey.LeftArrow, TuiKeyKind.Left),
            (ConsoleKey.RightArrow, TuiKeyKind.Right),
            (ConsoleKey.UpArrow, TuiKeyKind.Up),
            (ConsoleKey.DownArrow, TuiKeyKind.Down),
            (ConsoleKey.Home, TuiKeyKind.Home),
            (ConsoleKey.End, TuiKeyKind.End),
            (ConsoleKey.PageUp, TuiKeyKind.PageUp),
            (ConsoleKey.PageDown, TuiKeyKind.PageDown),
        };

        foreach (var (key, expected) in cases)
        {
            var mapped = ConsoleKeyReader.Map(new ConsoleKeyInfo(
                '\0',
                key,
                shift: false,
                alt: false,
                control: false));

            Assert.Equal(expected, mapped.Kind);
        }
    }

    /// <summary>
    /// The keys a text-entry screen binds but the cockpit has no use for. They are
    /// named here for the same reason the navigation keys are: a host has to be
    /// able to tell Enter from a character, or every field would commit on the
    /// first letter typed.
    /// </summary>
    [Fact]
    public void TheTextEntryKeysKeepTheirIdentityThroughTheMapping()
    {
        var cases = new (ConsoleKey Key, TuiKeyKind Expected)[]
        {
            (ConsoleKey.Enter, TuiKeyKind.Enter),
            (ConsoleKey.Tab, TuiKeyKind.Tab),
            (ConsoleKey.Backspace, TuiKeyKind.Backspace),
            (ConsoleKey.Delete, TuiKeyKind.Delete),
            (ConsoleKey.Escape, TuiKeyKind.Escape),
        };

        foreach (var (key, expected) in cases)
        {
            var mapped = ConsoleKeyReader.Map(new ConsoleKeyInfo(
                '\0',
                key,
                shift: false,
                alt: false,
                control: false));

            Assert.Equal(expected, mapped.Kind);
        }
    }

    /// <summary>
    /// Naming those keys must not take the control characters they used to arrive
    /// as away from the cockpit: <c>ReplayPlayback.Apply</c> matches on
    /// <see cref="TuiKeyKind.Character"/> and ignored every one of them, so each
    /// still has to be a kind the cockpit does nothing with rather than becoming
    /// a character it would try to act on.
    /// </summary>
    [Fact]
    public void TheTextEntryKeysCarryNoGlyph()
    {
        foreach (var key in new[] { TuiKeyKind.Enter, TuiKeyKind.Tab, TuiKeyKind.Backspace, TuiKeyKind.Delete, TuiKeyKind.Escape })
        {
            Assert.Equal('\0', new TuiKey(key).Glyph);
            Assert.False(new TuiKey(key).IsQuit);
        }
    }

    /// <summary>
    /// Escape is recognised from its character as well as from its key name, because
    /// a Unix console hands it over as the bare control character with no key name at
    /// all. Without this a screen that binds Escape never sees it there: it arrives
    /// as a character, is not printable, and is dropped — which is how Escape came to
    /// do nothing on macOS while working on Windows.
    /// <para>
    /// Ctrl-C is not affected: it is matched first and keeps its own kind.
    /// </para>
    /// </summary>
    [Fact]
    public void EscapeIsRecognisedFromItsCharacterWhereTheConsoleNamesNoKey()
    {
        var mapped = ConsoleKeyReader.Map(new ConsoleKeyInfo('\u001b', 0, false, false, false));

        Assert.Equal(TuiKeyKind.Escape, mapped.Kind);
        Assert.Equal('\0', mapped.Glyph);
    }

    /// <summary>
    /// The other control characters are recognised the same way, because the same
    /// console names none of them: a form that could commit but not erase would be a
    /// form with no backspace.
    /// </summary>
    [Theory]
    [InlineData('\t', TuiKeyKind.Tab)]
    [InlineData('\r', TuiKeyKind.Enter)]
    [InlineData('\n', TuiKeyKind.Enter)]
    [InlineData('\b', TuiKeyKind.Backspace)]
    [InlineData('\u007f', TuiKeyKind.Backspace)]
    public void TheTextEntryControlCharactersAreRecognisedWhereTheConsoleNamesNoKey(char typed, TuiKeyKind expected)
    {
        var mapped = ConsoleKeyReader.Map(new ConsoleKeyInfo(typed, 0, false, false, false));

        Assert.Equal(expected, mapped.Kind);
    }

    /// <summary>
    /// The space bar and an ordinary letter keep their own identity through the same
    /// path: this is what stops the recognition above swallowing every character.
    /// </summary>
    [Fact]
    public void AnOrdinaryCharacterIsStillACharacterWhereTheConsoleNamesNoKey()
    {
        Assert.Equal(
            new TuiKey(TuiKeyKind.Character, 'x'),
            ConsoleKeyReader.Map(new ConsoleKeyInfo('x', 0, false, false, false)));

        Assert.Equal(
            new TuiKey(TuiKeyKind.Character, ' '),
            ConsoleKeyReader.Map(new ConsoleKeyInfo(' ', 0, false, false, false)));
    }

    [Fact]
    public void CharactersAndTheInterruptKeepTheirIdentityThroughTheMapping()
    {
        Assert.Equal(
            new TuiKey(TuiKeyKind.Character, 'q'),
            ConsoleKeyReader.Map(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)));

        Assert.Equal(
            new TuiKey(TuiKeyKind.Character, ' '),
            ConsoleKeyReader.Map(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)));

        var interrupt = ConsoleKeyReader.Map(
            new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));
        Assert.Equal(new TuiKey(TuiKeyKind.Interrupt), interrupt);
        Assert.True(interrupt.IsQuit);
    }

    [Fact]
    public void OnlyTheTwoQuitKeysAreQuitKeys()
    {
        Assert.True(new TuiKey(TuiKeyKind.Character, 'q').IsQuit);
        Assert.True(new TuiKey(TuiKeyKind.Character, 'Q').IsQuit);
        Assert.True(new TuiKey(TuiKeyKind.Interrupt).IsQuit);
        Assert.False(new TuiKey(TuiKeyKind.Character, 'n').IsQuit);
        Assert.False(new TuiKey(TuiKeyKind.Left).IsQuit);
        Assert.False(new TuiKey(TuiKeyKind.Character, ' ').IsQuit);
    }

    private static TuiKey Key(char glyph) => new(TuiKeyKind.Character, glyph);

    private static TuiKey Key(TuiKeyKind kind) => new(kind);

    /// <summary>A producer that hands over a fixed sequence and then reports end of input.</summary>
    private static Func<TuiKey?> Producer(params TuiKey[] keys)
    {
        var queue = new Queue<TuiKey>(keys);
        return () => queue.Count > 0 ? queue.Dequeue() : null;
    }

    private static List<TuiKey> Drain(IKeySource queue)
    {
        var keys = new List<TuiKey>();
        while (queue.Wait(TimeSpan.Zero, out var key) == KeyWait.Key)
        {
            keys.Add(key);
        }

        return keys;
    }
}