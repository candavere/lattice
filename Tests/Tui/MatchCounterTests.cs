using Lattice.Cli.Presentation;
using Lattice.Environment;
using Lattice.Generator;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Covers the counter that gives <c>evaluate</c> a real progress figure. The
/// whole justification for this type is that its number is measured from the
/// work rather than estimated, so the tests are about what it counts and what it
/// refuses to count.
/// </summary>
public class MatchCounterTests
{
    private static MapGraph AMap() => MapGenerator.Generate(1, new GeneratorConfig(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap));

    [Fact]
    public void AnUnstartedCounterReportsZeroOfItsBudget()
    {
        var counter = new MatchCounter(4);

        Assert.Equal(new CommandProgress(0, 4, "matches"), counter.Snapshot());
    }

    [Fact]
    public void EachWrappedFactoryCallCountsExactlyOneMatch()
    {
        var counter = new MatchCounter(3);
        var factory = counter.Wrap(seed => AMap());

        factory(1);
        Assert.Equal(new CommandProgress(1, 3, "matches"), counter.Snapshot());

        factory(2);
        factory(3);
        Assert.Equal(new CommandProgress(3, 3, "matches"), counter.Snapshot());
    }

    [Fact]
    public void TheWrappedFactoryStillReturnsTheRealMap()
    {
        // The counter observes; it must not substitute. A factory that returned
        // a shared or placeholder map would quietly change every evaluation.
        var counter = new MatchCounter(1);
        var expected = AMap();

        var actual = counter.Wrap(_ => expected)(7);

        Assert.Same(expected, actual);
    }

    [Fact]
    public void TheWrappedFactoryStillSeedsFromTheSeedItWasGiven()
    {
        // Seed propagation is the harness's determinism contract. The wrapper
        // forwards the argument untouched, which this asserts by giving two
        // different seeds to the same wrapped factory.
        var seen = new List<ulong>();
        var counter = new MatchCounter(2);
        var factory = counter.Wrap(seed =>
        {
            seen.Add(seed);
            return AMap();
        });

        factory(11);
        factory(22);

        Assert.Equal(new ulong[] { 11, 22 }, seen);
    }

    [Fact]
    public void CountingPastTheBudgetIsRefusedRatherThanOverstated()
    {
        // The budget is derived from the seeds and pairings the request actually
        // has, so a mismatch means the derivation is wrong. Letting the counter
        // run past it would print "5/4 matches", which is not a real figure.
        var counter = new MatchCounter(1);
        var factory = counter.Wrap(_ => AMap());

        factory(1);
        Assert.Throws<InvalidOperationException>(() => factory(1).GetHashCode());
    }

    [Fact]
    public void AZeroBudgetIsRefusedBecauseItDescribesNoWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchCounter(0));
    }
}
