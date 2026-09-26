using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lattice.Environment;

namespace Lattice.Trajectories;

/// <summary>
/// A canonical, deterministic digest of a complete <see cref="SimulationState"/>:
/// the serialization is hashed with SHA-256 and the hex digest is what a
/// recording stores per step, so <c>replay --verify</c> can attest to the state
/// each tick produced and not merely the step results.
///
/// <para><b>What the text covers.</b> Everything that determines the next tick:
/// the episode seed, the tick counter, every agent's id/zone/score and its
/// in-flight crossing (from, to, ticks remaining), every choke's identity and
/// capacity (base, dynamic override, effective), the per-zone occupancy, the
/// per-choke edge load, each zone's id/capacity/<i>position</i>, each resource's
/// id/zone/<i>position</i>, and the claimed resource set. The tick-varying
/// <see cref="DynamicMapOverrides.ChokeCapacities"/> snapshot is the one piece
/// of persistent state a step line does <i>not</i> already carry in its
/// <see cref="Observation"/>, so the digest is genuinely additional evidence
/// rather than a restatement of the step result.</para>
///
/// <para><b>Determinism rules.</b> Field order is fixed by this file and never
/// inferred: agents, chokes, zones and resources are emitted in <i>array</i>
/// order, never in dictionary order, and the dynamic override is looked up by
/// choke id so the text cannot depend on
/// <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}"/>
/// iteration order. Every number is formatted with
/// <see cref="CultureInfo.InvariantCulture"/>, so a machine whose locale uses a
/// comma decimal separator produces the same bytes. The state is entirely
/// <see cref="int"/> and <see cref="ulong"/>, so no floating-point value ever
/// reaches the formatter and no rounding mode can perturb a digest.</para>
///
/// <para><b>Positions are state, not rendering.</b> <see cref="Zone.Position"/>
/// is read by <see cref="Simulation.TransitTicks"/> — the Manhattan distance
/// between two zones is the kinematic length of the edge and sets how many
/// ticks a crossing burns — and both zone and resource positions are read by
/// the perception filter to build the observations a step line carries. A
/// position therefore changes the simulation, not just its drawing, so it is
/// hashed. The one genuinely inert map field is <c>Zone.Role</c> /
/// <c>ResourceNode.Role</c> / <c>ChokePoint.Role</c>: the step contract never
/// reads them, so relabeling a room cannot change a digest and is left out.
/// <c>MapLimits.Unlimited</c> capacity values hash as their integer, since the
/// simulation compares against the same constant.</para>
///
/// <para><b>RNG.</b> The step function consumes no randomness: the
/// simulation core is a total function of the state, and
/// <see cref="Rng"/> is used only by the map generator, the agents and the
/// tests. There is therefore no evolving RNG state to record. The seed is
/// hashed as <i>provenance</i> — it identifies the episode the digest belongs
/// to and is what the header's own map/config were generated under — not as a
/// transition input: <see cref="Simulation.CreateInitial"/> builds the initial
/// state deterministically from the map and config and never reads the seed.</para>
///
/// <para><b>Derived occupancy.</b> The per-tick <c>nodeLoad</c>/<c>edgeLoad</c>
/// arrays the step core builds are locals, reseeded from the state at the top
/// of every <see cref="Simulation.Step"/>, so they are not state of their own.
/// They are re-derived here for completeness and so the choke gate's inputs are
/// visible in the digest. Be clear about what that buys: the derivation
/// mirrors the start-of-tick reseed, which does not yet include the
/// same-tick reservations the step core adds when it admits a crossing, so on a
/// contested choke the hashed load can be one lower than the load the gate
/// actually evaluated. Because the figures are a pure function of the agents,
/// chokes and zones already hashed alongside them, they add no discriminating
/// power at all — they are documentation of the derived view, not extra
/// evidence.</para>
///
/// <para><b>A coupling worth knowing about.</b> The text reads the
/// per-tick override map and the effective capacity through
/// <see cref="DynamicMapOverrides"/>, which stores an entry only when the
/// effective capacity differs from the choke's base. So this digest encodes
/// that storage choice: reworking the overrides to always materialize an entry
/// would change every recorded digest without changing a single simulated
/// outcome. The coupling is accepted deliberately — the override is the piece
/// of persistent state an <see cref="Observation"/> does not carry, so it is
/// exactly the thing worth authenticating — but a refactor of that class must
/// expect to re-record the golden fixture and bump
/// <see cref="FormatTag"/>-bearing expectations rather than assume the digests
/// are stable.</para>
/// </summary>
public static class SimulationStateHash
{
    /// <summary>
    /// Version tag of the canonical text format, hashed into the digest so a
    /// future field-order change cannot silently collide with a recorded one.
    /// </summary>
    private const string FormatTag = "lattice-state-hash/2";

    private const char Absent = '-';

    /// <summary>
    /// Emitted in place of a map field that is absent on a hand-built
    /// recording. It is not producible by any well-formed field value, so a
    /// state carrying it can never match a recorded digest.
    /// </summary>
    private const string Corrupt = "<corrupt>";

