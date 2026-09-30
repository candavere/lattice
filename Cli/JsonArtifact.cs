using System.Text.Json;

namespace Lattice.Cli;

/// <summary>
/// The one place a JSON artifact is serialized and written to disk, so that
/// every artifact the CLI emits is byte-identical on Windows, macOS and Linux.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonSerializerOptions.WriteIndented"/> is the trap this type
/// exists to defuse. <c>System.Text.Json</c>'s indented writer breaks lines with
/// <see cref="Environment.NewLine"/>, which is <c>"\r\n"</c> on Windows and
/// <c>"\n"</c> everywhere else. An artifact serialized that way therefore differs
/// from its committed golden fixture by newlines alone — same tokens, same
/// digits, same order — so every byte-identical comparison of CLI output is
/// green on Linux and macOS and red on <c>windows-latest</c>. The repository
/// pins <c>*.json</c> to LF via <c>.gitattributes</c>, so the artifacts the CLI
/// writes have to agree with that: LF only, on every OS.
/// </para>
///
/// <para>
/// Normalization happens after serialization rather than by configuring the
/// writer. The original reason was that <c>JsonWriterOptions.NewLine</c> did not
/// exist before .NET 9 while this project targeted <c>net8.0</c>; the project
/// now targets <c>net10.0</c>, so that blocker is gone and
/// <c>JsonWriterOptions.NewLine</c> is available. The replacement below is kept
/// because it is still lossless for this writer, not because the API is
/// unavailable; moving the newline onto the writer is a behaviour-bearing
/// change and is deliberately not made in a comment-only edit. Replacing
/// <c>"\r\n"</c> with <c>"\n"</c> is sufficient
/// and lossless here: the only newlines a serializer can emit are the
/// indentation breaks it writes itself, and a <c>CR</c> that arrived inside a
/// string value is escaped to the two-character sequence <c>\r</c> rather than
/// written literally, so no lone <c>CR</c> can survive to be missed here.
/// </para>
///
/// <para>
/// Only line endings are touched. Property order, indentation width and every
/// value come straight from the serializer and are unaffected.
/// </para>
/// </remarks>
internal static class JsonArtifact
{
    /// <summary>
    /// The indented options the artifacts are written with. The only reason
    /// this is not a field on <c>CliApp</c> is that the newline normalization
    /// below must be applied to whatever these options produce, and keeping the
    /// two together makes it hard to serialize without normalizing.
    /// </summary>
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Serializes <paramref name="value"/> as an indented JSON artifact whose
    /// newlines are LF on every OS.
    /// </summary>
    internal static string SerializeIndented<T>(T value) =>
        Normalize(JsonSerializer.Serialize(value, IndentedOptions));

    /// <summary>
    /// Rewrites Windows line endings as LF. A no-op on macOS and Linux, where
    /// the serializer already emitted <c>"\n"</c>.
    /// </summary>
    internal static string Normalize(string json) =>
        json.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Writes an artifact to <paramref name="path"/> with LF newlines and a
    /// single trailing LF. This is the only artifact write path, so the
    /// terminator is decided in one place too.
    /// </summary>
    internal static void Write(string path, string json) =>
        File.WriteAllText(path, json + "\n");
}
