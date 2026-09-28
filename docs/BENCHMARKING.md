# Benchmarking: protocol, methodology, and how to reproduce

This page documents how the committed throughput record was produced and how
to reproduce or re-gate it. Machine-readable data:
[`benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json);
reference record and honest-reading notes:
[`benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md).

A second, separate record covers GitHub-hosted runners:
[`benchmarks/runner_class_throughput_benchmark.json`](../benchmarks/runner_class_throughput_benchmark.json)
with its evidence note
[`benchmarks/runner_class_summary.md`](../benchmarks/runner_class_summary.md).

## The one-line answer

`dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`

Full flag reference: [CLI reference — benchmark](CLI.md#benchmark-measure-the-work-load-matrix).

## Host and provenance of the committed record

This is the **bare-metal research reference**. The hosted-runner record is
separate and listed in the next section.

| Field | Value |
| --- | --- |
| Commit (measured tree) | `41530efb35ef620c8d0722e20bcd30970468785a` |
| Runtime | .NET 10.0.12, Release |
| GC mode | Workstation |

Numbers come from a single reference host; its full metadata (CPU, cores, OS, runtime) is recorded in the artifact.

## Host and provenance of the hosted-runner record

| Field | Value |
| --- | --- |
| Runner label (pinned) | `macos-26` (newest GA arm64 macOS label) |
| Runner image build | `macos-26-arm64/20260907.0351` |
| Source revision (all 5 samples) | `633ecf70c82e1b71255a6dac8e09bdca0b5b243b` |
| Host | macOS 26.6.2, Arm64, Apple M1 (Virtual), 3 cores |
| Runtime | .NET 10.0.12, Release, Workstation GC |
| Protocol | `--runs 10 --steps 100000 --warmup 50000` |
| Representative session | run `36469918606` (most typical of the five) |

Both records are kept because they measure different host classes and neither
substitutes for the other. The bare-metal record is untouched; the runner-class
record is a new, separately named artifact.

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

- **AC power is required on a laptop host.** Before any benchmark session on a
  laptop, confirm the machine is plugged in and check the power state with
  `pmset -g batt`; do not record a baseline on battery. This is not a
  formality: in `FINDING-009` the same protocol, host and runtime measured
  roughly 2x apart between battery and AC, and battery sessions erased most of
  the re-anchor shift, so a battery run silently looks like a large regression.
- Full reference run:
  `dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json`
- Quick smoke:
  `dotnet run -c Release --project Cli -- benchmark --runs 2 --warmup 1000 --steps 20000`
- CI gate (`.github/workflows/benchmarks.yml`): re-benchmarks the matrix and
  fails on a **>20% regression** against this record only when the host
  fingerprint matches this record's host class: **OS family + architecture +
  .NET runtime major + logical cores + CPU model** (trimmed, case-folded; a
  missing or empty host field is also a mismatch). GitHub-hosted runners
  are a different host class than the reference record, so they
  get an informational cross-host comparison plus the structural checks, not
  a throughput verdict. It installs the same .NET 10 runtime the baseline was
  recorded under, so a cross-runtime delta is never misread as a regression;
  on any mismatch it prints a cross-host comparison table instead of failing.
  The cross-host smoke pass (ubuntu x64, .NET 8) is classified structurally
  with `--smoke`: workloads present and medians positive, never a
  throughput-ratio adjudication from a shortened-budget run. The reference
  record remains the research reference.
- **CI throughput gate (hosted runner, `runner-class-gate`).** A hosted
  runner is a different host class from the reference record, so CI enforces
  throughput against its **own** runner-class record, measured on the runner
  class it runs on: `macos-26` (pinned, not `macos-latest` — the newest GA
  arm64 macOS label), .NET 10, image `macos-26-arm64/20260907.0351`, recorded
  as five full-protocol sessions on tree
  `633ecf70c82e1b71255a6dac8e09bdca0b5b243b`
  ([runs 36469355765](https://github.com/candavere/lattice/actions/runs/36469355765),
  [36469885628](https://github.com/candavere/lattice/actions/runs/36469885628),
  [36469902164](https://github.com/candavere/lattice/actions/runs/36469902164),
  [36469918606](https://github.com/candavere/lattice/actions/runs/36469918606),
  [36469934258](https://github.com/candavere/lattice/actions/runs/36469934258)).
  The strict verdict needs the full fingerprint — OS family, architecture,
  .NET runtime major, logical cores, CPU model — **and** a matching recorded
  protocol, so a shortened or smoke budget can never be adjudicated against
  it. Tolerances come from the *measured between-run* spread, not from
  within-run sample dispersion, which on this host class reads 1.3–2.8× too
  tight for four of the five workloads. Four workloads are armed; a genuine
  drop of roughly 9–21% still trips them.
  `policy_lookahead_mcts_32` is **recorded but never adjudicated**: at 1.81×
  between-run spread no threshold wide enough to stop the gate false-firing on
  jitter would still detect a real regression, so the record declares it
  informational and the comparator reports its ratio without a verdict. The
  bounded single retry is kept. See
  [`benchmarks/runner_class_summary.md`](../benchmarks/runner_class_summary.md)
  for the full spread analysis, the limits of a hosted-runner inference
  (including that a pinned label is not a pinned image), and the
  re-collection procedure.
- The committed baseline was **re-anchored** after the CI regression gate
  proved unstable against the earlier one (noisy micro/policy medians), and
  re-anchored again on 2026-09-26 for the runtime patch .NET 10.0.10 →
  10.0.12 (reference host, AC power) using the same conservative method: three
  consecutive full-protocol sessions, committing the session that is low for
  the noisy workloads, near-typical elsewhere. The speedup against the
  previous record is environmental, not an engine change — identical configs
  and protocol, effectively identical GC counters and allocations, and a
  uniform +63% to +93% shift across all five workloads including
  search-bound MCTS — so a matching-host pass has real headroom and a
  genuine >20% drop still trips the gate.

## Boundaries

Throughput is hardware- and build-profile-scoped. Numbers vary with hardware,
runtime version, and GC configuration; each committed record is one host
class, one runtime, one protocol. No scaling or infrastructure claim is made.

A hosted-runner gate is a narrower claim than a hardware one: it bounds
throughput regressions **on the runner class that recorded it**, and its
tolerances are wide because that class is noisy — between-run spread on the
recorded tree was 1.41×–1.81× per workload. A wide gate here means a real
regression must be large to be caught; it does not mean the runner is fast,
stable, or representative of any other machine. The AC-power requirement
above applies to a laptop host recording a reference; it does not apply to a
hosted runner, which is the point of keeping the two records apart.