namespace Lattice.Environment;

/// <summary>
/// Defines which <see cref="AgentAction"/> values are well-formed for a map.
/// This is the contract agents must satisfy (and T3.2's random-legal agent
/// relies on constructing in-space actions by hand). It checks shape only —
/// kind is a defined enum value and ids are in the map's range — not rules
/// the step function tolerates as no-ops (e.g. moving to a non-adjacent
/// zone). Kept pure and total so any action value yields an answer.
/// </summary>
public static class ActionSpace
{
    /// <summary>
    /// Returns every reason <paramref name="action"/> is outside the action
    /// space for <paramref name="map"/>; empty means the action is in space.
    /// A <see cref="ActionKind.Wait"/> ignores its target fields entirely.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentAction action, MapGraph map)
    {
        var problems = new List<string>(2);

        if (action.Kind is not (ActionKind.Wait or ActionKind.Move or ActionKind.Collect))
        {
            problems.Add($"Unknown ActionKind value: {(int)action.Kind}.");
        }

        if (action.Kind == ActionKind.Move && (action.ZoneId < 0 || action.ZoneId >= map.Zones.Length))
        {
            problems.Add($"Move targets zone {action.ZoneId}, but this map has {map.Zones.Length} zone(s).");
        }

        if (action.Kind == ActionKind.Collect && (action.ResourceId < 0 || action.ResourceId >= map.Resources.Length))
        {
            problems.Add($"Collect targets resource {action.ResourceId}, but this map has {map.Resources.Length} resource node(s).");
        }

        return problems;
    }

    /// <summary>
    /// Convenience form of <see cref="Validate"/>: true when the action
    /// produces no problems for <paramref name="map"/>.
    /// </summary>
    public static bool IsValid(AgentAction action, MapGraph map) => Validate(action, map).Count == 0;
}