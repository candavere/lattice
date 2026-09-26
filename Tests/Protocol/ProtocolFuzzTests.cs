using System.Text;
using Lattice.Environment;
using Lattice.Protocol;
using Lattice.Tests.Fuzz;
using Xunit;

namespace Lattice.Tests.Protocol;

/// <summary>
/// Seeded fuzzing of the protocol reader, over the conforming fixture corpus.
/// </summary>
/// <remarks>
/// The parser is the one component in this repository that is handed bytes chosen
/// by somebody else, so it is fuzzed as pure bytes with no simulation in the loop
/// — which is only possible because the protocol project references nothing else
/// (spec §11.1). The definition of a clean rejection is
/// <see cref="ExceptionContract.IsGraceful"/>, the same boundary the trajectory
/// fuzz targets use: a typed, domain-level refusal is expected, and anything else
/// escaping — a <see cref="NullReferenceException"/>, an
/// <see cref="IndexOutOfRangeException"/>, a
/// <see cref="System.Text.Json.JsonException"/> that escaped classification — is
/// an unhandled crash and fails the case with its seed.
/// <para>
/// Mutations are seeded through <see cref="TextMutator"/> and
/// <see cref="Rng"/>, so a failing case always reproduces exactly from the seed
/// the harness reports. Two code paths are driven per case, because they fail
/// differently: <see cref="ProtocolParser.Parse"/> with no context, and the
/// fully-contextual path that expects a specific protocol version and step.
/// </para>
/// <para>
/// The critical tokens are the values a mutation is most likely to damage
/// meaningfully — the discriminator, the version, the step, the kind, and a
/// reason — so that the token-swap operator explores "right shape, wrong value"
/// rather than only byte noise.
/// </para>
/// </remarks>
public class ProtocolFuzzTests
{
    private const int BaseSeed = 2024;

    private static readonly string[] CriticalTokens =
        ["type", "protocol", "step", "kind", "reason", "zone_id", "resource_id", "max_ticks", "agent_count"];

    [Fact]
    public void Parser_Survives_The_Seeded_Corpus_Within_Budget()
    {
        var corpus = ProtocolFixtures.ValidNames
            .Select(name => Encoding.UTF8.GetString(ProtocolFixtures.Line(name)))
            .ToArray();

        Assert.NotEmpty(corpus);

        FuzzHarness.Run("ProtocolParser", BaseSeed, FuzzHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng((ulong)caseSeed);
            var seed = corpus[rng.Next(0, corpus.Length)];
            var mutated = TextMutator.Mutate(seed, rng, CriticalTokens);
            var bytes = Encoding.UTF8.GetBytes(mutated);

            FuzzHarness.ExpectGraceful(
                $"seeded mutation of \"{Truncate(seed)}\" (caseSeed={caseSeed})",
                () => ProtocolParser.Parse(bytes));

            // The contextual paths take the same bytes through the typed entry
            // points, which is where the version and step checks actually run.
            FuzzHarness.ExpectGraceful(
                $"seeded mutation through the handshake path (caseSeed={caseSeed})",
                () => ProtocolParser.ParseHello(bytes));

            FuzzHarness.ExpectGraceful(
                $"seeded mutation through the step path (caseSeed={caseSeed})",
                () => ProtocolParser.ParseAction(bytes, rng.Next(0, 64)));

            FuzzHarness.ExpectGraceful(
                $"seeded mutation through the observation path (caseSeed={caseSeed})",
                () => ProtocolParser.ParseObservation(bytes));
        });
    }

    /// <summary>
    /// The framing layer on its own, including the raw depth scan, which is the
    /// part of the reader a hostile peer can aim straight at: it is handed bytes
    /// and must answer with a verdict rather than an exception it did not classify.
    /// </summary>
    [Fact]
    public void Framing_Survives_The_Seeded_Corpus_Within_Budget()
    {
        var corpus = ProtocolFixtures.ValidNames
            .Select(name => ProtocolFixtures.Bytes(name))
            .ToArray();

        FuzzHarness.Run("ProtocolFraming", BaseSeed, FuzzHarness.DefaultIterations, caseSeed =>
        {
            var rng = new Rng((ulong)caseSeed);
            var mutated = TextMutator.Mutate(Encoding.UTF8.GetString(corpus[rng.Next(0, corpus.Length)]), rng, CriticalTokens);
            var bytes = Encoding.UTF8.GetBytes(mutated);

            FuzzHarness.ExpectGraceful(
                $"seeded framing input (caseSeed={caseSeed})",
                () => ProtocolFraming.MaxContainerDepth(bytes));

            FuzzHarness.ExpectGraceful(
                $"seeded framing validation (caseSeed={caseSeed})",
                () => ProtocolFraming.Validate(bytes));

            using var stream = new MemoryStream(bytes);
            FuzzHarness.ExpectGraceful(
                $"seeded framing read (caseSeed={caseSeed})",
                () =>
                {
                    while (ProtocolFraming.ReadLine(stream) is { } line)
                    {
                        ProtocolFraming.Validate(line);
                    }
                });
        });
    }

    /// <summary>
    /// The writer is the other side of the same boundary: it is handed a message
    /// that has already been through the parser, and its output goes straight onto
    /// the wire. A crash here would take down the host, not the peer.
    /// </summary>
    [Fact]
    public void Writer_Survives_Every_Message_The_Parser_Accepts()
    {
        foreach (var name in ProtocolFixtures.ValidNames)
        {
            var parsed = ProtocolParser.Parse(ProtocolFixtures.Line(name));

            FuzzHarness.ExpectGraceful($"writing {name}", () =>
            {
                var line = ProtocolWriter.WriteLine(parsed);
                Assert.DoesNotContain((byte)0x0D, line);
                Assert.Equal((byte)0x0A, line[^1]);
                ProtocolWriter.EncodeChecked(parsed);
            });
        }
    }

    /// <summary>
    /// A rejection must carry exactly one reason from the closed set, whatever the
    /// input looked like. A code outside the set, or a message naming two codes,
    /// would mean the §8 contract and the implementation had drifted — and §9.3
    /// makes that a scoring bug, not a cosmetic one.
    /// </summary>
    [Fact]
    public void Every_Rejection_Carries_Exactly_One_Reason_From_The_Closed_Set()
    {
        foreach (var name in ProtocolFixtures.ValidNames)
        {
            var seed = Encoding.UTF8.GetString(ProtocolFixtures.Line(name));

            for (var index = 0; index < 64; index++)
            {
                var rng = new Rng((ulong)(BaseSeed + index));
                var mutated = TextMutator.Mutate(seed, rng, CriticalTokens);
                var bytes = Encoding.UTF8.GetBytes(mutated);

                var exception = Record.Exception(() => ProtocolParser.Parse(bytes));
                if (exception is null)
                {
                    continue;
                }

                var violation = Assert.IsType<ProtocolViolation>(exception);
                Assert.Contains(violation.Reason, ProtocolReasons.All);
                Assert.StartsWith(
                    $"protocol {violation.Reason.ToWireString()}: ",
                    violation.Message,
                    StringComparison.Ordinal);
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 48 ? value : value[..48] + "…";
}
