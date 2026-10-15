using Lattice.Cli.Presentation;
using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Stage 3: the slash console as pure layout. Menu model over the Launchpad catalog,
/// guards, colours from the shared theme only, and frozen text frames.
/// </summary>
public class SlashConsoleTests
{
    private static SlashConsoleRequest Home(PaneSize size, GlyphMode glyphs = GlyphMode.Unicode, bool utf8 = true) =>
        new("", [], 0, false, "/Users/test/lattice", null, null, null, false, size, glyphs, ColorDepth.TrueColor, utf8);

    private static SlashConsoleRequest Menu(string input, int selected, PaneSize size, GlyphMode glyphs = GlyphMode.Unicode, bool utf8 = true) =>
        new(input, SlashConsoleMenu.Filter(input), selected, true, "/Users/test/lattice", null, null, null, false, size, glyphs, ColorDepth.TrueColor, utf8);

    [Fact]
    public void MenuListsNineCatalogCommandsInOrderWithTheirOwnDescriptionsPlusExit()
    {
        var entries = SlashConsoleMenu.AllEntries();
        Assert.Equal(LaunchpadCatalog.Commands.Count + 1, entries.Count);
        for (var i = 0; i < LaunchpadCatalog.Commands.Count; i++)
        {
            Assert.Equal("/" + LaunchpadCatalog.Commands[i].Name, entries[i].Name);
            Assert.Equal(LaunchpadCatalog.Commands[i].Summary, entries[i].Description);
        }

        Assert.Equal(SlashConsoleMenu.ExitName, entries[^1].Name);
        Assert.Equal(SlashConsoleMenu.ExitDescription, entries[^1].Description);
    }

    [Fact]
    public void FilterMatchesPrefixCaseInsensitivelyAndQuitAlias()
    {
        Assert.Equal(10, SlashConsoleMenu.Filter("/").Count);
        Assert.Single(SlashConsoleMenu.Filter("/sim"));
        Assert.Equal("/simulate", SlashConsoleMenu.Filter("/sim")[0].Name);
        Assert.Single(SlashConsoleMenu.Filter("/SIM"));
        var e = SlashConsoleMenu.Filter("/e");
        Assert.Contains(e, x => x.Name == "/evaluate");
        Assert.Contains(e, x => x.Name == "/exit");
        Assert.Single(SlashConsoleMenu.Filter("/quit"));
        Assert.Equal("/exit", SlashConsoleMenu.Filter("/quit")[0].Name);
        Assert.Single(SlashConsoleMenu.Filter("/q"));
        Assert.Empty(SlashConsoleMenu.Filter("/x"));
        Assert.Empty(SlashConsoleMenu.Filter("simulate"));
    }

    [Fact]
    public void ToCommandLineStripsOneLeadingSlash()
    {
        Assert.Equal("lattice simulate --seed 42", SlashConsoleMenu.ToCommandLine("/simulate --seed 42"));
        Assert.Equal("lattice", SlashConsoleMenu.ToCommandLine("/"));
        Assert.True(SlashConsoleMenu.IsExit("/exit"));
        Assert.True(SlashConsoleMenu.IsExit("/quit"));
        Assert.True(SlashConsoleMenu.IsExit("/EXIT"));
        Assert.False(SlashConsoleMenu.IsExit("/simulate"));
    }

    [Fact]
    public void HomeShowsWordmarkPlaceholderCwdAndPlainTitle()
    {
        var req = Home(new PaneSize(100, 32));
        var cells = SlashConsoleLayout.Render(req);
        var text = string.Concat(cells.ToLines());
        Assert.Contains(SlashConsoleLayout.Placeholder, text);
        Assert.Contains("/Users/test/lattice", text);
        Assert.Contains("⣀", text);
        // No competing top-left header: row 0 stays blank.
        Assert.Equal(' ', cells[0, 0].Glyph);
        Assert.Equal(' ', cells[2, 0].Glyph);
        // Blue left edge on the centred bounded input bar, at the shared input row.
        var (colX, colW) = SlashConsoleLayout.ContentColumn(100);
        Assert.True(colX > 0, "input column must leave breathing room.");
        var cursor = SlashConsoleLayout.InputCursor(req);
        Assert.Equal(Palette.AccentSecondary, cells[colX, cursor.Row].Foreground);
        // Input does not fill the full terminal width.
        Assert.Equal(' ', cells[0, cursor.Row].Glyph);
    }

