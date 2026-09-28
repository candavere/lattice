# Runner-class throughput reference (GitHub-hosted runner)

Machine-readable record:
[`runner_class_throughput_benchmark.json`](./runner_class_throughput_benchmark.json). The five sessions it was drawn from are committed under [`runner_class_samples/`](./runner_class_samples/) so every number below can be recomputed from the artifacts rather than taken on trust.

> This is a **hosted-runner** reference, recorded separately from the bare-metal research reference in [`throughput_benchmark.json`](./throughput_benchmark.json) and its notes in [`throughput_summary.md`](./throughput_summary.md). It does not restate, replace, or reinterpret that record. The bare-metal record is unchanged by this one, and a hosted runner still gets an informational cross-host comparison against it.

## Why a second record exists

`FINDING-010` closed the gate on an unlike host class and recorded the boundary honestly: CI no longer enforces throughput on GitHub-hosted runners *until a runner-class baseline exists*. Hosted runners are a different host class from the bare-metal M1 reference - 3 virtualized cores against 8 physical ones - so no threshold on that comparison can be honest. A measurement taken on this runner class is the only thing that can be gated on this runner class.

## Host and provenance

| Field | Value |
| --- | --- |
| Runner label (pinned, not `macos-latest`) | `macos-26` |
| Runner image build | `macos-26-arm64/20260907.0351` |
| Source revision (all samples) | `633ecf70c82e1b71255a6dac8e09bdca0b5b243b` |
| Runtime | .NET 10.0.12, Release |
| GC mode | Workstation |
| OS / arch / CPU / cores | macOS 26.6.2 / Arm64 / Apple M1 (Virtual) / 3 |
| Protocol | `--runs 10 --steps 100000 --warmup 50000` |
| Samples | 5 full-protocol sessions on the same tree |
| Representative run | [36469918606](https://github.com/candavere/lattice/actions/runs/36469918606) |

`macos-26` was chosen after reading GitHub's current hosted-runner table: it is the newest **GA** arm64 macOS label. (`xcode-27` is Public preview, so it is not GA and was not used.) The label is pinned explicitly rather than using `macos-latest`, so the runner class is stated instead of rolling.

policy_lookahead_mcts_32 reports decisions/sec and runs 100 ticks per iteration by design, in the baseline record too.

## How the representative run was chosen

The session whose five workload medians, each divided by that workload's cross-run median, sit closest to the overall mean. This is the most typical session - explicitly neither the best of the samples nor a deliberately conservative low one.

The record's `Metadata` and `Workloads` are that session's artifact unchanged. One field is added: `RunnerImage`, which the CLI does not emit, transcribed from the run's own image-release log line.

## The five sessions

| Run | Trigger | Timestamp (UTC) |
| --- | --- | --- |
| [36469355765](https://github.com/candavere/lattice/actions/runs/36469355765) | push | 2026-09-28T19:02:17.6664190+00:00 |
| [36469885628](https://github.com/candavere/lattice/actions/runs/36469885628) | workflow_dispatch | 2026-09-28T19:06:58.2461490+00:00 |
| [36469902164](https://github.com/candavere/lattice/actions/runs/36469902164) | workflow_dispatch | 2026-09-28T19:06:58.7449770+00:00 |
| [36469918606](https://github.com/candavere/lattice/actions/runs/36469918606) | workflow_dispatch | 2026-09-28T19:07:07.9475550+00:00 |
| [36469934258](https://github.com/candavere/lattice/actions/runs/36469934258) | workflow_dispatch | 2026-09-28T19:07:28.1645190+00:00 |

The first session is the push run; the other four were dispatched together within about half a minute of each other and ran on separate hosted VMs. They are concurrent, so the spread below includes whatever variation the hosted fleet showed under that concurrency — a deliberately pessimistic reading, since ordinary CI runs mostly one at a time.

### Median throughput per session (steps/s, or decisions/s for the MCTS case)

| Workload | 55765 | 85628 | 02164 | 18606 | 34258 | cross-run median |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `micro_raw_2agent` | 1,028,279 | 666,068 | 729,592 | 804,362 | 908,526 | 804,362 |
| `facility_static_4agent` | 588,207 | 435,089 | 457,483 | 424,022 | 417,161 | 435,089 |
| `dynamic_contention_4agent` | 377,132 | 316,355 | 292,944 | 301,025 | 253,194 | 301,025 |
| `stress_topology_4agent` | 15,165 | 10,023 | 9,057 | 10,681 | 12,905 | 10,681 |
| `policy_lookahead_mcts_32` | 782 | 432 | 449 | 589 | 634 | 589 |

## Measured spread, and why the within-run tolerance is not enough

The comparator's generic tolerance is derived from a workload's *within-run* sample dispersion (`1 - k·StdDev/Median`). That measures jitter inside one session, while the risk on a hosted runner is variation *between* sessions. On this runner class the two are not the same quantity:

| Workload | between-run max/min | between-run CV | median within-run CV | between ÷ within |
| --- | ---: | ---: | ---: | ---: |
| `micro_raw_2agent` | 1.544x | 0.1558 | 0.1700 | 0.92x |
| `facility_static_4agent` | 1.410x | 0.1365 | 0.1024 | 1.33x |
| `dynamic_contention_4agent` | 1.489x | 0.1308 | 0.0757 | 1.73x |
| `stress_topology_4agent` | 1.674x | 0.1902 | 0.0672 | 2.83x |
| `policy_lookahead_mcts_32` | 1.813x | 0.2236 | 0.1008 | 2.22x |

The ratio is the point. For four of the five workloads the *between*-session variation is larger than the *within*-session variation the generic tolerance reads, so a tolerance derived from within-run dispersion is too tight by that factor:

`stress_topology_4agent` is the clearest case: its within-run CV looks stable at 0.0672, yet five sessions on one tree span 1.674x — a between/within ratio of 2.83x. `micro_raw_2agent` is the exception and is genuinely jitter-dominated *within* a session (its within-run CV is already wider than its between-run CV), which is why it arms with the least headroom. The hosted runner's throughput is therefore calibrated from the measured between-run spread, and the within-run derivation is bypassed for this record via explicit per-workload thresholds in the workflow.

## The rule, and which workloads it arms

The rule was fixed before the samples were inspected:

> min_ratio = lowest sample median / cross-run median; allowed = min(0.95, 0.95 * min_ratio); a workload is armed only when 0.95 * min_ratio >= 0.75.

Safety factor 0.95, arm floor 0.75, cap 0.95.

Every workload's value is recorded, including the ones that miss, so it is clear which fell short and by how much:

| Workload | min/cross-run median | arm-test value | allowed ratio | armed? | shortfall vs floor |
| --- | ---: | ---: | ---: | --- | ---: |
| `micro_raw_2agent` | 0.828 | 0.787 | 0.787 | **yes** | — |
| `facility_static_4agent` | 0.959 | 0.911 | 0.911 | **yes** | — |
| `dynamic_contention_4agent` | 0.841 | 0.799 | 0.799 | **yes** | — |
| `stress_topology_4agent` | 0.848 | 0.806 | 0.806 | **yes** | — |
| `policy_lookahead_mcts_32` | 0.733 | 0.697 | 0.697 | no | 0.053 |

**Armed:** `micro_raw_2agent`, `facility_static_4agent`, `dynamic_contention_4agent`, `stress_topology_4agent`.

**Informational:** `policy_lookahead_mcts_32` — measured 1.813x between runs, the widest of the matrix. Its arm-test value 0.697 falls 0.053 short of the 0.75 floor, so no threshold wide enough to stop the gate false-firing on jitter would still detect a real regression. It is recorded, reported, and never adjudicated — the comparator prints its ratio and a note rather than a verdict.

## What the armed gate still catches

| Workload | trips below | i.e. a drop of |
| --- | ---: | ---: |
| `micro_raw_2agent` | 78.7% of the baseline median | 21.3% |
| `facility_static_4agent` | 91.1% of the baseline median | 8.9% |
| `dynamic_contention_4agent` | 79.9% of the baseline median | 20.1% |
| `stress_topology_4agent` | 80.6% of the baseline median | 19.4% |

Backtest: all five recorded sessions pass this gate, so the calibration does not fire on jitter this runner class has actually produced. A genuine drop of roughly 9–21% still trips it.

## Limits of a hosted-runner inference

- **A pinned label is not a pinned image.** The image build drifts under a stable label. GitHub exposes no default environment variable carrying the image build, so a fresh CI artifact cannot stamp `RunnerImage`; the comparator compares that field only when both sides carry one. If an image update shifts throughput systematically, the gate will present it as a regression. The remedy is to re-collect this record, not to widen the threshold.
- **The image check is currently inert in CI.** It exists so the record cannot silently claim an image it did not measure, and so the check works unchanged if a runner ever exposes the build.
- **Five sessions bound the spread, not the tail.** The minimum of five samples is a floor, not a guarantee; a rarer slow run than any observed would be a false alarm.
- **This is a hosted-VM reference, not a performance claim about the engine.** It bounds regressions *on this runner class*. It says nothing about the bare-metal host, and it is not a scaling or throughput claim.
- **`micro_raw_2agent` arms narrowly.** Its arm-test value 0.787 clears the floor by only 0.037, and the lowest recorded session already sits at 0.828 of the cross-run median. It is the workload most likely to need re-collection first.

## Reproducing

```bash
# one full-protocol session on the runner class
gh workflow run benchmarks.yml --ref main \
  -f runs=10 -f steps=100000 -f warmup=50000
```

The measurement is uploaded as artifact `runner-class-macos-arm64` (`if: always()`, so it survives a failed gate). Re-collecting means taking five sessions on one tree, re-running the rule above, and re-anchoring this record and `Provenance.ArmedWorkloads` together.

## Gating

`.github/workflows/benchmarks.yml` job `runner-class-gate` compares a fresh full-protocol measurement against this record with `--strict-if-matching` and the per-workload thresholds above. The strict verdict needs a matching OS family, architecture, .NET runtime major, logical cores, and CPU model, plus a matching recorded protocol; anything else degrades to an informational cross-host table. A shortened or smoke budget can therefore never be adjudicated against this record. The bounded single retry is kept: a transient dip clears, a dip that persists across both passes is real.
