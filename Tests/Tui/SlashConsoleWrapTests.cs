using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Stage 2: the results workspace wraps instead of clipping. Every character
/// of every line stays visible across rows; nothing is replaced by an ellipsis.
/// </summary>
public class SlashConsoleWrapTests
{
    [Fact]
    public void ShortLineIsUnchanged()
    {
        Assert.Equal(["abc"], SlashConsoleWrap.Wrap("abc", 80));
    }

    [Fact]
    public void LongLineWrapsAtWordBoundaries()
    {
        Assert.Equal(["aaa bbb", "ccc"], SlashConsoleWrap.Wrap("aaa bbb ccc", 7));
    }

    [Fact]
    public void LongWordBreaksMidWord()
    {
        Assert.Equal(["abc", "def", "gh"], SlashConsoleWrap.Wrap("abcdefgh", 3));
    }

    [Fact]
    public void WrappedRowsLoseNoCharacters()
    {
        const string text = "{\"zones\": 12, \"resources\": 8, \"seed\": 42} with a trailing tail";
        var rows = SlashConsoleWrap.Wrap(text, 20);
        Assert.True(rows.Count > 1);
        Assert.Equal(text, string.Join(' ', rows));
    }

    [Fact]
    public void NewlinesSplitFirst()
    {
        Assert.Equal(["a", "b"], SlashConsoleWrap.Wrap("a\nb", 10));
    }

    [Fact]
    public void EmptyTextYieldsOneBlankRow()
    {
        Assert.Equal([""], SlashConsoleWrap.Wrap(string.Empty, 10));
    }

    [Fact]
    public void TinyWidthBreaksEveryCharacter()
    {
        Assert.Equal(["a", "b"], SlashConsoleWrap.Wrap("ab", 0));
    }

    [Fact]
    public void WrapLinesHandlesManyLines()
    {
        Assert.Equal(["aa", "bb", "c"], SlashConsoleWrap.WrapLines(["aa", "bb", "c"], 10));
        Assert.Equal(["aa", "b", "b"], SlashConsoleWrap.WrapLines(["aa bb"], 3));
    }
}
