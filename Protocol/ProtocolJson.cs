using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance and the matching
/// <see cref="JsonDocumentOptions"/> that the parser and writer share.
/// </summary>
/// <remarks>
/// Every setting here is a spec requirement rather than a default choice, so
/// they are spelled out and commented rather than inherited:
/// <list type="bullet">
/// <item><see cref="JsonUnmappedMemberHandling.Disallow"/> is the §8.3 rule
/// "Lattice MUST refuse a message carrying a field that is not in the schema".
/// </item>
/// <item><see cref="JsonNumberHandling.Strict"/> is what makes <c>"protocol": "1"</c>
/// and <c>"protocol": 1.0</c> a mismatch rather than a lenient coercion (§2).</item>
/// <item><see cref="JsonIgnoreCondition.WhenWritingNull"/> implements the §5.2
/// rule 5 omission of a null <c>string?</c>, and with it the §6.1 omission of a
/// conditional action field.</item>
/// <item><see cref="ProtocolLimits.MaxJsonDepth"/> is the §7 <c>max_json_depth</c>,
/// and it also bounds the reader's recursion so a hostile payload cannot
/// exhaust the stack before <see cref="ProtocolFraming"/> has measured it.</item>
/// </list>
/// The encoder is left at <see cref="JavaScriptEncoder.Default"/>, which escapes
/// every non-ASCII code point as <c>\uXXXX</c>. That is a determinism choice as
/// much as a safety one: the output is pure ASCII regardless of platform, writer,
/// or host encoding, and a raw LF or CR can never appear inside a string value,
/// which §1 forbids.
/// </remarks>
internal static class ProtocolJson
{
    /// <summary>Options for <c>JsonSerializer</c>, shared by the parser and the writer.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = ProtocolLimits.MaxJsonDepth,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        Converters = { new ProtocolReasonConverter(), new ProtocolActionKindConverter() },
    };

    /// <summary>
    /// Options for the throwaway <see cref="JsonDocument"/> the parser uses to
    /// read the discriminator and to walk for unknown members. It shares
    /// <see cref="ProtocolLimits.MaxJsonDepth"/> with <see cref="Options"/> so the
    /// two stages cannot disagree about what "too deep" means.
    /// </summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        MaxDepth = ProtocolLimits.MaxJsonDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };
}
