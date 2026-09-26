# Benchmarking: protocol, methodology, and how to reproduce

This page documents how the committed throughput record was produced and how
to reproduce or re-gate it. Machine-readable data:
[`benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json);
reference record and honest-reading notes:
[`benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md).

## The one-line answer

`dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`

Full flag reference: [CLI reference — benchmark](CLI.md#benchmark--measure-the-work-load-matrix).

## Host and provenance of the committed record

| Field | Value |
| --- | --- |
| Commit (measured tree) | `41530efb35ef620c8d0722e20bcd30970468785a` |
| Runtime | .NET 10.0.12, Release |
| OS | macOS 27.0.0 |
| CPU | Apple M1, Arm64, 8 cores |
| Addressable RAM | 8 GiB |
| GC mode | Workstation |

## Harness protocol

Every case runs the same protocol (`Analytics/Benchmarking/BenchmarkHarness`):

1. **Warm-up anchors a digest.** An FNV-1a step digest is computed over the
   episode's (state, result) stream; the budget is realized as 1–2 full
   iterations so the exact measured path is JIT-settled.
2. **Forced GC sweep before each measured iteration**, so every run starts
   from a clean heap.
3. **Per-step latency** is sampled with `Stopwatch.GetTimestamp` into one
   combined histogram; throughput (steps/sec, or decisions/sec for the MCTS
   case) is the median over measured iterations. Managed allocation comes from
   `GC.GetAllocatedBytesForCurrentThread`; Gen0/1/2 collection-count deltas
   record GC pressure.
4. **Every measured iteration must reproduce the warm-up anchor's step
   digest.** Off-script runs fail loudly instead of reporting timings.

## Five workloads

| Workload | What it measures |
| :--- | :--- |
| raw stepping | The pure step contract at minimum configuration |
| mixed static facility | Stepping plus structured topology |
| dynamic contention | Active portcullis and choke contention |
| stress topology | 30-zone map at the contract ceiling |
| MCTS decisions | Decisions/sec under 32 rollouts, not engine steps/sec |

Medians on the committed record span from 14,916 steps/s (30-zone stress case)
to 899,075 steps/s (micro case). The MCTS case prices per-decision cost under
32 independent depth-12 continuations, so its latency histogram is dominated
by single heavy decisions, and its iteration budget is small by design.

## Honest reading of the numbers

- **Allocation is real and reported as real.** The step contract returns an
  immutable `StepResult` and the harness rebuilds per-agent observations each
  tick, so the core allocates roughly KB/tick in raw cases and far more under
  stress/MCTS. The committed record carries measured values; the regression
  suite pins allocation growing **linearly** in tick count. No "0 bytes
  allocated" claim is made.
- **The stress case runs 4 agents, not 16.** `SimulationConfig` validates
  agent count inclusive 2..4; the 30-zone stress map is driven at the contract
  ceiling rather than a nominal 16-agent roster.
- **The MCTS case reports decisions/sec, not steps/sec.** One decision runs
  thousands of fork steps; its per-iteration budget is 100 ticks by design.
- **Instrumentation is included.** Per-step `Stopwatch.GetTimestamp` and the
  digest mix are part of the measured tick.

## Reproducing and gating

- Full reference run:
  `dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`
- Quick smoke:
  `dotnet run -c Release --project Cli -- benchmark --runs 2 --warmup 1000 --steps 20000`
- CI gate (`.github/workflows/benchmarks.yml`): re-benchmarks the matrix and
  fails on a **>20% regression** against this record only when the host
  fingerprint matches this record's host class: **OS family + architecture +
  .NET runtime major + logical cores + CPU model** (trimmed, case-folded; a
  missing or empty host field is also a mismatch). GitHub-hosted runners
  (macos-14: 3 vCPU, virtualized) are a different class than this bare-metal
  M1 record — same OS family and architecture, unlike core count — so they
  get an informational cross-host comparison plus the structural checks, not
  a throughput verdict. It installs the same .NET 10 runtime the baseline was
  recorded under, so a cross-runtime delta is never misread as a regression;
  on any mismatch it prints a cross-host comparison table instead of failing.
  The cross-host smoke pass (ubuntu x64, .NET 8) is classified structurally
  with `--smoke`: workloads present and medians positive, never a
  throughput-ratio adjudication from a shortened-budget run. This bare-metal
  M1 record remains the research reference.
- The committed baseline was **re-anchored** after the CI regression gate
  proved unstable against the earlier one (noisy micro/policy medians), and
  re-anchored again on 2026-09-26 for the runtime patch .NET 10.0.10 →
  10.0.12 (same Apple M1, AC power) using the same conservative method: three
  consecutive full-protocol sessions, committing the session that is low for
  the noisy workloads, near-typical elsewhere. The speedup against the
  previous record is environmental, not an engine change — identical configs
  and protocol, effectively identical GC counters and allocations, and a
  uniform +63% to +93% shift across all five workloads including
  search-bound MCTS — so a matching-host pass has real headroom and a
  genuine >20% drop still trips the gate.

## Boundaries

Throughput is hardware- and build-profile-scoped. Numbers vary with hardware,
runtime version, and GC configuration; the committed record is one host
class, one runtime, one protocol. No scaling or infrastructure claim is made.