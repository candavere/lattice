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

    [Fact]
    public void DetectionAgainstTheRealEnvironmentReturnsAUsableRecord()
    {
        var capabilities = CapabilityDetector.Detect();

        Assert.True(capabilities.Width > 0);
        Assert.True(capabilities.Height > 0);
        Assert.True(Enum.IsDefined(capabilities.Depth));
    }
}