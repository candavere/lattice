using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Lattice.Environment;

/// <summary>
/// A rule that can override a choke's <see cref="ChokePoint.MaxOccupancy"/>
/// for a given tick of the episode. Rules are pure and deterministic — the
/// same (stepCount, claims) always yields the same capacity — so dynamic
/// topology never breaks the byte-identical replay guarantee. Returning
/// <c>null</c> means "no override for this choke; fall back to the base
/// <see cref="MapGraph"/> value". Rules that disagree on one choke resolve
/// last-in-list-wins when evaluated together by
/// <see cref="DynamicMapRuleSet"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "ruleKind")]
[JsonDerivedType(typeof(TimedPortcullisRule), "timed-portcullis")]
[JsonDerivedType(typeof(EventLockedChokeRule), "event-locked-choke")]
public interface IDynamicMapRule
{
    /// <summary>
    /// The choke capacity this rule dictates for the given tick, or
    /// <c>null</c> when it does not apply to <paramref name="chokeId"/> at
    /// <paramref name="stepCount"/>. <paramref name="claims"/> exposes which
    /// resources have been claimed so far, which is what trigger-based rules
    /// (e.g. sealing a vault behind the first fetch) consult.
    /// </summary>
    int? EffectiveChokeCapacity(int chokeId, int stepCount, IReadOnlyCollection<int> claims);
}

/// <summary>
/// A timed portcullis: toggles one choke's capacity between an open value and
/// a closed value on a fixed schedule. The choke is open for the first
/// <paramref name="OpenTicks"/> ticks of each cycle and closed for the
/// following <paramref name="ClosedTicks"/> ticks, repeating forever. The
/// canonical authoring (OpenCapacity 1 / ClosedCapacity 0) swings a
/// single-lane gate from traversable to impassable every
/// <paramref name="OpenTicks"/> / <paramref name="ClosedTicks"/> ticks.
/// </summary>
public sealed record TimedPortcullisRule(
    int ChokeId,
    int OpenTicks,
    int ClosedTicks,
    int OpenCapacity = 1,
    int ClosedCapacity = 0) : IDynamicMapRule
{
    /// <inheritdoc />
    public int? EffectiveChokeCapacity(int chokeId, int stepCount, IReadOnlyCollection<int> claims)
    {
        if (chokeId != ChokeId)
        {
            return null;
        }

        var cycle = Math.Max(1, OpenTicks + ClosedTicks);
        return stepCount % cycle < OpenTicks ? OpenCapacity : ClosedCapacity;
    }
}

/// <summary>
/// An event-locked choke: becomes impassable (<paramref name="LockedCapacity"/>,
/// 0 by default) once a specific resource has been claimed — e.g. sealing the
/// vault room behind the agent that made the first fetch. The lock is
/// permanent and applies from the tick after the triggering claim lands.
/// </summary>
public sealed record EventLockedChokeRule(
    int ChokeId,
    int TriggerResourceId,
    int LockedCapacity = 0) : IDynamicMapRule
{
    /// <inheritdoc />
    public int? EffectiveChokeCapacity(int chokeId, int stepCount, IReadOnlyCollection<int> claims)
    {
        if (chokeId != ChokeId)
        {
            return null;
        }

        return claims.Contains(TriggerResourceId) ? LockedCapacity : null;
    }
}

/// <summary>
/// An immutable collection of <see cref="IDynamicMapRule"/>s holding the
/// environmental mutation policy for an episode. Evaluated by
/// <see cref="ComputeChokeCapacities"/> into a per-tick snapshot of override
/// capacities; rules that govern the same choke resolve last-in-list-wins.
/// </summary>
public sealed class DynamicMapRuleSet
{
    /// <summary>The empty rule set: no dynamic topology, base map values apply.</summary>
    public static DynamicMapRuleSet None { get; } = new(Array.Empty<IDynamicMapRule>());

    /// <summary>
    /// Creates a rule set from <paramref name="rules"/>, freezing the list so
    /// later mutation cannot corrupt an in-flight episode. The parameter type
    /// mirrors <see cref="Rules"/> exactly so the type round-trips through
    /// JSON like every other step contract.
    /// </summary>
    public DynamicMapRuleSet(IReadOnlyList<IDynamicMapRule> rules)
    {
        Rules = rules is null ? Array.Empty<IDynamicMapRule>() : rules.ToArray();
    }

