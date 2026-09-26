# Throughput Benchmark — Reference Record

Machine-readable data: [`throughput_benchmark.json`](./throughput_benchmark.json)

## Host & provenance

| Field | Value |
| --- | --- |
| Commit (measured tree) | `41530efb35ef620c8d0722e20bcd30970468785a` |
| Timestamp (UTC) | 2026-09-26T06:56:05Z |
| Runtime | .NET 10.0.12 |
| Configuration | Release |
| GC mode | Workstation |

Numbers come from a single reference host; its full metadata (CPU, cores, OS, runtime) is recorded in the artifact.

> This record was **re-anchored** twice. First, after the CI regression gate
> proved unstable against the earlier baseline: that record's `micro_raw` and
> `policy` medians were high-edge samples of normal host noise (consecutive
> runs on the same machine + runtime spread ~30% and ~17% for those
> workloads), so the baseline became a **conservative** full-protocol session.
> Second, on 2026-09-26, for the runtime patch .NET 10.0.10 → 10.0.12 (same
> reference host, AC power): the same conservative method — three consecutive
> full-protocol sessions, committing the session that is low for the noisy
> workloads, near-typical elsewhere. The speedup against the previous record
> is **environmental**, not an engine change: identical configs and protocol,
> effectively identical GC counters and allocations, and a uniform +63% to
> +93% shift across all five workloads including search-bound MCTS (the
> generated maps carry only unlimited-capacity chokes, so trajectories are
> unchanged). This is **not an engine speedup** and must not be read as one.
> A matching-host pass still has real headroom and a genuine >20% drop still
> trips the strict gate.

## Protocol

**AC power is required on a laptop host.** Before any session, confirm the
machine is plugged in and check the power state with `pmset -g batt`; do not
record a baseline on battery. Per `FINDING-009`, the same protocol, host and
runtime measured roughly 2x apart between battery and AC, and battery sessions
erased most of the re-anchor shift — so a battery run silently reads as a large
regression.

`dotnet run --project Cli -- benchmark --runs 10 --warmup 50000 --commit <sha> --cpu "<cpu model>"`

Every case runs the same harness protocol (`Analytics/Benchmarking/BenchmarkHarness`):

1. Warm-up anchors an FNV-1a step digest over the episode's (state, result)
   stream; the budget is realized as 1–2 full iterations so the exact measured
   path is JIT-settled.
2. `10` measured iterations; a forced GC sweep precedes each so every run
   starts from a clean heap.
3. Per-step latency is sampled with `Stopwatch.GetTimestamp` into one combined
   histogram; throughput (steps/sec, or decisions/sec for the MCTS case) is
   taken per iteration, then summarized as median/mean/std-dev (n-1).
4. Managed allocation is the `GC.GetAllocatedBytesForCurrentThread` delta
   across the measured window, divided by total steps; GC Gen0/1/2 counts are
   the `GC.CollectionCount` deltas over the same window.
5. Every measured iteration must reproduce the warm-up anchor's step digest —
   divergence fails the run, never silently skips it.

Maps are generated from fixed seeds (`WorkloadCatalog`), so all hosts and all
revisions benchmark identical topologies.

## Results (median of 10 iterations)

| Workload | Map | Agents | Dynamic topology | Median throughput | Mean step latency | p95 step latency | Alloc / step |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `micro_raw_2agent` | 3z/5c/8r | 2 Randoms | — | **899,075 steps/s** | 1.17 µs | 2.21 µs | 3,213 B |
| `facility_static_4agent` | 10z/17c/20r | 2 Greedy + 2 Randoms | — | **657,189 steps/s** | 1.55 µs | 1.58 µs | 4,630 B |
| `dynamic_contention_4agent` | 10z/17c/20r | 4 Randoms | portcullis + event lock | **416,170 steps/s** | 2.45 µs | 2.46 µs | 7,097 B |
| `stress_topology_4agent` | 30z/58c/57r | 2 Greedy + 2 Scouts | — | **14,916 steps/s** | 67.1 µs | 66.5 µs | 106,309 B |
| `policy_lookahead_mcts_32` | 3z/5c/8r | 2 MCTS (32 rollouts, depth 12) | — | **750 decisions/s** | 2.77 ms | 3.27 ms | 11.5 MB |

Full per-run dispersion (std-dev of per-iteration throughput) is in the JSON.

## Honest reading of the numbers

- **Allocation is real, and reported as real.** The step contract returns an
  immutable `StepResult` (observations, rewards, info) and the harness rebuilds
  per-agent `Observation`s each tick, so the core allocates roughly KB/tick in
  raw cases and far more under stress/MCTS search. No claim of "0 bytes
  allocated" is made — the committed record carries the measured values and
  the regression suite still pins that allocation grows **linearly** in tick
  count. In raw cases Gen2 stays flat (20 over the whole run) and Gen0
  reclamation dominates.
- **The stress case runs 4 agents, not 16.** `SimulationConfig` validates
  agent count inclusive 2..4, so the 30-zone stress map is driven at the
  contract ceiling rather than a nominal 16-agent roster. The deviation is
  explicit, not silent.
- **The MCTS case reports decisions/sec, not steps/sec.** One decision runs
  rolloutsPerAction (32) independent depth-12 continuations — thousands of
  fork steps — so its per-decision cost, not engine stepping, is what the
  workload prices. The per-iteration budget (100 ticks) is small by design;
  the per-step latency histogram of that case is dominated by single heavy
  decisions.
- **Instrumentation is included.** Per-step `Stopwatch.GetTimestamp` and the
  digest mix are part of the measured tick, so every number reflects the
  harness's honest observability overhead.
- **The stress case's p95 step latency sits below its mean** (66.5 µs vs
  67.1 µs). The mean is pulled above the 95th percentile by a heavy right
  tail in the per-step histogram — a small fraction of much longer steps
  inside the measured window. Both are reported as measured.

## Reproducing / gating

- Full reference run: `dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`
- Quick smoke: `dotnet run -c Release --project Cli -- benchmark --runs 2 --warmup 1000 --steps 20000`
- CI gate (`.github/workflows/benchmarks.yml`): re-benchmarks the matrix and
  fails on a >20% regression against this record only when the host
  fingerprint matches this record's host class: **OS family + architecture +
  .NET runtime major + logical cores + CPU model** (trimmed, case-folded; a
  missing or empty host field is also a mismatch). GitHub-hosted runners
  are a different host class than the reference record, so they get an
  informational cross-host comparison plus
  the structural checks — CI does not enforce throughput on them until a
  runner-class baseline exists. The gate installs the same .NET 10 runtime
  the baseline was recorded under, so a cross-runtime delta is never misread
  as a regression; on any mismatch it prints a cross-host comparison table
  instead of failing. The bounded cross-host smoke pass (ubuntu x64, .NET 8)
  is classified structurally with `--smoke`: the comparator checks workloads
  present and medians positive and never adjudicates throughput ratios from a
  shortened-budget run. The reference record remains the research
  reference.