# Findings Ledger

The `candavere/lattice` repository maintains this ledger to keep an honest,
append-only record of criticisms, observed edge cases, and resolved issues
surrounding the engine, its verification harnesses, and its public claims. The
ledger deliberately reuses the vocabulary of the project's documentation — see
[`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md),
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md), and the governing
thesis in [`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md).

A finding is recorded as an objective observation first and an assessment
second; the observation section is kept free of interpretation so that the
facts survive independently of the disposition that was eventually reached.
No entry is ever rewritten or laundered into promotional summary — when a
later finding extends an earlier one, it is filed as a new entry that
supersedes by reference, and the original entry keeps its identifier.

This ledger is a **maintained historical log**. Its append-only character is
enforced by editorial policy and by git history, not by an automated
verification script; if a scripted append-only audit (one that machine-checks
that historical entries are never mutated) is introduced, this status will be
reviewed and the replacement documented here. Each resolution also carries a
**resolution release status**: entries resolved before the `v2.3.1` tag commit
`c0e8342` are tagged `RELEASED_IN_v2.3.1`; entries resolved after that tag are
tagged `MAIN_ONLY` and, where they are packaged into the published `v2.3.2`
release (tag `v2.3.2` → commit `4f7816f`), additionally tagged
`RELEASED_IN_v2.3.2`.

## Record schema

| Field | Meaning | Allowed values |
| --- | --- | --- |
| **ID** | Stable sequential identifier | `FINDING-001`, `FINDING-002`, ... |
| **Date** | Date the finding was recorded (UTC) | `YYYY-MM-DD` |
| **Source / Provenance** | How the finding arose | `INTERNAL_AUDIT` (in-repo audit against the invariant and reproducibility contracts), `AI_ASSISTED_REVIEW` (automated/AI-assisted sweep — seeded fuzz, property, or mutation analysis), or `EXTERNAL_INDEPENDENT` (filed by an outside party via a public issue) |
| **Target Surface** | Component, document, or claim affected | file path, test namespace, or named claim |
| **Observation** | Objective statement of the discrepancy or failure mode | prose, interpretation separated below |
| **Severity & Confidence** | Impact; confidence in the observation | Severity `High` / `Medium` / `Low`; Confidence `Confirmed` / `Bounded` |
| **Disposition** | What happened with the finding | `FIXED`, `ACCEPTED_LIMITATION`, `REJECTED_WITH_EVIDENCE`, or `OPEN` |
| **Resolution & Evidence Link** | Commit SHA, test file, fixture, or documentation section that closes or bounds the finding | repository-relative path or `long-sha` |
| **Resolution Release Status** | Which published release contains the resolution | `RELEASED_IN_v2.3.1`, `MAIN_ONLY`, or `MAIN_ONLY → RELEASED_IN_v2.3.2` |

## Closure standards

- **`FIXED`** requires a committed change plus a green verification suite at the
  recorded commit (all .NET tests pass, all Python CI tests pass).
- **`ACCEPTED_LIMITATION`** means the behavior is intentional and publicly
  disclosed in the documentation and/or committed artifacts; the finding stays
  in the ledger as a permanent boundary rather than a defect.
- **`REJECTED_WITH_EVIDENCE`** means reproductions were attempted and failed, or
  a counterexample was produced that falsified the observation. The evidence is
  linked.
- **`OPEN`** means no disposition has been reached. New entries default to
  `OPEN` until a closure standard is met.

External parties may file new findings through the public issue template whose
schema is defined in [`reproduction_packet.md`](reproduction_packet.md)
(Reporting procedures). Reported findings are ingested with provenance
`EXTERNAL_INDEPENDENT`.

---

## FINDING-001 — Replay scope calibration

