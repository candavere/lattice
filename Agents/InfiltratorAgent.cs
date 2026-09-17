using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// A deterministic stealth policy for the infiltration scenario. The
/// infiltrator knows the dungeon layout (scouted in the briefing — static map
/// geometry is read from the observation) but the enemy position is masked by
/// fog-of-war: it projects every <see cref="Observation"/> through its own
/// <see cref="PerceptionFilter"/> and folds the result into an
/// <see cref="AgentBeliefMap"/>, so routing and threat avoidance run over what
/// it has actually seen, never the omniscient rival state.
///
/// The policy is two-phase: while any <see cref="DungeonRoles.TreasureChest"/>
/// remains unclaimed it makes for the <see cref="DungeonRoles.TreasureVault"/>;
/// once the haul is complete it returns to the <see cref="DungeonRoles.EntryHall"/>
/// to claim the <see cref="DungeonRoles.ObjectiveExtraction"/> and exfiltrate.
///
/// The raiding discipline is "dash and bolt": it never stands in a room the
/// guard could be walking into, taking at most one claim per vault visit before
/// falling back out the covert choke and letting the pursuit decay. Stealth is
/// modeled on the guard's own perception envelope (the <see cref="SentryPatrolAgent"/>
/// laws: a zone within 2 graph hops AND a bounded Chebyshev distance reads as
/// visible); while a live sighting marks a landing as dangerous the infiltrator
/// routes around it, and when every exit is contested past the patience budget
/// it forces the least-bad path rather than stall the episode. All ties resolve
/// to canonical order, so the policy is a total, deterministic function of the
/// observation stream.
/// </summary>
public sealed class InfiltratorAgent : IAgent
{
    /// <summary>Default fog-of-war cone, in graph hops.</summary>
    public const int DefaultVision = 2;

    /// <summary>Default ticks a guard sighting stays active for evasion.</summary>
    public const int DefaultThreatRecallTicks = 3;

    /// <summary>The Chebyshev bound of the guard's perception envelope (mirrors the sentry agent).</summary>
    public const int DefaultConeChebyshevRange = SentryPatrolAgent.DefaultChebyshevRange;

    /// <summary>Default straight-tick hiding budget before the infiltrator forces progress.</summary>
    public const int DefaultPatienceTicks = 4;

    private readonly int _sentryId;
    private readonly int _vision;
    private readonly int _threatRecallTicks;
    private readonly int _coneChebyshevRange;
    private readonly int _patienceTicks;
    private readonly AgentBeliefMap _belief;
    private PerceptionFilter? _filter;
    private int _tick;
    private int _consecutiveWaits;
    private int _claimsThisVisit;
    private int _visitZone = -1;

    /// <summary>
    /// Creates the infiltrator for agent slot <paramref name="agentId"/> evading
    /// the guard <paramref name="sentryId"/>. The belief map and visit counter
    /// are per-instance state; reuse the agent across episodes only if a fresh
    /// memory is intended.
    /// </summary>
    public InfiltratorAgent(
        int agentId,
        int sentryId,
        int vision = DefaultVision,
        int threatRecallTicks = DefaultThreatRecallTicks,
        int coneChebyshevRange = DefaultConeChebyshevRange,
        int patienceTicks = DefaultPatienceTicks)
    {
        if (agentId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(agentId), agentId, "AgentId must be non-negative.");
        }

        if (sentryId == agentId)
        {
            throw new ArgumentException("The infiltrator cannot evade itself.", nameof(sentryId));
        }

