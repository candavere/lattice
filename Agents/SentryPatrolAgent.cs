using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The state a <see cref="SentryPatrolAgent"/> is in after a decision:
/// <see cref="Patrol"/> while no infiltrator is inside its perception horizon,
/// <see cref="Pursuit"/> while an infiltrator is — a mode change is the pivot
/// the scenario and tests assert on.
/// </summary>
public enum SentryMode
{
    /// <summary>No infiltrator in the bounded perception horizon; the guard walks its assigned loop.</summary>
    Patrol,

    /// <summary>An infiltrator was detected inside the perception horizon; the guard routes toward its position.</summary>
    Pursuit,
}

/// <summary>
/// A deterministic guard policy. In <see cref="SentryMode.Patrol"/> it walks a
/// fixed zone loop (its assigned beat) one first-hop at a time. Perception is a
/// Chebyshev bounded-hop horizon: an infiltrator is "seen" only when it is
/// within <see cref="Vision"/> graph hops of the sentry (projected through
/// <see cref="PerceptionFilter"/>) <em>and</em> within <see cref="ChebyshevRange"/>
/// map units by Chebyshev distance. When the infiltrator enters that envelope
/// the guard pivots to <see cref="SentryMode.Pursuit"/>: it routes toward the
/// infiltrator's position — real-time while it stays observed, and toward the
/// last-known timestamped (<see cref="KnowledgeStatus.Stale"/>) position when
/// the infiltrator breaks line of sight, for at most
/// <see cref="PursuitRecallTicks"/> ticks after the final sighting, after which
/// the guard falls back to patrol. Decisions are a deterministic function of
/// the observation stream: all perception and memory live in the
/// <see cref="PerceptionFilter"/>, and the patrol cursor advances only from
/// zone observations, so identical observation streams yield identical
/// serialized actions.
/// </summary>
public sealed class SentryPatrolAgent : IAgent
{
    /// <summary>Default perception cone, in graph hops.</summary>
    public const int DefaultVision = 2;

    /// <summary>Default spatial bound of the perception envelope, in Chebyshev map units.</summary>
    public const int DefaultChebyshevRange = 4;

    /// <summary>Default ticks of last-known recall once an infiltrator breaks line of sight.</summary>
    public const int DefaultPursuitRecallTicks = 3;

    private readonly int _infiltratorId;
    private readonly int _vision;
    private readonly int _chebyshevRange;
    private readonly int _pursuitRecallTicks;
    private readonly int[] _patrolRoute;
    private PerceptionFilter? _filter;
    private int _tick;
    private int _patrolIndex;
    private bool _hasHistory;

    /// <summary>
    /// Creates a guard for agent slot <paramref name="agentId"/> watching for
    /// the rival <paramref name="infiltratorId"/>. <paramref name="patrolRoute"/>
    /// is the beat it walks in a cycle; when omitted it defaults to the
    /// infiltration dungeon's outer loop (<c>SentryPost → EntryHall → Corridor →
    /// Armory → SentryPost</c>).
    /// </summary>
    public SentryPatrolAgent(
        int agentId,
        int infiltratorId,
        int vision = DefaultVision,
        int chebyshevRange = DefaultChebyshevRange,
        int pursuitRecallTicks = DefaultPursuitRecallTicks,
        int[]? patrolRoute = null)
    {
        if (agentId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(agentId), agentId, "AgentId must be non-negative.");
        }

        if (infiltratorId == agentId)
        {
            throw new ArgumentException("The sentry cannot watch itself.", nameof(infiltratorId));
        }