| Field | Value |
| --- | --- |
| **ID** | `FINDING-001` |
| **Date** | 2026-09-19 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | Public determinism claims — README “Step contracts and replay determinism”, `docs/SUPPORT_AND_REPRODUCIBILITY.md`, `docs/adr-001.md`/`adr-002.md`, governing thesis |
| **Observation** | The published text described replay determinism with unqualified “byte-identical”/“byte-for-byte” wording across runs and platforms. The engine does not maintain a canonical simulation-state hash tree; the serializable replay artifact is the ordered stream of per-step serialized `StepResult` records written as JSONL. The two guarantees — (a) same-input determinism of transition results, and (b) byte-for-byte round-trip identity of a written file when re-read and re-written — are distinct, and the unqualified wording could be read as byte-level identity of artifacts across platforms, which the architecture does not canonicalize. |
| **Severity & Confidence** | High / Confirmed |
| **Disposition** | `FIXED` |
| **Resolution & Evidence Link** | Documentation narrowed to per-step serialized `StepResult` equivalence and a research-preview boundary in `6e361af`, `7cd3ac1`, `c18bc87`, `d14a471`, `abb78fa`. Cross-platform executable verification of the replay contract was added in `1c6fa80` (windows, linux, macos) against `Tests/fixtures/golden_trajectory.jsonl`. The four-tier equivalence vocabulary is specified in `docs/SUPPORT_AND_REPRODUCIBILITY.md`, Section 1 (Equivalence vocabulary) and Section 5 (Replay verification); the invariant is formalized as Invariant 5 in `docs/INVARIANT_SPECIFICATION.md`. |
| **Resolution & Release Status** | `RELEASED_IN_v2.3.1` (all closure commits predate the `c0e8342` tag commit). |

## FINDING-002 — Benchmark gate sensitivity

| Field | Value |
| --- | --- |
| **ID** | `FINDING-002` |
| **Date** | 2026-09-19 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | CI benchmark comparator — `.github/workflows/benchmarks.yml`, `compare_benchmarks.py` |
| **Observation** | Benchmark steps ran on shared, virtualized hosted runners whose CPU throughput visibly jitters between runs and across OS images. Fixed absolute pass/fail thresholds risk two error directions: a healthy change misclassified as a regression, and a genuine (if small) regression classified as noise. The committed baseline was also produced on a non-representative runtime for some images (e.g. an OS default .NET differing from the committed baseline version). |
| **Severity & Confidence** | Medium / Confirmed |
| **Disposition** | `FIXED` |
| **Resolution & Evidence Link** | Retry-on-jitter and coefficient-of-variation-derived per-workload tolerances implemented in `75a1ffa`; the hosted-runner comparator was reclassified as a structural smoke with a unit-tested comparator and a deliberately bounded smoke scope in `af28837`; macos runner runtime alignment in `4a64820`/`d604fc0`. The comparator contract is covered by unit tests in `.github/workflows/test_compare_benchmarks.py` (tolerances, fingerprinting, exit codes, smoke classification). |
| **Resolution & Release Status** | `RELEASED_IN_v2.3.1` (all closure commits predate the `c0e8342` tag commit). |

## FINDING-003 — Release asset staging failure

| Field | Value |
| --- | --- |
| **ID** | `FINDING-003` |
| **Date** | 2026-09-20 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | Release aggregation and publishing job — `.github/workflows/release.yml` |
| **Observation** | The release aggregation job aborted approximately nine seconds into its run, during staging and before any asset upload. Root causes: (a) a flattened directory-path mismatch — the upload steps staged artifacts under a path layout the aggregation step did not reconstruct, so aggregation resolved no inputs; (b) a GitHub token whose scope did not permit the release write call, so the job surfaced a permissions error rather than a content error, which masked the staging problem during triage. No source or release asset was corrupted; the failure was purely in the delivery pipeline. |
| **Severity & Confidence** | High / Confirmed |
| **Disposition** | `FIXED` |
| **Resolution & Evidence Link** | Staging path layout and token permission model corrected in `c0e8342`; the tagged release `v2.3.1` points at that commit, so the fix is included in the shipped assets. Release hygiene is further enforced by tag-to-project version parity, a native smoke matrix, SHA-256 checksum generation, and SBOM emission in `f6b2e33` and `bc24c0b`; published checksums are exercised by [`reproduction_packet.md`](reproduction_packet.md). |
| **Resolution & Release Status** | `RELEASED_IN_v2.3.1` (the fix lands in the `c0e8342` tag commit itself). |

## FINDING-004 — Trajectory parser truncation and null-guard gaps

