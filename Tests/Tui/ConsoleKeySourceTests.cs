using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The demand-driven console key source: it reads only while waited on, so a
/// screen that is not waiting holds no blocked reader. Construction touches
/// nothing, which is what keeps a refused run from reaching the console.
/// </summary>
public class ConsoleKeySourceTests
{
    /// <summary>A console that answers from a script instead of a terminal.</summary>
    private sealed class ScriptedConsole
    {
        private readonly Queue<bool> _availability = new();
        private readonly Queue<Func<ConsoleKeyInfo>> _reads = new();

        internal int AvailabilityCalls { get; private set; }

        internal int ReadCalls { get; private set; }

        internal void NothingWaiting() => _availability.Enqueue(false);

        internal void KeyWaiting(ConsoleKeyInfo key)
        {
            _availability.Enqueue(true);
            _reads.Enqueue(() => key);
        }

        internal bool Available()
        {
            AvailabilityCalls++;
            return _availability.Count > 0 && _availability.Dequeue();
        }

        internal ConsoleKeyInfo Read()
        {
            ReadCalls++;
            return _reads.Dequeue()();
        }
    }

    [Fact]
    public void DeliversAWaitingKey()
    {
        var console = new ScriptedConsole();
        console.KeyWaiting(new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false));
        using var source = new ConsoleKeySource(console.Available, console.Read);

        Assert.Equal(KeyWait.Key, source.Wait(TimeSpan.FromSeconds(5), out var key));
        Assert.Equal(new TuiKey(TuiKeyKind.Character, 'h'), key);
    }

    [Fact]
    public void DecodesNamedKeysThroughTheSharedMapping()
    {
        var console = new ScriptedConsole();
        console.KeyWaiting(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
        using var source = new ConsoleKeySource(console.Available, console.Read);

        Assert.Equal(KeyWait.Key, source.Wait(TimeSpan.FromSeconds(5), out var key));
        Assert.Equal(new TuiKey(TuiKeyKind.Left), key);
    }

    [Fact]
    public void CtrlCArrivesAsAnInterrupt()
    {
        var console = new ScriptedConsole();
        console.KeyWaiting(new ConsoleKeyInfo('\u0003', ConsoleKey.NoName, false, false, false));
        using var source = new ConsoleKeySource(console.Available, console.Read);

        Assert.Equal(KeyWait.Key, source.Wait(TimeSpan.FromSeconds(5), out var key));
        Assert.True(key.IsQuit);
        Assert.Equal(TuiKeyKind.Interrupt, key.Kind);
    }

    [Fact]
    public void TimesOutWhenNothingArrivesAndReadsNothing()
    {
        var console = new ScriptedConsole();
        for (var i = 0; i < 100; i++)
        {
            console.NothingWaiting();
        }

        using var source = new ConsoleKeySource(console.Available, console.Read);

        Assert.Equal(KeyWait.TimedOut, source.Wait(TimeSpan.FromMilliseconds(100), out _));
        Assert.Equal(0, console.ReadCalls);
    }

    [Fact]
    public void ZeroTimeoutChecksOnce()
    {
        var console = new ScriptedConsole();
        console.NothingWaiting();
        using var source = new ConsoleKeySource(console.Available, console.Read);

        Assert.Equal(KeyWait.TimedOut, source.Wait(TimeSpan.Zero, out _));
        Assert.Equal(1, console.AvailabilityCalls);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public void ClosesWhenAvailabilityThrows(Type fault)
    {
        var calls = 0;
        using var source = new ConsoleKeySource(
            () =>
            {
                calls++;
                throw (Exception)Activator.CreateInstance(fault)!;
            },
            () => throw new InvalidOperationException("must not be read"));

        Assert.Equal(KeyWait.Closed, source.Wait(TimeSpan.FromSeconds(5), out _));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ClosesWhenAWaitingReadThrows()
    {
        var reads = 0;
        using var source = new ConsoleKeySource(
            () => true,
            () =>
            {
                reads++;
                throw new InvalidOperationException("the terminal went away");
            });

        Assert.Equal(KeyWait.Closed, source.Wait(TimeSpan.FromSeconds(5), out _));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ConstructionTouchesNothing()
    {
        var availabilityCalls = 0;
        var readCalls = 0;

        using var source = new ConsoleKeySource(
            () =>
            {
                availabilityCalls++;
                throw new InvalidOperationException("must not be asked");
            },
            () =>
            {
                readCalls++;
                throw new InvalidOperationException("must not be read");
            });

        Assert.Equal(0, availabilityCalls);
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public void DisposeIsIdempotentAndReadsAfterItStayClosed()
    {
        var console = new ScriptedConsole();
        console.KeyWaiting(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));
        var source = new ConsoleKeySource(console.Available, console.Read);

        source.Dispose();
        source.Dispose();

        Assert.Equal(KeyWait.Closed, source.Wait(TimeSpan.FromSeconds(1), out _));
    }

    /// <summary>
    /// The default source is built over the real console without touching it:
    /// construction alone asks nothing, which is the refusal-order property.
    /// </summary>
    [Fact]
    public void DefaultConstructionAsksTheConsoleForNothing()
    {
        using var source = new ConsoleKeySource();
    }
}