    /// <summary>The rules in evaluation order (later rules win on conflicts).</summary>
    public IReadOnlyList<IDynamicMapRule> Rules { get; }

    /// <summary>
    /// Evaluates every rule against the map at <paramref name="stepCount"/>,
    /// returning the choke overrides that differ from the base map values.
    /// Only meaningful overrides are stored, so the result doubles as the
    /// minimal diff between the base topology and this tick's topology.
    /// </summary>
    public ImmutableDictionary<int, int> ComputeChokeCapacities(
        int stepCount,
        IReadOnlyCollection<int> claims,
        MapGraph map)
    {
        var builder = ImmutableDictionary.CreateBuilder<int, int>();
        foreach (var choke in map.ChokePoints)
        {
            var overridden = false;
            var capacity = choke.MaxOccupancy;
            foreach (var rule in Rules)
            {
                if (rule.EffectiveChokeCapacity(choke.Id, stepCount, claims) is { } value)
                {
                    capacity = value;
                    overridden = true;
                }
            }

            if (overridden && capacity != choke.MaxOccupancy)
            {
                builder[choke.Id] = capacity;
            }
        }

        return builder.ToImmutable();
    }
}

/// <summary>
/// The per-tick dynamic topology attached to a <see cref="SimulationState"/>.
/// <see cref="ChokeCapacities"/> is the snapshot of choke overrides effective
/// for that state's tick (choke id → capacity); <see cref="Rules"/> is the
/// policy that produced it and governs future ticks. The step core consults
/// <see cref="EffectiveChokeCapacity"/> for move gating and
/// <see cref="Advance"/> to derive the next state's overrides, so dynamic
/// capacity interleaves with the pure step function without mutating anything.
/// </summary>
public sealed record DynamicMapOverrides
{
    /// <summary>The canonical empty overrides: no rules, no overrides.</summary>
    public static DynamicMapOverrides None { get; } = new();

    /// <summary>
    /// The mutation policy in force for the episode. Fixed per episode; only
    /// the derived <see cref="ChokeCapacities"/> snapshot moves between ticks.
    /// </summary>
    public DynamicMapRuleSet Rules { get; init; } = DynamicMapRuleSet.None;

    /// <summary>
    /// The choke capacity overrides effective for the owning state's tick,
    /// keyed by choke id. Chokes absent from the dictionary use their base
    /// <see cref="MapGraph"/> capacity.
    /// </summary>
    public ImmutableDictionary<int, int> ChokeCapacities { get; init; } = ImmutableDictionary<int, int>.Empty;

    /// <summary>
    /// Builds the overrides snapshot for the first tick (step count 0) of an
    /// episode governed by <paramref name="rules"/>.
    /// </summary>
    public static DynamicMapOverrides ForInitialTick(DynamicMapRuleSet rules, MapGraph map)
    {
        var effective = rules ?? DynamicMapRuleSet.None;
        return new()
        {
            Rules = effective,
            ChokeCapacities = effective == DynamicMapRuleSet.None
                ? ImmutableDictionary<int, int>.Empty
                : effective.ComputeChokeCapacities(0, Array.Empty<int>(), map),
        };
    }

    /// <summary>
    /// The capacity of choke <paramref name="chokeIndex"/> for the owning
    /// state's tick: the dynamic override when one exists, otherwise the base
    /// <see cref="ChokePoint.MaxOccupancy"/>. Callers (the move gateway, routing
    /// checks) must use this so dynamic topology is honored everywhere.
    /// </summary>
    public int EffectiveChokeCapacity(MapGraph map, int chokeIndex)
    {
        var choke = map.ChokePoints[chokeIndex];
        return ChokeCapacities.TryGetValue(choke.Id, out var capacity) ? capacity : choke.MaxOccupancy;
    }

    /// <summary>
    /// The overrides for the next tick: rule-derived capacities are recomputed
    /// at <paramref name="nextStepCount"/> against <paramref name="claims"/>, so
    /// the snapshot always reflects the rules for that exact tick (a
    /// portcullis that reopens, a lock that lifts). Rules are sticky — an
    /// episode governed by a portcullis stays governed by it.
    /// </summary>
    public DynamicMapOverrides Advance(int nextStepCount, IReadOnlyCollection<int> claims, MapGraph map) =>
        this with { ChokeCapacities = Rules.ComputeChokeCapacities(nextStepCount, claims, map) };
}