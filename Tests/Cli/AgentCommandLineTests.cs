using Lattice.Cli;
using Xunit;

namespace Lattice.Tests.Cli;

/// <summary>
/// The spec §3.3 splitter, unit-tested as the pure function it is.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that make the rule checkable rather than described. The
/// rule exists because a shell is the one thing that can turn an argument into a
/// command, and because the three CI operating systems would otherwise supply
/// three different splitters — so the behaviour is pinned here, on every
/// platform, against strings rather than against a live process.
/// </para>
/// <para>
/// The Windows cases are the load-bearing ones. A path like
/// <c>C:\agents\python.exe</c> is the argument a user is most likely to type
/// unquoted, and a splitter that treated a backslash as an escape would either
/// mangle it or refuse it; both would be a defect the reader would discover on
/// their own machine, on the one platform where the rule is hardest to get right.
/// </para>
/// </remarks>
public class AgentCommandLineTests
{
    [Fact]
    public void Splits_On_Unquoted_Whitespace()
    {
        Assert.Equal(["python3", "agent.py"], Split("python3 agent.py"));
    }

    [Fact]
    public void Collapses_Runs_Of_Whitespace_And_Ignores_The_Ends()
    {
        // Runs separate, they do not produce empty arguments, and a padded command
        // line is not an empty command.
        Assert.Equal(["python3", "agent.py"], Split("   python3 \t\t agent.py  "));
    }

    [Fact]
    public void A_Double_Quote_Groups_Whitespace_Into_One_Argument()
    {
        Assert.Equal(["python3", "my agent.py", "--flag"], Split("python3 \"my agent.py\" --flag"));
    }

    [Fact]
    public void An_Empty_Quoted_String_Is_A_Legal_Empty_Argument()
    {
        Assert.Equal(["agent", ""], Split("agent \"\""));
    }

    [Fact]
    public void Inside_Quotes_A_Backslash_Escapes_A_Double_Quote()
    {
        Assert.Equal(["say \"hi\""], Split("\"say \\\"hi\\\"\""));
    }

    [Fact]
    public void Inside_Quotes_A_Backslash_Escapes_A_Backslash()
    {
        Assert.Equal(["a\\b"], Split("\"a\\\\b\""));
    }

    [Fact]
    public void Inside_Quotes_Any_Other_Backslash_Is_That_Character()
    {
        // \n is not an escape here, and a Windows path inside quotes survives
        // exactly as it does outside them.
        Assert.Equal(["C:\\agents\\n.exe"], Split("\"C:\\agents\\n.exe\""));
    }

    [Fact]
    public void Outside_Quotes_A_Backslash_Is_An_Ordinary_Character()
    {
        // The rule that makes an unquoted Windows path work: there is no escape
        // sequence outside a group at all, so nothing can eat the separator.
        Assert.Equal(["C:\\agents\\python.exe"], Split("C:\\agents\\python.exe"));
    }

    [Fact]
    public void Outside_Quotes_A_Backslash_Does_Not_Escape_A_Space()
    {
        // The sharpest statement of "a backslash is literal outside a group": the
        // space still separates, because there is no escape sequence out here to
        // stop it. A shell would have produced one argument here; this must not,
        // or the rule would be a shell after all.
        Assert.Equal(["a\\", "b"], Split("a\\ b"));
    }

    [Fact]
    public void A_Group_That_Opens_Mid_Argument_Extends_That_Argument()
    {
        // The backslash is a byte, and the quote still groups — into the element
        // already being built, because grouping is about whitespace, not about
        // starting a new argument. Both halves are pinned so neither can be
        // "fixed" without the other.
        Assert.Equal(["a\\b c"], Split("a\\\"b c\""));
    }

    [Fact]
    public void A_Single_Quote_Is_An_Ordinary_Character_With_No_Grouping_Meaning()
    {
        // Deliberately not shell semantics: there is no single-quote rule, so
        // 'my file' is two arguments and the quote characters survive.
        Assert.Equal(["'my", "file'"], Split("'my file'"));
    }

    [Fact]
    public void Shell_Metacharacters_Are_Bytes_In_An_Argument()
    {
        // No globbing, no expansion, no chaining. The characters arrive in the
        // argument because nothing in Lattice interprets them (§3.2).
        Assert.Equal(["rm", "-rf", "a*b", "$HOME", "x|y", "a&b", "c;d"], Split("rm -rf a*b $HOME x|y a&b c;d"));
    }

    [Fact]
    public void An_Unterminated_Quote_Is_A_Usage_Error()
    {
        AssertRejected("python3 \"agent.py", "unterminated");
    }

    [Fact]
    public void An_Empty_Command_Is_A_Usage_Error()
    {
        AssertRejected(string.Empty, "empty");
        AssertRejected("    ", "empty");
    }

    [Fact]
    public void A_Command_Whose_Program_Is_Empty_Is_A_Usage_Error()
    {
        // Splits cleanly into one argument, and there is still no program to run.
        AssertRejected("\"\"", "program");
    }

    [Fact]
    public void A_Null_Command_Line_Is_A_Usage_Error()
    {
        Assert.False(AgentCommandLine.TrySplit(null, out var argv, out var reason));
        Assert.Empty(argv);
        Assert.NotEmpty(reason);
    }

    /// <summary>Splits, asserting success, and returns the argv.</summary>
    private static string[] Split(string commandLine)
    {
        Assert.True(
            AgentCommandLine.TrySplit(commandLine, out var argv, out var reason),
            $"'{commandLine}' was rejected: {reason}");

        return argv;
    }

    /// <summary>Asserts the command line is refused, and that the reason says why.</summary>
    private static void AssertRejected(string commandLine, string expectedFragment)
    {
        Assert.False(AgentCommandLine.TrySplit(commandLine, out var argv, out var reason));
        Assert.Empty(argv);
        Assert.Contains(expectedFragment, reason, StringComparison.OrdinalIgnoreCase);
    }
}
