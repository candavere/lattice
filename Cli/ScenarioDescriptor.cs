using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lattice.Agents;
using Lattice.Environment;
using Lattice.Generator;

namespace Lattice.Cli;

/// <summary>
/// One rejected scenario descriptor, named by the JSON path of the offending
/// field (<c>Map.Overrides.ChokeMaxOccupancy[2].MaxOccupancy</c>) so a reader
/// can go straight to it. <see cref="Message"/> says what is wrong in terms
/// the descriptor's own vocabulary.
/// </summary>
public sealed record ScenarioError(string FieldPath, string Message)
{
    public override string ToString() => $"{FieldPath}: {Message}";
}

/// <summary>
/// Every way a descriptor can be rejected, as one exception carrying all of
/// them. Collecting rather than throwing on the first fault is deliberate: an
/// author fixing a hand-written descriptor wants the whole list in one run,
/// and <c>validate-scenario</c> is exactly the command that shows it.
/// </summary>
public sealed class ScenarioValidationException : Exception
{
    public ScenarioValidationException(IReadOnlyList<ScenarioError> errors)
        : base(string.Join("; ", errors.Select(error => error.ToString())))
    {
        Errors = errors;
    }

    /// <summary>Every rejection found, in document order.</summary>
    public IReadOnlyList<ScenarioError> Errors { get; }
}

/// <summary>
/// One agent seat in a scenario, resolved from the descriptor: the closed
/// policy name, the slot it plays, and the two parameters only some policies
/// take. <see cref="RivalSlot"/> is the opponent a sentry watches or an
/// infiltrator evades; it is null for every other policy, and a descriptor that
/// names it for one is rejected rather than ignored. <see cref="Vision"/> is
/// the decision-time perception cone in graph hops, or
/// <see cref="SimulationConfig.UnboundedVision"/>.
/// </summary>
public sealed record ScenarioSlot(
    int Slot,
    string Policy,
    string? Role,
    int? RivalSlot,
    int Vision);

/// <summary>
/// A validated, fully-resolved scenario. This is the materialized form: the
/// descriptor's generator choice has not been run yet (a scenario is a
/// <em>family</em> until a seed is supplied), but every field that does not
/// depend on the seed is already checked.
/// </summary>
public sealed record ScenarioDescriptor(
    int SchemaVersion,
    string Id,
    string? Name,
    string? Description,
    ScenarioMapSpec Map,
    int AgentCount,
    int StepLimit,
    int TransitSpeed,
    IReadOnlyList<ScenarioSlot> Slots,
    string VictoryCondition,
    string ScoringScheme)
{
    /// <summary>
    /// Materializes the seed-dependent part: runs the named generator family and
    /// applies the descriptor's ordered overrides and additions on top, so the
    /// result is a pure function of (descriptor, seed) exactly as the built-in
    /// families already are.
    /// </summary>
    public MapGraph BuildMap(ulong seed) => Map.Build(seed);
}

/// <summary>
/// How a scenario's map is obtained: either a hand-authored static graph (no
/// generator at all) or a seeded generator family plus an ordered list of
/// overrides and additions. Exactly one of the two forms is present.
/// </summary>
public abstract record ScenarioMapSpec
{
    /// <summary>Resolves the concrete map for <paramref name="seed"/>.</summary>
    public abstract MapGraph Build(ulong seed);

    /// <summary>The hand-authored form: the whole graph is in the file.</summary>
    public sealed record Static(MapGraph Map) : ScenarioMapSpec
    {
        public override MapGraph Build(ulong seed) => Map;
    }

    /// <summary>
    /// The generated form: a seeded family supplies the skeleton and the
    /// descriptor's <see cref="Overrides"/>, then its
    /// <see cref="AddedZones"/>/<see cref="AddedResources"/>/
    /// <see cref="AddedChokePoints"/>, are applied in that order. The family's
    /// own seed variation is untouched — the overrides narrow one declared map
    /// at a time and never reach back into the generator.
    /// </summary>
    public sealed record Generated(
        string Family,
        IReadOnlyList<ZoneMaxOccupancyOverride> Overrides,
        IReadOnlyList<Zone> AddedZones,
        IReadOnlyList<ResourceNode> AddedResources,
        IReadOnlyList<ChokePoint> AddedChokePoints) : ScenarioMapSpec
    {
        public override MapGraph Build(ulong seed)
        {
            var generated = Generate(seed, Family);
            var zones = ApplyZoneOverrides(generated.Zones, Overrides);
            var chokes = generated.ChokePoints;
            var resources = generated.Resources;

            if (AddedZones.Count > 0)
            {
                zones = [.. zones, .. AddedZones];
            }

            if (AddedChokePoints.Count > 0)
            {
                chokes = [.. chokes, .. AddedChokePoints];
            }

            if (AddedResources.Count > 0)
            {
                resources = [.. resources, .. AddedResources];
            }

            return new MapGraph(zones, resources, chokes);
        }

        private static MapGraph Generate(ulong seed, string family) => family switch
        {
            BuiltInScenarios.StandardFamily => MapGenerator.Generate(seed, BuiltInScenarios.DefaultGeneratorConfig),
            BuiltInScenarios.BottleneckFamily => BottleneckScenario.ForSeed(seed),
            _ => throw new InvalidOperationException($"Unknown generator family '{family}'."),
        };

        private static Zone[] ApplyZoneOverrides(Zone[] zones, IReadOnlyList<ZoneMaxOccupancyOverride> overrides)
        {
            if (overrides.Count == 0)
            {
                return zones;
            }

            // Replaced in place, array order preserved: an override is a
            // statement about one zone of the family map, not a re-sort.
            var result = (Zone[])zones.Clone();
            var indexById = new Dictionary<int, int>();
            for (var i = 0; i < result.Length; i++)
            {
                indexById[result[i].Id] = i;
            }

            foreach (var entry in overrides)
            {
                if (indexById.TryGetValue(entry.ZoneId, out var index))
                {
                    result[index] = result[index] with { MaxOccupancy = entry.MaxOccupancy };
                }
            }

            return result;
        }
    }
}

/// <summary>An ordered statement setting one zone's occupancy capacity.</summary>
public sealed record ZoneMaxOccupancyOverride(int ZoneId, int MaxOccupancy);