| Field | Value |
| --- | --- |
| **ID** | `FINDING-004` |
| **Date** | 2026-09-20 |
| **Source / Provenance** | `AI_ASSISTED_REVIEW` |
| **Target Surface** | `Trajectories/TrajectoryReader.cs` — JSONL parsing path feeding `TrajectoryReplay.Verify` |
| **Observation** | A seeded fuzz sweep (structured JSONL mutation; two base seeds × 800 generated cases per target) drove the reader with malformed trajectories and surfaced unchecked parse paths. Five malformed-input classes reached the reconstruct/replay path without a deterministic, typed failure: (1) header with a null `SimulationConfig`; (2) header with a null `Map` or a map missing `Zones`/`Resources`/`ChokePoints`; (3) final line with a null `FinalScores`; (4) `FinalScores` shorter than the config’s declared agent count (truncated episode); (5) an individual step with a null `Actions` or `Result` array. Accepted-but-invalid records could be fed onward where they produced confusing downstream behavior instead of naming the offending field. |
| **Severity & Confidence** | High / Confirmed |
| **Disposition** | `FIXED` |
| **Resolution & Evidence Link** | `cd3a00b` introduced typed `InvalidDataException` guards naming the offending field and line number; five committed regression fixtures in `Tests/fixtures/fuzz/` (`trajectory_null_simulation_config.jsonl`, `trajectory_missing_map_resources.jsonl`, `trajectory_short_final_scores.jsonl`, `trajectory_null_final_scores.jsonl`, `trajectory_null_actions.jsonl`); the expected exception contract is enforced in `Tests/Fuzz/ExceptionContract.cs`. Green suite confirmed at the recorded commit. |
| **Resolution & Release Status** | `MAIN_ONLY` → `RELEASED_IN_v2.3.2` (the closure commit `cd3a00b` postdates the `c0e8342` tag commit and is packaged into the `v2.3.2` patch release). |

## FINDING-005 — Mutation-coverage survivors in the transition surface

| Field | Value |
| --- | --- |
| **ID** | `FINDING-005` |
| **Date** | 2026-09-20 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | `Environment/Simulation.cs` and `Environment/PerceptionFilter.cs` — transition and perception law coverage |
| **Observation** | Stryker mutation analysis was scoped to the two transition-bearing files (`stryker-config.json`, `mutate: Simulation.cs, PerceptionFilter.cs`). The analysis surfaced survivors concentrated at transition-boundary conditions that the then-existing unit tests did not exercise: same-tick choke capacity admission versus destination-node occupancy; a transiting agent collecting from its departure node; terminality on a resource-less map; fork/terminal edge states (zero ticks, tick limit, reset of the terminal flag); and projection-boundary rejections. Twelve distinct critical behaviors were identified as uncovered. |
| **Severity & Confidence** | Medium / Confirmed |
| **Disposition** | `FIXED` |
| **Resolution & Evidence Link** | Tooling pinned in `.config/dotnet-tools.json` and mutation scope configured in `stryker-config.json` (`8eb86fc`); twelve targeted tests added in `ce2620b` across `Tests/Environment/CapacityTests.cs`, `DynamicTopologyTests.cs`, `EnvironmentLoopTests.cs`, `PerceptionFilterTests.cs`, `SimulationForkTests.cs`, and `TransitTests.cs`. The scoped mutation score recorded at the calibration run was 98.43% (calibrated strategy; `high` 80 / `low` 60 / `break` 0 thresholds are the committed gate). Boundary: the per-run HTML/JSON Stryker report is generated locally and is not checked in; the committed gate is the config, not the report artifact. |
| **Resolution & Release Status** | `MAIN_ONLY` → `RELEASED_IN_v2.3.2` (the closure commits `8eb86fc` and `ce2620b` postdate the `c0e8342` tag commit and are packaged into the `v2.3.2` patch release). |

## FINDING-007 — EdgeChoke not-found path uncovered; fuzz-harness kill attribution is batch-state dependent

