using Lattice.Environment;

namespace Lattice.Agents;

/// <summary>
/// The bottleneck evaluation topology: a procedural, seed-varying map family
/// that funnels both agents through capacity-1 single-lane chokes so transit
/// denials and claim races actually occur. Every seed draws a distinct
/// topology (choke placement, corridor layout, and vault resource
/// distribution all vary), so a mirrored-seat paired study over a seed suite
/// yields a genuinely dispersive delta distribution rather than the identical
/// CI = [0, 0] replays a fixed single-lane map produced.
///
/// The family guarantees non-zero contention by construction: the two spawn
/// arms are geometric mirror images, so both agents always reach the shared
/// antechamber on the same tick and request the same capacity-1 vault gate in
/// the same tick — a transit denial — regardless of the seed-chosen geometry.
/// </summary>
public static class BottleneckScenario
{
    /// <summary>Zone role tag for the left spawn arm.</summary>
    public const string SpawnLeftRole = "SpawnLeft";

    /// <summary>Zone role tag for the right spawn arm.</summary>
    public const string SpawnRightRole = "SpawnRight";

    /// <summary>Zone role tag for the shared single-lane antechamber.</summary>
    public const string AntechamberRole = "Antechamber";

    /// <summary>Zone role tag for the resource vault behind the shared gate.</summary>
    public const string VaultRole = "Vault";

    /// <summary>Choke role tag for every single-lane gate.</summary>
    public const string PortcullisRole = "Portcullis";

    /// <summary>
    /// The fixed seed that yields the canonical reference topology returned by
    /// <see cref="Build"/>. Kept stable so documentation, renderers, and tests
    /// can rely on a reproducible representative of the family.
    /// </summary>
    public const ulong CanonicalSeed = 1001;

    /// <summary>
    /// The canonical representative bottleneck topology, procedurally generated
    /// from <see cref="CanonicalSeed"/>. Same seed, same bytes, on every host.
    /// </summary>
    public static MapGraph Build() => ForSeed(CanonicalSeed);

    /// <summary>
    /// The seed-propagation contract the evaluation harness relies on: every
    /// seed produces a distinct, deterministic bottleneck topology via
    /// <see cref="ProceduralBottleneckGenerator"/>. The run seed drives both
    /// the map layout and the mirrored seating of the evaluation.
    /// </summary>
    public static MapGraph ForSeed(ulong seed) => ProceduralBottleneckGenerator.Generate(seed);
}