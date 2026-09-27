namespace Lattice.Protocol;

/// <summary>
/// The two action checks that need more than the line itself: §6.1's
/// conditional-presence rule, and §6.3's action-space rule.
/// </summary>
/// <remarks>
/// Both checks are kept out of <see cref="ProtocolParser"/> on purpose. The
/// parser reports what a line <em>said</em>; these report what the map
/// <em>allows</em>. §6.3 is a claim about a specific map, and spec §11.2 keeps
/// the wire↔<c>AgentAction</c> mapping in the stage-3 runner — which already
/// references both sides — precisely so the protocol project never has to see
/// <c>Observation</c>, <c>MapGraph</c>, or <c>ActionSpace</c>.
/// <para>
/// <see cref="RequireConditionalFields"/> needs nothing but the action, so the
/// parser calls it. <see cref="RequireWithinActionSpace"/> needs the map's
/// bounds, so the caller supplies them as two integers and calls it once the map
/// is in hand. It is the wire-side form of the three checks in the §6.3 table —
/// kind, <c>Move</c> zone range, <c>Collect</c> resource range — and it exists
/// so that <c>illegal_action</c> is produced <em>before</em> any mapping happens.
/// The environment's <c>ActionSpace.Validate</c> remains the authority at
/// receipt, as §6.3 requires; this is the pre-check, not a replacement.
/// </para>
/// </remarks>
public static class ProtocolActionGuard
{
    /// <summary>
    /// Enforces §6.1's conditional-presence rule, which is why the wire omits
    /// what the record would ignore:
    /// <c>zone_id</c> is required for <c>Move</c> and forbidden for
    /// <c>Wait</c>/<c>Collect</c>; <c>resource_id</c> is required for
    /// <c>Collect</c> and forbidden for <c>Wait</c>/<c>Move</c>. An explicitly
    /// sent <c>-1</c> is also refused: that is the record's sentinel, not an
    /// addressable zone or resource.
    /// </summary>
    /// <exception cref="ProtocolViolation">
    /// <see cref="ProtocolReason.SchemaViolation"/> — the rule is part of the
    /// message schema, so a breach of it is a schema violation (spec §6.1, §8.3).
    /// </exception>
    public static void RequireConditionalFields(ActionMessage action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var (zoneExpected, resourceExpected) = action.Kind switch
        {
            ProtocolActionKind.Move => (true, false),
            ProtocolActionKind.Collect => (false, true),
            ProtocolActionKind.Wait => (false, false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action.Kind,
                "Not a protocol v1 action kind."),
        };

        Require(action.ZoneId, zoneExpected, "zone_id", action.Kind);
        Require(action.ResourceId, resourceExpected, "resource_id", action.Kind);
    }

    /// <summary>
    /// Enforces the §6.3 table against a specific map, given its bounds: the
    /// number of zones and the number of resources. Raises
    /// <c>illegal_action</c> for an action that is well formed but outside the
    /// action space for this map.
    /// </summary>
    /// <param name="action">The action to check.</param>
    /// <param name="zoneCount">
    /// The map's zone count, so a legal <c>Move</c> target is
    /// <c>0..zoneCount-1</c>.
    /// </param>
    /// <param name="resourceCount">
    /// The map's resource count, so a legal <c>Collect</c> target is
    /// <c>0..resourceCount-1</c>.
    /// </param>
    /// <remarks>
    /// §6.3 fixes this as a <b>shape</b> check: it does not ask whether a move is
    /// to an adjacent zone, whether a choke is passable, or whether the agent is
    /// in transit. An in-shape but ineffective action MUST be applied, not
    /// refused — the step function resolves it exactly as it does for an
    /// in-process agent.
    /// </remarks>
    public static void RequireWithinActionSpace(ActionMessage action, int zoneCount, int resourceCount)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(zoneCount);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);

        // The conditional rule is a schema matter and is checked first, so an
        // absent target is reported as the schema violation it is rather than as
        // an out-of-range target.
        RequireConditionalFields(action);

        switch (action.Kind)
        {
            case ProtocolActionKind.Move when action.ZoneId >= zoneCount:
                throw new ProtocolViolation(
                    ProtocolReason.IllegalAction,
                    $"Move targets zone {action.ZoneId}, outside 0..{zoneCount - 1}.");
            case ProtocolActionKind.Collect when action.ResourceId >= resourceCount:
                throw new ProtocolViolation(
                    ProtocolReason.IllegalAction,
                    $"Collect targets resource {action.ResourceId}, outside 0..{resourceCount - 1}.");
            default:
                return;
        }
    }

    private static void Require(int? value, bool expected, string field, ProtocolActionKind kind)
    {
        if (expected)
        {
            if (value is null)
            {
                throw new ProtocolViolation(
                    ProtocolReason.SchemaViolation,
                    $"{field} is required for {kind}.");
            }

            if (value < 0)
            {
                throw new ProtocolViolation(
                    ProtocolReason.SchemaViolation,
                    $"{field}={value} is the record's -1 sentinel, not an addressable target.");
            }

            return;
        }

        if (value is not null)
        {
            throw new ProtocolViolation(
                ProtocolReason.SchemaViolation,
                $"{field} must be absent for {kind}.");
        }
    }
}