| Field | Value |
| --- | --- |
| **ID** | `FINDING-007` |
| **Date** | 2026-09-22 |
| **Source / Provenance** | `AI_ASSISTED_REVIEW` |
| **Target Surface** | `Environment/Simulation.cs` — `EdgeChoke` not-found sentinel and the collect-loop resolution bound; `Tests/Fuzz/CliArgumentFuzzTests.cs` |
| **Observation** | (a) The `EdgeChoke` not-found sentinel (`return -1` in `Simulation.cs`) was never exercised: a crossing over a choke-less pair silently skipped the not-found branch, so a corrupt map indexing an absent choke would silently charge `edgeLoad[+1]` instead of surfacing an indexed-out failure. Stryker previously recorded the `-1 → +1` mutant as `NoCoverage`. (b) The resolution-collect loop bound `rank < agentCount` is semantically identical under the `rank <= agentCount` mutant — the extra rank re-resolves the rank-0 agent, which is already processed and claim-gated — yet the CLI-argument fuzz harness reported a kill for that mutant in one batch run. The fuzz harness writes its generated scenario/rule/replay fixtures under a fixed `/tmp/lattice-fuzz-cli-<seed>` workspace and only writes a file when it does not already exist; under Stryker's parallel per-mutant batches a race on that shared workspace changes generated vectors between mutants, so a mutant that deterministically survives a sequential run can appear killed in a batch. The same pattern affected the `zoneCapacity > 0 → >= 0` mutant. |
| **Severity & Confidence** | 439: High / Confirmed. Kill attribution flakiness: Medium / Confirmed (reproduced both as survivor and as batch-killed across runs). |
| **Disposition** | (a) `FIXED` — the uncovered not-found path is now exercised and its loud-failure contract asserted. (b) `ACCEPTED_LIMITATION` — the two survivors are equivalent mutants; deterministic full-suite runs pass against each applied mutant, and the fuzz-attributed kills are documented as batch-state artifacts rather than real detections. |
| **Resolution & Evidence Link** | `TransitAcrossMissingChoke_FailsLoudly_InsteadOfSilentlyChargingSiblingEdge` in `Tests/Environment/TransitTests.cs` and `ResolutionRotation_WrapRank_DoesNotDoubleAwardOrDoubleClaim` in `Tests/Environment/SimulationStepTests.cs` (added in the Stage-3 commit). Baseline record: `benchmarks/mutation_stryker_summary.json` (99.61% raw clean Stryker; reproducible killed/survived disposition and survivor rationales corrected for the line-222 `_agentLastSeenTicks` claim). Determinism checks: full 419-test suite passes with each of the 258 and 309 mutants applied to a clean tree; `CliArgumentFuzzTests` passes three consecutive isolated runs against each. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

## FINDING-006 — Context-dependent policy divergence (preserved negative result)

| Field | Value |
| --- | --- |
| **ID** | `FINDING-006` |
| **Date** | 2026-09-19 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | Policy-environment interaction claims — evaluation artifacts and research-preview statements in README / `site/` |
| **Observation** | Mirrored-seat evaluations of a 32-rollout MCTS policy against the Scout policy produced opposing results across map regimes, so any single-number summary (“MCTS is better” or “Scout is better”) would be false in context. On the standard map suites, MCTS matched Scout in none of the mirrored seats: mean per-match delta −1.12 (dev, 50 seeds) and −1.25 (heldout, 50 seeds), with 0 of 100 seeds favorable to MCTS. Under capacity-1 bottleneck contention the same policy turned favorable: mean delta +1.42 (dev, 30 seeds; 27/30 seeds) and +1.38 (heldout, 30 seeds; 28/30 seeds). The positive direction is an empirical, hardware-scoped observation and does not generalize to production-strength claims. |
| **Severity & Confidence** | High / Confirmed |
| **Disposition** | `ACCEPTED_LIMITATION` — the negative result is preserved, publicly disclosed, and explicitly scoped to research preview; no production-confidence claim is made from either direction. |
| **Resolution & Evidence Link** | Both raw artifacts are committed and cited: `benchmarks/mcts_evaluation_results.json` (source revision `5783ca1`) and `benchmarks/bottleneck_evaluation_results.json` (source revision `1c6fa80`). The procedural bottleneck generator and paired contention harness were added in `c568897` and `cb91bc3`/`f4af28d`; the empirical artifact was committed in `e6ba4bf`. Research-preview scoping and calibration of the surrounding claims are in `6e361af`, `67ad127`, and `abb78fa`. |
| **Resolution & Release Status** | `RELEASED_IN_v2.3.1` (all closure commits predate the `c0e8342` tag commit). |

## FINDING-008 — Bottleneck empirical evidence re-anchored after choke admission fix