        if (vision != SimulationConfig.UnboundedVision && vision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vision), vision, "Vision must be UnboundedVision (-1) or >= 1.");
        }

        if (threatRecallTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threatRecallTicks), threatRecallTicks, "ThreatRecallTicks must be >= 1.");
        }

        if (coneChebyshevRange < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(coneChebyshevRange), coneChebyshevRange, "ConeChebyshevRange must be >= 1.");
        }

        if (patienceTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patienceTicks), patienceTicks, "PatienceTicks must be >= 0.");
        }

        AgentId = agentId;
        _sentryId = sentryId;
        _vision = vision;
        _threatRecallTicks = threatRecallTicks;
        _coneChebyshevRange = coneChebyshevRange;
        _patienceTicks = patienceTicks;
        _belief = new AgentBeliefMap(agentId);
    }

    /// <inheritdoc />
    public int AgentId { get; }

    /// <summary>The fog-of-war cone, in graph hops.</summary>
    public int Vision => _vision;

    /// <summary>The guard slot this infiltrator evades.</summary>
    public int SentryId => _sentryId;

    /// <summary>The belief map, exposed read-only for tests.</summary>
    public AgentBeliefMap Belief => _belief;

    /// <inheritdoc />
    public AgentAction Decide(Observation observation)
    {
        _filter ??= new PerceptionFilter(observation.Map, AgentId, _vision);
        var partial = _filter.Project(++_tick, observation);
        _belief.Update(partial);

        if (_belief.MyZone is not int myZone)
        {
            return new AgentAction(ActionKind.Wait);
        }

        if (myZone != _visitZone)
        {
            _claimsThisVisit = 0;
            _visitZone = myZone;
        }

        var (guardSight, guardZone) = GuardSighting(partial);
        var guardVisible = guardSight.Status == KnowledgeStatus.Observed;
        var guardAge = guardZone is null ? int.MaxValue : partial.Tick - guardSight.LastSeenTick;
        var guardActive = guardZone is not null && (guardVisible || guardAge <= _threatRecallTicks);
        var exposedHere = guardActive
            && guardZone is int gzHere
            && InsideGuardCone(observation, myZone, gzHere);
        var guardOnTop = guardActive
            && guardZone is int gzTop
            && Pathfinder.Distance(observation.Map, myZone, gzTop) == 1;
        var guardCoLocated = guardZone == myZone;

        var goal = GoalZone(observation);
        var unclaimed = myZone == goal ? UnclaimedInZone(observation, myZone) : Array.Empty<ResourceNode>();
        var firstClaimPending = unclaimed.Length > 0 && _claimsThisVisit == 0;
        var continuedClaimSafe = unclaimed.Length > 0
            && !guardCoLocated
            && (!guardActive || (!guardOnTop && !exposedHere));

        if (firstClaimPending || continuedClaimSafe)
        {
            _consecutiveWaits = 0;
            _claimsThisVisit++;
            return new AgentAction(ActionKind.Collect, ResourceId: unclaimed[0].Id);
        }

        if (exposedHere)
        {
            return EscapeCone(observation, partial, myZone, guardZone!.Value);
        }

        return ChooseStealthMove(observation, partial, myZone, goal, guardZone, guardVisible, guardActive);
    }

    /// <summary>
    /// Escape response when the infiltrator's current room is inside the
    /// guard's live perception cone: pick the exit that clears the cone
    /// outright (deepest Chebyshev separation from the guard and furthest in
    /// the graph), or — when every exit is still within the cone — dash to the
    /// deepest separation anyway rather than stand still while the guard moves
    /// in. Never waits. Losing the guard by running is always better than being
    /// cornered.
    /// </summary>
    private AgentAction EscapeCone(
        Observation observation,
        PartialObservation partial,
        int myZone,
        int guardZone)
    {
        int escapeZone = -1;
        var escapeSafety = int.MinValue;
        var outsideFound = false;
        int dashZone = -1;
        var dashSafety = int.MinValue;
        var dashFound = false;

        foreach (var candidate in Pathfinder.Neighbors(observation.Map, myZone))
        {
            var safety = CandidateSafety(observation, candidate, guardZone);
            var outside = !InsideGuardCone(observation, candidate, guardZone);
            if (outside)
            {
                if (!outsideFound
                    || safety > escapeSafety
                    || (safety == escapeSafety && candidate > escapeZone))
                {
                    escapeZone = candidate;
                    escapeSafety = safety;
                    outsideFound = true;
                }
            }
            else if (!dashFound
                || safety > dashSafety
                || (safety == dashSafety && candidate > dashZone))
            {
                dashZone = candidate;
                dashSafety = safety;
                dashFound = true;
            }
        }

        var choose = outsideFound ? escapeZone : dashFound ? dashZone : -1;
        if (choose < 0)
        {
            _consecutiveWaits++;
            return new AgentAction(ActionKind.Wait);
        }

        if (_belief.IsEdgeBusy(myZone, choose, partial.Tick))
        {
            _consecutiveWaits++;
            return new AgentAction(ActionKind.Wait); // the escape choke is held this tick
        }

        _consecutiveWaits = 0;
        return new AgentAction(ActionKind.Move, ZoneId: choose);
    }

    /// <summary>
    /// Ranks every legal move, in stealth order. Candidates are rejected while
    /// the guard is currently visible and the landing sits inside its projected
    /// perception cone; the guard's own observed room is always off-limits while
    /// it is being watched. Once the guard sighting goes stale, the cone loses
    /// authority and any remaining path is allowed (guarding progress against
    /// endless hiding). The ranking prefers the furthest-along path to the
    /// objective, tilts toward the deepest Chebyshev separation from the guard's
    /// last-known room, and breaks ties by zone id. If every landing is
    /// contested past the patience budget, the least-bad path is forced rather
    /// than stalling the episode. A capacity-1 gate the belief marks occupied
    /// this tick is refused; a move resets the patience counter, a
    /// <see cref="ActionKind.Wait"/> is the hiding response when every landing
    /// is exposed.
    /// </summary>
    private AgentAction ChooseStealthMove(
        Observation observation,
        PartialObservation partial,
        int myZone,
        int goal,
        int? guardZone,
        bool guardVisible,
        bool guardActive)
    {
        (int Hops, int ZoneId) best = (-1, -1);
        var bestSafety = int.MinValue;
        var bestFound = false;
        (int Hops, int ZoneId) fallback = (-1, -1);
        var fallbackSafety = int.MinValue;
        var fallbackFound = false;

        foreach (var candidate in Pathfinder.Neighbors(observation.Map, myZone))
        {
            var remaining = Pathfinder.Distance(observation.Map, candidate, goal);
            if (remaining is null)
            {
                continue;
            }

            var exposed = guardActive
                && guardZone is int gz
                && InsideGuardCone(observation, candidate, gz);
            var blocked = guardVisible && guardZone is int watched && candidate == watched;
            var safety = CandidateSafety(observation, candidate, guardZone);
            var reachable = remaining.Value < best.Hops
                || (remaining.Value == best.Hops && safety > bestSafety)
                || (remaining.Value == best.Hops && safety == bestSafety && candidate < best.ZoneId);

            if (!blocked && !exposed)
            {
                if (!bestFound || reachable)
                {
                    best = (remaining.Value, candidate);
                    bestSafety = safety;
                    bestFound = true;
                }
            }
            else
            {
                // The patience fallback also admits bolting straight past the
                // watched guard (a simultaneous co-location is a physical
                // interception the run records) — the alternative is rotting in
                // a cul-de-sac until the tick budget expires.
                var fallbackReachable = remaining.Value < fallback.Hops
                    || (remaining.Value == fallback.Hops && safety > fallbackSafety)
                    || (remaining.Value == fallback.Hops && safety == fallbackSafety && candidate < fallback.ZoneId);
                if (!fallbackFound || fallbackReachable)
                {
                    fallback = (remaining.Value, candidate);
                    fallbackSafety = safety;
                    fallbackFound = true;
                }
            }
        }

        var choose = bestFound ? best.ZoneId : fallbackFound && _consecutiveWaits >= _patienceTicks ? fallback.ZoneId : -1;
        if (choose < 0)
        {
            _consecutiveWaits++;
            return new AgentAction(ActionKind.Wait); // every landing is inside the guard's cone: hide
        }

        if (_belief.IsEdgeBusy(myZone, choose, partial.Tick))
        {
            _consecutiveWaits++;
            return new AgentAction(ActionKind.Wait); // a portcullis is held this tick
        }

        _consecutiveWaits = 0;
        return new AgentAction(ActionKind.Move, ZoneId: choose);
    }

    /// <summary>
    /// The guard's last-known zone from this tick's perception: its real-time
    /// zone when observed, or the stale last-known position otherwise. Unknown
    /// drives aggressive routing (<paramref name="guardZone"/> is null) — the
    /// infiltrator moves blind but the threat model has no anchor.
    /// </summary>
    private static (AgentSight Sighting, int? Zone) GuardSighting(PartialObservation partial)
    {
        var sighting = partial.Agents.FirstOrDefault(agent => agent.AgentId == InfiltrationScenario.SentryAgentId);
        var zone = sighting?.LastKnownState?.ZoneId;
        return (sighting ?? new AgentSight(InfiltrationScenario.SentryAgentId, KnowledgeStatus.Unknown, null, -1), zone);
    }

    /// <summary>
    /// Whether <paramref name="zoneId"/> would be perceived by the guard parked
    /// in <paramref name="guardZone"/>: within <see cref="SentryPatrolAgent.DefaultVision"/>
    /// graph hops and <see cref="ConeChebyshevRange"/> Chebyshev units. This is
    /// exactly the <see cref="SentryPatrolAgent"/> perception envelope — the
    /// infiltrator models its pursuer's senses, not the pursuer's intentions.
    /// </summary>
    private bool InsideGuardCone(Observation observation, int zoneId, int guardZone)
    {
        var hops = Pathfinder.Distance(observation.Map, zoneId, guardZone);
        if (hops is null || hops.Value > SentryPatrolAgent.DefaultVision)
        {
            return false;
        }

        return Pathfinder.Chebyshev(
            observation.Map.Zones[zoneId].Position,
            observation.Map.Zones[guardZone].Position) <= _coneChebyshevRange;
    }

    /// <summary>
    /// Chebyshev separation in map units from the guard's last-known room
    /// (used to prefer landings far from the guard; unbounded when unknown).
    /// </summary>
    private int CandidateSafety(Observation observation, int candidate, int? guardZone) =>
        guardZone is int gz && gz >= 0 && gz < observation.Map.Zones.Length && candidate < observation.Map.Zones.Length
            ? Pathfinder.Chebyshev(observation.Map.Zones[candidate].Position, observation.Map.Zones[gz].Position)
            : int.MaxValue;

    /// <summary>
    /// The current objective room: <see cref="DungeonMapBuilder.TreasureVaultZone"/>
    /// while any treasure chest remains unclaimed (the infiltrator is the only
    /// agent that ever claims chests, so <see cref="Observation.Claims"/> is a
    /// safe, deterministic read), otherwise the
    /// <see cref="DungeonMapBuilder.EntryHallZone"/> for extraction.
    /// </summary>
    private int GoalZone(Observation observation)
    {
        foreach (var resource in observation.Map.Resources)
        {
            if (resource.Role == DungeonRoles.TreasureChest && !observation.Claims.Contains(resource.Id))
            {
                return DungeonMapBuilder.TreasureVaultZone;
            }
        }

        return DungeonMapBuilder.EntryHallZone;
    }

    /// <summary>
    /// Unclaimed resources in <paramref name="zoneId"/>, ascending by id
    /// (deterministic collect choice; the vault and entry hall each hold a
    /// single role, so this never races two objectives).
    /// </summary>
    private static ResourceNode[] UnclaimedInZone(Observation observation, int zoneId) =>
        observation.Map.Resources
            .Where(resource => resource.ZoneId == zoneId && !observation.Claims.Contains(resource.Id))
            .OrderBy(resource => resource.Id)
            .ToArray();
}