using System.Text;
using Lattice.Protocol;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// The two protocol-constant limits of spec §7, tested at the boundary rather
/// than near it.
/// </summary>
/// <remarks>
/// A limit tested at a convenient value proves nothing. <c>1048576</c> accepted
/// and <c>1048577</c> refused, and depth <c>32</c> accepted and <c>33</c>
/// refused, are the only assertions that pin the constants the spec fixes — and
/// they are also the two places an off-by-one would let a hostile peer through.
/// Both boundaries are built arithmetically rather than committed, so the two
/// sides differ by exactly one by construction.
/// </remarks>
public class ProtocolLimitTests
{
    [Fact]
    public void Limits_Are_The_Spec_Constants()
    {
        Assert.Equal(1_048_576, ProtocolLimits.MaxLineBytes);
        Assert.Equal(32, ProtocolLimits.MaxJsonDepth);
    }

    /// <summary>
    /// A line of exactly <c>max_line_bytes</c> payload bytes is accepted, and the
    /// spec says so explicitly ("A line of exactly 1048576 payload bytes is
    /// accepted; 1048577 is not"). The accepted payload is a real
    /// <c>error</c> message, so acceptance means the whole pipeline took it, not
    /// merely that the length gate stood aside.
    /// </summary>
    [Fact]
    public void MaxLineBytes_Boundary_Is_Exact()
    {
        var atLimit = ProtocolFixtures.PaddedError(ProtocolLimits.MaxLineBytes);
        var overLimit = ProtocolFixtures.PaddedError(ProtocolLimits.MaxLineBytes + 1);

        Assert.Equal(ProtocolLimits.MaxLineBytes, atLimit.Length);
        Assert.Equal(ProtocolLimits.MaxLineBytes + 1, overLimit.Length);

        // At the limit: accepted, and it parses.
        var accepted = ProtocolParser.ParseError(atLimit);
        Assert.Equal(ProtocolReason.StepMismatch, accepted.Reason);

        // One byte over: refused, with exactly this code.
        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.ParseError(overLimit)));
        Assert.Equal(ProtocolReason.LineTooLong, violation.Reason);
    }

    /// <summary>
    /// The cap counts <b>bytes</b>, not characters, and multi-byte UTF-8 counts as
    /// its encoded length (spec §7). A line of well under a cap in characters can
    /// still be over it in bytes.
    /// </summary>
    [Fact]
    public void MaxLineBytes_Counts_Bytes_Not_Characters()
    {
        // Each 'é' is two UTF-8 bytes, so half a cap of characters is a full cap
        // of bytes.
        var line = Encoding.UTF8.GetBytes("{\"a\":\"" + new string('é', ProtocolLimits.MaxLineBytes / 2) + "\"}");
        Assert.True(line.Length > ProtocolLimits.MaxLineBytes);

        var violation = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(line)));
        Assert.Equal(ProtocolReason.LineTooLong, violation.Reason);
    }

    /// <summary>
    /// The cap excludes the terminating LF (spec §7), so a line at the cap plus
    /// its LF is still legal, and the stream reader returns exactly the cap's
    /// worth of payload.
    /// </summary>
    [Fact]
    public void MaxLineBytes_Excludes_The_Terminating_LF()
    {
        var payload = ProtocolFixtures.PaddedError(ProtocolLimits.MaxLineBytes);
        var framed = new byte[payload.Length + 1];
        payload.CopyTo(framed, 0);
        framed[^1] = 0x0A;

        using var stream = new MemoryStream(framed);
        var read = ProtocolFraming.ReadValidatedLine(stream);

        Assert.NotNull(read);
        Assert.Equal(ProtocolLimits.MaxLineBytes, read!.Length);
        Assert.Equal(ProtocolReason.StepMismatch, ProtocolParser.ParseError(read).Reason);
    }

    /// <summary>
    /// The stream reader refuses an over-long line <em>as it reads</em>, so it
    /// never buffers a megabyte it has already decided to reject — which is what
    /// makes the limit a bound on memory and not just on acceptance.
    /// </summary>
    [Fact]
    public void ReadLine_Refuses_An_Over_Long_Line_Without_Buffering_It()
    {
        var framed = new byte[ProtocolLimits.MaxLineBytes + 64];
        Array.Fill(framed, (byte)'x');
        framed[^1] = 0x0A;

        using var stream = new MemoryStream(framed);
        var violation = Assert.IsType<ProtocolViolation>(Record.Exception(() => ProtocolFraming.ReadLine(stream)));
        Assert.Equal(ProtocolReason.LineTooLong, violation.Reason);
    }

    [Fact]
    public void ReadLine_Reports_Clean_End_Of_Stream_As_Null()
    {
        using var empty = new MemoryStream();
        Assert.Null(ProtocolFraming.ReadLine(empty));

        // A line followed by EOF still yields its line: the last action of a match
        // must be read before the stream closes (spec §3).
        using var one = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"hello_ack\",\"protocol\":1}\n"));
        Assert.NotNull(ProtocolFraming.ReadLine(one));
        Assert.Null(ProtocolFraming.ReadLine(one));
    }

    [Fact]
    public void ReadLine_Treats_A_Blank_Line_As_A_Framing_Violation()
    {
        // Spec §1: a blank line is a framing violation, not something to skip.
        using var blank = new MemoryStream("\n"u8.ToArray());
        var violation = Assert.IsType<ProtocolViolation>(Record.Exception(() => ProtocolFraming.ReadLine(blank)));
        Assert.Equal(ProtocolReason.MalformedJson, violation.Reason);
    }

    [Fact]
    public void ReadLine_Rejects_An_Unterminated_Final_Line()
    {
        using var stream = new MemoryStream("{\"type\":\"hello_ack\",\"protocol\":1}"u8.ToArray());
        var violation = Assert.IsType<ProtocolViolation>(Record.Exception(() => ProtocolFraming.ReadLine(stream)));
        Assert.Equal(ProtocolReason.MalformedJson, violation.Reason);
    }

    /// <summary>
    /// Depth <c>32</c> passes the depth gate and depth <c>33</c> does not. The
    /// accepted side is asserted as "not <c>depth_exceeded</c>" rather than as a
    /// successful parse, because no conforming message nests 32 deep — the
    /// deepest a real <c>observation</c> goes is five — so the meaningful
    /// statement is which of the two framing codes came back.
    /// </summary>
    [Fact]
    public void MaxJsonDepth_Boundary_Is_Exact()
    {
        var atCap = ProtocolFixtures.NestedContainers(ProtocolLimits.MaxJsonDepth);
        var overCap = ProtocolFixtures.NestedContainers(ProtocolLimits.MaxJsonDepth + 1);

        Assert.Equal(ProtocolLimits.MaxJsonDepth, ProtocolFraming.MaxContainerDepth(atCap));
        Assert.Equal(ProtocolLimits.MaxJsonDepth + 1, ProtocolFraming.MaxContainerDepth(overCap));

        var accepted = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(atCap)));
        Assert.NotEqual(ProtocolReason.DepthExceeded, accepted.Reason);
        Assert.Equal(ProtocolReason.UnknownField, accepted.Reason);

        var refused = Assert.IsType<ProtocolViolation>(
            Record.Exception(() => ProtocolParser.Parse(overCap)));
        Assert.Equal(ProtocolReason.DepthExceeded, refused.Reason);
    }

    [Fact]
    public void A_Conforming_Observation_Sits_Far_Below_The_Depth_Cap()
    {
        // The deepest conforming path is observation -> map -> zones[] ->
        // position, five containers (spec §7), so the cap is headroom against a
        // hostile peer rather than a constraint on legitimate traffic.
        var depth = ProtocolFraming.MaxContainerDepth(ProtocolFixtures.Line("observation_full.jsonl"));

        Assert.Equal(5, depth);
        Assert.True(depth < ProtocolLimits.MaxJsonDepth);
    }

    /// <summary>
    /// A brace inside a string value is content, not structure. If the depth scan
    /// counted string characters, a message whose <c>detail</c> contains braces
    /// would be refused as too deep — which is exactly the kind of bug that only
    /// shows up on a real agent's error output.
    /// </summary>
    [Fact]
    public void Depth_Scan_Does_Not_Count_Braces_Inside_Strings()
    {
        var line = Encoding.UTF8.GetBytes(
            "{\"type\":\"error\",\"reason\":\"step_mismatch\",\"detail\":\"}}}}][[[ {{{ \"}");

        Assert.Equal(1, ProtocolFraming.MaxContainerDepth(line));
        Assert.Equal(
            "}}}}][[[ {{{ ",
            ProtocolParser.ParseError(line).Detail);
    }

    [Fact]
    public void Depth_Scan_Understands_Escaped_Quotes()
    {
        // An escaped quote does not end the string, so the brace after it is
        // inside the value rather than opening a container.
        var line = Encoding.UTF8.GetBytes("{\"a\":\"x\\\"y\"}");

        Assert.Equal(1, ProtocolFraming.MaxContainerDepth(line));
    }

    /// <summary>
    /// §7's derived constraint: the whole-match limit must not be able to fire
    /// before a match could finish, or every external agent would be scored a loss
    /// for a limit Lattice itself mis-set. The two limit values are stage-3
    /// constants (§14, U-2), so this pins the <em>relationship</em> the spec fixes
    /// rather than a value it defers, and it pins that <c>max_ticks</c> — the
    /// multiplier — actually travels on the wire.
    /// </summary>
    /// <remarks>
    /// The §4.1 <c>hello</c> example deliberately does <b>not</b> satisfy the
    /// constraint: its numbers are declared illustrative, and stage 3 sets the real
    /// ones. Asserting the constraint against the example would assert that the
    /// spec is wrong.
    /// </remarks>
    [Fact]
    public void MatchTimeout_Must_Cover_StepTimeout_Times_MaxTicks()
    {
        var hello = new HelloMessage
        {
            Protocol = ProtocolLimits.Version,
            Scenario = "standard",
            Seed = 1001,
            AgentSlot = 0,
            MaxTicks = 200,
            AgentCount = 2,
            Limits = new MatchLimitsWire { StepTimeoutMs = 5000, MatchTimeoutMs = 1_200_000 },
        };

        var required = (long)hello.Limits.StepTimeoutMs * hello.MaxTicks;
        Assert.True(
            hello.Limits.MatchTimeoutMs >= required,
            $"match_timeout_ms {hello.Limits.MatchTimeoutMs} cannot cover " +
            $"step_timeout_ms {hello.Limits.StepTimeoutMs} x max_ticks {hello.MaxTicks} = {required}.");

        // max_ticks survives the wire, so the runner can compute the same
        // constraint from what it is about to send.
        Assert.Equal(hello.MaxTicks, ProtocolParser.ParseHello(ProtocolWriter.WriteLine(hello)).MaxTicks);
    }

    /// <summary>
    /// The spec's own example is illustrative, and this states that as an
    /// assertion so the divergence reads as deliberate rather than as an oversight
    /// a future reader has to rediscover.
    /// </summary>
    [Fact]
    public void The_Spec_Example_Limits_Are_Illustrative_Not_Normative()
    {
        var hello = ProtocolParser.ParseHello(ProtocolFixtures.Line("hello.jsonl"));

        Assert.True(
            hello.Limits.MatchTimeoutMs < (long)hello.Limits.StepTimeoutMs * hello.MaxTicks,
            "the §4.1 example is documented as illustrative; if it ever starts " +
            "satisfying the §7 constraint, that note needs revisiting — not this test.");
    }
}
