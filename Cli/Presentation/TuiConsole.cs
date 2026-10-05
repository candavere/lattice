using Lattice.Tui;

namespace Lattice.Cli.Presentation;

/// <summary>
/// The console the interactive viewer is composed from: the process-wide setting
/// its run owns, and the key source it reads from.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than two ambient lookups, so the seams a run needs can be handed
/// to it instead of found. A real run uses <see cref="Default"/>; a test supplies
/// its own, which is how the viewer can be driven with no console, with a terminal
/// that refuses to start, or with a console whose input mode cannot be read.
/// </para>
/// <para>
/// The key source is a factory rather than an instance because each run gets a
/// reader and disposes it: a queue, and the thread behind it, belong to one run.
/// A caller with its own setting takes <see cref="Default"/> with that one member
/// replaced, rather than rebuilding the key reader too.
/// </para>
/// </remarks>
/// <param name="ControlCAsInput">The Ctrl-C-as-input setting the run owns while it lasts.</param>
/// <param name="Keys">Builds the run's key source.</param>
public sealed record TuiConsole(IControlCAsInput ControlCAsInput, Func<IKeySource> Keys)
{
    /// <summary>The real console: the process's own, read a key at a time.</summary>
    public static TuiConsole Default { get; } = new(
        new ConsoleControlCAsInput(),
        () => new KeyQueue(ConsoleKeyReader.FromConsole()));
}
