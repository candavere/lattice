using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The reading half of the input path: characters, the escape sequences terminals
/// send for the navigation keys, and end of input. Driven through the queue's own
/// completion signal, so nothing here waits for a thread to be scheduled.
/// </summary>
public class KeyReaderTests
{
    [Fact]
    public void CharactersArrowsAndTheNumericNavigationFormAllArriveAsKeys()
    {
        using var queue = new KeyQueue(new StringReader("n \u001b[C\u001b[D\u001b[A\u001b[B\u001b[H\u001b[F\u001b[5~\u001b[6~"));

        Assert.True(queue.Completion.WaitOne(Generous));

        Assert.Equal(
            new[]
            {
                new TuiKey(TuiKeyKind.Character, 'n'),
                new TuiKey(TuiKeyKind.Character, ' '),
                new TuiKey(TuiKeyKind.Right),
                new TuiKey(TuiKeyKind.Left),
                new TuiKey(TuiKeyKind.Up),
                new TuiKey(TuiKeyKind.Down),
                new TuiKey(TuiKeyKind.Home),
                new TuiKey(TuiKeyKind.End),
                new TuiKey(TuiKeyKind.PageUp),
                new TuiKey(TuiKeyKind.PageDown),
            },
            Drain(queue));
    }

    [Fact]
    public void CtrlCArrivesAsAnInterruptRatherThanAsACharacter()
    {
        using var queue = new KeyQueue(new StringReader("x\u0003"));

        Assert.True(queue.Completion.WaitOne(Generous));

        var keys = Drain(queue);
        Assert.Equal(new TuiKey(TuiKeyKind.Character, 'x'), keys[0]);
        Assert.Equal(new TuiKey(TuiKeyKind.Interrupt), keys[1]);
        Assert.True(keys[1].IsQuit);
        Assert.False(keys[0].IsQuit);
    }

    [Fact]
    public void ALoneEscapeIsAKeyRatherThanAHalfReadSequence()
    {
        using var queue = new KeyQueue(new StringReader("\u001b"));

        Assert.True(queue.Completion.WaitOne(Generous));

        Assert.Equal(new[] { new TuiKey(TuiKeyKind.Character, '\u001b') }, Drain(queue));
    }

    [Fact]
    public void AnOpenReaderWithNothingQueuedTimesOutAndThenReportsEndOfInput()
    {
        var reader = new BlockingReader();
        using var queue = new KeyQueue(reader);

        Assert.Equal(KeyWait.TimedOut, queue.Wait(TimeSpan.FromMilliseconds(20), out _));

        reader.Complete();
        Assert.True(queue.Completion.WaitOne(Generous));

        Assert.Equal(KeyWait.Closed, queue.Wait(TimeSpan.FromMilliseconds(20), out _));
    }

    [Fact]
    public void AnEmptyReaderReportsEndOfInputImmediately()
    {
        using var queue = new KeyQueue(new StringReader(string.Empty));

        Assert.True(queue.Completion.WaitOne(Generous));

        Assert.Equal(KeyWait.Closed, queue.Wait(TimeSpan.Zero, out _));
    }

    /// <summary>Long enough that a loaded machine does not lose the race, short enough to fail.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static List<TuiKey> Drain(IKeySource queue)
    {
        var keys = new List<TuiKey>();
        while (queue.Wait(TimeSpan.Zero, out var key) == KeyWait.Key)
        {
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>A reader that blocks until the test says input has ended.</summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly ManualResetEventSlim _ended = new(false);

        public override int Read()
        {
            _ended.Wait();
            return -1;
        }

        internal void Complete() => _ended.Set();
    }
}