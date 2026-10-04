// Exists only so the test assembly and the CLI can reach Tui's internal repaint
// seam. No behaviour, no API: the seam stays internal and the public surface is
// unchanged.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lattice.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("lattice")]