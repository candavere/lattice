using Lattice.Tui;
using Xunit;

namespace Lattice.Tests.Tui;

/// <summary>
/// Drives capability detection entirely from injected environment values, so no
/// test needs a real terminal and every branch is reachable on every platform.
/// </summary>
public class CapabilityDetectorTests
{
    private const string TrueColorTerm = "xterm-256color";

    private static TerminalEnvironment Env(
        string? colorTerm = null,
        string? term = null,
        string? noColor = null,
        string? lang = null,
        bool inputRedirected = false,
        bool outputRedirected = false,
        int width = 120,
        int height = 40) =>
        new(colorTerm, term, noColor, lang, inputRedirected, outputRedirected, width, height);

    [Fact]
    public void ColorTermTruecolorGivesTrueColor()
    {
        var capabilities = CapabilityDetector.Detect(Env(colorTerm: "truecolor", term: TrueColorTerm));

        Assert.Equal(ColorDepth.TrueColor, capabilities.Depth);
    }

    [Fact]
    public void ColorTerm24bitAlsoGivesTrueColor()
    {
        var capabilities = CapabilityDetector.Detect(Env(colorTerm: "24bit", term: TrueColorTerm));

        Assert.Equal(ColorDepth.TrueColor, capabilities.Depth);
    }

    [Fact]
    public void A256ColorTermGives256Colours()
    {
        var capabilities = CapabilityDetector.Detect(Env(term: TrueColorTerm));

        Assert.Equal(ColorDepth.Ansi256, capabilities.Depth);
    }

    [Fact]
    public void APlainColorTermGivesTheSixteenBaseColours()
    {
        var capabilities = CapabilityDetector.Detect(Env(term: "xterm-color"));

        Assert.Equal(ColorDepth.Ansi16, capabilities.Depth);
    }

    [Fact]
    public void AVt100GivesTheSixteenBaseColours()
    {
        var capabilities = CapabilityDetector.Detect(Env(term: "vt100"));

        Assert.Equal(ColorDepth.Ansi16, capabilities.Depth);
    }

    [Fact]
    public void ADumbOrUnsetTermGivesNoColour()
    {
        Assert.Equal(ColorDepth.None, CapabilityDetector.Detect(Env(term: "dumb")).Depth);
        Assert.Equal(ColorDepth.None, CapabilityDetector.Detect(Env(term: null)).Depth);
        Assert.Equal(ColorDepth.None, CapabilityDetector.Detect(Env(term: string.Empty)).Depth);
    }

    [Fact]
    public void NoColorBeatsEveryOtherSignal()
    {
        var capabilities = CapabilityDetector.Detect(
            Env(colorTerm: "truecolor", term: TrueColorTerm, noColor: "1"));

        Assert.Equal(ColorDepth.None, capabilities.Depth);
        Assert.Null(capabilities.PanelFill);
    }

    [Fact]
    public void AnEmptyNoColorIsNotAHonouredRequest()
    {
        var capabilities = CapabilityDetector.Detect(Env(colorTerm: "truecolor", noColor: string.Empty));

        Assert.Equal(ColorDepth.TrueColor, capabilities.Depth);
    }

    [Fact]
    public void RedirectedOutputGivesNoColourEvenWhenTheTermClaimsTruecolor()
    {
        var capabilities = CapabilityDetector.Detect(
            Env(colorTerm: "truecolor", term: TrueColorTerm, outputRedirected: true));

        Assert.Equal(ColorDepth.None, capabilities.Depth);
    }

    [Fact]
    public void RedirectedOutputNeverPaintsAPanelBackground()
    {
        var capabilities = CapabilityDetector.Detect(
            Env(colorTerm: "truecolor", term: TrueColorTerm, outputRedirected: true));

        Assert.Null(capabilities.PanelFill);
    }

    [Fact]
    public void AnInteractiveTruecolorTerminalPaintsThePanelBackground()
    {
        var capabilities = CapabilityDetector.Detect(Env(colorTerm: "truecolor", term: TrueColorTerm));

        Assert.Equal(Palette.PanelBackground, capabilities.PanelFill);
    }