| Field | Value |
| --- | --- |
| **ID** | `FINDING-008` |
| **Date** | 2026-09-26 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | `benchmarks/bottleneck_evaluation_results.json` and every doc quoting it (README "Results at a glance" / "Raw numbers", `docs/SUPPORT_AND_REPRODUCIBILITY.md` §3 and §5, `docs/VALIDATION_PLAN.md` experiment 3, `docs/reproduction_packet.md` experiment 3, `docs/CLAIM_CALIBRATION_MATRIX.md`); root cause in `Environment/Simulation.cs` choke admission |
| **Observation** | Every choke crossing (instant, one-tick, and multi-tick) previously passed without the capacity-admission rule, so the committed bottleneck matches were played under a more permissive crossing rule than the spec describes. Commit `28c89d3` made all three crossing kinds pass one admission rule, changing no other engine or test logic; the committed artifact and every doc quoting it still carried the pre-fix numbers. The identical protocol (dev + heldout seed suites, seeds 1001–1030 / 2001–2030, 32 rollouts per action, 200-tick mirrored matches, bottleneck scenario) was re-run on the fixed tree and produced different numbers under an identical config. |
| **Severity & Confidence** | Medium / Confirmed |
| **Disposition** | `FIXED` — the bottleneck result was re-run under the identical config and the artifact regenerated; every doc quote was re-anchored to the new committed values. The engine correction itself is `28c89d3`. This is a correction of the evidence, recorded as a first-class result: the old values are preserved side by side below, in `FINDING-006`, and in git history (`e6ba4bf`). |
| **Resolution & Evidence Link** | Regenerated artifact: `benchmarks/bottleneck_evaluation_results.json` (source revision `28c89d3`), mirrored byte-identically to `site/benchmarks/bottleneck_evaluation_results.json`. Old → new, dev suite: mean paired delta +1.417 → +2.033, 95% CI [1.106, 1.727] → [1.66, 2.407], mean contention saturation 0.22 → 0.159. Held-out suite: mean paired delta +1.383 → +2.6, 95% CI [1.074, 1.692] → [2.2, 3], mean contention saturation 0.272 → 0.165. Match win rate fell while the mean paired delta rose: dev 0.55 → 0.50 (losses 22 → 28 of 60) and held-out 0.533 → 0.50 (losses 14 → 19 of 60), read from the pre-fix and regenerated artifacts. The conclusion held on both suites: the direction stayed positive (further from zero than before) and the 95% CI still excludes zero — lower bounds 1.66 (dev) and 2.2 (held-out), read from the regenerated artifact. Contention saturation changed under the corrected gate and is recorded as measured. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

---

## Ledger index

| ID | Date | Target | Severity | Disposition | Closure commit |
| --- | --- | --- | --- | --- | --- |
| `FINDING-001` | 2026-09-19 | Determinism claim precision | High | `FIXED` | `6e361af` (+ `1c6fa80`) |
| `FINDING-002` | 2026-09-19 | CI benchmark gate | Medium | `FIXED` | `75a1ffa` (+ `af28837`) |
| `FINDING-003` | 2026-09-20 | Release staging/publish job | High | `FIXED` | `c0e8342` |
| `FINDING-004` | 2026-09-20 | `TrajectoryReader` input guards | High | `FIXED` | `cd3a00b` |
| `FINDING-005` | 2026-09-20 | Transition-surfaces mutation coverage | Medium | `FIXED` | `ce2620b` (+ `8eb86fc`) |
| `FINDING-006` | 2026-09-19 | Policy-environment claims | High | `ACCEPTED_LIMITATION` | `e6ba4bf` (+ `abb78fa`) |
| `FINDING-007` | 2026-09-22 | EdgeChoke not-found path + batch-state kill attribution | High | `FIXED` / `ACCEPTED_LIMITATION` | Stage-3 commit (`TransitTests`, `SimulationStepTests`, summary artifact) |
| `FINDING-008` | 2026-09-26 | Bottleneck evidence re-anchor | Medium | `FIXED` | Evidence re-anchor commit (artifact + docs; engine fix `28c89d3`) |

The ledger is maintained under the governing evidentiary standards of
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md);
the corresponding public-claim mappings are calibrated in
[`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md).