/// <summary>
/// The closed set of generator families a descriptor may name. These are the
/// families the engine already ships; a descriptor selects one of them rather
/// than describing generation steps, so nothing here can grow a new mechanic.
/// </summary>
public static class BuiltInScenarios
{
    /// <summary>The seeded <see cref="MapGenerator"/> family (<c>generate --seed</c>).</summary>
    public const string StandardFamily = "standard";

    /// <summary>The seeded capacity-1 contention family (<c>evaluate --scenario bottleneck</c>).</summary>
    public const string BottleneckFamily = "bottleneck";

    /// <summary>
    /// The generator configuration the standard family is built with — the same
    /// <c>new(3, 5, 1, 1, 3, DefaultRetryCap)</c> the CLI's own
    /// <c>generate</c> and default <c>simulate</c> use, named once so a
    /// descriptor cannot quietly build a different map from the same family
    /// name.
    /// </summary>
    public static GeneratorConfig DefaultGeneratorConfig { get; } =
        new(3, 5, 1, 1, 3, GeneratorConfig.DefaultRetryCap);
}

/// <summary>
/// The closed vocabulary of win conditions and scoring schemes. Every entry is
/// a rule <see cref="Simulation"/> already implements; a descriptor naming
/// anything else is rejected with the menu quoted, because a scenario file that
/// silently accepted an unimplemented win rule would be a claim the engine
/// cannot honour.
/// </summary>
public static class ScenarioMechanics
{
    /// <summary>
    /// Win conditions, each naming one of the two terminal reasons
    /// <see cref="Simulation.Step"/> actually raises, or
    /// <see cref="FirstOfEither"/> for the engine's real behaviour (the episode
    /// ends on whichever arrives first). Declaring a condition states which
    /// terminal reason the scenario is about; the recorded run always reports
    /// the reason that actually fired.
    /// </summary>
    public static readonly IReadOnlyList<string> VictoryConditions =
        ["resources-exhausted", "step-limit", FirstOfEither];

    /// <summary>
    /// Scoring schemes. <see cref="ResourcesClaimed"/> is the only one the
    /// engine has: <see cref="Simulation.Step"/> adds exactly 1 to a score per
    /// successfully claimed resource, and
    /// <see cref="Simulation"/>'s winner is the highest such score with ties
    /// going to the lowest slot.
    /// </summary>
    public static readonly IReadOnlyList<string> ScoringSchemes = [ResourcesClaimed];

    /// <summary>All resources claimed terminates the episode.</summary>
    public const string ResourcesExhausted = "resources-exhausted";

    /// <summary>The tick budget running out terminates the episode.</summary>
    public const string StepLimit = "step-limit";

    /// <summary>Whichever of the two arrives first, which is what the engine does.</summary>
    public const string FirstOfEither = "first-of-either";

    /// <summary>One point per resource claimed.</summary>
    public const string ResourcesClaimed = "resources-claimed";

    /// <summary>
    /// The closed policy menu. Every name is an agent that exists in
    /// <c>Lattice.Agents</c> today; the loader builds each one through its real
    /// constructor rather than through a factory that could drift from it.
    /// </summary>
    public static readonly IReadOnlyList<string> Policies =
        ["greedy", "random", "mcts", "scout", "sentry", "infiltrator"];

    /// <summary>
    /// The policies that need to know which slot holds their opponent. A
    /// descriptor using one of these without naming a rival slot is rejected,
    /// because the agent's constructor requires it and defaulting it would
    /// invent a pairing the file never stated.
    /// </summary>
    public static readonly IReadOnlyList<string> RivalPolicies = ["sentry", "infiltrator"];

    /// <summary>
    /// The policies whose constructor takes a vision cone. Descriptors may set
    /// it for these; for every other policy naming it is rejected, because
    /// <c>GreedyCollectorAgent</c> and <c>RandomAgent</c> decide from the full
    /// observation and a cone would be a field the engine never reads.
    /// </summary>
    public static readonly IReadOnlyList<string> VisionPolicies = ["scout", "sentry", "infiltrator"];

    /// <summary>
    /// The policies that implement <see cref="IDecidesFromPerception"/>, and so
    /// are the only rosters a descriptor can ask to have decision-time
    /// perceptions recorded for. The recording is all-or-nothing across the
    /// roster (<see cref="ScenarioRunner.Run"/> refuses a partial one), so a
    /// descriptor that records them with any other policy in the roster is
    /// rejected at load rather than at the first tick.
    /// </summary>
    public static readonly IReadOnlyList<string> PerceivingPolicies = ["sentry", "infiltrator"];
}

/// <summary>
/// Reads and validates a v3.1 scenario descriptor.
/// <para>
/// The reader is hand-written over <see cref="JsonElement"/> rather than
/// <c>JsonSerializer.Deserialize</c> for two reasons the contract depends on.
/// First, a rejected descriptor must name the offending field by JSON path;
/// the serializer reports unknown members as an exception message with no
/// stable path. Second, rejecting unknown fields must be a decision with a
/// message, not a side effect of a deserializer setting — the same discipline
/// <c>Protocol/ProtocolSchema.cs</c> applies to the external-agent wire.
/// </para>
/// <para>
/// Numbers are read through <see cref="JsonElement.TryGetInt32"/> on the raw
/// token, which is culture-invariant by construction: a descriptor written on
/// a machine whose locale renders decimals with a comma is rejected rather
/// than silently reinterpreted.
/// </para>
/// </summary>
public static class ScenarioLoader
{
    /// <summary>The only scenario-descriptor schema this build accepts.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Upper bound on a descriptor's size in bytes. A scenario is a topology
    /// declaration, not a data file; past this size a descriptor is doing
    /// something other than describing an environment, and the allocation
    /// should be refused before the JSON is even parsed.
    /// </summary>
    public const int MaxDescriptorBytes = 1 << 20;

    /// <summary>Upper bound on zones, so a descriptor cannot request a pathological graph.</summary>
    public const int MaxZones = 256;

    /// <summary>Upper bound on resources.</summary>
    public const int MaxResources = 4096;

    /// <summary>Upper bound on choke points (edges).</summary>
    public const int MaxChokePoints = 8192;

