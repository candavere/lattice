using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// The one function that decides what a pane may draw: a string made safe for a
/// one-character-per-cell grid, and cut to the pane's width in whole columns
/// with the cut marked.
/// </summary>
/// <remarks>
/// The width arithmetic in these tests is written out longhand rather than
/// delegated back to the helper, so a test cannot pass because the helper
/// agrees with itself.
/// </remarks>
public class CellTextTests
{
    /// <summary>The Unicode replacement character, which is itself one column.</summary>
    private const string Replaced = "\uFFFD";

    [Fact]
    public void AStringThatFitsIsReturnedUnchanged()
    {
        Assert.Equal("abc", CellText.Clip("abc", 3, GlyphMode.Unicode));
        Assert.Equal("abc", CellText.Clip("abc", 4, GlyphMode.Unicode));
        Assert.Equal(string.Empty, CellText.Clip(string.Empty, 0, GlyphMode.Unicode));
    }

    [Fact]
    public void TheSameStringInstanceComesBackWhenNothingNeededDoing()
    {
        var text = "agent0: Wait";

        Assert.Same(text, CellText.Clip(text, 20, GlyphMode.Unicode));
        Assert.Same(text, CellText.Sanitize(text, GlyphMode.Unicode));
    }

    [Fact]
    public void AClippedUnicodeStringKeepsWidthMinusOneColumnsAndEndsInOneEllipsis()
    {
        // Five columns: four of text and one marker. The marker is one column
        // because the helper only chooses it after measuring it.
        Assert.Equal("abcd\u2026", CellText.Clip("abcdefgh", 5, GlyphMode.Unicode));
        Assert.Equal(5, CellText.Clip("abcdefgh", 5, GlyphMode.Unicode).Length);
    }

    [Fact]
    public void AClippedAsciiStringKeepsWidthMinusThreeColumnsAndEndsInThreeDots()
    {
        Assert.Equal("ab...", CellText.Clip("abcdefgh", 5, GlyphMode.Ascii));
        Assert.Equal(5, CellText.Clip("abcdefgh", 5, GlyphMode.Ascii).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void ASmallWidthNeverThrowsAndNeverExceedsTheWidth(int width)
    {
        foreach (var mode in new[] { GlyphMode.Unicode, GlyphMode.Ascii })
        {
            var clipped = CellText.Clip("a-very-long-recorded-action-list", width, mode);

            Assert.True(
                clipped.Length <= width,
                $"width {width} in {mode} mode produced {clipped.Length} columns: '{clipped}'.");
        }
    }

    [Theory]
    [InlineData(1, ".")]
    [InlineData(2, "..")]
    [InlineData(3, "...")]
    public void AnAsciiMarkerTooWideForThePaneIsCutToThePane(int width, string expected)
    {
        // Three dots do not fit in one column. The marker is cut rather than
        // dropped, because a clipped row that shows no mark at all is the defect
        // this function exists to fix.
        Assert.Equal(expected, CellText.Clip("abcdefgh", width, GlyphMode.Ascii));
    }

    [Theory]
    [InlineData(1, "\u2026")]
    [InlineData(2, "a\u2026")]
    [InlineData(3, "ab\u2026")]
    public void AUnicodeMarkerIsOneColumnSoItFitsWherever(int width, string expected)
    {
        Assert.Equal(expected, CellText.Clip("abcdefgh", width, GlyphMode.Unicode));
    }

    [Fact]
    public void ANegativeWidthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CellText.Clip("abc", -1, GlyphMode.Unicode));
    }

    [Fact]
    public void TheMarkerIsOnlyChosenWhenItIsItselfOneColumn()
    {
        // The choice is measured, not assumed: the Unicode marker is used only
        // because the helper's own width contract says it is one column.
        Assert.True(CellText.IsSingleColumn(CellText.UnicodeEllipsis));
        Assert.Equal(CellText.UnicodeEllipsis.ToString(), CellText.Ellipsis(GlyphMode.Unicode));
        Assert.Equal(CellText.AsciiEllipsis, CellText.Ellipsis(GlyphMode.Ascii));
    }

    [Fact]
    public void PlainAsciiNeedsNoSanitising()
    {
        Assert.Equal("Sentry -> 12", CellText.Sanitize("Sentry -> 12", GlyphMode.Ascii));
        Assert.Equal("Sentry -> 12", CellText.Sanitize("Sentry -> 12", GlyphMode.Unicode));
    }

    [Theory]
    [InlineData("a\u0007b")]
    [InlineData("a\u001bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u009bb")]
    public void AControlCharacterIsReplaced(string text)
    {
        Assert.Equal("a" + Replaced + "b", CellText.Sanitize(text, GlyphMode.Unicode));
        Assert.Equal("a?b", CellText.Sanitize(text, GlyphMode.Ascii));
    }

    [Fact]
    public void AZeroWidthJoinerAndItsNeighbourAreReplaced()
    {
        Assert.Equal("a" + Replaced + "b", CellText.Sanitize("a\u200db", GlyphMode.Unicode));
        Assert.Equal("a" + Replaced + "b", CellText.Sanitize("a\u200cb", GlyphMode.Unicode));
    }

