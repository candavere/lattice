using Lattice.Cli;
using Lattice.Cli.Presentation;
using Lattice.Tui;

if (args is ["--version"] or ["-v"])
{
    Console.Out.WriteLine(CliApp.Version);
    return 0;
}

// The capabilities are resolved here, once, and handed in: the entry point is the
// only place that reads the real console, so a caller anywhere below it decides
// from a value rather than from whatever the process happens to be attached to.
// This is what makes a bare `lattice` a screen in a terminal and a usage line in a
// pipe, without either answer being re-derived further in.
return CliApp.Run(
    args,
    Console.Out,
    Console.Error,
    CliTerminal.For(Console.Error),
    TuiConsole.Default,
    CapabilityDetector.Detect());