    /// <summary>Upper bound on the tick budget, bounding any single episode's length.</summary>
    public const int MaxStepLimit = 100_000;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    /// <summary>
    /// The SHA-256 of the descriptor's exact source bytes, lowercase hex. This
    /// is the digest recorded in a trajectory header, and it is computed over
    /// the bytes as read — never over a re-serialization, a path, or an mtime.
    /// Two descriptors that differ by a single byte, including by one byte of
    /// whitespace or by CRLF versus LF, therefore get different digests even
    /// when they parse to the same scenario. That is the point: the digest
    /// identifies the file, not the meaning.
    /// </summary>
    public static string ComputeDigest(byte[] rawBytes) =>
        Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

    /// <summary>
    /// Reads a descriptor from disk exactly once, returning the raw bytes
    /// alongside the validated descriptor and its digest. Reading once is what
    /// makes the digest trustworthy: there is no window in which the file could
    /// be re-read and hashed differently from the one that was parsed.
    /// </summary>
    public static (ScenarioDescriptor Descriptor, string Digest) LoadFile(string path)
    {
        byte[] raw;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ScenarioValidationException([new ScenarioError(path, "file does not exist.")]);
            }

            if (info.Length > MaxDescriptorBytes)
            {
                throw new ScenarioValidationException([new ScenarioError(
                    path,
                    $"descriptor is {info.Length} bytes, above the {MaxDescriptorBytes}-byte limit.")]);
            }

