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
| **Target Surface** | Public determinism claims — README “Step contracts and replay determinism”, `docs/SUPPORT_AND_REPRODUCIBILITY.md`, `docs/adr/0002-mapgraph-graph-over-zones.md`/`0003-simultaneous-actions-step-contract.md`, governing thesis |
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

## FINDING-009 — Throughput baseline re-anchored for the runtime patch (.NET 10.0.10 → 10.0.12)

| Field | Value |
| --- | --- |
| **ID** | `FINDING-009` |
| **Date** | 2026-09-26 |
| **Source / Provenance** | `INTERNAL_AUDIT` |
| **Target Surface** | `benchmarks/throughput_benchmark.json` and every doc quoting it (README "Results at a glance" / "Performance, qualified", `docs/BENCHMARKING.md`, `benchmarks/throughput_summary.md`); no engine, config, or test files |
| **Observation** | The committed record was produced under .NET 10.0.10 (commit `b7459a1`, 2026-09-18), while the installed runtime is now .NET 10.0.12 and the CI gate installs .NET 10.0.x — so the record's runtime provenance no longer described the runtime a matching-host re-benchmark actually runs under. A full-protocol run on the same host under .NET 10.0.12 came in faster on all 5 workloads under identical configs, protocol, and GC counts. Three consecutive sessions on the current tree reproduced the shift only on AC power: the same protocol on battery (51%, discharging) came in at roughly the old levels (micro ~0%, stress +3%), so the absolute magnitude is host-environment-sensitive and the anchor was re-taken on AC power. |
| **Severity & Confidence** | Medium / Confirmed |
| **Disposition** | `FIXED` — all 5 workloads re-anchored on .NET 10.0.12 using the same conservative method as the first re-anchor (`d604fc0`): three consecutive full-protocol sessions on the current tree, committing the whole session that is **low for the noisy workloads, near-typical elsewhere** (not a per-workload mix across sessions). Evidence and docs only; no engine or config change. The speedup against the previous record is **environmental**, not an engine speedup, and must not be read as one: the evidence is identical configs and protocol, effectively identical GC counters and allocations, a uniform +63% to +93% shift across all five workloads including search-bound MCTS, and unlimited-capacity chokes in the generated maps, so trajectories are unchanged. |
| **Resolution & Evidence Link** | New artifact: `benchmarks/throughput_benchmark.json` (source revision `41530ef`, .NET 10.0.12, AC power). Old → new per workload (median): `micro_raw_2agent` 653,736 → 899,075 steps/s (+38%); `facility_static_4agent` 359,071 → 657,189 steps/s (+83%); `dynamic_contention_4agent` 232,978 → 416,170 steps/s (+79%); `stress_topology_4agent` 9,145 → 14,916 steps/s (+63%); `policy_lookahead_mcts_32` 394 → 750 decisions/s (+90%). Config and protocol fields identical to the old file; GC counters effectively identical (stress Gen1 270 → 265 is a measured GC-timing delta, not a config change; all other counters byte-identical); allocations near-identical (`dynamic_contention` 7,107 → 7,097 B/step, all others byte-identical). The generated maps carry only unlimited-capacity chokes, so trajectories are unchanged and the four raw workloads' speedup is a clean environmental comparison. Gate sanity (CI comparator, strict gate armed: macOS family + arm64 + .NET 10 major): the new baseline passes against all three sessions — per-workload ratio vs allowed threshold: `dynamic_contention` 1.000–1.025 vs 0.75; `facility_static` 1.052–1.053 vs 0.75; `micro_raw` 1.000–1.287 vs 0.6; `stress_topology` 0.995–1.000 vs 0.75; `policy_lookahead` 0.960–1.010 vs 0.85. Power-state observation: three battery-mode sessions on the same tree came in at roughly the old levels (micro median ~617k vs ~1.09M on AC; stress ~9.4k vs ~14.9k on AC) — the same host + runtime differs ~2× between battery and AC, so the record's numbers are AC-mode measurements on this host class. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

---

## FINDING-010 — Throughput gate armed on an unlike host class after the re-anchor

