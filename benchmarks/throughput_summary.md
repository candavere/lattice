# Throughput Benchmark — Reference Record

Machine-readable data: [`throughput_benchmark.json`](./throughput_benchmark.json)

## Host & provenance

| Field | Value |
| --- | --- |
| Commit | `0ce60b888d3e2d7d8d58083812d0ebdc09c61317` |
| Timestamp (UTC) | 2026-09-18T19:40:17Z |
| Runtime | .NET 10.0.10 |
| Configuration | Release |
| OS | macOS 27.0.0 |
| CPU | Apple M1 |
| Architecture | Arm64 |
| Cores | 8 |
| Addressable RAM | 8 GiB (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`) |
| GC mode | Workstation |

## Protocol

`dotnet run --project Cli -- benchmark --runs 10 --warmup 50000 --commit <sha> --cpu "Apple M1"`

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
| `micro_raw_2agent` | 3z/5c/8r | 2 Randoms | — | **768,611 steps/s** | 1.35 µs | 1.46 µs | 3,213 B |
| `facility_static_4agent` | 10z/17c/20r | 2 Greedy + 2 Randoms | — | **405,026 steps/s** | 2.82 µs | 3.92 µs | 4,630 B |
| `dynamic_contention_4agent` | 10z/17c/20r | 4 Randoms | portcullis + event lock | **243,434 steps/s** | 4.74 µs | 6.42 µs | 7,107 B |
| `stress_topology_4agent` | 30z/58c/57r | 2 Greedy + 2 Scouts | — | **8,991 steps/s** | 113 µs | 146 µs | 106,309 B |
| `policy_lookahead_mcts_32` | 3z/5c/8r | 2 MCTS (32 rollouts, depth 12) | — | **463 decisions/s** | 4.67 ms | 7.05 ms | 11.5 MB |

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

## Reproducing / gating

- Full reference run: `dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`
- Quick smoke: `dotnet run -c Release --project Cli -- benchmark --runs 2 --warmup 1000 --steps 20000`
- CI gate (`.github/workflows/benchmarks.yml`): re-benchmarks the matrix and
  fails on a >20% regression against this record when the host fingerprint
  (OS family + architecture) matches; otherwise it prints a cross-host
  comparison table and enforces the structural checks.