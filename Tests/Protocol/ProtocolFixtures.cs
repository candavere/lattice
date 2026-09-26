using System.Text;
using Lattice.Tests.Fuzz;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// Shared plumbing for the protocol suite: the fixture corpus under
/// <c>Tests/fixtures/protocol/</c>, and the location of the normative spec
/// itself so the §4.1 example lines can be compared against the fixtures rather
/// than trusted to match.
/// </summary>
/// <remarks>
/// The fixtures are the fuzz seed corpus as well as the test inputs. That is
/// deliberate: the seeds are then exactly the shapes the contract says are
/// valid, so a mutation that is still accepted after a mutation is either a real
/// hole in the strictness rule or a fact about the shape, and both are worth a
/// look.
/// </remarks>
internal static class ProtocolFixtures
{
    private const int MaxDepth = 12;

    /// <summary>
    /// Every conforming line in the corpus, in a stable order. Rejection
    /// fixtures are read explicitly by the tests that assert a specific code, so
    /// the fuzz seed corpus stays the set of things a conforming agent may send.
    /// </summary>
    public static IReadOnlyList<string> ValidNames { get; } =
    [
        "hello.jsonl",
        "hello_ack.jsonl",
        "observation.jsonl",
        "observation_full.jsonl",
        "action.jsonl",
        "action_wait.jsonl",
        "action_collect.jsonl",
        "error.jsonl",
    ];

    /// <summary>The absolute path of the fixture directory.</summary>
    public static string Directory { get; } = Path.GetDirectoryName(
        FixtureResolver.Fixture(Path.Combine("protocol", "hello.jsonl")))!;

    /// <summary>The bytes of one fixture, terminating LF included.</summary>
    public static byte[] Bytes(string name) => File.ReadAllBytes(FixtureResolver.Fixture(Path.Combine("protocol", name)));

    /// <summary>
    /// One fixture's payload with the terminating LF stripped, which is the form
    /// the parser takes.
    /// </summary>
    public static byte[] Line(string name)
    {
        var bytes = Bytes(name);
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)0x0A, bytes[^1]);
        return bytes[..^1];
    }

    /// <summary>
    /// The §4.1 example line for <paramref name="type"/>, read out of the
    /// normative spec at test time.
    /// </summary>
    /// <remarks>
    /// Reading the doc rather than restating its lines here is the only way the
    /// "verbatim" claim can be checked. A duplicated constant in a test asserts
    /// that the test agrees with itself; this asserts that the fixtures still
    /// agree with the document that is the contract, so a §4.1 edit that is not
    /// mirrored in the fixtures fails the build instead of silently diverging.
    /// </remarks>
    public static string SpecExample(string type)
    {
        var spec = File.ReadAllText(SpecPath());
        var heading = $"**`{type}`**";
        var headingIndex = spec.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(headingIndex >= 0, $"the spec has no {heading} example section.");

        var fence = spec.IndexOf("```json", headingIndex, StringComparison.Ordinal);
        Assert.True(fence >= 0, $"the {heading} example has no ```json block.");

        var start = spec.IndexOf('\n', fence) + 1;
        var end = spec.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the {heading} example block is unterminated.");

        return spec[start..end].Trim();
    }

    /// <summary>The absolute path of the normative protocol specification.</summary>
    public static string SpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "EXTERNAL_AGENT_PROTOCOL.md");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"docs/EXTERNAL_AGENT_PROTOCOL.md not found while searching upward from {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// A fully conforming <c>error</c> line whose encoded length is exactly
    /// <paramref name="targetBytes"/>, built by padding the diagnostic
    /// <c>detail</c> string.
    /// </summary>
    /// <remarks>
    /// The length boundary is generated rather than committed. A fixture at
    /// exactly 1 MiB is a megabyte of repository for a fact that is arithmetic:
    /// the padding length is derived from the measured fixed prefix and suffix, so
    /// the two sides of the boundary differ by exactly one byte. <c>error</c> is
    /// the message chosen because <c>detail</c> is a free-form string, which lets
    /// the <em>accepted</em> side of the boundary be a line that also parses —
    /// a payload that is merely not-too-long would prove less.
    /// </remarks>
    public static byte[] PaddedError(int targetBytes)
    {
        const string prefix = "{\"type\":\"error\",\"reason\":\"step_mismatch\",\"detail\":\"";
        const string suffix = "\"}";
        var overhead = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
        var padding = targetBytes - overhead;
        Assert.True(padding >= 0, $"target of {targetBytes} bytes is below the fixed overhead of {overhead}.");

        return Encoding.UTF8.GetBytes(prefix + new string('x', padding) + suffix);
    }

    /// <summary>
    /// A line whose maximum container nesting is exactly
    /// <paramref name="targetDepth"/>. The outermost object counts as one.
    /// </summary>
    /// <remarks>
    /// The envelope is a real <c>hello_ack</c> with the nesting hung off an
    /// undeclared member, so the depth gate and the schema gate can be told apart:
    /// at the cap the line is refused as <c>unknown_field</c>, one past it as
    /// <c>depth_exceeded</c>. Asserting only "not depth_exceeded" would be weaker
    /// — a line refused for some unrelated reason would pass it.
    /// </remarks>
    public static byte[] NestedContainers(int targetDepth)
    {
        var builder = new StringBuilder("{\"type\":\"hello_ack\",\"protocol\":1,\"a\":");
        builder.Append('[', targetDepth - 1);
        builder.Append(']', targetDepth - 1);
        builder.Append('}');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