| Field | Value |
| --- | --- |
| **ID** | `FINDING-010` |
| **Date** | 2026-09-26 |
| **Source / Provenance** | `INTERNAL_AUDIT` — Benchmarks workflow run `36229846123` (red at `83456ad`) |
| **Target Surface** | `.github/workflows/compare_benchmarks.py` fingerprint arming; `.github/workflows/benchmarks.yml`; no engine, config, or baseline-artifact files |
| **Observation** | After the re-anchor (`FINDING-009`), the Benchmarks workflow failed at `83456ad`: setup, build, and the five-case benchmark passed, and the compare step exited 1 on both the first pass and its retry. Verbatim from the failed run — first pass (08:30:14Z): `baseline host: macOS 27.0.0 / Arm64 / .NET 10.0.12` vs `current host : macOS 14.8.9 / Arm64 / .NET 10.0.12` → `fingerprint match: True  (strict gate armed: True)`; per workload (current vs baseline median, ratio, allowed): `dynamic_contention_4agent` 238,057 vs 416,170 (57.2%, allowed 0.873481x); `facility_static_4agent` 468,384 vs 657,189 (71.3%, allowed 0.848851x); `micro_raw_2agent` 773,745 vs 899,075 (86.1%, above its 0.6 floor); `policy_lookahead_mcts_32` 630 vs 750 (84.0%, allowed 0.85x); `stress_topology_4agent` 12,233 vs 14,916 (82.0%, allowed 0.95x). Retry pass (08:31:59Z): identical fingerprints, strict gate armed again — `dynamic_contention_4agent` 303,545 vs 416,170 (72.9%); `facility_static_4agent` 514,407 vs 657,189 (78.3%); `micro_raw_2agent` 949,031 (1.056); `policy_lookahead_mcts_32` 643 (85.7% vs allowed 0.85); `stress_topology_4agent` 12,486 vs 14,916 (83.7%, allowed 0.95x) → `##[error]Process completed with exit code 1.` The cause: the committed baseline is a bare-metal reference host (8 cores, .NET 10.0.12) while the runner is a virtualized hosted runner with fewer cores — an unlike host class — but the comparator fingerprinted only OS family + architecture + .NET runtime major, so the strict gate armed on matching family/arch/runtime and adjudicated a host-speed shortfall as a workload regression. The runner's CPU model, core count, and RAM are not printed in the log; the baseline's `Cores: 8` and the artifact metadata made the class difference recoverable only from the artifact. |
| **Severity & Confidence** | High / Confirmed (root cause read from the real failed-run log, not assumed) |
| **Disposition** | `FIXED` — the strict fingerprint is now OS family + architecture + .NET runtime major + logical cores + CPU model string (trimmed, case-folded); every component must be present on BOTH sides and equal, so a missing or empty host field is a mismatch (cross-host, informational), never a silent strict pass. Both fingerprints and the classification are printed on every run. The gate keeps its retry and structural smoke and its thresholds; the CI result artifact is now uploaded with `if: always()` so future runner numbers are recoverable without log access. The reference record is unchanged and remains the research reference. Stated honestly: **CI no longer enforces throughput on GitHub-hosted runners until a runner-class baseline exists** — hosted runners get an informational cross-host comparison plus the structural checks. |
| **Resolution & Evidence Link** | Six tests added in `.github/workflows/test_compare_benchmarks.py` (RED first: different core count and different CPU model each classified cross-host informational with exit 0 despite a 0.5x/0.9x dip; a fully matching host still strict-fails at exit 1 and still passes above floor; a missing `Cpu` field on either side is informational with the field named in the log). Local verification against the committed baseline: cores 3 + medians 0.5x → informational exit 0; host unchanged + stress 0.9x → strict FAIL exit 1; the baseline itself → PASS exit 0. Gate docs re-anchored in `docs/BENCHMARKING.md` and `benchmarks/throughput_summary.md`. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

---

## FINDING-011 — Replay attested to step results but not to the state each tick produced

