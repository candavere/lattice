using Lattice.Cli;
using Lattice.Cli.Presentation;

if (args is ["--version"] or ["-v"])
{
    Console.Out.WriteLine(CliApp.InformationalVersion);
    return 0;
}

return CliApp.Run(
    args,
    Console.Out,
    Console.Error,
    CliTerminal.For(Console.Error));