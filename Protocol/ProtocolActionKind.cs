using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// The three <c>kind</c> values of an <c>action</c> (spec §6.1), spelled with
/// the exact casing of the <c>ActionKind</c> member names it projects
/// (<c>Environment/StepContracts.cs:9-19</c>).
/// </summary>
/// <remarks>
/// The wire spellings are <c>"Wait"</c>, <c>"Move"</c>, and <c>"Collect"</c> —
/// and nothing else. <c>"move"</c>, <c>"MOVE"</c>, <c>"Idle"</c>, and the
/// integer <c>1</c> are all schema violations (spec §6.1, §8.3), which is why
/// this type does not use the framework's enum converter: that one matches
/// member names case-insensitively, and its absence is what keeps the
/// <c>ActionKind</c> unknown-value branch in <c>ActionSpace.Validate</c>
/// unreachable from the wire.
/// </remarks>
public enum ProtocolActionKind
{
    /// <summary>Take no action this tick. Carries neither <c>zone_id</c> nor <c>resource_id</c>.</summary>
    Wait,

    /// <summary>Move to <c>zone_id</c>. Carries <c>zone_id</c> only.</summary>
    Move,

    /// <summary>Collect <c>resource_id</c>. Carries <c>resource_id</c> only.</summary>
    Collect,
}
