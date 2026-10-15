using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Stage 1: the dotted wordmark data, its guards, and the narrow Braille exception.
/// </summary>
public class WordmarkTests
{
    [Fact]
    public void RowsAreExactlyEightRowsOfFiftyEightCharacters()
    {
        Assert.Equal(Wordmark.Height, Wordmark.Rows.Length);
        Assert.Equal(8, Wordmark.Rows.Length);
        foreach (var row in Wordmark.Rows)
        {
            Assert.Equal(Wordmark.Width, row.Length);
            Assert.Equal(58, row.Length);
        }
    }

    [Fact]
    public void MaskIsSameShapeUsingOnlyFAndDot()
    {
        Assert.Equal(8, Wordmark.Mask.Length);
        foreach (var row in Wordmark.Mask)
        {
            Assert.Equal(58, row.Length);
            foreach (var c in row)
            {
                Assert.True(c == 'F' || c == '.', $"mask char '{c}' is not F or dot.");
            }
        }

        // Flat-word rule: no depth/shading cells.
        Assert.DoesNotContain('S', string.Concat(Wordmark.Mask));
    }

    [Fact]
    public void MaskMatchesRowsProgrammatically()
    {
        for (var r = 0; r < Wordmark.Height; r++)
        {
            for (var c = 0; c < Wordmark.Width; c++)
            {
                var mask = Wordmark.Mask[r][c];
                var glyph = Wordmark.Rows[r][c];
                if (mask == '.')
                {
                    Assert.True(glyph == ' ', $"row {r} col {c}: mask dot but glyph U+{(int)glyph:X4}.");
                }
                else
                {
                    Assert.True(glyph != ' ' && glyph != '\u2800', $"row {r} col {c}: mask {mask} but blank glyph.");
                    Assert.True(Wordmark.IsBraille(glyph), $"row {r} col {c}: mask {mask} but glyph U+{(int)glyph:X4} is not Braille.");
                }
            }
        }
    }

    [Fact]
    public void EveryWordmarkCharacterIsBrailleOrSpace()
    {
        foreach (var row in Wordmark.Rows)
        {
            foreach (var c in row)
            {
                Assert.True(Wordmark.IsWordmarkChar(c), $"U+{(int)c:X4} is not Braille or space.");
            }
        }
    }

    [Fact]
    public void BrailleRangeConstantCoversExactlyU2800ToU28FF()
    {
        Assert.Equal(0x2800, Wordmark.BrailleRangeStart);
        Assert.Equal(0x28FF, Wordmark.BrailleRangeEnd);
        Assert.True(Wordmark.IsBraille('\u2800'));
        Assert.True(Wordmark.IsBraille('⣿'));
        Assert.False(Wordmark.IsBraille(' '));
        Assert.False(Wordmark.IsBraille('─'));
    }

    [Fact]
    public void WordmarkColoursAreNearWhiteForegroundAndDepthStep()
    {
        Assert.Equal("#eeeeee", Wordmark.Foreground.ToHex());
        Assert.Equal("#3c3c3c", Wordmark.Depth.ToHex());
    }

    // The three source facts re-verified by tests (checked on 07dcc6d).

    [Fact]
    public void CellBufferDrawTextWritesBrailleWithoutSanitising()
    {
        var buffer = new CellBuffer(4, 1);
        var style = new Cell(' ', Rgb.FromHex("#eeeeee"));
        buffer.DrawText(0, 0, "\u2800⣿ ", style);
        Assert.Equal('\u2800', buffer[0, 0].Glyph);
        Assert.Equal('⣿', buffer[1, 0].Glyph);
        Assert.Equal(' ', buffer[2, 0].Glyph);
    }

    [Fact]
    public void CellTextTreatsBrailleAsSingleColumn()
    {
        Assert.True(CellText.IsSingleColumn('\u2800'));
        Assert.True(CellText.IsSingleColumn('⣿'));
        var text = "\u2800⣿";
        Assert.Equal(text, CellText.Sanitize(text, GlyphMode.Unicode));
    }

    [Fact]
    public void GlyphsResolveThrowsForBraille()
    {
        // At source (07dcc6d): Resolve passes Unicode through when utf8 is true and
        // only throws via ToAscii when utf8 is false. The wordmark must still never
        // go through Resolve: the ASCII path would throw and crash the fallback.
        Assert.Equal('⣿', Glyphs.Resolve('⣿', utf8: true));
        Assert.Throws<KeyNotFoundException>(() => Glyphs.Resolve('⣿', utf8: false));
        Assert.Throws<KeyNotFoundException>(() => Glyphs.ToAscii('⣿'));
    }

    [Fact]
    public void WordmarkModuleNeverCallsTheAsciiResolverPath()
    {
        var dir = FindRepoRoot();
        var sources = Directory.GetFiles(Path.Combine(dir, "Tui"), "Wordmark*.cs");
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Glyphs.Resolve(", text);
            Assert.DoesNotContain("ToAscii(", text);
            Assert.DoesNotContain("GlyphModes.Glyph(", text);
            Assert.DoesNotContain("Glyphs.Glyph(", text);
        }
    }