        if (vision != SimulationConfig.UnboundedVision && vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vision), vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        if (chebyshevRange < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chebyshevRange), chebyshevRange, "ChebyshevRange must be >= 1.");
        }

        if (pursuitRecallTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pursuitRecallTicks), pursuitRecallTicks, "PursuitRecallTicks must be >= 1.");
        }

        if (patrolRoute is null || patrolRoute.Length < 2)
        {
            patrolRoute ??= new[]
            {
                DungeonMapBuilder.SentryPostZone,
                DungeonMapBuilder.EntryHallZone,
                DungeonMapBuilder.CorridorZone,
                DungeonMapBuilder.ArmoryZone,
            };
        }

        if (patrolRoute.Any(zone => zone < 0))
        {
            throw new ArgumentException("Patrol route zone ids must be non-negative.", nameof(patrolRoute));
        }

        AgentId = agentId;
        _infiltratorId = infiltratorId;
        _vision = vision;
        _chebyshevRange = chebyshevRange;
        _pursuitRecallTicks = pursuitRecallTicks;
        _patrolRoute = patrolRoute.ToArray();
        _patrolIndex = 1; // the loop starts moving on the first tick
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <summary>The patrol beat this guard walks, in cyclic order.</summary>
    public IReadOnlyList<int> PatrolRoute => _patrolRoute;

    /// <summary>The guard's perception cone, in graph hops.</summary>
    public int Vision => _vision;

    /// <summary>The spatial bound of the perception envelope, in Chebyshev map units.</summary>
    public int ChebyshevRange => _chebyshevRange;

    /// <summary>The mode the guard decided for the most recent observation.</summary>
    public SentryMode Mode { get; private set; }

    /// <summary>The zone the guard is routing toward, as decided for the most recent observation.</summary>
    public int? PursuitTargetZone { get; private set; }

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        _filter ??= new PerceptionFilter(observation.Map, AgentId, _vision);
        var partial = _filter.Project(++_tick, observation);
        var myZone = ObservationView.MyZone(observation);

        if (TryResolvePursuitTarget(partial, observation, myZone, out var targetZone))
        {
            Mode = SentryMode.Pursuit;
            PursuitTargetZone = targetZone;
            var hop = Pathfinder.FirstHop(observation.Map, myZone, targetZone);
            return hop is null
                ? new AgentAction(ActionKind.Wait)
                : new AgentAction(ActionKind.Move, ZoneId: hop.Value);
        }

        Mode = SentryMode.Patrol;
        PursuitTargetZone = null;

        if (_hasHistory && myZone == _patrolRoute[_patrolIndex])
        {
            _patrolIndex = (_patrolIndex + 1) % _patrolRoute.Length;
        }

        var waypoint = _patrolRoute[_patrolIndex];
        var patrolHop = Pathfinder.FirstHop(observation.Map, myZone, waypoint);
        _hasHistory = true;
        return patrolHop is null
            ? new AgentAction(ActionKind.Wait)
            : new AgentAction(ActionKind.Move, ZoneId: patrolHop.Value);
    }

    /// <summary>
    /// Determines whether the infiltrator is inside the guard's Chebyshev
    /// bounded-hop perception horizon, and the zone to move toward if so. An
    /// observed infiltrator (inside the hop cone this tick) alerts only when
    /// also within the Chebyshev range — the two bounds must agree. Once an
    /// infiltrator breaks sight, its <see cref="KnowledgeStatus.Stale"/> last
    /// known position keeps the guard in pursuit for at most
    /// <see cref="_pursuitRecallTicks"/> ticks after the sighting, phrasing "the
    /// guard chases where it last saw the intruder, then gives up and resumes
    /// patrol".
    /// </summary>
    private bool TryResolvePursuitTarget(
        PartialObservation partial,
        Observation observation,
        int myZone,
        out int targetZone)
    {
        targetZone = -1;
        var sighting = partial.Agents.FirstOrDefault(agent => agent.AgentId == _infiltratorId);
        if (sighting is null)
        {
            return false;
        }

        if (sighting.Status == KnowledgeStatus.Observed && sighting.LastKnownState is not null)
        {
            if (!InsideChebyshevRange(observation.Map, myZone, sighting.LastKnownState.ZoneId))
            {
                return false;
            }

            targetZone = sighting.LastKnownState.ZoneId;
            return true;
        }

        if (sighting.Status == KnowledgeStatus.Stale && sighting.LastKnownState is not null)
        {
            var sightingAge = partial.Tick - sighting.LastSeenTick;
            if (sightingAge > _pursuitRecallTicks)
            {
                return false;
            }

            targetZone = sighting.LastKnownState.ZoneId;
            return targetZone != myZone;
        }

        return false;
    }

    private bool InsideChebyshevRange(MapGraph map, int fromZone, int toZone) =>
        Pathfinder.Chebyshev(map.Zones[fromZone].Position, map.Zones[toZone].Position) <= _chebyshevRange;
}