| Field | Value |
| --- | --- |
| **ID** | `FINDING-011` |
| **Date** | 2026-09-26 |
| **Source / Provenance** | `INTERNAL_AUDIT` — commit `87a7569` |
| **Target Surface** | `Trajectories/SimulationStateHash.cs` (new), `Trajectories/TrajectoryModel.cs`, `Trajectories/TrajectoryReader.cs`, `Trajectories/TrajectoryReplay.cs`, `Trajectories/TrajectoryWriter.cs`, `Cli/CliApp.cs`; no engine or simulation-behavior files |
| **Observation** | `replay --verify` proved that the recorded `StepResult` for each tick was the one the engine would produce, but nothing bound those results to the state they were computed from: a hand-edited recording could carry results that no reachable state produces and still verify. A per-tick SHA-256 digest over the serialized `SimulationState` closes that gap, and the trajectory format moves to **schema v3**. The digest covers the post-step state, so `hash[N]` is the pre-state of step `N+1` and the episode is chain-pinned. `StateHash` is a trailing optional field on each step line, omitted when absent, so a pre-hash recording still serializes to exactly the bytes it did before. |
| **Reversed ruling — zone positions are read, so they are hashed** | The first cut excluded `Position` from the digest on the reasoning that position is a presentation-layer embedding, not simulation state. **That ruling was reversed.** Zone positions *are* read by the step function: `Simulation.TransitTicks` dereferences `map.Zones[fromZoneId].Position` and `map.Zones[toZoneId].Position` to compute the Manhattan edge cost (`Environment/Simulation.cs:424-425`), so a position change is a behaviour change and belongs in the attested state. Positions are now hashed. |
| **Tamper hole found and closed** | Stripping every digest from a schema-3 recording downgraded the check: a recording that *declares* schema 3 but carries no digest was treated as a legacy file and reported only `no state hash: step-level verification only`, so a fully forged file went green. A declared-current-schema recording with every hash stripped is now a **discrepancy, not a notice**, and stripping can no longer downgrade the check. |
| **Null-`Position` crash found by the fuzzer, fixed at the reader boundary** | `GridPoint` is a reference type and the reader validated that the map's arrays were present but not their elements, so a hand-edited `"Position":null` survived the whole simulation — `TransitTicks` returns before reading positions under `InstantTransit` — and then faulted inside the digest. Null zone/resource/choke entries and absent positions are now rejected in `TrajectoryReader` with a message naming the field, and `CanonicalText` is total for recordings built in memory. |
| **Severity & Confidence** | High / Confirmed (all four points read from the commit diff and its tests) |
| **Disposition** | `FIXED` — per-tick SHA-256 state hash, schema 2 → 3, plus two correctness fixes surfaced while building it: `TrajectoryWriter.Write` stamped `CurrentVersion` unconditionally, so rewriting a hash-less schema-2 recording relabelled it schema 3 with zero digests (it now re-emits the recording's own header version), and the null-map-element gap above. |
| **Resolution & Evidence Link** | Tests in `Tests/Trajectories/`: `CurrentSchemaVersion_IsThree`, `Record_StampsAStateHashOnEveryStep`, `Record_ProducesADistinctHashPerStep`, `Write_EmitsTheStateHashFieldOnEachStepLine`, `HashlessSteps_SerializeWithoutTheStateHashField`, `ReadBack_PreservesEveryStateHash`, `Reader_RejectsAMalformedStateHash` / `ATruncatedStateHash` / `AnUppercaseStateHash`, `Write_PreservesTheHeaderSchemaVersionOfALegacyRecording`, `Reader_RejectsAMapWhoseZoneHasNoPosition`, `Reader_RejectsAMapWithANullZoneEntry`, `Reader_RejectsAMapWhoseResourceHasNoPosition`, `CanonicalText_TotalOverACorruptMap`; verification tests `TamperedStateHash_FailsEvenThoughTheStepResultsAreIntact`, `TamperedStateHash_NamesTheFirstMismatchedTickAndBothDigests`, `CurrentSchemaRecordingWithEveryHashStripped_IsAProblemNotANotice`, `PartiallyHashedRecording_IsAProblem`, `LegacyRecordingWithoutHashes_VerifiesOnStepResultsWithANotice`, `LegacyRecording_StillReportsARealTamper`, `ReadBackFromDisk_HashesStillVerify`. Three adversarial fixtures added to the existing fuzz corpus (`trajectory_null_zone_position.jsonl`, `trajectory_null_zone_entry.jsonl`, `trajectory_null_resource_position.jsonl`). Field order is fixed in the source file and never inferred; no dictionary or set is ever enumerated; the state is entirely `int`/`ulong`, so no float reaches the formatter. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

---

## FINDING-012 — Committed site recordings had silently drifted off the shipped engine

| Field | Value |
| --- | --- |
| **ID** | `FINDING-012` |
| **Date** | 2026-09-26 |
| **Source / Provenance** | `INTERNAL_AUDIT` — commit `41ad54a` |
| **Target Surface** | `site/demo.jsonl`, `site/infiltration.jsonl`, `site/demo.svg`, `site/infiltration.svg`, `site/app.js`, `site/index.html`, `ui_tests/test_demo_page_ui.py`, `.github/workflows/ci.yml`; no engine or simulation-behavior files |
| **Observation** | Both committed site recordings were last written at `0902d4a`, before `Observation.StepNumber` existed and before the choke arbitration and crossing gate changed, so `replay --verify` failed on **every step of both** files. Nothing in the build noticed: the page still played them, and a recording invalidated by a later engine change sat quietly on the published site. |
| **Disposition** | `FIXED` — both recordings regenerated from the seed and config their own headers and the page's reproduce commands already name (**seed 42 for both**) and re-rendered as paired SVGs with the committed render command. They now verify tick by tick, state hashes included, at schema v3: `site/demo.jsonl` **27 steps**, `site/infiltration.jsonl` **20 steps**. The site copy was repointed at the moments these recordings actually contain — the vault goes "last known" on the Infiltrator view at ticks 9, 19 and 20 and on the Sentry view only at the final tick 20 — and the UI fog check is now derived from the loaded recording instead of hard-coded ticks. A CI job replays every recording under `site/` with `--verify`, so a future engine change fails the build instead of the page. |
| **Seed deliberately not changed** | The seed was **not** varied to recover a more flattering episode. The recordings were re-recorded at the seed the docs and page already published, so the documented reproduce commands stay true. |
| **Mid-episode Sentry stale window no longer exists** | The demo previously narrated a mid-episode Sentry "stale" window at **ticks 9-13**. The re-recorded episode does not contain it: the Sentry view has no "last known" moment until the final tick 20. The guided callout now leads with tick 9 on the Infiltrator view, and the "recorded with an earlier engine revision" caveat is replaced with the fact that both files are re-recorded on the shipped engine and gated in CI. This is a change in what the recording contains, not a removal of evidence. |
| **Severity & Confidence** | Medium / Confirmed (step counts, seed and schema read from the committed artifacts) |
| **Resolution & Evidence Link** | Verification recorded at `41ad54a`: build 0W/0E; `dotnet test` 480/480; golden replay 12/12 steps and hashes; both site replays 27/27 and 20/20; `ui_tests` 3/3. `ui_tests/test_demo_page_ui.py` grew to derive the frame count from the file the page loaded and to compare the page's per-room status against a re-derivation of its own 2-hop reconstruction, so the check follows the recording when an engine change moves the episode. |
| **Resolution & Release Status** | `MAIN_ONLY` (postdates `v2.3.2` tag commit `4f7816f`; not packaged into any published release). |

---

## FINDING-013 — Hosted runners had no runner-class reference, and the generic tolerance read the wrong dispersion

| Field | Value |
| --- | --- |
| **ID** | `FINDING-013` |
| **Date** | 2026-09-29 |
| **Source / Provenance** | `INTERNAL_AUDIT` — Benchmarks workflow runs [36469355765](https://github.com/candavere/lattice/actions/runs/36469355765), [36469885628](https://github.com/candavere/lattice/actions/runs/36469885628), [36469902164](https://github.com/candavere/lattice/actions/runs/36469902164), [36469918606](https://github.com/candavere/lattice/actions/runs/36469918606), [36469934258](https://github.com/candavere/lattice/actions/runs/36469934258) |
| **Target Surface** | `benchmarks/runner_class_throughput_benchmark.json` (new), `benchmarks/runner_class_samples/` (new), `benchmarks/runner_class_summary.md` (new), `.github/workflows/compare_benchmarks.py`, `.github/workflows/benchmarks.yml`, `.github/workflows/test_compare_benchmarks.py`, `docs/BENCHMARKING.md`; no engine, agent, trajectory, protocol, or site files; `benchmarks/throughput_benchmark.json` and `benchmarks/throughput_summary.md` unchanged |
| **Observation** | `FINDING-010` fixed the gate arming on an unlike host class and left an explicitly stated boundary: CI enforces no throughput on GitHub-hosted runners until a runner-class baseline exists. Two measured facts set the terms of closing it. (a) **Runner class.** Five full-protocol sessions (`--runs 10 --steps 100000 --warmup 50000`) on one tree (`633ecf7`, .NET 10.0.12, macOS 26.6.2 / Arm64 / Apple M1 (Virtual) / 3 cores, image `macos-26-arm64/20260907.0351`) spanned **1.410×–1.813× max/min per workload** between sessions. (b) **Wrong dispersion.** The comparator's generic tolerance is derived from *within*-run dispersion (`1 - k·StdDev/Median`), which on this host class is 1.3×–2.8× tighter than the between-run spread for four of five workloads: `stress_topology_4agent` reads a within-run CV of 0.0672 while five sessions on one tree span 1.674× (between/within 2.83×). `micro_raw_2agent` is the one exception, genuinely jitter-dominated *within* a session (0.1700 within vs 0.1558 between). The gate was additionally unable to distinguish a full-protocol pass from a shortened one — the fingerprint compared host identity but never the recorded budget. **(c) The five samples did not bound the host class.** The record built from those samples armed four workloads under a rule fixed in advance (`allowed = min(0.95, 0.95 × min_ratio)`, armed when `0.95 × min_ratio >= 0.75`), and its first live run — Benchmarks run [`36471478970`](https://github.com/candavere/lattice/actions/runs/36471478970) on commit `4d05585`, where the `runner-class-gate` job ran for the first time — failed on **both** passes, landing **below the minimum of all five recorded sessions on all five workloads** (second pass vs five-sample min: `micro_raw_2agent` 0.964, `facility_static_4agent` 0.804, `dynamic_contention_4agent` 0.718, `stress_topology_4agent` 0.966, `policy_lookahead_mcts_32` 0.862). `git diff 633ecf7..4d05585` touches no engine, agent, CLI, or benchmark source. The slowdown was whole-host rather than workload-specific — all five moved together, including the search-bound MCTS case. The five sessions had been dispatched within about half a minute of each other on a quiet repository, so they sampled one narrow set of conditions. |
| **Severity & Confidence** | Medium / Confirmed (all five calibration artifacts and the failing run's artifact downloaded from the runs above; the first pass is read from the comparator's own result table in the run log of job `109094538649`, the artifact upload holding the second pass only) |
| **Disposition** | `FIXED` for the arming gap and the protocol gap; `ACCEPTED_LIMITATION` for the width of any resulting gate and for the runner image; and the runner-class reference is **published demoted**, as a negative result. A pinned runner-class record exists as a **separate, separately named artifact** — the bare-metal research record is untouched and a hosted runner still gets an informational cross-host comparison against it. The comparator now also matches the *recorded protocol*, so a shortened or smoke budget is never adjudicated, and the arming decision lives in the record's own `Provenance.ArmedWorkloads` rather than in a workflow flag, where it cannot be forgotten. **The record's armed set is `[]`.** It was demoted, not re-thresholded: widening the allowed ratios until the run that prompted this passed would have manufactured a green build from a measurement known to be unrepresentative. The `runner-class-gate` job still measures, still prints the full fingerprint and a per-workload table, still notes any sub-threshold ratio, and exits 0 — it adjudicates nothing. The calibration is retained verbatim in `Provenance.Spread` (`ArmedByRule` per workload) so it is auditable and can be revived by a future sample set. **Honest bottom line: CI enforces no throughput on GitHub-hosted runners, and this evidence does not establish that a stable-enough hosted reference can be recorded at all.** **Cause of the failing run: unestablished.** It was uniformly slower than every recorded sample, which points to a whole-host difference rather than a workload-level one, but nothing gathered here identifies it. Two candidates were considered and neither is supported: concurrency with this repository's other jobs — hosted jobs run on separate virtual machines, so overlapping repository work is not by itself an explanation, and no host-contention measurement was taken — and neighbouring tenants on the same runner fleet, which is plausible for a hosted VM and not observable from inside the job. The slowdown was not reproduced and no run has been dispatched to characterise it. |
| **Resolution & Evidence Link** | New artifacts: `benchmarks/runner_class_throughput_benchmark.json` (representative session `36469918606`; `Metadata`/`Workloads` byte-identical to that run's downloaded artifact apart from the added `RunnerImage`; `Provenance` carries the runner label, image build, source revision, protocol, all five run URLs, per-session medians, the spread table, the derived thresholds, `ArmedWorkloads: []`, and a `Demotion` block with both passes of the failing run, per-workload evidence, and the unestablished-cause statement) and the five raw sessions in `benchmarks/runner_class_samples/run_<id>.json`; narrative, failure evidence, and limits in `benchmarks/runner_class_summary.md`. Comparator: optional `RunnerImage` fingerprint (compared only when both sides declare it), recorded-protocol matching, and baseline-declared `Provenance.ArmedWorkloads` (a baseline without it arms everything, so the bare-metal record behaves exactly as before; an armed set naming a workload outside its own matrix is a hard error; an **empty** armed set is a supported state the comparator already reads and reports, so the demotion required no code change). 18 new tests in `.github/workflows/test_compare_benchmarks.py` — verified red against the pre-change comparator (6 fail: runner-image mismatch, shortened budget, incomplete measurement, and the three armed-set cases) and green after; 88 pass, 70 pre-existing unchanged. Local replay of all five real sessions through the committed record with the workflow's exact flags: exit 0 for all five. Docs: `docs/BENCHMARKING.md`. Runner label moved `macos-14` → `macos-26` in `633ecf7` as a standalone preparatory commit, after reading GitHub's current hosted-runner table (`macos-26` is the newest GA arm64 label; `xcode-27` is Public preview and was not used); that commit's own run is calibration sample 1 and its CI, Benchmarks, and Pages workflows were green. This is an unversioned evidence-and-gate stage: no tag applies. |
| **Resolution Release Status** | `MAIN_ONLY` (postdates `v3.0.0`; this is an unversioned evidence-and-gate stage, so no tag applies). |

---

## FINDING-014 — The MCTS decision-throughput case cannot be gated on this host class, and four of five workloads can

| Field | Value |
| --- | --- |
| **ID** | `FINDING-014` |
| **Date** | 2026-09-29 |
| **Source / Provenance** | `INTERNAL_AUDIT` — re-collection of 20 full-protocol runner-class sessions (`--runs 10 --steps 100000 --warmup 50000`) on pinned commit `1b9426f5`, dispatched at 25-minute spacing across ~16.5 hours. All 20 raw artifacts in `benchmarks/runner_class_raw/run_<id>.json`, SHA-256 verified against the pre-registered manifest `benchmarks/runner_class_collection_manifest.md`; run IDs [`36485512323`](https://github.com/candavere/lattice/actions/runs/36485512323) through [`36577844146`](https://github.com/candavere/lattice/actions/runs/36577844146) |
| **Target Surface** | `benchmarks/runner_class_throughput_benchmark.json`, `benchmarks/runner_class_collection_manifest.md`, `.github/workflows/benchmarks.yml`, `docs/BENCHMARKING.md`; no engine, agent, CLI, trajectory, or protocol source |
| **Observation** | `FINDING-013` closed by demoting the runner-class record to `ArmedWorkloads: []` because five idle-dispatched samples did not bound the host class, and named the path back: re-collect a sample set that spans the conditions the gate actually meets, then re-derive the thresholds. That re-collection is complete: 20 sessions, **all 20 passing all seven pre-registered inclusion rules**, no exclusions, no cohort split (every run's own `Image Release` line reads `macos-26-arm64/20260907.0351`), and one pinned ref so the measured tree could not move. Applying the pre-registered rule unchanged (`allowed = min(0.95, 0.95 × min_ratio)`, armed when `0.95 × min_ratio >= 0.75`) to the 20-session cohort: `micro_raw_2agent` 0.7914, `facility_static_4agent` 0.8416, `dynamic_contention_4agent` 0.7804, `stress_topology_4agent` 0.8236 all clear the 0.75 floor and are armed; **`policy_lookahead_mcts_32` does not.** Its 20 sessions span **2.050× max/min** — the widest of the five by a wide margin — and its lowest session sits at 0.6753 of the cross-run median, giving an arm-test value of 0.6415, **0.1085 below the floor**. Its between-run CV (0.1947 population, 0.1998 sample) is the highest of the five, while its within-run CV is only 0.0788: the dispersion is between sessions, not inside them. It is also the search-bound case, so a dip there is not separable from runner jitter. The five armed workloads' own between-run CVs (0.1327–0.1657) and max/min spans (1.592×–1.661×) remain wide, which is the honest width of this host class and the reason the tolerances are per-workload rather than generic. |
| **Severity & Confidence** | Medium / Confirmed (all 20 artifacts downloaded, SHA-256 verified byte-for-byte against the pre-registered manifest, and every threshold in the record recomputed from the raw JSON rather than carried over from the audit; the MCTS ratio is a direct property of those 20 numbers) |
| **Disposition** | `ACCEPTED_LIMITATION` for `policy_lookahead_mcts_32`, which is permanently measured, printed, and compared but **never adjudicated** — recorded here as a negative result rather than armed on a widened tolerance. `FIXED` for the four-workload arming gap `FINDING-013` left open, now closed on a cohort that spans the conditions the gate meets. **Honest bottom line: CI now enforces throughput on GitHub-hosted runners for four of five workloads, and this evidence establishes that the fifth cannot be enforced on this host class at any tolerance the rule admits.** Widening the MCTS ratio until it armed would have manufactured a gate from a measurement known to be un-separable from jitter, which is the specific failure mode `FINDING-013` recorded and refused. |
| **Resolution & Evidence Link** | `benchmarks/runner_class_throughput_benchmark.json` — `Provenance.SourceRevision` = `1b9426f5`, `Provenance.Spread.Samples` = 20, `Provenance.SampleRuns` = all 20 run IDs with per-session medians, artifact paths, artifact SHA-256s and image release, `Provenance.Spread.ByWorkload` carrying CrossRunMedian / Min / Max / MaxOverMin / MinRatio / P5 / P95 / both CV variants / ArmTestValue / AllowedRatio, `Provenance.ArmedWorkloads` = the four named workloads, `Provenance.InformationalWorkloads` = `["policy_lookahead_mcts_32"]`, and `Provenance.Arming.NotArmed.policy_lookahead_mcts_32` recording the shortfall and why. `Provenance.Supersedes` retains the five-session record and run `36471478970` as historical evidence, not pooled into the calibration. `benchmarks/runner_class_collection_manifest.md` — 20/20 verdicts `INCLUDED` with the verified reason, no `pending evaluation` string remains. `.github/workflows/benchmarks.yml` — the four `--per-workload-threshold` values replaced with the derived `AllowedRatio` values (0.791 / 0.842 / 0.780 / 0.824). Comparator code unchanged: it already reads the record's declared armed set, so an empty-and-reasoned armed set required no code change. **Baseline denominator:** each workload's `MedianThroughputPerSecond` is the **cross-run median of the 20 sessions**, so the comparator divides by the same quantity the pre-registered rule divides by (`lowest session median / cross-run median`); a representative session's median is a different quantity, and using one silently tightened the tolerance below the derived value and failed 4 of the 20 cohort's own sessions — the 5-session record hid this only because its representative run happened to *be* the cross-run median on all five workloads. |
| **Resolution Release Status** | `MAIN_ONLY` (postdates `v3.0.0`; unversioned evidence-and-gate stage, no tag applies) |

---

## FINDING-015 — The same commit on the same runner image read 0.785 and 1.006 on the armed workload, and a re-dispatch turned that red into a green without establishing why

| Field | Value |
| --- | --- |
| **ID** | `FINDING-015` |
| **Date** | 2026-09-30 |
| **Source / Provenance** | `INTERNAL_AUDIT` — Benchmarks workflow run [`36692501509`](https://github.com/candavere/lattice/actions/runs/36692501509), [attempt 1](https://github.com/candavere/lattice/actions/runs/36692501509/attempts/1) and [attempt 2](https://github.com/candavere/lattice/actions/runs/36692501509/attempts/2), both at `headSha` `ca438d062a542ad939c3aa7590c54ce1f06d828d`; the committed record `benchmarks/runner_class_throughput_benchmark.json` and the two attempts' own run logs. The uploaded `runner-class-macos-arm64` artifacts (IDs `11086224232`, `11087666514`) hold each attempt's second-pass measurement |
| **Target Surface** | `docs/FINDINGS_LEDGER.md` (this entry) and the reported use of Benchmarks run re-dispatch as a remedy; no change to `.github/workflows/benchmarks.yml`, `benchmarks/runner_class_throughput_benchmark.json`, `.github/workflows/compare_benchmarks.py`, or any source |
| **Observation** | One `push` to `main`, one commit, two attempts. Both attempts ran `runner-class-gate` on the same `macos-26` label, the same image build `macos-26-arm64/20260907.0351` (each attempt's own `Image Release` line reads identically), the same fingerprint — `macOS 26.6.2 / Arm64 / .NET 10.0.12 / 3 cores / Apple M1 (Virtual)` with `fingerprint match: True` — and the same full protocol `--runs 10 --steps 100000 --warmup 50000`. Attempt 1 concluded `failure`, attempt 2 concluded `success`. Across the three same-SHA measurements of the armed `stress_topology_4agent` workload the comparator printed: attempt 1 first pass **0.802** (8,674 vs baseline median 10,815), attempt 1 second pass **0.785** (8,487 vs 10,815) — `workload regression on a matching host`, allowed 0.8236 — and attempt 2 **1.006** (10,876 vs 10,815). The other armed workloads moved comparably on the identical code: `micro_raw_2agent` 0.735 → 0.945 → 0.998, `dynamic_contention_4agent` 0.996 → 0.890 → 0.955, `facility_static_4agent` 0.910 → 0.985 → 0.950; the informational `policy_lookahead_mcts_32` read 0.734 → 0.685 → 1.174. Both passes of attempt 1 failed, so the workflow's own bounded re-measure did not clear it; the red-to-green transition came from a human-initiated re-dispatch of the whole run, which is a fresh VM and a fresh `workflow_dispatch`-style pass, not a third measurement of the same session. No measured code changed: `git diff ca438d0..f914e78 -- Engine Agents Analytics Tests Protocol Generator Trajectories Visualization` is empty, and the only non-doc change on `main` since is `PackAsTool`/`ToolCommandName` metadata in `Cli/Lattice.Cli.csproj`. Two of the three `stress_topology_4agent` readings — 0.802 and 0.785 — sit **below** the 0.8669 minimum that the committed 20-session cohort recorded for that workload, so they fall outside the calibrated envelope rather than inside it |
| **Severity & Confidence** | Medium / **Bounded.** The measurements themselves are Confirmed — read directly from both attempts' comparator tables, not inferred, with the run, both attempt URLs, the commit SHA, the image build and the fingerprint all agreeing. The bounds are on everything else: **the cause of the spread is unestablished**, and nothing gathered here distinguishes hosted-runner jitter from a co-tenant on the shared VM, from the three virtualized cores' frequency scaling, from build-machine state, or from a real difference of unknown origin. No host-contention measurement was taken, no core-pinning or isolation was attempted, and a two-sample comparison of one workload cannot bound a distribution that the 20-session cohort spent ~16.5 hours trying to characterise |
| **Disposition** | `OPEN` for the cause, and `ACCEPTED_LIMITATION` for the consequence. No threshold, baseline, record or workflow line was changed, because nothing here supports changing one: 0.785 is below the cohort's own recorded minimum, so on that measurement the gate did the only honest thing available and failed; 1.006 is comfortably inside the cohort's observed range (max/min 1.661) and on that measurement passing was equally honest. **What is recorded as a negative result is the remedy, not the gate.** Re-dispatch is not a diagnosis. It changed a `failure` into a `success` at an identical commit, image, fingerprint and protocol, and all four armed workloads moved across that boundary: `micro_raw_2agent` by 26.3 ratio points (0.735 → 0.998), `stress_topology_4agent` by 22.1 (0.785 → 1.006), `dynamic_contention_4agent` by 10.6 (0.890 → 0.996) and `facility_static_4agent` by 7.5 (0.910 → 0.985). Nothing in either log distinguishes a genuine recovery from a different draw of the same wide distribution, so a green re-dispatch must not be read as evidence that the red run was a flake, and must not be quoted as a performance claim. The bounded in-job re-measure behaved exactly as `FINDING-014` designed it to: two failures across two passes on the same VM did not self-clear, which is the correct outcome and is the reason this entry exists. Any future use of a re-dispatch as a remedy should be paired with both attempts' logs, as it is here |
| **Resolution & Evidence Link** | No code change. Evidence is this entry plus `benchmarks/runner_class_throughput_benchmark.json` (`Provenance.Spread.ByWorkload.stress_topology_4agent.MinRatio` = 0.8669, `MaxOverMin` = 1.661, `AllowedRatio` = 0.8236) and the two attempt logs linked above. Read together with `FINDING-013` (why a threshold from an unlike host class is not honest) and `FINDING-014` (why `policy_lookahead_mcts_32` is not armed at all on this host class); this entry does not supersede either and is filed alongside them |
| **Resolution Release Status** | `MAIN_ONLY` (postdates `v3.0.0`; unversioned evidence-and-gate stage, no tag applies) |

---

## FINDING-016 — FINDING-015 understated the later non-documentation diff: it also covered a workflow and the site page, not only tool packaging metadata

| Field | Value |
| --- | --- |
| **ID** | `FINDING-016` |
| **Date** | 2026-10-01 |
| **Source / Provenance** | `INTERNAL_AUDIT` — a stage-6 re-read of `FINDING-015` against the full repository diff between its two stated endpoints, `ca438d062a542ad939c3aa7590c54ce1f06d828d` and the fixed stage 5 checkpoint `2c02b7d12c6fd79333570758f88e07817c6d31cf`. No benchmark was re-run and no new measurement was taken; the same-SHA evidence at `ca438d0` is Benchmarks workflow run [`36692501509`](https://github.com/candavere/lattice/actions/runs/36692501509), [attempt 1](https://github.com/candavere/lattice/actions/runs/36692501509/attempts/1) and [attempt 2](https://github.com/candavere/lattice/actions/runs/36692501509/attempts/2), both at `headSha ca438d062a542ad939c3aa7590c54ce1f06d828d`, exactly as `FINDING-015` records it |
| **Target Surface** | `docs/FINDINGS_LEDGER.md` (this entry and the `FINDING-015` index row, by reference only); the audited range `ca438d0..2c02b7d` touching `.github/workflows/ci.yml`, `Cli/Lattice.Cli.csproj` and `site/index.html`. `FINDING-015` itself is **not** edited — the ledger's append-only policy in the file header forbids rewriting an entry, so this corrects it by reference. No source, workflow, benchmark record, baseline or threshold was changed |
| **Observation** | The `FINDING-015` Observation contains this sentence: "No measured code changed: `git diff ca438d0..f914e78 -- Engine Agents Analytics Tests Protocol Generator Trajectories Visualization` is empty, and **the only non-doc change on `main` since is `PackAsTool`/`ToolCommandName` metadata in `Cli/Lattice.Cli.csproj`**." The quoted clause is inaccurate as a statement about the whole repository. `git diff --name-status ca438d0 2c02b7d` reports **seven** changed paths, of which **three** are non-documentation: `M .github/workflows/ci.yml` (+66 lines, all three being the tool-packaging steps "Pack the CLI as a global tool", "Install the packed tool to a throwaway tool path" and "Verify the tool launcher matches dotnet run" — an addition to the existing `build-test` matrix, containing no `benchmark`, `--runs` or `--steps` line and therefore no measurement), `M Cli/Lattice.Cli.csproj` (+2 lines, being exactly `<PackAsTool>true</PackAsTool>` and `<ToolCommandName>lattice</ToolCommandName>`), and `M site/index.html` (1 line changed, the `<link rel="icon" ...>` data URI swapped for a blackletter L). The remaining four are documentation: `CHANGELOG.md`, `README.md`, `docs/CLI.md` and `docs/FINDINGS_LEDGER.md`. The corrected scope is therefore: **three** non-documentation paths, not one. The narrower claim the same sentence makes is unaffected and still holds — `git diff ca438d0 2c02b7d -- Engine Agents Analytics Tests Protocol Generator Trajectories Visualization` is empty, and no `.cs` file under `Cli/` changed across the range (`git diff --name-only ca438d0 2c02b7d -- 'Cli/*.cs'` is empty, and `Cli/CliApp.cs` has a zero-line diff), so the benchmark entry point the `regression-gate` and `runner-class-gate` jobs invoke (`dotnet run -c Release --project Cli -- benchmark`) is byte-unchanged even though its project file is not |
| **Severity & Confidence** | Low / **Confirmed.** Every path and line count is read directly from `git diff --name-status` and `git diff --stat` at the two fixed SHAs, and both benchmark attempts' `headSha` is read from `gh`, not inferred. The confidence is in the file list, which is deterministic. What is deliberately **not** claimed: that these three changes are harmless to every possible measurement. `Cli/Lattice.Cli.csproj` is a build input of the project the benchmark jobs run, so "packaging metadata" describes its *content*, not a proof of zero effect on a build; that is a separate question this entry does not answer, and no build-output or IL comparison was made here |
| **Disposition** | `FIXED` for the scope statement, by reference: `FINDING-016` is the correction of record, and `FINDING-015` keeps its identifier and its measurements. **Nothing else in `FINDING-015` is revised.** Its same-SHA evidence is preserved exactly as recorded — run `36692501509`, attempts 1 and 2, `headSha ca438d0`, `stress_topology_4agent` reading 0.802, 0.785 and 1.006, and the 0.8669 cohort minimum its Observation compares against. Its `OPEN` disposition for the unestablished cause, its `ACCEPTED_LIMITATION` for the consequence, its Medium severity, its `Bounded` confidence and its calibration conclusions all stand unchanged. In particular this entry **does not** diagnose the observed spread, does **not** revise any ratio, threshold, cause or calibration conclusion, and does **not** turn the re-dispatch of attempt 2 into evidence of recovery: re-dispatch remains un-diagnosed, and the two attempts remain a red and a green at one commit, one runner image, one fingerprint and one protocol. This is a documentation-accuracy fix about which files a diff touched, and nothing more |
| **Resolution & Evidence Link** | No code change. `git diff --name-status ca438d062a542ad939c3aa7590c54ce1f06d828d 2c02b7d12c6fd79333570758f88e07817c6d31cf` (7 paths, 3 non-documentation); `git diff --stat` on each of the three; `git diff --name-only ca438d0 2c02b7d -- 'Cli/*.cs'` (empty); `git diff --stat ca438d0 2c02b7d -- Engine Agents Analytics Tests Protocol Generator Trajectories Visualization` (empty); `gh run view 36692501509 --json headSha` for attempts 1 and 2. Read together with `FINDING-013` (why an unlike host class cannot be gated) and `FINDING-014` (why `policy_lookahead_mcts_32` is not armed), neither of which this entry supersedes |
| **Resolution Release Status** | `MAIN_ONLY` (postdates `v3.0.0`; unversioned evidence-and-documentation stage, no tag applies) |

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
| `FINDING-009` | 2026-09-26 | Throughput baseline re-anchor (runtime 10.0.10 → 10.0.12) | Medium | `FIXED` | Evidence re-anchor commit (artifact + docs; no engine changes) |
| `FINDING-010` | 2026-09-26 | Throughput gate armed on an unlike host class | High | `FIXED` | Gate host-class fix commit (comparator fingerprint + tests + workflow artifact upload + docs; no engine changes) |
| `FINDING-011` | 2026-09-26 | Replay did not attest to per-tick simulation state (schema v3 state hash) | High | `FIXED` | `87a7569` |
| `FINDING-012` | 2026-09-26 | Committed site recordings had drifted off the shipped engine | Medium | `FIXED` | `41ad54a` |
| `FINDING-013` | 2026-09-29 | Hosted runners had no runner-class reference; tolerance read within-run dispersion; 5 samples did not bound the host class | Medium | `FIXED` / `ACCEPTED_LIMITATION` | Runner-class reference commit, published **demoted** (new record + 5 samples + comparator fingerprint/protocol/armed-set + tests + docs; no engine changes) |
| `FINDING-014` | 2026-09-29 | 20-session runner-class re-collection: four workloads armed, `policy_lookahead_mcts_32` recorded as a negative result (2.050× between-run spread, 0.1085 below the arm floor) | Medium | `FIXED` / `ACCEPTED_LIMITATION` | Re-collection commit (re-derived record + 20 verified raw sessions + manifest verdicts + workflow thresholds + docs; no engine changes) |
| `FINDING-015` | 2026-09-30 | Same commit `ca438d0`, same runner image, same fingerprint: `stress_topology_4agent` read 0.802 → 0.785 → 1.006 across two attempts of one run; a re-dispatch turned red into green with the cause unestablished | Medium | `OPEN` (cause) / `ACCEPTED_LIMITATION` (re-dispatch is not a diagnosis) | No code change; entry plus run [`36692501509`](https://github.com/candavere/lattice/actions/runs/36692501509) attempts 1 and 2 |
| `FINDING-016` | 2026-10-01 | `FINDING-015` understated its later non-documentation diff: `ca438d0..2c02b7d` changed **three** non-doc paths (`.github/workflows/ci.yml` +66, `Cli/Lattice.Cli.csproj` +2 packaging metadata, `site/index.html` favicon), not one. Corrected by reference; the narrower measured-code claim and all same-SHA evidence stand | Low | `FIXED` (scope statement, by reference) | Docs-only ledger addition; no source, workflow, benchmark record or baseline changed |

The ledger is maintained under the governing evidentiary standards of
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md);
the corresponding public-claim mappings are calibrated in
[`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md).