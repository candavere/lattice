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