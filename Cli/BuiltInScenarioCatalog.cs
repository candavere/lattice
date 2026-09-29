using System.Reflection;

namespace Lattice.Cli;

/// <summary>
/// The built-in scenarios, each expressed as a committed descriptor under
/// <c>scenarios/</c> and embedded in this assembly so the CLI can resolve a
/// built-in name from any working directory.
/// <para>
/// The bytes embedded here and the bytes committed in the repository are the
/// same bytes, and a test asserts it. That is what makes a built-in invocation
/// carry a digest a reader can look up: the digest in the header is the digest
/// of a file that is really in the tree, not of a resource that merely claims
/// to be. The descriptor is the single source of truth for what the built-in
/// <em>is</em>; the code beside it supplies the pieces a data file cannot
/// (which agent class plays which seat), and the two are kept in step by the
/// equivalence tests rather than by a comment.
/// </para>
/// </summary>
public static class BuiltInScenarioCatalog
{
    /// <summary>The `simulate`/`evaluate` default: the standard seeded family.</summary>
    public const string Standard = "collection-skirmish";

    /// <summary>The Dungeon Infiltration &amp; Sentry Patrol demonstration.</summary>
    public const string Infiltration = "dungeon-infiltration";

    /// <summary>The capacity-1 contention family the paired evaluation uses.</summary>
    public const string Bottleneck = "bottleneck-contention";

    private const string ResourcePrefix = "Lattice.Cli.scenarios.";

    private static readonly string[] All = [Standard, Infiltration, Bottleneck];

    /// <summary>The descriptor file name a built-in resolves through.</summary>
    public static string FileName(string id) => $"{id}.json";

    /// <summary>
    /// The exact source bytes of a built-in descriptor, as committed. Throws
    /// for an id this build does not ship rather than returning empty bytes, so
    /// a missing resource is a loud failure and never a silently digest-free
    /// recording.
    /// </summary>
    public static byte[] Read(string id)
    {
        var assembly = typeof(BuiltInScenarioCatalog).Assembly;
        var name = ResourcePrefix + FileName(id);
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Built-in scenario '{id}' is not embedded in this assembly (expected resource '{name}').");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Every built-in id this build ships.</summary>
    public static IReadOnlyList<string> Ids => All;
}
