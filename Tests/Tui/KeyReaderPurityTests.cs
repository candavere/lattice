using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Building a key reader must not touch the process-wide console input setting.
/// </summary>
/// <remarks>
/// Deliberately compilable against every revision of this seam, so it can be run
/// red: it uses nothing but the public factory and the process's own setting. Before
/// the repair the factory set <see cref="System.Console.TreatControlCAsInput"/> as a
/// side effect, which on Windows is a console input call that throws when standard
/// input is redirected — on the way to a run that was only going to be refused.
/// </remarks>
public class KeyReaderPurityTests
{
    [Fact]
    public void BuildingTheReaderLeavesTheProcessInputSettingAlone()
    {
        var prior = Console.TreatControlCAsInput;

        try
        {
            Console.TreatControlCAsInput = false;
            ConsoleKeyReader.FromConsole();

            Assert.False(
                Console.TreatControlCAsInput,
                "building a key reader changed the process-wide input setting; the refusal check has to come first.");
        }
        finally
        {
            Console.TreatControlCAsInput = prior;
        }
    }
}