    [Fact]
    public void PlainTitleFallbackShowsWhenWordmarkCannot()
    {
        // Small, ASCII and non-UTF-8 terminals fall back to the centred plain title.
        foreach (var size in new[] { new PaneSize(60, 20), new PaneSize(40, 12) })
        {
            var cells = SlashConsoleLayout.Render(Home(size));
            Assert.Contains(Wordmark.PlainTitle, string.Concat(cells.ToLines()));
        }

        var ascii = SlashConsoleLayout.Render(Home(new PaneSize(100, 32), GlyphMode.Ascii, utf8: false));
        Assert.Contains(Wordmark.PlainTitle, string.Concat(ascii.ToLines()));
    }

    [Fact]
    public void SmallTerminalsFallBackWithoutBraille()
    {
        foreach (var size in new[] { new PaneSize(60, 20), new PaneSize(40, 12) })
        {
            var cells = SlashConsoleLayout.Render(Home(size));
            foreach (var line in cells.ToLines())
            {
                foreach (var c in line)
                {
                    Assert.False(c >= 0x2800 && c <= 0x28FF, $"Braille at {size.Width}x{size.Height}.");
                }
            }

            Assert.Contains(Wordmark.PlainTitle, string.Concat(cells.ToLines()));
        }
    }

    [Fact]
    public void MenuOpenBelowThirtyRowsFallsBackWithoutBraille()
    {
        var cells = SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(62, 29)));
        foreach (var line in cells.ToLines())
        {
            foreach (var c in line)
            {
                Assert.False(c >= 0x2800 && c <= 0x28FF, "Braille below 62x30 with menu open.");
            }
        }

        Assert.Contains(Wordmark.PlainTitle, string.Concat(cells.ToLines()));
    }

    [Fact]
    public void AsciiNeverEmitsBraille()
    {
        foreach (var req in new[]
        {
            Home(new PaneSize(100, 32), GlyphMode.Ascii, utf8: false),
            Menu("/", 0, new PaneSize(100, 32), GlyphMode.Ascii, utf8: false),
        })
        {
            var cells = SlashConsoleLayout.Render(req);
            foreach (var line in cells.ToLines())
            {
                foreach (var c in line)
                {
                    Assert.True(c < 128, $"'{c}' U+{(int)c:X4} is not ASCII.");
                }
            }

            Assert.Contains(Wordmark.PlainTitle, string.Concat(cells.ToLines()));
        }
    }

    [Fact]
    public void SelectedRowIsOrangeOnTheElementBand()
    {
        var size = new PaneSize(100, 32);
        var req = Menu("/", 0, size);
        var cells = SlashConsoleLayout.Render(req);
        var (colX, _) = SlashConsoleLayout.ContentColumn(size.Width);
        var inputRow = SlashConsoleLayout.InputCursor(req).Row;
        // Menu sits above the input in the same centred column; selected row carries the orange edge.
        var menuRow = inputRow - SlashConsoleMenu.Filter("/").Count - 1;
        var edge = cells[colX, menuRow];
        Assert.Equal(Palette.Accent, edge.Foreground);
        Assert.Equal(Theme.SelectionBackground, cells[colX + 5, menuRow].Background);
        Assert.Equal(Palette.Accent, cells[colX + 2, menuRow].Foreground);
        // Unselected row: text and muted, no selection band.
        Assert.Equal(Palette.TextPrimary, cells[colX + 2, menuRow + 1].Foreground);
    }

    [Fact]
    public void InputCursorAlignsWithRenderedInput()
    {
        // Empty line: cursor at the text origin over the placeholder.
        var home = Home(new PaneSize(100, 32));
        var (colX, _) = SlashConsoleLayout.ContentColumn(100);
        Assert.Equal((PlaceholderRow(SlashConsoleLayout.Render(home).ToLines()), colX + 2), SlashConsoleLayout.InputCursor(home));

        // Typed input: cursor just past the last visible character.
        var typed = home with { Input = "/sim", Menu = SlashConsoleMenu.Filter("/sim"), MenuOpen = true };
        var typedCursor = SlashConsoleLayout.InputCursor(typed);
        Assert.Equal(colX + 2 + "/sim".Length, typedCursor.Col);
        var typedLines = SlashConsoleLayout.Render(typed).ToLines();
        Assert.Equal('/', typedLines[typedCursor.Row][typedCursor.Col - "/sim".Length]);

        // Clipped input: cursor stays on the last visible column.
        var longReq = home with { Input = "/" + new string('x', 200) };
        var longCursor = SlashConsoleLayout.InputCursor(longReq);
        var (longColX, longColW) = SlashConsoleLayout.ContentColumn(100);
        Assert.Equal(longColX + longColW - 1, longCursor.Col);
    }

    [Fact]
    public void ResultIsClippedWithEllipsisAndTruncationMarker()
    {
        var req = new SlashConsoleRequest(
            "/generate --seed 42", [], 0, false, "/Users/test/lattice", null,
            "exit 0 ok 0.4s",
            ["{\"zones\": 12} with a very long trailing tail that must be clipped with an ellipsis mark and more words to overflow the pane width surely"],
            true, new PaneSize(100, 32), GlyphMode.Unicode, ColorDepth.TrueColor, true);
        var cells = SlashConsoleLayout.Render(req);
        var text = string.Join('\n', cells.ToLines());
        Assert.Contains("exit 0", text);
        Assert.Contains(SlashConsoleLayout.TruncationMarker, text);
        Assert.True(text.Contains("…", StringComparison.Ordinal) || text.Contains("...", StringComparison.Ordinal), "result must be clipped with an ellipsis mark.");
    }

    [Fact]
    public void MenuSitsAboveInputInTheSameCentredColumn()
    {
        var size = new PaneSize(100, 32);
        var cells = SlashConsoleLayout.Render(Menu("/", 0, size));
        var lines = cells.ToLines();
        var menuRow = Array.FindIndex(lines, l => l.Contains("/generate"));
        var inputRow = Array.FindLastIndex(lines, l => l.Contains("│ /") || l.Contains("| /"));
        Assert.True(menuRow >= 0 && inputRow >= 0, "menu and input must both render.");
        Assert.True(menuRow < inputRow, "the slash menu must sit above the input.");
        var (colX, colW) = SlashConsoleLayout.ContentColumn(size.Width);
        Assert.Contains("/generate", lines[menuRow].Substring(colX, Math.Min(colW, lines[menuRow].Length - colX)));
    }

    [Fact]
    public void WordmarkIsUniformNearWhiteWithNoSplitOrDepth()
    {
        var cells = SlashConsoleLayout.Render(Home(new PaneSize(100, 32)));
        var seen = 0;
        for (var y = 0; y < cells.Height; y++)
        {
            for (var x = 0; x < cells.Width; x++)
            {
                var cell = cells[x, y];
                if (cell.Glyph >= 0x2800 && cell.Glyph <= 0x28FF)
                {
                    seen++;
                    Assert.Equal(Wordmark.Foreground, cell.Foreground);
                }
            }
        }

        Assert.True(seen > 0, "expected wordmark Braille cells.");
    }

    [Fact]
    public void NoDesignReviewLabelsAreRendered()
    {
        var frames = new[]
        {
            SlashConsoleLayout.Render(Home(new PaneSize(100, 32))).ToLines(),
            SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(100, 32))).ToLines(),
            SlashConsoleLayout.Render(Home(new PaneSize(60, 20))).ToLines(),
        };

        foreach (var lines in frames)
        {
            var text = string.Concat(lines).ToLowerInvariant();
            Assert.DoesNotContain("sidepiece", text);
            Assert.DoesNotContain("mockup", text);
            Assert.DoesNotContain("iteration", text);
            Assert.DoesNotContain("caption", text);
            Assert.DoesNotContain("s2", text);
            Assert.DoesNotContain("h2", text);
        }
    }

    [Fact]
    public void WorkingDirectoryIsPinnedBottomLeftOnHomeMenuAndResult()
    {
        var home = SlashConsoleLayout.Render(Home(new PaneSize(100, 32)));
        var menu = SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(100, 32)));
        var resultReq = new SlashConsoleRequest(
            "/generate --seed 1", [], 0, false, "/tmp/outside-repo", null,
            "exit 0 ok 0.1s", ["ok"], false, new PaneSize(100, 32), GlyphMode.Unicode, ColorDepth.TrueColor, true);
        var result = SlashConsoleLayout.Render(resultReq);

        var homeCwd = home.ToLines()[home.Height - 2];
        var menuCwd = menu.ToLines()[menu.Height - 2];
        var resultCwd = result.ToLines()[result.Height - 2];
        Assert.StartsWith("  /Users/test/lattice", homeCwd, StringComparison.Ordinal);
        Assert.StartsWith("  /Users/test/lattice", menuCwd, StringComparison.Ordinal);
        Assert.StartsWith("  /tmp/outside-repo", resultCwd, StringComparison.Ordinal);
    }

    [Fact]
    public void LongWorkingDirectoryClipsWithoutHidingTheLocation()
    {
        var longPath = "/Users/test/" + new string('a', 120) + "/lattice-project";
        var req = Home(new PaneSize(100, 32)) with { WorkingDirectory = longPath };
        var cells = SlashConsoleLayout.Render(req);
        var cwdRow = cells.ToLines()[cells.Height - 2];
        Assert.Contains("lattice-project", cwdRow);
        Assert.True(cwdRow.Trim().Length <= 52, "cwd must clip to roughly half the terminal width.");
    }

    [Fact]
    public void NarrowAndAsciiLayoutsStayFunctionalWithoutOverlap()
    {
        foreach (var size in new[] { new PaneSize(62, 16), new PaneSize(60, 20), new PaneSize(40, 12) })
        {
            var cells = SlashConsoleLayout.Render(Home(size));
            var text = string.Concat(cells.ToLines());
            Assert.Contains(Wordmark.PlainTitle, text);
        }

        var ascii = SlashConsoleLayout.Render(Home(new PaneSize(100, 32), GlyphMode.Ascii, utf8: false));
        Assert.Contains(Wordmark.PlainTitle, string.Concat(ascii.ToLines()));
    }

    private static int FirstBrailleRow(string[] lines)
    {
        for (var y = 0; y < lines.Length; y++)
        {
            foreach (var c in lines[y])
            {
                if (c >= 0x2800 && c <= 0x28FF)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    private static int PlaceholderRow(string[] lines)
    {
        for (var y = 0; y < lines.Length; y++)
        {
            if (lines[y].Contains(SlashConsoleLayout.Placeholder, StringComparison.Ordinal))
            {
                return y;
            }
        }

        return -1;
    }

    private static void AssertCentredGroup(int width, int height, string[] lines, int groupTop, int groupBottom)
    {
        var usable = height - 2;
        Assert.True(groupTop >= 0, "group must not start above the canvas.");
        Assert.True(groupBottom <= usable - 1, "group must not collide with the footer.");
        var above = groupTop;
        var below = usable - (groupBottom + 1);
        Assert.True(Math.Abs(above - below) <= 1, $"free space above ({above}) and below ({below}) must differ by at most one row.");
    }

    [Theory]
    [InlineData(237, 66)]
    [InlineData(120, 40)]
    [InlineData(100, 32)]
    [InlineData(80, 24)]
    [InlineData(62, 16)]
    public void HomeGroupIsCentredInUsableCanvas(int width, int height)
    {
        var lines = SlashConsoleLayout.Render(Home(new PaneSize(width, height))).ToLines();
        var top = FirstBrailleRow(lines);
        var input = PlaceholderRow(lines);
        Assert.True(top >= 0 && input > top, "wordmark and input must both render.");
        AssertCentredGroup(width, height, lines, top, input);
    }

    [Theory]
    [InlineData(100, 31)]
    [InlineData(100, 32)]
    [InlineData(100, 33)]
    public void OddAndEvenHeightsCentreWithinOneRow(int width, int height)
    {
        var lines = SlashConsoleLayout.Render(Home(new PaneSize(width, height))).ToLines();
        AssertCentredGroup(width, height, lines, FirstBrailleRow(lines), PlaceholderRow(lines));
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(100, 32)]
    public void MenuStackIsCentredAsOneGroup(int width, int height)
    {
        var size = new PaneSize(width, height);
        var lines = SlashConsoleLayout.Render(Menu("/", 0, size)).ToLines();
        var top = FirstBrailleRow(lines);
        var input = Array.FindLastIndex(lines, l => l.TrimEnd().EndsWith("│ /", StringComparison.Ordinal) || l.TrimEnd().EndsWith("| /", StringComparison.Ordinal));
        Assert.True(top >= 0 && input > top, "wordmark, menu and input must render.");
        AssertCentredGroup(width, height, lines, top, input);
    }

    [Fact]
    public void HomeAt237x66PinsScreenshotComposition()
    {
        var lines = SlashConsoleLayout.Render(Home(new PaneSize(237, 66))).ToLines();
        Assert.Equal(26, FirstBrailleRow(lines));
        Assert.Equal(33, FirstBrailleRow(lines) + Wordmark.Height - 1);
        Assert.Equal(36, PlaceholderRow(lines));
        Assert.StartsWith("  /Users/test/lattice", lines[64], StringComparison.Ordinal);
        Assert.Contains(SlashConsoleLayout.HomeHints.Split(' ')[0], lines[65]);
    }

    [Fact]
    public void ConstrainedMenuKeepsViewportAndFooter()
    {
        var size = new PaneSize(40, 12);
        var cells = SlashConsoleLayout.Render(Menu("/", 9, size));
        var lines = cells.ToLines();
        var input = Array.FindLastIndex(lines, l => l.TrimEnd().EndsWith("│ /", StringComparison.Ordinal) || l.TrimEnd().EndsWith("| /", StringComparison.Ordinal));
        Assert.True(input >= 0 && input <= size.Height - 3, "input must stay above the footer.");
        Assert.Contains("/exit", string.Concat(lines), StringComparison.Ordinal);
        Assert.StartsWith("  /Users/test/lattice", lines[size.Height - 2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(10, 5)]
    [InlineData(2, 8)]
    public void DegenerateSizesNeverThrow(int width, int height)
    {
        var size = new PaneSize(width, height);
        SlashConsoleLayout.Render(Home(size));
        SlashConsoleLayout.Render(Menu("/", 0, size));
    }

    [Fact]
    public void ResultsStayTopAnchoredAndReadable()
    {
        var size = new PaneSize(100, 32);
        var req = new SlashConsoleRequest(
            "/generate --seed 42", [], 0, false, "/Users/test/lattice", null,
            "exit 0 ok 0.4s",
            ["{\"zones\": 12}"],
            false, size, GlyphMode.Unicode, ColorDepth.TrueColor, true);
        var lines = SlashConsoleLayout.Render(req).ToLines();
        var input = Array.FindLastIndex(lines, l => l.Contains("/generate --seed 42", StringComparison.Ordinal));
        Assert.Equal(12, input);
        Assert.Contains("exit 0 ok", lines[14]);
    }

    [Fact]
    public void HomeRestoredWhenResultCleared()
    {
        var size = new PaneSize(100, 32);
        var withResult = new SlashConsoleRequest(
            "/generate --seed 42", [], 0, false, "/Users/test/lattice", null,
            "exit 0 ok 0.4s", ["{\"zones\": 12}"], false,
            size, GlyphMode.Unicode, ColorDepth.TrueColor, true);
        var cleared = withResult with { Input = "", ResultTitle = null, ResultLines = null };
        var inputWithResult = Array.FindLastIndex(
            SlashConsoleLayout.Render(withResult).ToLines(),
            l => l.Contains("/generate --seed 42", StringComparison.Ordinal));
        var homeInput = PlaceholderRow(SlashConsoleLayout.Render(cleared).ToLines());
        Assert.Equal(12, inputWithResult);
        Assert.Equal(19, homeInput);
    }

    [Fact]
    public void ResizeTallShortTallIsStateless()
    {
        var tall = SlashConsoleLayout.Render(Home(new PaneSize(100, 40))).ToLines();
        var shortFrame = SlashConsoleLayout.Render(Home(new PaneSize(100, 20))).ToLines();
        var tallAgain = SlashConsoleLayout.Render(Home(new PaneSize(100, 40))).ToLines();
        Assert.Equal(tall, tallAgain);
        AssertCentredGroup(100, 40, tall, FirstBrailleRow(tall), PlaceholderRow(tall));
        AssertCentredGroup(100, 20, shortFrame, FirstBrailleRow(shortFrame), PlaceholderRow(shortFrame));
    }

    [Fact]
    public void ConsoleDrawsOnlyFromSharedThemeRoles()
    {
        var dir = RepoRoot();
        foreach (var file in Directory.GetFiles(Path.Combine(dir, "Tui"), "SlashConsole*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Rgb.FromHex(", text);
            Assert.DoesNotContain("new Rgb(", text);
        }
    }

    [Fact]
    public void ConsoleTextMeetsFourPointFiveOnItsBackgrounds()
    {
        var frames = new[]
        {
            Home(new PaneSize(100, 32)),
            Menu("/", 0, new PaneSize(100, 32)),
            Menu("/sim", 0, new PaneSize(100, 32)),
            Home(new PaneSize(62, 16)),
        };

        foreach (var req in frames)
        {
            var cells = SlashConsoleLayout.Render(req);
            for (var y = 0; y < cells.Height; y++)
            {
                for (var x = 0; x < cells.Width; x++)
                {
                    var cell = cells[x, y];
                    if (cell.Glyph == ' ' || IsDecoration(cell.Glyph))
                    {
                        continue;
                    }

                    if (IsBrailleDepth(cell))
                    {
                        continue;
                    }

                    var fg = cell.Foreground ?? Palette.TextPrimary;
                    var bg = cell.Background ?? Theme.ScreenBackground;
                    Assert.True(
                        Contrast(fg, bg) >= 4.5,
                        $"[{x},{y}] '{cell.Glyph}' {fg.ToHex()} on {bg.ToHex()} = {Contrast(fg, bg):0.00}:1");
                }
            }
        }
    }

    [Theory]
    [InlineData("slash-console-home-100x32", 100, 32, "", false, 0, false)]
    [InlineData("slash-console-home-80x24", 80, 24, "", false, 0, false)]
    [InlineData("slash-console-home-62x16", 62, 16, "", false, 0, false)]
    [InlineData("slash-console-menu-100x32", 100, 32, "/", true, 0, false)]
    [InlineData("slash-console-menu-62x30", 62, 30, "/", true, 1, false)]
    [InlineData("slash-console-filter-100x32", 100, 32, "/sim", true, 0, false)]
    public void GoldensMatchFrozenFrames(string name, int width, int height, string input, bool menuOpen, int selected, bool ascii)
    {
        var size = new PaneSize(width, height);
        var glyphs = ascii ? GlyphMode.Ascii : GlyphMode.Unicode;
        var menu = menuOpen ? SlashConsoleMenu.Filter(input) : Array.Empty<SlashMenuItem>();
        var req = new SlashConsoleRequest(input, menu, selected, menuOpen, "/Users/test/lattice", null, null, null, false, size, glyphs, ColorDepth.TrueColor, !ascii);
        var frame = SlashConsoleLayout.Render(req).ToLines();
        var frozen = Frozen(name);
        Assert.Equal(frozen.Length, frame.Length);
        for (var row = 0; row < frame.Length; row++)
        {
            Assert.True(frame[row] == frozen[row], $"row {row} of {name}:\n  frame:  |{frame[row]}|\n  frozen: |{frozen[row]}|");
        }
    }

    [Fact]
    public void SmallGoldensMatchFrozenFrames()
    {
        foreach (var (name, width, height) in new[]
        {
            ("slash-console-small-60x20", 60, 20),
            ("slash-console-small-40x12", 40, 12),
            ("slash-console-menu-62x29", 62, 29),
        })
        {
            var size = new PaneSize(width, height);
            var notice = $"terminal is {width}x{height}; console needs 62x16 (62x30 with menu); resize for it";
            var menuOpen = name.Contains("menu", StringComparison.Ordinal);
            var menu = menuOpen ? SlashConsoleMenu.Filter("/") : Array.Empty<SlashMenuItem>();
            var req = new SlashConsoleRequest(menuOpen ? "/" : "", menu, 0, menuOpen, "/Users/test/lattice", notice, null, null, false, size, GlyphMode.Unicode, ColorDepth.TrueColor, true);
            var frame = SlashConsoleLayout.Render(req).ToLines();
            var frozen = Frozen(name);
            Assert.Equal(frozen.Length, frame.Length);
            for (var row = 0; row < frame.Length; row++)
            {
                Assert.True(frame[row] == frozen[row], $"row {row} of {name}:\n  frame:  |{frame[row]}|\n  frozen: |{frozen[row]}|");
            }
        }
    }

    [Fact]
    public void AsciiAndResultGoldensMatchFrozenFrames()
    {
        var ascii = SlashConsoleLayout.Render(Home(new PaneSize(100, 32), GlyphMode.Ascii, utf8: false)).ToLines();
        AssertFramesEqual("slash-console-ascii-100x32", ascii);

        var asciiMenu = SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(100, 32), GlyphMode.Ascii, utf8: false)).ToLines();
        AssertFramesEqual("slash-console-ascii-menu-100x32", asciiMenu);

        var result = new SlashConsoleRequest(
            "/generate --seed 42", [], 0, false, "/Users/test/lattice", null,
            "exit 0 ok 0.4s",
            ["{\"zones\": 12, \"resources\": 8, \"seed\": 42} with a very long trailing tail that must be clipped with an ellipsis mark"],
            true, new PaneSize(100, 32), GlyphMode.Unicode, ColorDepth.TrueColor, true);
        AssertFramesEqual("slash-console-result-100x32", SlashConsoleLayout.Render(result).ToLines());
    }

    private static void AssertFramesEqual(string name, string[] frame)
    {
        var frozen = Frozen(name);
        Assert.Equal(frozen.Length, frame.Length);
        for (var row = 0; row < frame.Length; row++)
        {
            Assert.True(frame[row] == frozen[row], $"row {row} of {name}:\n  frame:  |{frame[row]}|\n  frozen: |{frozen[row]}|");
        }
    }

    private static string[] Frozen(string name)
    {
        var path = Path.Combine(RepoRoot(), "Tests", "Tui", "Goldens", name + ".txt");
        Assert.True(File.Exists(path), $"{path} is missing.");
        return File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lattice.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static bool IsDecoration(char c) =>
        c is '│' or '|' or '─' or '-' or '+' or '╭' or '╮' or '╰' or '╯' or '┌' or '┐' or '└' or '┘';

    private static bool IsBrailleDepth(Cell cell) =>
        cell.Glyph >= 0x2800 && cell.Glyph <= 0x28FF && cell.Foreground == Wordmark.Depth;

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Rgb color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    private static double Contrast(Rgb fg, Rgb bg)
    {
        var l1 = Luminance(fg);
        var l2 = Luminance(bg);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