    [Fact]
    public void ACombiningMarkIsReplaced()
    {
        Assert.Equal("a" + Replaced + "b", CellText.Sanitize("a\u0301b", GlyphMode.Unicode));
    }

    [Fact]
    public void AVariationSelectorIsReplaced()
    {
        Assert.Equal(Replaced, CellText.Sanitize("\uFE0F", GlyphMode.Unicode));
        Assert.Equal("?", CellText.Sanitize("\uFE0F", GlyphMode.Ascii));
    }

    [Fact]
    public void ACjkIdeographIsReplacedBecauseItIsTwoColumns()
    {
        Assert.Equal(Replaced + Replaced, CellText.Sanitize("\u6F22\u5B57", GlyphMode.Unicode));
        Assert.Equal("??", CellText.Sanitize("\u6F22\u5B57", GlyphMode.Ascii));
    }

    [Fact]
    public void AFullwidthFormIsReplacedBecauseItIsTwoColumns()
    {
        Assert.Equal(Replaced, CellText.Sanitize("\uFF01", GlyphMode.Unicode));
    }

    [Fact]
    public void AnEmojiBecomesOneReplacementColumnAndIsNeverSplit()
    {
        // U+1F600 is a surrogate pair: two UTF-16 units, one code point, and two
        // columns in a terminal that has it. Both units are consumed and one
        // column is emitted, so the pair cannot be cut in half at a clip boundary.
        Assert.Equal(Replaced, CellText.Sanitize("\uD83D\uDE00", GlyphMode.Unicode));
        Assert.Equal(1, CellText.Sanitize("\uD83D\uDE00", GlyphMode.Unicode).Length);
        Assert.Equal("?", CellText.Sanitize("\uD83D\uDE00", GlyphMode.Ascii));
    }

    [Fact]
    public void ALoneSurrogateIsReplacedRatherThanCarriedThrough()
    {
        Assert.Equal("a" + Replaced + "b", CellText.Sanitize("a\uD83Db", GlyphMode.Unicode));
        Assert.Equal("a" + Replaced, CellText.Sanitize("a\uDE00", GlyphMode.Unicode));
    }

    [Fact]
    public void TheContractIsStatedOnTheSanitisedString()
    {
        // Two UTF-16 units that sanitise to one column, in a pane one column wide:
        // the sanitised string fits, so it is not clipped and there is no mark.
        Assert.Equal(Replaced, CellText.Clip("\uD83D\uDE00", 1, GlyphMode.Unicode));
    }

    [Theory]
    [InlineData('a', true)]
    [InlineData(' ', true)]
    [InlineData('\u2500', true)] // box drawing: the glyph set this library draws with
    [InlineData('\u25A0', true)] // geometric shapes
    [InlineData('\u2588', true)] // block elements
    [InlineData('\u2026', true)] // horizontal ellipsis
    [InlineData('\uFFFD', true)] // the replacement character itself
    [InlineData('\u6F22', false)] // CJK unified ideograph
    [InlineData('\uFF01', false)] // fullwidth exclamation
    [InlineData('\u0301', false)] // combining acute accent
    [InlineData('\u200D', false)] // zero width joiner
    [InlineData('\uFE0F', false)] // variation selector 16
    [InlineData('\u001B', false)] // escape
    public void OneColumnIsAStatedRulePerCodePoint(char value, bool expected)
    {
        Assert.Equal(expected, CellText.IsSingleColumn(value));
    }

    [Fact]
    public void EveryAsciiModeResultOverTheAwkwardInputsIsAscii()
    {
        foreach (var input in new[]
        {
            "\u6F22\u5B57 role",
            "\uD83D\uDE00\uD83D\uDE01",
            "a\u0301b",
            "a\u0007b",
            "\uFE0F\u200D",
            "plain recorded action text",
        })
        {
            var sanitised = CellText.Sanitize(input, GlyphMode.Ascii);

            Assert.All(sanitised, glyph => Assert.True(glyph < 128, $"'{glyph}' is not ASCII."));
        }
    }

    [Fact]
    public void ClippingTwiceChangesNothing()
    {
        // The panes compose: a role column is cut to twelve, then the row it is
        // part of is cut to the pane. The second cut must find nothing to cut, or
        // the ellipsis of one column would be eaten by the ellipsis of the next.
        foreach (var mode in new[] { GlyphMode.Unicode, GlyphMode.Ascii })
        {
            var once = CellText.Clip(CellText.Clip("SentryPatrolAgentLong", 12, mode), 12, mode);

            Assert.Equal(CellText.Clip("SentryPatrolAgentLong", 12, mode), once);
        }
    }

    [Fact]
    public void NoResultEverContainsAHalfSurrogate()
    {
        foreach (var mode in new[] { GlyphMode.Unicode, GlyphMode.Ascii })
        {
            foreach (var input in new[] { "\uD83D", "\uDE00", "x\uD83D", "\uD83Dy", "\uD83D\uDE00" })
            {
                var clipped = CellText.Clip(input, 4, mode);

                Assert.All(clipped, glyph => Assert.False(char.IsSurrogate(glyph), $"'{glyph}' is half a surrogate pair."));
            }
        }
    }
}