    /// <summary>
    /// The canonical serialization of <paramref name="state"/> under
    /// <paramref name="seed"/>. Exposed for tests and for diagnosing a digest
    /// mismatch; the digest itself is <see cref="Compute"/>.
    /// </summary>
    public static string CanonicalText(SimulationState state, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(state);

        var map = state.Map;
        var nodeLoad = new int[map.Zones.Length];
        var edgeLoad = new int[map.ChokePoints.Length];
        SeedOccupancy(state, nodeLoad, edgeLoad);

        var text = new StringBuilder();
        Append(text, FormatTag);
        Append(text, Invariant($"seed={seed}"));
        Append(text, Invariant($"tick={state.StepCount}"));

        Append(text, Invariant($"agents={state.Agents.Length}"));
        for (var i = 0; i < state.Agents.Length; i++)
        {
            var agent = state.Agents[i];
            var transit = agent.Transit is { } crossing
                ? Invariant($"{crossing.FromZoneId}>{crossing.ToZoneId}>{crossing.RemainingTicks}")
                : Absent.ToString();
            Append(text, Invariant($"agent.{i}={agent.AgentId};{agent.ZoneId};{agent.Score};{transit}"));
        }

        // Chokes in map array order; the override is looked up by choke id so
        // the text never depends on dictionary iteration order.
        Append(text, Invariant($"chokes={map.ChokePoints.Length}"));
        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            var choke = map.ChokePoints[i];
            var overridden = state.Dynamics.ChokeCapacities.TryGetValue(choke.Id, out var capacity);
            var effective = state.Dynamics.EffectiveChokeCapacity(map, i);
            var overrideText = overridden
                ? capacity.ToString(CultureInfo.InvariantCulture)
                : Absent.ToString(CultureInfo.InvariantCulture);
            Append(text, Invariant(
                $"choke.{i}={choke.Id};{choke.FromZoneId};{choke.ToZoneId};{choke.MaxOccupancy};{overrideText};{effective};{edgeLoad[i]}"));
        }

        Append(text, Invariant($"zones={map.Zones.Length}"));
        for (var i = 0; i < map.Zones.Length; i++)
        {
            // A zone read from a file is validated by TrajectoryReader, but a
            // recording can also be built in memory, so a null zone or a null
            // Position is emitted as a marker rather than dereferenced. The
            // marker cannot equal any recorded field, so such a state fails
            // verification instead of faulting inside it.
            var zone = map.Zones[i];
            Append(text, zone is { Position: { } position }
                ? Invariant($"zone.{i}={zone.Id};{position.X};{position.Y};{zone.MaxOccupancy};{nodeLoad[i]}")
                : Invariant($"zone.{i}={Corrupt}"));
        }

        // Resources are episode-constant like the zones, but their positions
        // reach the recorded step results through PerceptionFilter, so they are
        // hashed for the same reason: a map edit that moved a resource must not
        // be able to pass unnoticed.
        Append(text, Invariant($"resources={map.Resources.Length}"));
        for (var i = 0; i < map.Resources.Length; i++)
        {
            var resource = map.Resources[i];
            Append(text, resource is { Position: { } position }
                ? Invariant($"resource.{i}={resource.Id};{resource.ZoneId};{position.X};{position.Y}")
                : Invariant($"resource.{i}={Corrupt}"));
        }

        // Claims are sorted so the digest attests to the claimed <i>set</i>:
        // the step core only ever tests membership, so insertion order carries
        // no meaning and must not be able to fail a replay.
        var claims = state.Claims;
        var ordered = claims.Length == 0 ? Array.Empty<int>() : (int[])claims.Clone();
        Array.Sort(ordered);
        var claimText = new StringBuilder();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0)
            {
                claimText.Append(';');
            }

            claimText.Append(ordered[i].ToString(CultureInfo.InvariantCulture));
        }

        Append(text, Invariant($"claims={claimText}"));

        return text.ToString();
    }

    /// <summary>
    /// The SHA-256 digest of <see cref="CanonicalText"/> as 64 lowercase hex
    /// characters — the value a step line records and the verifier recomputes.
    /// </summary>
    public static string Compute(SimulationState state, ulong seed)
    {
        var bytes = Encoding.UTF8.GetBytes(CanonicalText(state, seed));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Rebuilds the per-tick occupancy arrays the step core seeds at the top of
    /// every tick: an agent at a node loads that node, an agent mid-crossing
    /// loads the choke backing its edge instead. Mirrors the seeding loop in
    /// <c>Simulation.Step</c>.
    /// </summary>
    private static void SeedOccupancy(SimulationState state, int[] nodeLoad, int[] edgeLoad)
    {
        foreach (var agent in state.Agents)
        {
            if (agent.Transit is not { } crossing)
            {
                nodeLoad[agent.ZoneId]++;
                continue;
            }

            var chokeIndex = BackingChoke(state.Map, crossing.FromZoneId, crossing.ToZoneId);
            if (chokeIndex >= 0)
            {
                edgeLoad[chokeIndex]++;
            }
        }
    }

    /// <summary>
    /// The index of the choke backing the undirected edge between two zones, or
    /// -1 when no choke connects them. Capacity is attributed to the edge in
    /// either direction, matching the step core.
    /// </summary>
    private static int BackingChoke(MapGraph map, int fromZoneId, int toZoneId)
    {
        for (var i = 0; i < map.ChokePoints.Length; i++)
        {
            var choke = map.ChokePoints[i];
            if ((choke.FromZoneId == fromZoneId && choke.ToZoneId == toZoneId)
                || (choke.FromZoneId == toZoneId && choke.ToZoneId == fromZoneId))
            {
                return i;
            }
        }

        return -1;
    }

    private static void Append(StringBuilder text, string line) => text.Append(line).Append('\n');

    /// <summary>
    /// Composite formatting runs under the invariant culture explicitly rather
    /// than inheriting the ambient one, so a comma-decimal locale cannot change
    /// a digest.
    /// </summary>
    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