    [Fact]
    public void RedirectedInputAndOutputAreReportedSeparately()
    {
        var capabilities = CapabilityDetector.Detect(
            Env(term: TrueColorTerm, inputRedirected: true, outputRedirected: false));

        Assert.True(capabilities.InputRedirected);
        Assert.False(capabilities.OutputRedirected);
    }

    [Theory]
    [InlineData("en_US.UTF-8", true)]
    [InlineData("en_US.utf8", true)]
    [InlineData("en_US", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void Utf8SupportComesFromTheLocale(string? lang, bool expected)
    {
        var capabilities = CapabilityDetector.Detect(Env(term: TrueColorTerm, lang: lang));

        Assert.Equal(expected, capabilities.Utf8);
    }

    [Fact]
    public void TheMinimumLayoutIsOneHundredByThirty()
    {
        Assert.Equal(100, TerminalCapabilities.MinimumWidth);
        Assert.Equal(30, TerminalCapabilities.MinimumHeight);
    }

    [Theory]
    [InlineData(100, 30, true)]
    [InlineData(200, 60, true)]
    [InlineData(99, 30, false)]
    [InlineData(100, 29, false)]
    [InlineData(80, 25, false)]
    public void TheMinimumSizeFlagIsReportedForLaterScreens(int width, int height, bool expected)
    {
        var capabilities = CapabilityDetector.Detect(Env(term: TrueColorTerm, width: width, height: height));

        Assert.Equal(expected, capabilities.MeetsMinimumSize);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(1)]
    [InlineData(80)]
    public void AUsableWidthIsKeptExactlyAsReported(int reported)
    {
        Assert.Equal(reported, TerminalEnvironment.UsableWidth(reported));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AnUnusableWidthFallsBackToTheConventionalEighty(int reported)
    {
        Assert.Equal(80, TerminalEnvironment.UsableWidth(reported));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(1)]
    [InlineData(25)]
    public void AUsableHeightIsKeptExactlyAsReported(int reported)
    {
        Assert.Equal(reported, TerminalEnvironment.UsableHeight(reported));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AnUnusableHeightFallsBackToTheConventionalTwentyFive(int reported)
    {
        Assert.Equal(25, TerminalEnvironment.UsableHeight(reported));
    }

    [Theory]
    [InlineData("C", null, "en_US.UTF-8", "C")]
    [InlineData(null, "C", "en_US.UTF-8", "C")]
    [InlineData(null, null, "en_US.UTF-8", "en_US.UTF-8")]
    [InlineData("C", "en_US.UTF-8", null, "C")]
    [InlineData("", "C", "en_US.UTF-8", "C")]
    [InlineData("C", "", "en_US.UTF-8", "C")]
    [InlineData("", "en_US.UTF-8", "", "en_US.UTF-8")]
    [InlineData(null, null, null, null)]
    [InlineData("", "", "", null)]
    public void TheLocaleIsReadInPosixPrecedence(
        string? lcAll, string? lcCtype, string? lang, string? expected)
    {
        Assert.Equal(expected, TerminalEnvironment.LocaleFrom(lcAll, lcCtype, lang));
    }

    [Fact]
    public void AnAllCapsLocaleBeatsAUtf8Lang()
    {
        // The regression this pins: LANG said UTF-8 while LC_ALL said C, so
        // detection reported UTF-8 on a terminal that is not UTF-8.
        var locale = TerminalEnvironment.LocaleFrom("C", null, "en_US.UTF-8");

        Assert.False(CapabilityDetector.IsUtf8(Env(lang: locale)));
    }

    /// <summary>
    /// The real environment is the one that varies by host: attached to a
    /// terminal it reports the window, redirected it reports nothing usable.
    /// Detection must hand back a layout either way, which is the invariant the
    /// start-up screen depends on.
    /// </summary>
    [Fact]
    public void DetectionAgainstTheRealEnvironmentReturnsAUsableRecord()
    {
        var capabilities = CapabilityDetector.Detect();

        Assert.True(capabilities.Width > 0);
        Assert.True(capabilities.Height > 0);
        Assert.True(Enum.IsDefined(capabilities.Depth));
    }
}