            raw = File.ReadAllBytes(path);
        }
        catch (ScenarioValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ScenarioValidationException([new ScenarioError(path, $"could not be read: {ex.Message}")]);
        }

        return (Load(raw), ComputeDigest(raw));
    }

    /// <summary>Validates raw descriptor bytes and resolves the scenario they declare.</summary>
    public static ScenarioDescriptor Load(byte[] raw)
    {
        var errors = new List<ScenarioError>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ScenarioValidationException(
                [new ScenarioError("(document)", $"malformed JSON: {ex.Message}")]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ScenarioValidationException(
                    [new ScenarioError("(document)", $"must be a JSON object, found {root.ValueKind.ToString().ToLowerInvariant()}.")]);
            }

            var descriptor = ReadRoot(root, errors);
            if (errors.Count > 0)
            {
                throw new ScenarioValidationException(errors);
            }

            return descriptor;
        }
    }

    private static ScenarioDescriptor ReadRoot(JsonElement root, List<ScenarioError> errors)
    {
        var known = new[] { "SchemaVersion", "Id", "Name", "Description", "Map", "Simulation", "Slots", "Victory", "Scoring" };
        RejectUnknown(root, "", known, errors);

        var schemaVersion = ReadInt(root, "SchemaVersion", errors) ?? 0;
        if (schemaVersion != CurrentSchemaVersion)
        {
            errors.Add(new ScenarioError(
                "SchemaVersion",
                $"must be {CurrentSchemaVersion} (the only version this build accepts), found {schemaVersion}."));
        }

        // Id is required, not merely optional-and-defaulted: it is the name a
        // recording's provenance and a reader's documentation both refer to,
        // and an unnamed scenario could not be told apart from any other.
        var id = ReadRequiredString(root, "Id", errors);
        if (id is not null && !IsKebabCase(id))
        {
            errors.Add(new ScenarioError(
                "Id",
                $"must be lower-case kebab-case (a-z, 0-9, and single dashes), found '{id}'."));
        }

        var name = ReadString(root, "Name", errors);
        var description = ReadString(root, "Description", errors);

        var map = ReadMap(root, errors);
        var (agentCount, stepLimit, transitSpeed) = ReadSimulation(root, errors);
        var slots = ReadSlots(root, agentCount, errors);
        var victory = ReadEnum(root, "Victory", "Condition", ScenarioMechanics.VictoryConditions, errors);
        var scoring = ReadEnum(root, "Scoring", "Scheme", ScenarioMechanics.ScoringSchemes, errors);

        return new ScenarioDescriptor(
            schemaVersion,
            id ?? string.Empty,
            name,
            description,
            map ?? new ScenarioMapSpec.Static(new MapGraph([], [], [])),
            agentCount,
            stepLimit,
            transitSpeed,
            slots ?? [],
            victory ?? string.Empty,
            scoring ?? string.Empty);
    }

    // ---------------------------------------------------------------- map

    private static ScenarioMapSpec? ReadMap(JsonElement root, List<ScenarioError> errors)
    {
        if (!root.TryGetProperty("Map", out var map) || map.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ScenarioError("Map", "is required and must be an object."));
            return null;
        }

        // The field set is the union of the two forms' fields, so that
        // "Generator on a static map" is reported by the branch below as a
        // named contradiction rather than as three separate unknown fields.
        // Each branch then re-checks against its own exact set.
        RejectUnknown(
            map,
            "Map",
            ["Source", "Generator", "Overrides", "AddZones", "AddResources", "AddChokePoints", "Zones", "Resources", "ChokePoints"],
            errors);

        var source = ReadEnum(map, "Source", null, ["generated", "static"], errors, prefix: "Map.");
        var isGenerated = map.TryGetProperty("Generator", out _);
        var isStaticShaped = map.TryGetProperty("Zones", out _)
            || map.TryGetProperty("Resources", out _)
            || map.TryGetProperty("ChokePoints", out _);

        // The union check above already names every field present, so a
        // mixed-shape map is reported once, by this branch, instead of being
        // re-reported field by field as an unknown key. Reporting it here also
        // keeps the per-branch exact-field check from firing on a map that has
        // already been rejected for a better reason.
        if (source == "static" && isGenerated)
        {
            errors.Add(new ScenarioError(
                "Map.Generator",
                "is not allowed when 'Map.Source' is 'static'; a hand-authored map declares its whole topology in the file."));
            return new ScenarioMapSpec.Static(new MapGraph([], [], []));
        }

        if (source == "generated" && isStaticShaped)
        {
            errors.Add(new ScenarioError(
                "Map",
                "declares 'Source: generated' together with hand-authored topology arrays; a generated map takes its base from the family and adds to it through the 'Add*' fields."));
            return new ScenarioMapSpec.Static(new MapGraph([], [], []));
        }

        return source == "generated" || isGenerated
            ? ReadGeneratedMap(map, errors)
            : ReadStaticMap(map, errors);
    }

    private static ScenarioMapSpec ReadStaticMap(JsonElement map, List<ScenarioError> errors)
    {
        // A static map carries no generator, no overrides, and no additions:
        // everything it has is in the file. A generated-only field appearing
        // here is named as the contradiction it is.
        foreach (var generatedOnly in new[] { "Generator", "Overrides", "AddZones", "AddResources", "AddChokePoints" })
        {
            if (map.TryGetProperty(generatedOnly, out _))
            {
                errors.Add(new ScenarioError(
                    $"Map.{generatedOnly}",
                    "is not allowed when 'Map.Source' is 'static'; a hand-authored map declares its whole topology in the file."));
                return new ScenarioMapSpec.Static(new MapGraph([], [], []));
            }
        }

        RejectUnknown(map, "Map", ["Source", "Zones", "Resources", "ChokePoints"], errors);

        var zones = ReadZones(map, errors, allowEmpty: false);
        var resources = ReadResources(map, errors);
        var chokes = ReadChokePoints(map, errors);

        if (zones is not null && resources is not null && chokes is not null)
        {
            ValidateTopology(zones, resources, chokes, errors);
        }

        return new ScenarioMapSpec.Static(new MapGraph(zones ?? [], resources ?? [], chokes ?? []));
    }

    private static ScenarioMapSpec ReadGeneratedMap(JsonElement map, List<ScenarioError> errors)
    {
        RejectUnknown(
            map,
            "Map",
            ["Source", "Generator", "Overrides", "AddZones", "AddResources", "AddChokePoints"],
            errors);

        if (!map.TryGetProperty("Generator", out var generator) || generator.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ScenarioError("Map.Generator", "is required when 'Map.Source' is 'generated'."));
            return new ScenarioMapSpec.Static(new MapGraph([], [], []));
        }

        RejectUnknown(generator, "Map.Generator", ["Family"], errors);
        var family = ReadEnum(
            generator,
            "Family",
            null,
            [BuiltInScenarios.StandardFamily, BuiltInScenarios.BottleneckFamily],
            errors,
            prefix: "Map.Generator.");

        var overrides = ReadZoneOverrides(map, errors);
        var addedZones = ReadAddedZones(map, errors);
        var addedResources = ReadAddedResources(map, errors);
        var addedChokes = ReadAddedChokes(map, errors);

        // Ids added on top of a generated family must not collide with ids the
        // family already hands out. The family's own ids are seeded, so the
        // check that can be made without a seed is against each other; the
        // collision is caught when the scenario runs and reports it, rather than
        // being papered over by renumbering (which would silently move a zone
        // a choke or a resource already refers to).
        if (addedZones is not null && addedChokes is not null)
        {
            ValidateTopology(addedZones, addedResources ?? [], addedChokes, errors, prefix: "Map.Add");
        }

        return new ScenarioMapSpec.Generated(
            family ?? string.Empty,
            overrides ?? [],
            addedZones ?? [],
            addedResources ?? [],
            addedChokes ?? []);
    }

    private static IReadOnlyList<ZoneMaxOccupancyOverride>? ReadZoneOverrides(JsonElement map, List<ScenarioError> errors)
    {
        if (!map.TryGetProperty("Overrides", out var overrides) || overrides.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (overrides.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ScenarioError("Map.Overrides", "must be an object."));
            return null;
        }

        RejectUnknown(overrides, "Map.Overrides", ["ZoneMaxOccupancy"], errors);
        if (!overrides.TryGetProperty("ZoneMaxOccupancy", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Map.Overrides.ZoneMaxOccupancy", "must be an array."));
            return null;
        }

        var result = new List<ZoneMaxOccupancyOverride>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var path = $"Map.Overrides.ZoneMaxOccupancy[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["ZoneId", "MaxOccupancy"], errors);
            var zoneId = ReadInt(item, "ZoneId", errors, path);
            var max = ReadInt(item, "MaxOccupancy", errors, path);
            if (zoneId is { } z && z < 0)
            {
                errors.Add(new ScenarioError($"{path}.ZoneId", $"must be >= 0, found {z}."));
            }

            if (max is { } m && m < 0)
            {
                errors.Add(new ScenarioError($"{path}.MaxOccupancy", $"must be >= 0 (0 means impassable), found {m}."));
            }

            if (zoneId is not null && max is not null)
            {
                result.Add(new ZoneMaxOccupancyOverride(zoneId.Value, max.Value));
            }
        }

        return result;
    }

    private static Zone[]? ReadAddedZones(JsonElement map, List<ScenarioError> errors) =>
        ReadZoneArray(map, "AddZones", "Map.AddZones", errors, allowEmpty: true);

    private static ResourceNode[]? ReadAddedResources(JsonElement map, List<ScenarioError> errors)
    {
        if (!map.TryGetProperty("AddResources", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Map.AddResources", "must be an array."));
            return null;
        }

        var result = new List<ResourceNode>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var path = $"Map.AddResources[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["Id", "ZoneId", "X", "Y", "Role"], errors);
            var id = ReadInt(item, "Id", errors, path);
            var zoneId = ReadInt(item, "ZoneId", errors, path);
            if (id is { } i && i < 0)
            {
                errors.Add(new ScenarioError($"{path}.Id", $"must be >= 0, found {i}."));
            }

            if (zoneId is { } z && z < 0)
            {
                errors.Add(new ScenarioError($"{path}.ZoneId", $"must be >= 0, found {z}."));
            }

            var position = ReadPoint(item, "X", "Y", errors, path);
            if (id is null || zoneId is null || position is null)
            {
                continue;
            }

            result.Add(new ResourceNode(id.Value, zoneId.Value, position, ReadString(item, "Role", errors)));
        }

        return ValidateResourceBounds(result, errors, "Map.AddResources") ? result.ToArray() : null;
    }

    private static ChokePoint[]? ReadAddedChokes(JsonElement map, List<ScenarioError> errors)
    {
        if (!map.TryGetProperty("AddChokePoints", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Map.AddChokePoints", "must be an array."));
            return null;
        }

        var result = new List<ChokePoint>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var path = $"Map.AddChokePoints[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["Id", "FromZoneId", "ToZoneId", "MaxOccupancy", "Role"], errors);
            var id = ReadInt(item, "Id", errors, path);
            var from = ReadInt(item, "FromZoneId", errors, path);
            var to = ReadInt(item, "ToZoneId", errors, path);
            var max = ReadInt(item, "MaxOccupancy", errors, path);

            if (id is { } i && i < 0)
            {
                errors.Add(new ScenarioError($"{path}.Id", $"must be >= 0, found {i}."));
            }

            if (from is { } f && f < 0)
            {
                errors.Add(new ScenarioError($"{path}.FromZoneId", $"must be >= 0, found {f}."));
            }

            if (to is { } t && t < 0)
            {
                errors.Add(new ScenarioError($"{path}.ToZoneId", $"must be >= 0, found {t}."));
            }

            if (max is { } m && m < 0)
            {
                errors.Add(new ScenarioError($"{path}.MaxOccupancy", $"must be >= 0 (0 means impassable), found {m}."));
            }

            if (from is { } f2 && to is { } t2 && f2 == t2)
            {
                errors.Add(new ScenarioError(
                    $"{path}.ToZoneId",
                    $"must differ from 'FromZoneId' ({f2}); a choke that starts and ends at one zone is not an edge the step contract can traverse."));
            }

            if (id is null || from is null || to is null)
            {
                continue;
            }

            result.Add(new ChokePoint(id.Value, from.Value, to.Value, max ?? MapLimits.Unlimited, ReadString(item, "Role", errors)));
        }

        return ValidateChokeBounds(result, errors, "Map.AddChokePoints") ? result.ToArray() : null;
    }

    private static Zone[]? ReadZoneArray(
        JsonElement map,
        string property,
        string path,
        List<ScenarioError> errors,
        bool allowEmpty)
    {
        if (!map.TryGetProperty(property, out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return allowEmpty ? [] : null;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError(path, "must be an array."));
            return null;
        }

        var result = new List<Zone>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(itemPath, "must be an object."));
                continue;
            }

            RejectUnknown(item, itemPath, ["Id", "X", "Y", "MaxOccupancy", "Role"], errors);
            var id = ReadInt(item, "Id", errors, itemPath);
            if (id is { } i && i < 0)
            {
                errors.Add(new ScenarioError($"{itemPath}.Id", $"must be >= 0, found {i}."));
            }

            var position = ReadPoint(item, "X", "Y", errors, itemPath);
            var max = ReadInt(item, "MaxOccupancy", errors, itemPath);
            if (max is { } m && m < 0)
            {
                errors.Add(new ScenarioError($"{itemPath}.MaxOccupancy", $"must be >= 0 (0 means impassable), found {m}."));
            }

            if (id is null || position is null)
            {
                continue;
            }

            result.Add(new Zone(id.Value, position, max ?? MapLimits.Unlimited, ReadString(item, "Role", errors)));
        }

        return ValidateZoneBounds(result, errors, path, allowEmpty) ? result.ToArray() : null;
    }

    private static Zone[]? ReadZones(JsonElement map, List<ScenarioError> errors, bool allowEmpty) =>
        ReadZoneArray(map, "Zones", "Map.Zones", errors, allowEmpty);

    private static ResourceNode[]? ReadResources(JsonElement map, List<ScenarioError> errors)
    {
        if (!map.TryGetProperty("Resources", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Map.Resources", "must be an array."));
            return null;
        }

        var result = new List<ResourceNode>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var path = $"Map.Resources[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["Id", "ZoneId", "X", "Y", "Role"], errors);
            var id = ReadInt(item, "Id", errors, path);
            var zoneId = ReadInt(item, "ZoneId", errors, path);
            if (id is { } i && i < 0)
            {
                errors.Add(new ScenarioError($"{path}.Id", $"must be >= 0, found {i}."));
            }

            if (zoneId is { } z && z < 0)
            {
                errors.Add(new ScenarioError($"{path}.ZoneId", $"must be >= 0, found {z}."));
            }

            var position = ReadPoint(item, "X", "Y", errors, path);
            if (id is null || zoneId is null || position is null)
            {
                continue;
            }

            result.Add(new ResourceNode(id.Value, zoneId.Value, position, ReadString(item, "Role", errors)));
        }

        return ValidateResourceBounds(result, errors, "Map.Resources") ? result.ToArray() : null;
    }

    private static ChokePoint[]? ReadChokePoints(JsonElement map, List<ScenarioError> errors)
    {
        if (!map.TryGetProperty("ChokePoints", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Map.ChokePoints", "must be an array."));
            return null;
        }

        var result = new List<ChokePoint>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var path = $"Map.ChokePoints[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["Id", "FromZoneId", "ToZoneId", "MaxOccupancy", "Role"], errors);
            var id = ReadInt(item, "Id", errors, path);
            var from = ReadInt(item, "FromZoneId", errors, path);
            var to = ReadInt(item, "ToZoneId", errors, path);
            var max = ReadInt(item, "MaxOccupancy", errors, path);

            if (id is { } i && i < 0)
            {
                errors.Add(new ScenarioError($"{path}.Id", $"must be >= 0, found {i}."));
            }

            if (from is { } f && f < 0)
            {
                errors.Add(new ScenarioError($"{path}.FromZoneId", $"must be >= 0, found {f}."));
            }

            if (to is { } t && t < 0)
            {
                errors.Add(new ScenarioError($"{path}.ToZoneId", $"must be >= 0, found {t}."));
            }

            if (max is { } m && m < 0)
            {
                errors.Add(new ScenarioError($"{path}.MaxOccupancy", $"must be >= 0 (0 means impassable), found {m}."));
            }

            if (from is { } f2 && to is { } t2 && f2 == t2)
            {
                errors.Add(new ScenarioError(
                    $"{path}.ToZoneId",
                    $"must differ from 'FromZoneId' ({f2}); a choke that starts and ends at one zone is not an edge the step contract can traverse."));
            }

            if (id is null || from is null || to is null)
            {
                continue;
            }

            result.Add(new ChokePoint(id.Value, from.Value, to.Value, max ?? MapLimits.Unlimited, ReadString(item, "Role", errors)));
        }

        return ValidateChokeBounds(result, errors, "Map.ChokePoints") ? result.ToArray() : null;
    }

    private static bool ValidateZoneBounds(List<Zone> zones, List<ScenarioError> errors, string path, bool allowEmpty)
    {
        if (!allowEmpty && zones.Count == 0)
        {
            errors.Add(new ScenarioError(path, "must declare at least one zone; a map with no zones cannot host an episode."));
            return false;
        }

        if (zones.Count > MaxZones)
        {
            errors.Add(new ScenarioError(path, $"declares {zones.Count} zones, above the {MaxZones}-zone limit."));
            return false;
        }

        return true;
    }

    private static bool ValidateResourceBounds(List<ResourceNode> resources, List<ScenarioError> errors, string path)
    {
        if (resources.Count > MaxResources)
        {
            errors.Add(new ScenarioError(path, $"declares {resources.Count} resources, above the {MaxResources}-resource limit."));
            return false;
        }

        return true;
    }

    private static bool ValidateChokeBounds(List<ChokePoint> chokes, List<ScenarioError> errors, string path)
    {
        if (chokes.Count > MaxChokePoints)
        {
            errors.Add(new ScenarioError(path, $"declares {chokes.Count} choke points, above the {MaxChokePoints}-edge limit."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whole-graph checks that only make sense once every array has parsed:
    /// unique ids, unique authored coordinates, endpoints that exist, a
    /// passable graph that connects, and resources a spawning agent can
    /// actually reach. Obstacles are expressed with the engine's own capacity
    /// semantics — a zone or choke with <c>MaxOccupancy = 0</c> is impassable —
    /// so an "inaccessible resource" here is a real unreachable claim under the
    /// step contract, not a property of a wall the descriptor invented.
    /// </summary>
    private static void ValidateTopology(
        Zone[] zones,
        ResourceNode[] resources,
        ChokePoint[] chokes,
        List<ScenarioError> errors,
        string prefix = "Map")
    {
        // Everything from here to the end assumes unique ids and resolvable
        // endpoints; the count is taken first so a failure there can skip the
        // checks rather than fault inside them.
        var startOfTopologyChecks = errors.Count;

        RejectDuplicateIds(zones.Select(zone => zone.Id), $"{prefix}.Zones[].Id", errors);
        RejectDuplicateIds(resources.Select(resource => resource.Id), $"{prefix}.Resources[].Id", errors);
        RejectDuplicateIds(chokes.Select(choke => choke.Id), $"{prefix}.ChokePoints[].Id", errors);

        var zoneIds = zones.Select(zone => zone.Id).ToHashSet();

        foreach (var choke in chokes)
        {
            if (!zoneIds.Contains(choke.FromZoneId))
            {
                errors.Add(new ScenarioError(
                    $"{prefix}.ChokePoints[].FromZoneId",
                    $"choke {choke.Id} starts at zone {choke.FromZoneId}, which is not declared."));
            }

            if (!zoneIds.Contains(choke.ToZoneId))
            {
                errors.Add(new ScenarioError(
                    $"{prefix}.ChokePoints[].ToZoneId",
                    $"choke {choke.Id} ends at zone {choke.ToZoneId}, which is not declared."));
            }
        }

        foreach (var resource in resources)
        {
            if (!zoneIds.Contains(resource.ZoneId))
            {
                errors.Add(new ScenarioError(
                    $"{prefix}.Resources[].ZoneId",
                    $"resource {resource.Id} belongs to zone {resource.ZoneId}, which is not declared."));
            }
        }

        // A hand-authored map is the only form whose coordinates the author
        // fully controls, so it is the only one where two rooms at the same
        // point can be caught as the authoring mistake they are. A generated
        // family keeps its own seeded placement contract, which this must not
        // second-guess.
        if (prefix == "Map")
        {
            RejectDuplicatePositions(zones, errors);
            RejectDuplicatePositions(resources, errors);
        }

        // The checkers below index by zone id, so they can only run once every
        // id is known distinct and every endpoint is known to exist. Running
        // them on a graph that already failed those checks would turn a
        // readable rejection into an unhandled dictionary fault.
        var topologyIsCoherent = errors.Count == startOfTopologyChecks;
        if (!topologyIsCoherent)
        {
            return;
        }

        var graph = new MapGraph(zones, resources, chokes);
        if (zones.Length > 0 && !ConnectivityChecker.IsSatisfied(graph))
        {
            errors.Add(new ScenarioError(
                $"{prefix}.ChokePoints",
                "the declared topology is not connected: every zone must be reachable from every other through choke points."));
        }

        if (zones.Length > 0 && !NoIsolatedZoneChecker.IsSatisfied(graph))
        {
            errors.Add(new ScenarioError(
                $"{prefix}.ChokePoints",
                "at least one zone has no incident choke point and can never be entered or left."));
        }

        foreach (var zone in zones.Where(zone => zone.MaxOccupancy == 0))
        {
            errors.Add(new ScenarioError(
                $"{prefix}.Zones[].MaxOccupancy",
                $"zone {zone.Id} is declared impassable (0); an agent can never occupy it, so declaring it as a room is unreachable by construction."));
        }

        RejectUnreachableResources(zones, resources, chokes, errors, prefix);
    }

    private static void RejectDuplicateIds(IEnumerable<int> ids, string path, List<ScenarioError> errors)
    {
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                errors.Add(new ScenarioError(path, $"id {id} is declared more than once."));
            }
        }
    }

    private static void RejectDuplicatePositions(Zone[] zones, List<ScenarioError> errors)
    {
        var seen = new Dictionary<GridPoint, int>();
        foreach (var zone in zones)
        {
            if (seen.TryGetValue(zone.Position, out var other))
            {
                errors.Add(new ScenarioError(
                    "Map.Zones[].X/.Y",
                    $"zones {other} and {zone.Id} share position ({zone.Position.X},{zone.Position.Y})."));
            }
            else
            {
                seen[zone.Position] = zone.Id;
            }
        }
    }

    private static void RejectDuplicatePositions(ResourceNode[] resources, List<ScenarioError> errors)
    {
        var seen = new Dictionary<GridPoint, int>();
        foreach (var resource in resources)
        {
            if (seen.TryGetValue(resource.Position, out var other))
            {
                errors.Add(new ScenarioError(
                    "Map.Resources[].X/.Y",
                    $"resources {other} and {resource.Id} share position ({resource.Position.X},{resource.Position.Y})."));
            }
            else
            {
                seen[resource.Position] = resource.Id;
            }
        }
    }

    /// <summary>
    /// Every resource must be reachable by an agent that starts where
    /// <see cref="Simulation.CreateInitial(MapGraph, SimulationConfig)"/> puts
    /// it — zone <c>i % zoneCount</c> for slot <c>i</c> — walking only through
    /// chokes that are not capacity-0. A resource sealed behind an impassable
    /// choke or inside an unreachable zone would be unclaimable for the whole
    /// episode, which is a broken scenario rather than a hard one.
    /// </summary>
    private static void RejectUnreachableResources(
        Zone[] zones,
        ResourceNode[] resources,
        ChokePoint[] chokes,
        List<ScenarioError> errors,
        string prefix)
    {
        if (zones.Length == 0 || resources.Length == 0)
        {
            return;
        }

        var passable = new Dictionary<int, HashSet<int>>();
        foreach (var zone in zones)
        {
            passable[zone.Id] = [];
        }

        foreach (var choke in chokes)
        {
            if (choke.MaxOccupancy == 0)
            {
                continue;
            }

            if (passable.TryGetValue(choke.FromZoneId, out var from) && passable.TryGetValue(choke.ToZoneId, out var to))
            {
                from.Add(choke.ToZoneId);
                to.Add(choke.FromZoneId);
            }
        }

        // Slot 0 always spawns in zone 0; a descriptor with more agents than
        // zones round-robins, so the spawn set is zones 0..min(agents, zones)-1
        // and every one of them must reach every resource.
        foreach (var resource in resources)
        {
            if (!passable.ContainsKey(resource.ZoneId))
            {
                continue; // already reported as an undeclared zone
            }

            // Reachable from AT LEAST ONE spawn, not from all of them. An agent
            // starts in zone `i % zoneCount`, so a resource some agent cannot
            // walk to is ordinary asymmetric difficulty — a legitimate scenario
            // — and rejecting it would forbid the asymmetry the capacity model
            // exists to express. What is broken is a resource NO agent can
            // reach, which would sit unclaimable for the whole episode and make
            // "resources-exhausted" unreachable.
            var reachableFromAnySpawn = false;
            for (var slot = 0; slot < Math.Min(4, zones.Length) && !reachableFromAnySpawn; slot++)
            {
                if (Reachable(passable, zones[slot].Id, resource.ZoneId))
                {
                    reachableFromAnySpawn = true;
                }
            }

            if (!reachableFromAnySpawn)
            {
                errors.Add(new ScenarioError(
                    $"{prefix}.Resources[].ZoneId",
                    $"resource {resource.Id} in zone {resource.ZoneId} is unreachable: no spawning slot can walk to that zone through non-impassable choke points, so it could never be claimed."));
            }
        }
    }

    private static bool Reachable(Dictionary<int, HashSet<int>> adjacency, int from, int to)
    {
        if (from == to)
        {
            return true;
        }

        var seen = new HashSet<int> { from };
        var frontier = new Stack<int>();
        frontier.Push(from);
        while (frontier.Count > 0)
        {
            foreach (var next in adjacency[frontier.Pop()])
            {
                if (next == to)
                {
                    return true;
                }

                if (seen.Add(next))
                {
                    frontier.Push(next);
                }
            }
        }

        return false;
    }

    // --------------------------------------------------------- simulation

    private static (int AgentCount, int StepLimit, int TransitSpeed) ReadSimulation(JsonElement root, List<ScenarioError> errors)
    {
        if (!root.TryGetProperty("Simulation", out var simulation) || simulation.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ScenarioError("Simulation", "is required and must be an object."));
            return (0, 0, 0);
        }

        RejectUnknown(simulation, "Simulation", ["AgentCount", "StepLimit", "TransitSpeed"], errors);

        var agentCount = ReadInt(simulation, "AgentCount", errors) ?? 0;
        if (agentCount is < 2 or > 4)
        {
            errors.Add(new ScenarioError(
                "Simulation.AgentCount",
                $"must be between 2 and 4 (the range SimulationConfig accepts), found {agentCount}."));
        }

        var stepLimit = ReadInt(simulation, "StepLimit", errors) ?? 0;
        if (stepLimit < 1 || stepLimit > MaxStepLimit)
        {
            errors.Add(new ScenarioError(
                "Simulation.StepLimit",
                $"must be between 1 and {MaxStepLimit}, found {stepLimit}."));
        }

        var transitSpeed = ReadInt(simulation, "TransitSpeed", errors) ?? 0;
        if (transitSpeed != SimulationConfig.InstantTransit && transitSpeed < 1)
        {
            errors.Add(new ScenarioError(
                "Simulation.TransitSpeed",
                $"must be {SimulationConfig.InstantTransit} (instant transit) or >= 1, found {transitSpeed}."));
        }

        return (agentCount, stepLimit, transitSpeed);
    }

    // -------------------------------------------------------------- slots

    private static IReadOnlyList<ScenarioSlot>? ReadSlots(JsonElement root, int agentCount, List<ScenarioError> errors)
    {
        if (!root.TryGetProperty("Slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ScenarioError("Slots", "is required and must be an array."));
            return null;
        }

        var result = new List<ScenarioSlot>();
        var index = 0;
        foreach (var item in slots.EnumerateArray())
        {
            var path = $"Slots[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ScenarioError(path, "must be an object."));
                continue;
            }

            RejectUnknown(item, path, ["Slot", "Policy", "Role", "RivalSlot", "Vision"], errors);
            var slot = ReadInt(item, "Slot", errors, path);
            var policy = ReadEnum(item, "Policy", null, ScenarioMechanics.Policies, errors, prefix: "Slots.");

            if (slot is { } s && s != index - 1)
            {
                errors.Add(new ScenarioError(
                    $"{path}.Slot",
                    $"must equal its position in the array ({index - 1}) so roster order is explicit and total; found {s}."));
            }

            if (agentCount > 0 && slot is { } counted && counted >= agentCount)
            {
                errors.Add(new ScenarioError(
                    $"{path}.Slot",
                    $"is {counted}, but 'Simulation.AgentCount' is {agentCount}; slot ids must be 0..{agentCount - 1}."));
            }

            var rival = ReadInt(item, "RivalSlot", errors, path);
            var vision = ReadInt(item, "Vision", errors, path);

            if (policy is not null)
            {
                var needsRival = ScenarioMechanics.RivalPolicies.Contains(policy, StringComparer.Ordinal);
                if (needsRival && rival is null)
                {
                    errors.Add(new ScenarioError(
                        $"{path}.RivalSlot",
                        $"is required by policy '{policy}', which decides against a named opponent slot."));
                }

                if (!needsRival && rival is not null)
                {
                    errors.Add(new ScenarioError(
                        $"{path}.RivalSlot",
                        $"is not used by policy '{policy}', which decides from the full observation."));
                }

                if (rival is { } r && slot is { } own && r == own)
                {
                    errors.Add(new ScenarioError($"{path}.RivalSlot", $"must differ from the slot itself ({own})."));
                }

                if (rival is { } r2 && agentCount > 0 && r2 >= agentCount)
                {
                    errors.Add(new ScenarioError(
                        $"{path}.RivalSlot",
                        $"is {r2}, but 'Simulation.AgentCount' is {agentCount}."));
                }

                var takesVision = ScenarioMechanics.VisionPolicies.Contains(policy, StringComparer.Ordinal);
                if (takesVision && vision is { } v && v != SimulationConfig.UnboundedVision && v < 1)
                {
                    errors.Add(new ScenarioError(
                        $"{path}.Vision",
                        $"must be {SimulationConfig.UnboundedVision} (unbounded) or >= 1, found {v}."));
                }

                if (!takesVision && vision is not null)
                {
                    errors.Add(new ScenarioError(
                        $"{path}.Vision",
                        $"is not used by policy '{policy}', which decides from the full observation."));
                }
            }

            var effectiveVision = vision ?? SimulationConfig.UnboundedVision;
            result.Add(new ScenarioSlot(
                slot ?? (index - 1),
                policy ?? string.Empty,
                ReadString(item, "Role", errors),
                rival,
                effectiveVision));
        }

        if (agentCount > 0 && result.Count != agentCount)
        {
            errors.Add(new ScenarioError(
                "Slots",
                $"declares {result.Count} slot(s) but 'Simulation.AgentCount' is {agentCount}; the roster must cover slots 0..{agentCount - 1} exactly once."));
        }

        return result;
    }

    // ------------------------------------------------------------ helpers

    private static void RejectUnknown(JsonElement element, string path, IReadOnlyList<string> known, List<ScenarioError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var allowed = new HashSet<string>(known, StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (allowed.Contains(property.Name) || !reported.Add(property.Name))
            {
                continue;
            }

            errors.Add(new ScenarioError(
                $"{path}.{property.Name}".TrimStart('.'),
                $"is not a field of a scenario descriptor. Known fields here: {string.Join(", ", known)}."));
        }
    }

    private static int? ReadInt(JsonElement parent, string name, List<ScenarioError> errors, string prefix = "")
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Raw-token integer read: culture-invariant by construction, and a
        // decimal or exponent form is rejected rather than truncated.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            errors.Add(new ScenarioError(
                $"{prefix}.{name}".TrimStart('.'),
                $"must be a 32-bit integer, found '{value.GetRawText()}'."));
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// A string field that must be present. Distinct from
    /// <see cref="ReadString"/> because "absent" and "present but null" are the
    /// same thing to the optional reader, and a required field has to say so
    /// rather than resolve to null and be defaulted.
    /// </summary>
    private static string? ReadRequiredString(JsonElement parent, string name, List<ScenarioError> errors)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new ScenarioError(name, "is required."));
            return null;
        }

        return ReadString(parent, name, errors);
    }

    private static string? ReadString(JsonElement parent, string name, List<ScenarioError> errors, string prefix = "")
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ScenarioError(
                $"{prefix}.{name}".TrimStart('.'),
                $"must be a string, found {value.ValueKind.ToString().ToLowerInvariant()}."));
            return null;
        }

        return value.GetString();
    }

    private static string? ReadEnum(
        JsonElement parent,
        string objectName,
        string? valueName,
        IReadOnlyList<string> menu,
        List<ScenarioError> errors,
        string prefix = "")
    {
        var path = valueName is null ? $"{prefix}{objectName}" : $"{prefix}{objectName}.{valueName}";
        if (!parent.TryGetProperty(objectName, out var holder))
        {
            errors.Add(new ScenarioError(path, $"is required. Allowed values: {string.Join(", ", menu)}."));
            return null;
        }

        if (holder.ValueKind == JsonValueKind.String)
        {
            var direct = holder.GetString();
            if (menu.Contains(direct, StringComparer.Ordinal))
            {
                return direct;
            }

            errors.Add(new ScenarioError(path, $"'{direct}' is not one of: {string.Join(", ", menu)}."));
            return null;
        }

        if (holder.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ScenarioError(path, $"must be a string or an object carrying '{valueName ?? "Value"}'."));
            return null;
        }

        if (valueName is not null)
        {
            RejectUnknown(holder, path, [valueName], errors);
        }

        if (!holder.TryGetProperty(valueName ?? "Value", out var value))
        {
            errors.Add(new ScenarioError(path, $"is missing '{valueName ?? "Value"}'. Allowed values: {string.Join(", ", menu)}."));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ScenarioError(path, $"must be a string. Allowed values: {string.Join(", ", menu)}."));
            return null;
        }

        var text = value.GetString();
        if (menu.Contains(text, StringComparer.Ordinal))
        {
            return text;
        }

        errors.Add(new ScenarioError(path, $"'{text}' is not one of: {string.Join(", ", menu)}."));
        return null;
    }

    private static GridPoint? ReadPoint(JsonElement parent, string xName, string yName, List<ScenarioError> errors, string prefix)
    {
        var x = ReadInt(parent, xName, errors, prefix);
        var y = ReadInt(parent, yName, errors, prefix);
        return x is null || y is null ? null : new GridPoint(x.Value, y.Value);
    }

    private static bool IsKebabCase(string value) =>
        value.Length > 0
        && !value.StartsWith("-", StringComparison.Ordinal)
        && !value.EndsWith("-", StringComparison.Ordinal)
        && !value.Contains("--", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
