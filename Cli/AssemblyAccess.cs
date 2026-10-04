// Exists only so the test assembly can reach the CLI's internal repaint-pump
// seam. The CLI assembly's own name is already `lattice` (AssemblyName in
// Lattice.Cli.csproj), so this file grants access to Lattice.Tests alone.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lattice.Tests")]