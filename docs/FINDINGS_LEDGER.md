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

The ledger is maintained under the governing evidentiary standards of
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md);
the corresponding public-claim mappings are calibrated in
[`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md).