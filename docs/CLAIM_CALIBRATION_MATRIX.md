# Claim Calibration Matrix

This matrix maps each public statement made about `candavere/lattice` to the
artifact, test, or release asset that proves it, and states the boundary that
the statement is deliberately bounded to. It is the companion to the
[`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md): the ledger records where claims
have been wrong or incomplete; the matrix states where claims are today.

**Wording status values**

| Status | Meaning |
| --- | --- |
| `CONFIRMED_BOUNDED` | The claim as worded is asserted and its boundary is documented and reproducible. |
| `RESEARCH_PREVIEW_SCOPED` | The claim is an empirical research observation, hardware- and suite-scoped, carrying no production-confidence implication. |
| `DEFERRED_TO_P2` | The claim is deliberately not made yet; it is tracked for a defined future production-readiness stage and its absence is the honest position today. |

Evidence rows cite repository-relative paths. All commits referenced are on the
`main` branch; the reproducible release baseline is documented in
[`reproduction_packet.md`](reproduction_packet.md).

---

## Primary claims

| Public Claim | Documented Location | Evidence Source | Tested Matrix & Scope | Documented Boundary / Known Limit | Wording Status |
| --- | --- | --- | --- | --- | --- |
| Runtime transition determinism: a step is a pure function of the state and chosen actions under the .NET 8 BCL contract | README §Key Mechanics → Step contracts and replay determinism; README “Design principles”; [`adr-0003`](adr/0003-simultaneous-actions-step-contract.md); [`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) Invariants 1–3, 5; [`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md) §1 | `Tests/Property/StepDeterminismProperty.cs` (same assembly ⇒ identical serialized step), `CapacityProperty.cs`, `ConservationProperty.cs`, `GeneratorDeterminismProperty.cs`, `GeneratorWellformednessProperty.cs`, `SimulationConfigBoundaryProperty.cs`; `Tests/Environment/StepContractTests.cs`, `SimulationStepTests.cs`, `TransitTests.cs` | 400 seeded cases × 3 base seeds = 1,200 cases per property; deterministic RNG contract; same suite run on ubuntu, windows, and macos CI runners | Determinism is within the specified BCL contract and per-step serialized `StepResult` equivalence plus, where recorded, a single per-tick state digest; there is no canonical simulation-state hash *tree* (no Merkle structure or cross-episode chaining) and no concurrency/multithreading guarantee | `CONFIRMED_BOUNDED` |
| Replay equivalence across platforms: recorded episodes verified by `TrajectoryReplay.Verify` into the same step stream | README §Key Mechanics → Step contracts and replay determinism; README `replay` CLI reference; [`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md) §5; [`reproduction_packet.md`](reproduction_packet.md) experiment 2 | `Tests/fixtures/golden_trajectory.jsonl`; `Tests/Property/ReplayEquivalenceProperty.cs` (`RecordedTrajectory_ReconstructsAndReplays_Identically`); `Tests/Trajectories/TrajectoryTests.cs`; cross-platform replay-verify step in the CI OS matrix (`1c6fa80`) | Golden JSONL exercised on ubuntu, windows, and macos runners; 1,200 seeded round-trip/replay cases; round-trip rewrite byte-identity covered in `TrajectoryTests` | Equivalence is per-step serialized `StepResult` plus, for schema-3 recordings, per-tick SHA-256 state-digest equality; it is not byte-identical artifact equality across platforms and not a cross-version promise (trajectories are pinned to a release commit) | `CONFIRMED_BOUNDED` |
| Dynamic map/rule behavior: portcullis-timed, capacity-1 choke contention, and event-locked rule resolution behave per the dynamic topology contract | README §Key Mechanics → Dynamic topology; [`adr-0003`](adr/0003-simultaneous-actions-step-contract.md) addenda; [`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) — dynamic choke policy | `Tests/Environment/DynamicTopologyTests.cs`, `TransitTests.cs`; `Tests/fixtures/golden_dynamic_rules.json`; `Tests/Fuzz/DynamicMapRuleFuzzTests.cs` (2 base seeds × 800 cases); `Tests/Fuzz/CliArgumentVectors.cs` | Deterministic seeds; capacity-1 choke and portcullis oscillation; rule binding-time validation (last-in-list-wins, event lock); golden dynamic-rule fixture | Rule validity is enforced at binding time; the contract covers the specified rule grammar and does not generalize to arbitrarily composed rule sets; fuzz mutations stay inside the grammar-preserving JSONL envelope | `CONFIRMED_BOUNDED` |
| Perception is graph reachability (`d_G ≤ Vh`), not Euclidean distance: the observation operator carries no hidden state | README §Key Mechanics → Bounded perception and stale memory; [`docs/articles/02-fog-of-war-without-state-leakage.md`](articles/02-fog-of-war-without-state-leakage.md); [`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) Invariant 4 | `Tests/Property/PerceptionReachabilityProperty.cs` (1,200 seeded cases); `Tests/Environment/PerceptionFilterTests.cs`; `Tests/Agents/*` | BFS over static adjacency with ascending neighbor-id order; default `Vh`; cross-checked on dev and heldout map suites | Euclidean distance is explicitly not an oracle; dynamic gate state never enters the operator (see `FINDING-004` boundary in the invariant spec’s challenge question 2); observation is a set with no history implied | `CONFIRMED_BOUNDED` |
| Policy-environment interaction: 32-rollout MCTS underperforms Scout on standard maps yet outperforms Scout under bottleneck contention | README §CLI reference → evaluate; README §MCTS Empirical Evaluation (standard and contention-bearing); [`docs/articles/03-automated-map-fairness-profiling.md`](articles/03-automated-map-fairness-profiling.md) | `benchmarks/mcts_evaluation_results.json` (source revision `5783ca1`); `benchmarks/bottleneck_evaluation_results.json` (source revision `28c89d3`); procedural generator + paired harness `c568897`, `cb91bc3`, `f4af28d`, artifact `e6ba4bf` | Mirrored-seat scoring per [`adr-0004`](adr/0004-spawn-fairness-mirrored-seatings.md); 32 rollouts/action; standard: dev 50 + heldout 50 seeds (mean delta −1.12/−1.25, 0/100 seeds favorable to MCTS); bottleneck: dev 30 + heldout 30 seeds (mean delta +2.03/+2.60, 60/60 seeds favorable); macOS 8-core, .NET 10.0.10 (standard) / .NET 10.0.12 (bottleneck); ≤200 match steps | Empirical and context-dependent (see `FINDING-006`); hardware- and map-suite-scoped; implies no production-confidence claim and no extrapolation to longer rollouts or unlisted mapsets | `RESEARCH_PREVIEW_SCOPED` |
| Clean BCL dependency boundary: every production assembly is pure .NET 8 BCL with zero NuGet runtime dependencies | README banner and “Development and Verification Method” (§“pure .NET 8 BCL” bullet); README “Design principles” (BCL-only); [`docs/ECOSYSTEM.md`](ECOSYSTEM.md) | `Environment/Lattice.Environment.csproj`, `Cli/Lattice.Cli.csproj`, `Generator/Lattice.Generator.csproj`, `Trajectories/Lattice.Trajectories.csproj`, `Analytics/Lattice.Analytics.csproj`, `Agents/Lattice.Agents.csproj`, `Visualization/Lattice.Visualization.csproj` — zero `PackageReference` entries; `Lattice.sln` includes no `reference/` project | All solution-included production projects, `net8.0`; suite verified on ubuntu, windows, macos | BCL-only refers to build-time dependency scope; the sole NuGet consumers are the test project (xunit + Microsoft.NET.Test.Sdk, test-only). The `reference/` tree is an isolated, non-built directory that is not shipped as part of any release asset | `CONFIRMED_BOUNDED` |
| Release immutability: `v2.3.1` and `v2.3.2` artifacts are pinned, checksummed under `immutable: true` (`v2.3.2` published at commit `4f7816f`), and reproducible from a single committed revision; `v2.3.0` is historical and untouched but was published under `immutable: false` | README §Development and Verification Method (release verification); [`reproduction_packet.md`](reproduction_packet.md) (experiment 1 and appendices); [`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md) §4 (release immutability policy) and §5 | `.github/workflows/release.yml` (tag↔project parity, native smoke matrix, SHA-256 generation, SBOM emission); checked-in checksums and source commit pin in `docs/reproduction_packet.md`; tag `v2.3.1` → commit `c0e8342`; tag `v2.3.2` → commit `4f7816f`; tag `v2.3.0` → commit `67ad127` | Release job runs the smoke matrix on ubuntu, windows, macos; checksums exercised by the reproduction packet verification procedures | `immutable: true` is scoped exclusively to `v2.3.1` and `v2.3.2`, both published (`v2.3.1` at `c0e8342`, `v2.3.2` at `4f7816f` with `immutable: true`); it covers the binary assets and their pinned source commit and does not freeze `main` — post-release commits continue with `RESEARCH_PREVIEW_SCOPED` wording. `v2.3.0` is historical and untouched but was published under `immutable: false`; it is never modified, retagged, or deleted. Staging/publish defect history in `FINDING-003` | `CONFIRMED_BOUNDED` |

---

## Statements not made (deferred)

| Statement | Posture today | Tracked outcome |
| --- | --- | --- |
| Production-grade operational readiness (SLOs, capacity planning, failure handling under load) | Not asserted anywhere; README “Who Is This For?” and the site messaging scope Lattice as research preview | `DEFERRED_TO_P2` — a defined future production-readiness stage with explicit instrumentation and load evidence |
| Throughput/latency at scale or on unlisted hardware | Committed benchmark artifact is hardware-scoped (single host class) and runner comparisons are structural smokes (`FINDING-002`); no scaling claim is made | `DEFERRED_TO_P2` |
| Generalized policy superiority of MCTS or Scout outside the committed map suites | No general claim exists; the opposite direction is a preserved negative result (`FINDING-006`) | `DEFERRED_TO_P2` |

---

## Calibration record

| Status | Count |
| --- | --- |
| `CONFIRMED_BOUNDED` | 6 |
| `RESEARCH_PREVIEW_SCOPED` | 1 |
| `DEFERRED_TO_P2` | 3 |

The matrix is maintained under the evidentiary standards of
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md).
Discrepancies found while mapping claims are filed first in the
[`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md) and then closed here.