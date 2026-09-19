using Lattice.Cli;

if (args is ["--version"] or ["-v"])
{
    Console.Out.WriteLine(CliApp.Version);
    return 0;
}

return CliApp.Run(args, Console.Out, Console.Error);