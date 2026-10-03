using System.Threading;
using Lattice.Environment;
using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// Counts the matches an <c>evaluate</c> run has actually started, by observing
/// the map factory the CLI hands the harness.
/// </summary>
/// <remarks>
/// <para>
/// The harness builds a map exactly once per match, before running it
/// (<c>EvaluationHarness.Evaluate</c>), and takes that factory as an argument.
/// So wrapping the factory the CLI already supplies counts matches from the
/// inside of the real work, with no change to <c>Agents/**</c> and no estimate:
/// nothing here predicts how long a match will take or how many are left, it
/// tallies the ones that have begun.
/// </para>
/// <para>
/// The budget is <c>seeds x pairings</c>, which is the number of matches the
/// harness is going to attempt, computed from the same two values the spec was
/// built with. Running past it is refused rather than rendered, because a
/// counter that overruns its own budget is reporting a number the run cannot
/// justify.
/// </para>
/// </remarks>
public sealed class MatchCounter
{
    private readonly int _budget;
    private int _started;

    /// <summary>Creates a counter for a run budgeted to play <paramref name="budget"/> matches.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="budget"/> is not positive: a run with no matches has no
    /// progress to report.
    /// </exception>
    public MatchCounter(int budget)
    {
        if (budget < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), budget, "A match budget must be at least 1.");
        }

        _budget = budget;
    }

    /// <summary>Matches started so far, and the budget they are counted against.</summary>
    public CommandProgress Snapshot()
    {
        var started = Volatile.Read(ref _started);

        // Clamped rather than thrown: Snapshot is read from the repaint path,
        // where an exception would take down a run that is otherwise fine. The
        // count itself can never exceed the budget, because Advance refuses to
        // let it; the clamp is a belt-and-braces guard on a read.
        return new CommandProgress(Math.Min(started, _budget), _budget, "matches");
    }

    /// <summary>
    /// Returns <paramref name="factory"/> wrapped so each call counts one match
    /// and is otherwise passed through untouched — same argument, same returned
    /// map, so the run the harness performs is unchanged.
    /// </summary>
    public Func<ulong, MapGraph> Wrap(Func<ulong, MapGraph> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return seed =>
        {
            Advance();
            return factory(seed);
        };
    }

    private void Advance()
    {
        if (Interlocked.Increment(ref _started) > _budget)
        {
            throw new InvalidOperationException(
                $"The harness started more than the {_budget} match(es) this run was budgeted for. " +
                "The progress figure would no longer describe the work, so it is refused rather than reported.");
        }
    }
}
