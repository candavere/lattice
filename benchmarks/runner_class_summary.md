# Runner-class throughput reference (GitHub-hosted runner)

Machine-readable record:
[`runner_class_throughput_benchmark.json`](./runner_class_throughput_benchmark.json). The 20 sessions it was drawn from are committed under [`runner_class_raw/`](./runner_class_raw/) so every number below can be recomputed from the artifacts rather than taken on trust. The pre-registered manifest that fixed the rules before collection began is [`runner_class_collection_manifest.md`](./runner_class_collection_manifest.md).

> This is a **hosted-runner** reference, recorded separately from the bare-metal research reference in [`throughput_benchmark.json`](./throughput_benchmark.json) and its notes in [`throughput_summary.md`](./throughput_summary.md). It does not restate, replace, or reinterpret that record. The bare-metal record is unchanged by this one, and a hosted runner still gets an informational cross-host comparison against it.

> **Status: four of five workloads armed.** `policy_lookahead_mcts_32` is measured and printed but never adjudicated. This supersedes the earlier 5-session record, which was published demoted after failing its first live run on all five workloads. That history is kept in [*The first live run*](#the-first-live-run-and-the-demotion) below, because the demotion is what set the terms for this re-collection.

## Why a second record exists

`FINDING-010` closed the gate on an unlike host class and recorded the boundary honestly: CI no longer enforces throughput on GitHub-hosted runners *until a runner-class baseline exists*. Hosted runners are a different host class from the bare-metal M1 reference - 3 virtualized cores against 8 physical ones - so no threshold on that comparison can be honest. A measurement taken on this runner class is the only thing that can be gated on this runner class.

## Host and provenance

| Field | Value |
| --- | --- |
| Runner label (pinned, not `macos-latest`) | `macos-26` |
| Runner image build | `macos-26-arm64/20260907.0351` (identical across all 20 runs) |
| Source revision (all samples) | `1b9426f5247e485343296e0d8863795084552535` |
| Ref used for every dispatch | `refs/heads/runner-class-calibration-2026-09-29`, pushed at exactly that commit |
| Runtime | .NET 10.0.12, Release |
| GC mode | Workstation |
| OS / arch / CPU / cores | macOS 26.6.2 / Arm64 / Apple M1 (Virtual) / 3 |
| Protocol | `--runs 10 --steps 100000 --warmup 50000` |
| Samples | **20** full-protocol sessions on one pinned commit |
| Dispatch window | 2026-09-28T21:21:09Z → 2026-09-29T13:48:30Z, 25 minutes apart |
| Most typical session | [36574344509](https://github.com/candavere/lattice/actions/runs/36574344509) (reference only, **not** the baseline) |

`macos-26` was chosen after reading GitHub's current hosted-runner table: it is the newest **GA** arm64 macOS label. (`xcode-27` is Public preview, so it is not GA and was not used.) The label is pinned explicitly rather than using `macos-latest`, so the runner class is stated instead of rolling.

policy_lookahead_mcts_32 reports decisions/sec and runs 100 ticks per iteration by design, in the baseline record too.

**Why a branch ref, not `--ref main`.** A moving ref is not a pin. The fixed SHA was pushed to a clearly-named temporary branch and every dispatch targeted that, so the measured tree could not change while the collection was in progress. `main` was never altered. The commit is measured-code-identical to the older 5-session tree: `git diff 633ecf70... 1b9426f5... -- Engine Agents Analytics Cli Tests Protocol Generator Trajectories` is empty, so this is a same-code measurement on a different commit.

## The baseline is the cross-run median, not a session

`MedianThroughputPerSecond` on each workload in the record is the **cross-run median of the 20 accepted sessions**, not any one session's figure. `RepresentativeRun` is kept for reference only.

This is not a cosmetic choice, and getting it wrong is a real failure mode. The rule derives its allowed ratio as `0.95 × (lowest session median / cross-run median)`, while the comparator computes its ratio as `current / record median`. **Both sides must divide by the same quantity.** A representative session's median is not that quantity: on this cohort it sits between 0.905× and 1.004× of the cross-run median, and using it as the baseline silently tightens the effective tolerance below the derived value. Built that way, the record failed **4 of its own 20 cohort sessions** (`36485512323`, `36549765862`, `36555874290`, `36561735364`) on a matching host, matching image, and matching protocol - a gate rejecting measurements taken from the very set that calibrated it.

The 5-session record never exposed this, because its representative run happened to *be* the cross-run median on all five workloads. That coincidence is not a property worth depending on with 20 samples.

Verification: replaying all 20 real sessions through the committed record with the workflow's exact flags gives **20 pass / 0 fail**, and a synthetic 30% regression on a real session still fails the gate.

## The 20 sessions

Every session passed all seven pre-registered inclusion rules. None was excluded; no run was re-dispatched or retried into the cohort.

| # | Run | Timestamp (UTC) | image | verdict |
| ---: | --- | --- | --- | --- |
| 1 | [36485512323](https://github.com/candavere/lattice/actions/runs/36485512323) | 2026-09-28T21:21:56Z | 20260907.0351 | INCLUDED |
| 2 | [36485904887](https://github.com/candavere/lattice/actions/runs/36485904887) | 2026-09-28T21:25:25Z | 20260907.0351 | INCLUDED |
| 3 | [36489039586](https://github.com/candavere/lattice/actions/runs/36489039586) | 2026-09-28T21:54:12Z | 20260907.0351 | INCLUDED |
| 4 | [36491926155](https://github.com/candavere/lattice/actions/runs/36491926155) | 2026-09-28T22:22:31Z | 20260907.0351 | INCLUDED |
| 5 | [36532339243](https://github.com/candavere/lattice/actions/runs/36532339243) | 2026-09-29T06:41:44Z | 20260907.0351 | INCLUDED |
| 6 | [36535002867](https://github.com/candavere/lattice/actions/runs/36535002867) | 2026-09-29T07:10:10Z | 20260907.0351 | INCLUDED |
| 7 | [36537836695](https://github.com/candavere/lattice/actions/runs/36537836695) | 2026-09-29T07:39:09Z | 20260907.0351 | INCLUDED |
| 8 | [36540673750](https://github.com/candavere/lattice/actions/runs/36540673750) | 2026-09-29T08:07:15Z | 20260907.0351 | INCLUDED |
| 9 | [36543622754](https://github.com/candavere/lattice/actions/runs/36543622754) | 2026-09-29T08:35:43Z | 20260907.0351 | INCLUDED |
| 10 | [36546690090](https://github.com/candavere/lattice/actions/runs/36546690090) | 2026-09-29T09:04:11Z | 20260907.0351 | INCLUDED |
| 11 | [36549765862](https://github.com/candavere/lattice/actions/runs/36549765862) | 2026-09-29T09:32:36Z | 20260907.0351 | INCLUDED |
| 12 | [36552908013](https://github.com/candavere/lattice/actions/runs/36552908013) | 2026-09-29T10:01:56Z | 20260907.0351 | INCLUDED |
| 13 | [36555874290](https://github.com/candavere/lattice/actions/runs/36555874290) | 2026-09-29T10:30:11Z | 20260907.0351 | INCLUDED |
| 14 | [36558772911](https://github.com/candavere/lattice/actions/runs/36558772911) | 2026-09-29T10:58:22Z | 20260907.0351 | INCLUDED |
| 15 | [36561735364](https://github.com/candavere/lattice/actions/runs/36561735364) | 2026-09-29T11:27:05Z | 20260907.0351 | INCLUDED |
| 16 | [36564719815](https://github.com/candavere/lattice/actions/runs/36564719815) | 2026-09-29T11:55:35Z | 20260907.0351 | INCLUDED |
| 17 | [36567818281](https://github.com/candavere/lattice/actions/runs/36567818281) | 2026-09-29T12:24:15Z | 20260907.0351 | INCLUDED |
| 18 | [36571007131](https://github.com/candavere/lattice/actions/runs/36571007131) | 2026-09-29T12:52:39Z | 20260907.0351 | INCLUDED |
| 19 | [36574344509](https://github.com/candavere/lattice/actions/runs/36574344509) | 2026-09-29T13:21:03Z | 20260907.0351 | INCLUDED |
| 20 | [36577844146](https://github.com/candavere/lattice/actions/runs/36577844146) | 2026-09-29T13:49:02Z | 20260907.0351 | INCLUDED |

The full per-row detail - dispatch time, run URL, head SHA, job conclusions, artifact path and SHA-256, image release, and the verified inclusion reason - is in [`runner_class_collection_manifest.md`](./runner_class_collection_manifest.md). Every artifact was re-verified byte-for-byte against the SHA-256 recorded before collection began.

### Median throughput per session (steps/s, or decisions/s for the MCTS case)

| Run | `micro_raw_2agent` | `facility_static_4agent` | `dynamic_contention_4agent` | `stress_topology_4agent` | `policy_lookahead_mcts_32` |
| --- | ---: | ---: | ---: | ---: | ---: |
| 36485512323 | 800,731 | 428,688 | 237,879 | 10,775 | 424 |
| 36485904887 | 728,158 | 472,416 | 311,720 | 11,551 | 486 |
| 36489039586 | 830,974 | 417,515 | 322,898 | 10,592 | 644 |
| 36491926155 | 791,043 | 419,522 | 274,315 | 9,533 | 478 |
| 36532339243 | 659,477 | 402,912 | 268,969 | 9,376 | 436 |
| 36535002867 | 801,500 | 537,860 | 388,195 | 12,770 | 662 |
| 36537836695 | 701,369 | 463,825 | 349,502 | 13,827 | 710 |
| 36540673750 | 643,501 | 382,328 | 304,788 | 10,162 | 489 |
| 36543622754 | 884,924 | 463,003 | 267,746 | 9,904 | 527 |
| 36546690090 | 961,643 | 389,451 | 284,031 | 11,811 | 538 |
| 36549765862 | 637,590 | 392,204 | 247,370 | 10,019 | 550 |
| 36552908013 | 841,261 | 413,832 | 256,038 | 13,980 | 771 |
| 36555874290 | 701,023 | 445,371 | 242,790 | 11,351 | 376 |
| 36558772911 | 715,054 | 378,270 | 250,544 | 10,454 | 497 |
| 36561735364 | 751,625 | 398,633 | 242,571 | 10,641 | 610 |
| 36564719815 | 779,092 | 610,434 | 393,846 | 10,855 | 592 |
| 36567818281 | 1,015,043 | 599,471 | 374,301 | 15,571 | 771 |
| 36571007131 | 715,213 | 425,422 | 295,157 | 11,916 | 581 |
| 36574344509 | 731,680 | 428,553 | 319,981 | 10,747 | 564 |
| 36577844146 | 949,364 | 593,721 | 355,152 | 14,410 | 723 |
| **cross-run median** | **765,359** | **426,987** | **289,594** | **10,815** | **557** |

## Measured spread, and why the within-run tolerance is not enough

The comparator's generic tolerance is derived from a workload's *within-run* sample dispersion (`1 - k·StdDev/Median`). That measures jitter inside one session, while the risk on a hosted runner is variation *between* sessions. On this runner class the two are not the same quantity:

| Workload | between-run max/min | between-run CV (ddof=0) | between-run CV (ddof=1) | median within-run CV | between ÷ within |
| --- | ---: | ---: | ---: | ---: | ---: |
| `micro_raw_2agent` | 1.592x | 0.1327 | 0.1361 | 0.1648 | 0.81x |
| `facility_static_4agent` | 1.614x | 0.1588 | 0.1629 | 0.0841 | 1.89x |
| `dynamic_contention_4agent` | 1.656x | 0.1657 | 0.1700 | 0.0762 | 2.17x |
| `stress_topology_4agent` | 1.661x | 0.1474 | 0.1512 | 0.0640 | 2.30x |
| `policy_lookahead_mcts_32` | **2.050x** | **0.1947** | 0.1998 | 0.0788 | 2.47x |

The ratio is the point. For four of the five workloads the *between*-session variation is larger than the *within*-session variation the generic tolerance reads, so a tolerance derived from within-run dispersion is too tight by that factor. `stress_topology_4agent` is the clearest case: its within-run CV looks stable at 0.0640, yet 20 sessions on one commit span 1.661x — a between/within ratio of 2.30x. `micro_raw_2agent` is the exception and is genuinely jitter-dominated *within* a session (its within-run CV of 0.1648 is already wider than its between-run CV of 0.1327), which is why it arms with the least headroom. The hosted runner's throughput is therefore calibrated from the measured between-run spread, and the within-run derivation is bypassed for this record via explicit per-workload thresholds in the workflow.

## The rule, and which workloads it arms

The rule was fixed in the manifest **before any of these sessions were inspected**:

> min_ratio = lowest sample median / cross-run median; allowed = min(0.95, 0.95 * min_ratio); a workload is armed only when 0.95 * min_ratio >= 0.75.

Safety factor 0.95, arm floor 0.75, cap 0.95. Quantiles p5/p95 are by linear interpolation between order statistics; CVs are reported at both ddof = 0 and ddof = 1.

Applied to the 20 accepted sessions:

| Workload | cross-run median | min | max | min/cross-run | arm-test value | allowed ratio | armed by rule? | shortfall vs floor | armed in this record? |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |
| `micro_raw_2agent` | 765,359 | 637,590 | 1,015,043 | 0.8331 | 0.7914 | 0.7914 | **yes** | — | **yes** |
| `facility_static_4agent` | 426,987 | 378,270 | 610,434 | 0.8859 | 0.8416 | 0.8416 | **yes** | — | **yes** |
| `dynamic_contention_4agent` | 289,594 | 237,879 | 393,846 | 0.8214 | 0.7804 | 0.7804 | **yes** | — | **yes** |
| `stress_topology_4agent` | 10,815 | 9,376 | 15,571 | 0.8669 | 0.8236 | 0.8236 | **yes** | — | **yes** |
| `policy_lookahead_mcts_32` | 557 | 376 | 771 | 0.6753 | 0.6415 | 0.6415 | no | 0.1085 | no |

`Provenance.ArmedWorkloads` = `["micro_raw_2agent", "facility_static_4agent", "dynamic_contention_4agent", "stress_topology_4agent"]`.
`Provenance.InformationalWorkloads` = `["policy_lookahead_mcts_32"]`.

### Why MCTS stays informational

`policy_lookahead_mcts_32` spans **2.050×** max/min across 20 sessions - by far the widest in the matrix - and its lowest session sits at 0.6753 of the cross-run median, so `0.95 × min_ratio = 0.6415`, **0.1085 below the 0.75 arm floor**. Its between-run CV (0.1947) is the highest of the five while its within-run CV is only 0.0788: the dispersion is *between* sessions, not inside them. It is also the search-bound case, so a dip there is not separable from runner jitter.

It is therefore measured, printed in the per-workload table, and compared, but carries **no verdict**. This is recorded as a negative result in [`FINDINGS_LEDGER.md`](../docs/FINDINGS_LEDGER.md) (`FINDING-014`) rather than armed on a widened tolerance. Widening it until it armed would be the same failure the demotion below refused: manufacturing a gate from a measurement known to be unrepresentative.

Note that this is not a re-run of the old result for the same reason. The old record left MCTS informational at 0.697; the 20-session cohort puts it at 0.6415, i.e. **the wider cohort shows the MCTS case was never separable**, rather than showing it was marginal.

## The first live run, and the demotion

Kept because it set the terms for everything above.

The previous record was calibrated on 5 full-protocol sessions dispatched within about half a minute of each other. Its first live run was Benchmarks run [36471478970](https://github.com/candavere/lattice/actions/runs/36471478970) on commit `4d05585b718a8c62ce3344a88dea53ed807db8ab`. It failed on both passes, with every one of the five workloads below the minimum of all five recorded sessions:

| Workload | five-sample min | pass 1 | ratio to baseline | pass 2 | ratio to baseline | pass 2 vs five-sample min |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `micro_raw_2agent` | 666,068 | 609,119 | 0.757 | 641,848 | 0.798 | 0.964 |
| `facility_static_4agent` | 417,161 | 341,660 | 0.785 | 335,525 | 0.771 | 0.804 |
| `dynamic_contention_4agent` | 253,194 | 216,615 | 0.720 | 181,671 | 0.604 | 0.718 |
| `stress_topology_4agent` | 9,057 | 8,493 | 0.795 | 8,745 | 0.819 | 0.966 |
| `policy_lookahead_mcts_32` | 432 | 355 | 0.603 | 372 | 0.632 | 0.862 |

**Five idle-dispatched samples do not bound this host class.** `git diff 633ecf7..4d05585` touches no engine, agent, CLI, or benchmark source. The slowdown was whole-host rather than workload-specific: all five moved together, including the search-bound case. Its cause remains **unestablished** - two candidates were considered (concurrency with this repository's other jobs, and neighbouring tenants on the same fleet) and neither is supported by evidence gathered here. The slowdown was never reproduced or characterised.

The record was **demoted, not re-thresholded**: `ArmedWorkloads: []`, the calibration retained verbatim in `Provenance.Spread` so it could be revived by a future sample set.

### What the 20 sessions change, and what they do not

- The demotion is **not withdrawn**. It stands as the record of why five samples were insufficient.
- What changed is that a cohort large enough to judge the host class now exists - 20 full-protocol sessions across ~16.5 hours at 25-minute spacing, replacing 5 within ~30 seconds - and the pre-registered rule was applied to it unchanged.
- On the new cohort the lowest observed session sits at 0.82–0.89 of the cross-run median for the four armed workloads. The old failing run landed at 0.60–0.80 of the *old* baseline, below the old five-sample minimum on all five. The shape is what a real whole-host difference looks like, which is why a cohort that had never seen those conditions was the thing at fault, not the rule.
- The failing run is a different commit and stays **excluded** from the calibration. Its artifact is the second pass only; its first-pass figures remain comparator-table observations, never a downloaded JSON or an independent session. Same for the original 5 sessions on `633ecf70`: on file as historical evidence, not pooled.
- **No threshold was widened against any record or any observed run.** The four armed values are the rule's own output on the 20 sessions, to three decimals.

## Limits of a hosted-runner inference

- **The gate is narrow, and narrow on purpose.** It enforces four workloads. `policy_lookahead_mcts_32` is a permanent non-verdict, not a gap to be closed by loosening a threshold later.
- **The armed tolerances are wide because the host class is wide.** 20 sessions span 1.59×–2.05× per workload. A 0.7804–0.8416 allowed ratio is a real regression signal; anything subtler is below this host class's noise floor and is stated as such rather than adjudicated.
- **A pinned label is not a pinned image.** The image build drifts under a stable label. All 20 runs here reported the same build, so the cohort has no split - but a fresh CI artifact cannot stamp `RunnerImage` (GitHub exposes no environment variable for it), so the comparator compares that field only when both sides carry one. An image update that shifts throughput systematically would present as a regression; the remedy is to re-collect.
- **The image check is currently inert in CI.** It exists so the record cannot silently claim an image it did not measure, and so it works unchanged if a runner ever exposes the build.
- **A gate calibrated on 20 sessions is still a sample.** 20 is 4× the previous 5 and the upper end of the target band, not a convergence proof. The two extreme `micro_raw_2agent` sessions (637,590 and 1,015,043) are 2.6× apart within this cohort, so the tail of the distribution is not well characterised.
- **This is a hosted-VM reference, not a performance claim about the engine.** It concerns this runner class only. It says nothing about the bare-metal host, and it is not a scaling or throughput claim.
- **No AC-power prerequisite applies to a hosted runner.** That requirement governs a laptop recording a reference, which is precisely why the two records are kept apart.

## Reproducing

```bash
# one full-protocol session on the runner class
gh workflow run benchmarks.yml --ref main \
  -f runs=10 -f steps=100000 -f warmup=50000
```

The measurement is uploaded as artifact `runner-class-macos-arm64` (`if: always()`, so it survives a failed gate). Re-collecting means taking a new sample set on one pinned commit, re-running the rule above unchanged, and re-anchoring this record and `Provenance.ArmedWorkloads` together - setting the record's medians to the new **cross-run medians**, for the reason given above.

Replay a committed session against the committed record with the workflow's exact flags:

```bash
python3 .github/workflows/compare_benchmarks.py \
  benchmarks/runner_class_throughput_benchmark.json \
  benchmarks/runner_class_raw/36577844146.json \
  --per-workload-threshold micro_raw_2agent=0.7914 \
  --per-workload-threshold facility_static_4agent=0.8416 \
  --per-workload-threshold dynamic_contention_4agent=0.7804 \
  --per-workload-threshold stress_topology_4agent=0.8236 \
  --no-annotations --strict-if-matching
```

## Gating

`.github/workflows/benchmarks.yml` job `runner-class-gate` compares a fresh full-protocol measurement against this record with `--strict-if-matching` and the four per-workload thresholds above (the derived `AllowedRatio` values, carried at full precision so they match the record exactly). The comparison still requires a matching OS family, architecture, .NET runtime major, logical cores, and CPU model, plus a matching recorded protocol; anything else degrades to an informational cross-host table, so a shortened or smoke budget can never be adjudicated against this record.

The armed set lives in the record's own `Provenance.ArmedWorkloads`, not in a workflow flag, where it cannot be forgotten. The comparator reads it and reports both the armed and informational sets on every run. A single re-measure is attempted on a failure, to clear a transient dip; a dip that persists across both passes is a real signal.

Verification on the committed record: **all 20 cohort sessions pass**; a synthetic 30% regression on a real session fails; the 63 existing comparator tests are unchanged and green.
