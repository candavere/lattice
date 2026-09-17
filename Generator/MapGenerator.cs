using Lattice.Environment;

namespace Lattice.Generator;

/// <summary>
/// Immutable configuration for map generation. Invalid ranges are rejected in
/// the constructor so a generated map can never silently depend on a
/// hardening/or-normalizing step reading the config differently.
/// </summary>
public sealed class GeneratorConfig
{
    /// <summary>Lower bound on the number of zones, inclusive.</summary>
    public int MinZones { get; }

    /// <summary>Upper bound on the number of zones, inclusive.</summary>
    public int MaxZones { get; }

    /// <summary>Every zone must have at least this many incident choke points.</summary>
    public int MinChokePointsPerZone { get; }

    /// <summary>Lower bound on resources placed per zone, inclusive.</summary>
    public int MinResourcesPerZone { get; }

    /// <summary>Upper bound on resources placed per zone, inclusive.</summary>
    public int MaxResourcesPerZone { get; }

    /// <summary>Maximum generate attempts before the generator throws.</summary>
    public int RetryCap { get; }

    /// <summary>
    /// The default rejection budget, used when a caller does not pick its own.
    /// Generation is a bounded retry loop: after this many rejected candidates
    /// the generator raises <see cref="MapGenerationException"/> instead of
    /// silently degrading the constraints.
    /// </summary>
    public const int DefaultRetryCap = 50;

    /// <summary>
    /// Validates the ranges and throws <see cref="ArgumentOutOfRangeException"/>
    /// on nonsense so callers find out immediately instead of getting an
    /// impossible-to-satisfy generate loop.
    /// </summary>
    public GeneratorConfig(
        int MinZones,
        int MaxZones,
        int MinChokePointsPerZone,
        int MinResourcesPerZone,
        int MaxResourcesPerZone,
        int RetryCap)
    {
        if (MinZones < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(MinZones), MinZones, "At least two zones are required for a playable map.");
        }

        if (MaxZones < MinZones)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxZones), MaxZones, "MaxZones must be >= MinZones.");
        }

        if (MinChokePointsPerZone < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinChokePointsPerZone), MinChokePointsPerZone, "Every zone needs at least one choke point.");
        }

        if (MinResourcesPerZone < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinResourcesPerZone), MinResourcesPerZone, "Resources per zone cannot be negative.");
        }

        if (MaxResourcesPerZone < MinResourcesPerZone)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResourcesPerZone), MaxResourcesPerZone, "MaxResourcesPerZone must be >= MinResourcesPerZone.");
        }

        if (RetryCap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryCap), RetryCap, "RetryCap must be >= 1.");
        }

        this.MinZones = MinZones;
        this.MaxZones = MaxZones;
        this.MinChokePointsPerZone = MinChokePointsPerZone;
        this.MinResourcesPerZone = MinResourcesPerZone;
        this.MaxResourcesPerZone = MaxResourcesPerZone;
        this.RetryCap = RetryCap;
    }
}

/// <summary>
/// Optional caller-supplied acceptance predicate consulted after every
/// structural checker on a candidate that already satisfies them (e.g. a
/// spawn-bias tolerance computed by a map fairness profiler). Rejecting a
/// candidate here counts as a failed attempt in the same retry loop, so a map
/// that is structurally valid but unfair is regenerated rather than patched.
/// The predicate lives outside this project so the generator stays free of
/// agent/simulation dependencies — callers compose it from their own modules.
/// </summary>
public delegate bool MapAcceptanceGate(MapGraph candidate);

/// <summary>
/// The seeded map generator. Produces a candidate map, runs every constraint
/// checker on it, and — when a candidate fails — retries using the same
/// advancing random stream rather than re-seeding, so a fresh attempt cannot
/// accidentally replay the same bad layout. Exhausting the bounded rejection
/// budget (<see cref="GeneratorConfig.RetryCap"/>) raises a
/// <see cref="MapGenerationException"/> with a diagnostic instead of silently
/// returning an invalid map.
/// </summary>
public static class MapGenerator
{
    private const int PlaneSize = 1024;

    /// <summary>
    /// Generates a map satisfying every checker from
    /// <paramref name="config"/> under seed <paramref name="seed"/>. Same seed
    /// plus same config yields a byte-identical map every time; the retry loop
    /// advances one seeded stream (the canonical <see cref="Rng"/>) and never
    /// resets it. When <paramref name="acceptanceGate"/> is supplied, a
    /// structurally valid candidate must also pass the gate or it is retried
    /// like any other failure. Throws <see cref="MapGenerationException"/>
    /// with the failing checks when <see cref="GeneratorConfig.RetryCap"/>
    /// attempts are exhausted.
    /// </summary>
    public static MapGraph Generate(ulong seed, GeneratorConfig config, MapAcceptanceGate? acceptanceGate = null)
    {
        var rng = new Rng(seed);

        for (var attempt = 1; attempt <= config.RetryCap; attempt++)
        {
            var map = BuildCandidate(rng, config);

            var failures = new List<string>();
            if (!ConnectivityChecker.IsSatisfied(map))
            {
                failures.Add("connectivity");
            }

            if (!NoIsolatedZoneChecker.IsSatisfied(map))
            {
                failures.Add("no-isolated-zone");
            }

            if (!MinimumChokePointChecker.IsSatisfied(map, config.MinChokePointsPerZone))
            {
                failures.Add($"minimum-choke-point({config.MinChokePointsPerZone})");
            }

            if (failures.Count == 0 && acceptanceGate is not null && !acceptanceGate(map))
            {
                failures.Add("acceptance-gate");
            }

            if (failures.Count == 0)
            {
                return map;
            }

            if (attempt == config.RetryCap)
            {
                throw new MapGenerationException(seed, config.RetryCap, map.Zones.Length, failures);
            }
        }

        throw new InvalidOperationException(
            $"MapGenerator reached an invalid state for seed {seed}: RetryCap must be at least 1.");
    }

    private static MapGraph BuildCandidate(Rng rng, GeneratorConfig config)
    {
        var zoneCount = rng.Next(config.MinZones, config.MaxZones + 1);
        var zones = new Zone[zoneCount];

        for (var i = 0; i < zoneCount; i++)
        {
            zones[i] = new Zone(
                i,
                new GridPoint(rng.Next(0, PlaneSize), rng.Next(0, PlaneSize)));
        }

        var chokes = new List<ChokePoint>();
        var chokeId = 0;

        for (var i = 0; i < zoneCount - 1; i++)
        {
            chokes.Add(new ChokePoint(chokeId++, i, i + 1));
        }

        var extraEdgeCount = zoneCount;
        for (var i = 0; i < extraEdgeCount; i++)
        {
            var from = rng.Next(0, zoneCount);
            var to = rng.Next(0, zoneCount);
            if (from != to)
            {
                chokes.Add(new ChokePoint(chokeId++, from, to));
            }
        }

        var resources = new List<ResourceNode>();
        var resourceId = 0;

        foreach (var zone in zones)
        {
            var resourceCount = rng.Next(config.MinResourcesPerZone, config.MaxResourcesPerZone + 1);
            for (var i = 0; i < resourceCount; i++)
            {
                resources.Add(new ResourceNode(
                    resourceId++,
                    zone.Id,
                    new GridPoint(rng.Next(0, PlaneSize), rng.Next(0, PlaneSize))));
            }
        }

        return new MapGraph(zones, resources.ToArray(), chokes.ToArray());
    }
}