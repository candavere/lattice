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
        var cells = SlashConsoleLayout.Render(Home(new PaneSize(100, 32)));
        var text = string.Concat(cells.ToLines());
        Assert.Contains(Wordmark.PlainTitle, text);
        Assert.Contains(SlashConsoleLayout.Placeholder, text);
        Assert.Contains("/Users/test/lattice", text);
        Assert.Contains("⣀", text);
        // Blue left edge on the input bar.
        Assert.Equal(Palette.AccentSecondary, cells[0, 12].Foreground);
    }

    [Fact]
    public void PlainTitleIsAlwaysVisible()
    {
        foreach (var size in new[] { new PaneSize(100, 32), new PaneSize(80, 24), new PaneSize(62, 16), new PaneSize(60, 20), new PaneSize(40, 12) })
        {
            var cells = SlashConsoleLayout.Render(Home(size));
            Assert.Contains(Wordmark.PlainTitle, string.Concat(cells.ToLines()));
        }

        var menu = SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(100, 32)));
        Assert.Contains(Wordmark.PlainTitle, string.Concat(menu.ToLines()));
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
        var cells = SlashConsoleLayout.Render(Menu("/", 0, new PaneSize(100, 32)));
        // Selected row 0: orange edge, orange name on the element band.
        var edge = cells[0, 13];
        Assert.Equal(Palette.Accent, edge.Foreground);
        Assert.Equal(Theme.SelectionBackground, cells[5, 13].Background);
        Assert.Equal(Palette.Accent, cells[2, 13].Foreground);
        // Unselected row: text and muted, no band.
        Assert.Equal(Palette.TextPrimary, cells[2, 14].Foreground);
        Assert.True(cells[2, 14].Background is null || cells[2, 14].Background == Theme.PanelFill(ColorDepth.TrueColor));
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
