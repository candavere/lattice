using Lattice.Environment;
using Lattice.Protocol;

namespace Lattice.Agents.External;

/// <summary>
/// The wire ↔ <c>Observation</c> / <c>AgentAction</c> mapping of spec §11.2.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> place that knows both vocabularies, which is the
/// point of §11.2's boundary: <c>Lattice.Protocol</c> is a leaf that must not see
/// <c>Observation</c>, <c>MapGraph</c>, or <c>ActionSpace</c>, so it cannot do
/// this translation, and the runner already references both sides. Neither the
/// parser nor the writer is re-implemented here — they are used as they are, and
/// this type only decides what to hand them and how to read the result back.
/// </para>
/// <para>
/// The projection is mechanical and specified field by field in §5.2/§5.3:
/// every member becomes a wire field, <c>PascalCase</c> becomes
/// <c>snake_case</c>, nested records become nested objects, and a null
/// <c>string?</c> is <b>omitted</b> rather than emitted as <c>null</c>. The
/// <c>-1</c> sentinels of <see cref="AgentAction"/> are the other direction of
/// the same rule: §6.1's conditional-presence rule means the wire omits what the
/// record would ignore, so an absent target maps back to <c>-1</c> and no wire
/// field is ever carried that the record would discard.
/// </para>
/// </remarks>
public static class ExternalWire
{
    /// <summary>
    /// Projects an <see cref="Observation"/> onto the wire as an
    /// <see cref="ObservationMessage"/>.
    /// </summary>
    /// <param name="step">
    /// The 0-based wire step, i.e. the runner's loop index — the value the
    /// agent's action must echo (§5.4). Kept separate from
    /// <paramref name="observation"/>'s own <c>StepNumber</c> because they are
    /// distinct fields that §5.4 requires to be equal, and the envelope must not
    /// be populated from the wrong one.
    /// </param>
    /// <param name="observation">The observation to project.</param>
    public static ObservationMessage ToWire(int step, Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ObservationMessage
        {
            Step = step,
            AgentId = observation.AgentId,
            Map = ToWire(observation.Map),
            AgentStates = observation.AgentStates.Select(ToWire).ToArray(),
            Claims = observation.Claims.ToArray(),
            StepNumber = observation.StepNumber,
        };
    }

    /// <summary>Projects a <see cref="MapGraph"/> onto the wire (§5.3).</summary>
    public static MapGraphWire ToWire(MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return new MapGraphWire
        {
            Zones = map.Zones.Select(ToWire).ToArray(),
            Resources = map.Resources.Select(ToWire).ToArray(),
            ChokePoints = map.ChokePoints.Select(ToWire).ToArray(),
        };
    }

    /// <summary>Projects a <see cref="Zone"/> onto the wire (§5.3).</summary>
    public static ZoneWire ToWire(Zone zone) =>
        new()
        {
            Id = zone.Id,
            Position = new GridPointWire { X = zone.Position.X, Y = zone.Position.Y },
            MaxOccupancy = zone.MaxOccupancy,
            Role = zone.Role,
        };

    /// <summary>Projects a <see cref="ResourceNode"/> onto the wire (§5.3).</summary>
    public static ResourceNodeWire ToWire(ResourceNode resource) =>
        new()
        {
            Id = resource.Id,
            ZoneId = resource.ZoneId,
            Position = new GridPointWire { X = resource.Position.X, Y = resource.Position.Y },
            Role = resource.Role,
        };

    /// <summary>Projects a <see cref="ChokePoint"/> onto the wire (§5.3).</summary>
    public static ChokePointWire ToWire(ChokePoint choke) =>
        new()
        {
            Id = choke.Id,
            FromZoneId = choke.FromZoneId,
            ToZoneId = choke.ToZoneId,
            MaxOccupancy = choke.MaxOccupancy,
            Role = choke.Role,
        };

    /// <summary>Projects an <see cref="AgentState"/> onto the wire (§5.3).</summary>
    public static AgentStateWire ToWire(AgentState agent) =>
        new()
        {
            AgentId = agent.AgentId,
            ZoneId = agent.ZoneId,
            Score = agent.Score,
            Transit = agent.Transit is { } transit
                ? new InTransitWire
                {
                    FromZoneId = transit.FromZoneId,
                    ToZoneId = transit.ToZoneId,
                    RemainingTicks = transit.RemainingTicks,
                }
                : null,
        };

    /// <summary>
    /// Projects a validated wire <see cref="ActionMessage"/> back onto
    /// <see cref="AgentAction"/>, which is 1:1 (§6.1).
    /// </summary>
    /// <remarks>
    /// §6.1's conditional-presence rule supplies the record's <c>-1</c> defaults:
    /// a <c>Wait</c> carries neither target, a <c>Move</c> only
    /// <c>zone_id</c>, a <c>Collect</c> only <c>resource_id</c>. The caller has
    /// already run <see cref="ProtocolActionGuard.RequireWithinActionSpace"/>, so
    /// by the time this runs a present target is a real, in-range id and an absent
    /// one is genuinely the sentinel.
    /// </remarks>
    public static AgentAction ToAction(ActionMessage action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action.Kind switch
        {
            ProtocolActionKind.Wait => new AgentAction(ActionKind.Wait),
            ProtocolActionKind.Move => new AgentAction(ActionKind.Move, ZoneId: action.ZoneId!.Value),
            ProtocolActionKind.Collect => new AgentAction(ActionKind.Collect, ResourceId: action.ResourceId!.Value),
            var other => throw new ArgumentOutOfRangeException(
                nameof(action),
                other,
                "Not a protocol v1 action kind."),
        };
    }

    /// <summary>Projects an <see cref="AgentAction"/> onto the wire, for tests and for diagnostics.</summary>
    /// <remarks>
    /// The inverse of <see cref="ToAction"/>, applying §6.1's rule in the other
    /// direction: a field the record does not meaningfully carry is omitted rather
    /// than sent as the <c>-1</c> sentinel, which §6.1 forbids explicitly.
    /// </remarks>
    public static ActionMessage ToWire(int step, AgentAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return new ActionMessage
        {
            Step = step,
            Kind = action.Kind switch
            {
                ActionKind.Wait => ProtocolActionKind.Wait,
                ActionKind.Move => ProtocolActionKind.Move,
                ActionKind.Collect => ProtocolActionKind.Collect,
                var other => throw new ArgumentOutOfRangeException(
                    nameof(action),
                    other,
                    "Not an ActionKind member name."),
            },
            ZoneId = action.Kind == ActionKind.Move ? action.ZoneId : null,
            ResourceId = action.Kind == ActionKind.Collect ? action.ResourceId : null,
        };
    }
}
