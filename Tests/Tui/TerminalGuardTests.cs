using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Drives <see cref="TerminalGuard"/> through an injected
/// <see cref="StringWriter"/> and an injected Ctrl-C registrar, so all three
/// restore paths are exercised without a real terminal and without raising a
/// real <see cref="Console.CancelKeyPress"/>. The escape sequences are asserted
/// as literals because the whole contract is "these exact bytes, in this order".
/// </summary>
public class TerminalGuardTests
{
    private const string Enter = "\u001b[?1049h";
    private const string Hide = "\u001b[?25l";
    private const string Show = "\u001b[?25h";
    private const string Leave = "\u001b[?1049l";

    /// <summary>
    /// Stands in for the real Ctrl-C hook: captures whatever the guard
    /// registers and lets the test fire it, and counts detach calls so the
    /// unregistration path is covered rather than assumed.
    /// </summary>
    private sealed class FakeCancelSubscription : ICancelSubscription
    {
        private Action? _handler;

        internal int Registrations { get; private set; }

        internal int Unregistrations { get; private set; }

        internal bool Attached => _handler is not null;

        public void Register(Action onCancel)
        {
            Registrations++;
            _handler = onCancel;
        }

        public void Unregister()
        {
            Unregistrations++;
            _handler = null;
        }

        internal void Press()
        {
            if (_handler is null)
            {
                throw new InvalidOperationException("No cancel handler was registered.");
            }

            _handler();
        }
    }

    [Fact]
    public void EnteringSwitchesToTheAlternateScreenAndHidesTheCursor()
    {
        var output = new StringWriter();

        TerminalGuard.Enter(output);

        Assert.Equal(Enter + Hide, output.ToString());
    }

    [Fact]
    public void DisposingShowsTheCursorAndLeavesTheAlternateScreen()
    {
        var output = new StringWriter();

        using (TerminalGuard.Enter(output))
        {
        }

        Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
    }

    [Fact]
    public void ARestoreHappensOnlyOnceHoweverManyTimesTheHandleIsDisposed()
    {
        var output = new StringWriter();
        var guard = TerminalGuard.Enter(output);

        guard.Dispose();
        guard.Dispose();
        guard.Dispose();

        var written = output.ToString();
        Assert.Equal(1, CountOccurrences(written, Leave));
        Assert.Equal(1, CountOccurrences(written, Show));
    }

    [Fact]
    public void ACancelRestoresImmediately()
    {
        var output = new StringWriter();
        var subscription = new FakeCancelSubscription();

        using (TerminalGuard.Enter(output, subscription))
        {
            subscription.Press();

            Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
        }

        Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
    }

    [Fact]
    public void ACancelFollowedByDisposalDoesNotRestoreTwice()
    {
        var output = new StringWriter();
        var subscription = new FakeCancelSubscription();

        using (TerminalGuard.Enter(output, subscription))
        {
            subscription.Press();
        }

        Assert.Equal(1, CountOccurrences(output.ToString(), Leave));
    }

    [Fact]
    public void EnteringRegistersTheCancelHookExactlyOnceAndDisposalRemovesIt()
    {
        var output = new StringWriter();
        var subscription = new FakeCancelSubscription();

        var guard = TerminalGuard.Enter(output, subscription);
        Assert.Equal(1, subscription.Registrations);
        Assert.True(subscription.Attached);

        guard.Dispose();

        Assert.Equal(1, subscription.Unregistrations);
        Assert.False(subscription.Attached);
    }

    [Fact]
    public void DisposingTwiceDetachesOnlyOnce()
    {
        var output = new StringWriter();
        var subscription = new FakeCancelSubscription();
        var guard = TerminalGuard.Enter(output, subscription);

        guard.Dispose();
        guard.Dispose();

        Assert.Equal(1, subscription.Unregistrations);
    }

    [Fact]
    public void RunRestoresAfterANormalBody()
    {
        var output = new StringWriter();
        var ran = false;

        TerminalGuard.Run(output, () => ran = true);

        Assert.True(ran);
        Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
    }

    [Fact]
    public void RunRestoresWhenTheBodyThrowsAndDoesNotSwallowTheException()
    {
        var output = new StringWriter();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => TerminalGuard.Run(output, () => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", thrown.Message);
        Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
    }

    [Fact]
    public void RunExposesTheEnteredStateToTheBody()
    {
        var output = new StringWriter();
        string? duringBody = null;

        TerminalGuard.Run(output, () => duringBody = output.ToString());

        Assert.Equal(Enter + Hide, duringBody);
    }

    [Fact]
    public void ASubscriptionThatThrowsOnRegisterStillLeavesTheGuardUsable()
    {
        var output = new StringWriter();

        using (TerminalGuard.Enter(output, new ThrowingCancelSubscription()))
        {
        }

        Assert.Equal(Enter + Hide + Show + Leave, output.ToString());
    }

    [Fact]
    public void ADisposedOutputDoesNotPropagateOutOfTheRestore()
    {
        var output = new StringWriter();
        var guard = TerminalGuard.Enter(output);
        output.Dispose();

        guard.Dispose();
    }

    [Fact]
    public void ANullOutputIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalGuard.Enter(null!));
        Assert.Throws<ArgumentNullException>(() => TerminalGuard.Run(new StringWriter(), null!));
    }

    /// <summary>A host with no interrupt event at all.</summary>
    private sealed class ThrowingCancelSubscription : ICancelSubscription
    {
        public void Register(Action onCancel) => throw new PlatformNotSupportedException();

        public void Unregister()
        {
        }
    }

    [Fact]
    public void TheEnteredModeSequencesAreTheAnsiPrivateModeCodes()
    {
        Assert.Equal("\u001b[?1049h", TerminalGuard.EnterAlternateScreen);
        Assert.Equal("\u001b[?1049l", TerminalGuard.LeaveAlternateScreen);
        Assert.Equal("\u001b[?25l", TerminalGuard.HideCursor);
        Assert.Equal("\u001b[?25h", TerminalGuard.ShowCursor);
    }

    [Fact]
    public void CursorVisibilityIsGuardedRatherThanThrown()
    {
        // Under a redirected test host this reports false; on a real terminal it
        // reports true. Either way it must not throw, which is the only property
        // the restore path depends on.
        TerminalGuard.TrySetCursorVisible(false);
        var visible = TerminalGuard.TrySetCursorVisible(true);

        Assert.True(visible || Console.IsOutputRedirected);
    }

    [Fact]
    public void Utf8PreparationIsGuardedAndSkipsRedirectedOutput()
    {
        if (Console.IsOutputRedirected)
        {
            Assert.False(TerminalGuard.TryUseUtf8());
        }
        else
        {
            TerminalGuard.TryUseUtf8();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}