    [Fact]
    public void NoOtherProductionFileEmitsBraille()
    {
        var dir = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.StartsWith("Wordmark", StringComparison.Ordinal))
            {
                continue;
            }

            // Test files pin behaviour; only production code is gated here.
            if (file.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var c in text)
            {
                if (c >= 0x2800 && c <= 0x28FF)
                {
                    offenders.Add(file);
                    break;
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ShouldShowWordmarkGuards()
    {
        // Normal terminal shows it.
        Assert.True(Wordmark.ShouldShowWordmark(100, 32, utf8: true, GlyphMode.Unicode, menuOpen: false));
        Assert.True(Wordmark.ShouldShowWordmark(80, 24, utf8: true, GlyphMode.Unicode, menuOpen: false));
        Assert.True(Wordmark.ShouldShowWordmark(62, 16, utf8: true, GlyphMode.Unicode, menuOpen: false));
        // Menu open needs 62x30.
        Assert.True(Wordmark.ShouldShowWordmark(100, 30, utf8: true, GlyphMode.Unicode, menuOpen: true));
        Assert.True(Wordmark.ShouldShowWordmark(62, 30, utf8: true, GlyphMode.Unicode, menuOpen: true));
        Assert.False(Wordmark.ShouldShowWordmark(61, 30, utf8: true, GlyphMode.Unicode, menuOpen: true));
        Assert.False(Wordmark.ShouldShowWordmark(62, 29, utf8: true, GlyphMode.Unicode, menuOpen: true));
        // Size guards.
        Assert.False(Wordmark.ShouldShowWordmark(61, 32, utf8: true, GlyphMode.Unicode, menuOpen: false));
        Assert.False(Wordmark.ShouldShowWordmark(62, 15, utf8: true, GlyphMode.Unicode, menuOpen: false));
        Assert.False(Wordmark.ShouldShowWordmark(60, 20, utf8: true, GlyphMode.Unicode, menuOpen: false));
        Assert.False(Wordmark.ShouldShowWordmark(40, 12, utf8: true, GlyphMode.Unicode, menuOpen: false));
        // ASCII mode and non-UTF-8 never show Braille.
        Assert.False(Wordmark.ShouldShowWordmark(100, 32, utf8: true, GlyphMode.Ascii, menuOpen: false));
        Assert.False(Wordmark.ShouldShowWordmark(100, 32, utf8: false, GlyphMode.Unicode, menuOpen: false));
        Assert.False(Wordmark.ShouldShowWordmark(100, 32, utf8: false, GlyphMode.Ascii, menuOpen: false));
    }

    [Fact]
    public void AsciiModeNeverEmitsBraille()
    {
        var buffer = new CellBuffer(80, 24);
        var showed = Wordmark.Draw(buffer, 11, 2, 80, 24, utf8: true, GlyphMode.Ascii, ColorDepth.TrueColor, menuOpen: false);
        Assert.False(showed);
        AssertNoBraille(buffer);
        Assert.Contains(Wordmark.PlainTitle, string.Concat(buffer.ToLines()));
    }

    [Fact]
    public void NonUtf8LocaleNeverEmitsBraille()
    {
        var buffer = new CellBuffer(80, 24);
        var showed = Wordmark.Draw(buffer, 11, 2, 80, 24, utf8: false, GlyphMode.Unicode, ColorDepth.TrueColor, menuOpen: false);
        Assert.False(showed);
        AssertNoBraille(buffer);
        Assert.Contains(Wordmark.PlainTitle, string.Concat(buffer.ToLines()));
    }

    [Theory]
    [InlineData(100, 32, false)]
    [InlineData(80, 24, false)]
    [InlineData(62, 16, false)]
    [InlineData(60, 20, false)]
    [InlineData(40, 12, false)]
    [InlineData(100, 30, true)]
    [InlineData(62, 30, true)]
    [InlineData(61, 30, true)]
    [InlineData(62, 29, true)]
    public void PlainTitleIsVisibleAtEverySizeGuard(int width, int height, bool menuOpen)
    {
        var buffer = new CellBuffer(Math.Max(width, 10), Math.Max(height, 10));
        var showed = Wordmark.Draw(buffer, 0, 0, width, height, utf8: true, GlyphMode.Unicode, ColorDepth.TrueColor, menuOpen);
        if (showed)
        {
            // Wordmark shown: the 8x58 block drew Braille, and the plain title stays
            // available for the console's always-visible header/bottom line.
            AssertContainsBraille(buffer);
            var titleBuffer = new CellBuffer(Math.Max(width, 10), 1);
            Wordmark.DrawPlainTitleAt(titleBuffer, 0, 0, ColorDepth.TrueColor);
            Assert.Contains(Wordmark.PlainTitle, string.Concat(titleBuffer.ToLines()));
        }
        else
        {
            // Fallback: no Braille, plain title in the frame.
            AssertNoBraille(buffer);
            Assert.Contains(Wordmark.PlainTitle, string.Concat(buffer.ToLines()));
        }
    }

    private static void AssertContainsBraille(CellBuffer buffer)
    {
        var found = false;
        foreach (var line in buffer.ToLines())
        {
            foreach (var c in line)
            {
                if (c >= 0x2800 && c <= 0x28FF && c != ' ')
                {
                    found = true;
                }
            }
        }

        Assert.True(found, "expected Braille in the drawn wordmark block.");
    }

    [Fact]
    public void SmallTerminalFallsBackToPlainTitleWithoutBraille()
    {
        var buffer = new CellBuffer(40, 12);
        var showed = Wordmark.Draw(buffer, 0, 0, 40, 12, utf8: true, GlyphMode.Unicode, ColorDepth.TrueColor, menuOpen: false);
        Assert.False(showed);
        AssertNoBraille(buffer);
        Assert.Contains(Wordmark.PlainTitle, string.Concat(buffer.ToLines()));
    }

    [Fact]
    public void DrawRowsSetsUniformNearWhiteForeground()
    {
        foreach (var depth in new[] { ColorDepth.TrueColor, ColorDepth.Ansi256, ColorDepth.Ansi16, ColorDepth.None })
        {
            var buffer = new CellBuffer(Wordmark.Width, Wordmark.Height);
            Wordmark.DrawRows(buffer, 0, 0, depth);
            for (var r = 0; r < Wordmark.Height; r++)
            {
                for (var c = 0; c < Wordmark.Width; c++)
                {
                    var mask = Wordmark.Mask[r][c];
                    var cell = buffer[c, r];
                    if (mask == '.')
                    {
                        Assert.Equal(' ', cell.Glyph);
                    }
                    else
                    {
                        Assert.Equal('F', mask);
                        Assert.Equal(Wordmark.Rows[r][c], cell.Glyph);
                        if (depth != ColorDepth.None)
                        {
                            Assert.Equal(Wordmark.Foreground, cell.Foreground);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void DrawRowsHasNoDepthCellsAtAnyDepth()
    {
        foreach (var depth in new[] { ColorDepth.TrueColor, ColorDepth.Ansi256, ColorDepth.Ansi16, ColorDepth.None })
        {
            var buffer = new CellBuffer(Wordmark.Width, Wordmark.Height);
            Wordmark.DrawRows(buffer, 0, 0, depth);
            for (var r = 0; r < Wordmark.Height; r++)
            {
                for (var c = 0; c < Wordmark.Width; c++)
                {
                    var cell = buffer[c, r];
                    Assert.NotEqual(Wordmark.Depth, cell.Foreground);
                }
            }
        }

        Assert.Equal(0, CountMask('S'));
        Assert.True(CountMask('F') > 0, "expected foreground cells in the mask.");
    }

    [Theory]
    [InlineData(ColorDepth.TrueColor)]
    [InlineData(ColorDepth.Ansi256)]
    [InlineData(ColorDepth.Ansi16)]
    [InlineData(ColorDepth.None)]
    public void WordmarkRendersThroughFrameDiffWithRightColours(ColorDepth depth)
    {
        var buffer = new CellBuffer(Wordmark.Width, Wordmark.Height);
        Wordmark.DrawRows(buffer, 0, 0, depth);
        var bytes = FrameDiff.Render(null, buffer, depth);

        if (depth == ColorDepth.None)
        {
            // No colour channels are emitted, but absolute cursor positioning still is.
            Assert.DoesNotContain("38;", bytes);
            Assert.DoesNotContain("48;", bytes);
        }
        else
        {
            Assert.Contains("[", bytes);
        }

        // Every drawn letter glyph reaches the emitted bytes in its cell.
        for (var r = 0; r < Wordmark.Height; r++)
        {
            for (var c = 0; c < Wordmark.Width; c++)
            {
                var mask = Wordmark.Mask[r][c];
                var glyph = Wordmark.Rows[r][c];
                if (mask == 'F')
                {
                    Assert.Contains(glyph.ToString(), bytes);
                }
            }
        }

        if (depth == ColorDepth.TrueColor)
        {
            // Flat word: uniform near-white #eeeeee -> 238;238;238, no depth step.
            Assert.Contains("38;2;238;238;238", bytes);
            Assert.DoesNotContain("38;2;60;60;60", bytes);
        }

        Assert.Equal(0, CountMask('S'));
    }

    private static int CountMask(char kind)
    {
        var count = 0;
        foreach (var row in Wordmark.Mask)
        {
            foreach (var c in row)
            {
                if (c == kind)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static void AssertNoBraille(CellBuffer buffer)
    {
        foreach (var line in buffer.ToLines())
        {
            foreach (var c in line)
            {
                Assert.False(c >= 0x2800 && c <= 0x28FF, $"found Braille U+{(int)c:X4} where plain title was required.");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tui", "Wordmark.cs")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.True(dir is not null, "could not locate the repository root from the test host.");
        return dir!;
    }
}
