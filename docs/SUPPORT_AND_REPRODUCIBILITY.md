# Support and Reproducibility

Lattice is an auditable multi-agent research and benchmarking environment for
deterministic Dec-POMDP experiments under partial observability, dynamic
topology, and resource contention. This document names exactly what the project
supports, what it explicitly does not support, what it does not yet claim, and
the commands that reproduce every committed artifact. It is the operational
companion to the governing thesis in
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md).

> **Release status: Research Preview — evaluation by maintainers and
> collaborators.** The support contract in this document targets `v2.3.1`.
> Publishing that release does not by itself qualify Lattice for production
> use; it remains a research instrument until independent security review,
> soak testing, and formal fuzzing are complete (see Section 2).

## Equivalence vocabulary

Four distinct guarantees are used throughout this repository. They are never
interchangeable, and no document may upgrade one into another:

1. **Engine transition determinism.** Under the stated runtime contract, the
   same `SimulationState` plus the same `AgentAction[]` produces the same next
   state. This is a property of the pure step contract
   ([`adr-002.md`](adr-002.md)).
2. **Per-step serialized `StepResult` replay equivalence.** Replaying a
   recording's actions from a fresh initial state reconstructs ticks whose
   serialized `StepResult`s equal the recorded ones. `TrajectoryReplay.Verify`
   asserts exactly this on the tested CI platforms; the `replay --verify`
   command below exercises it.

The four guarantees above are formalized as an implementation-agnostic,
clean-room contract in
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) — the transition
laws an independent oracle or checker in any language must reproduce, plus
three falsifiable external challenge questions and the submission contract for
oracle verification reports.
3. **Same-host normalized JSONL byte identity.** Two fresh episodes recorded
   from the same seed and the same actions produce byte-identical JSONL only
   where line-ending and formatting normalization is verified on identical host
   environments, and only on hosts where that identity is explicitly tested.
   This guarantee is deliberately narrow: newlines are written as a bare `\n`
   on every platform, but a raw byte identity claim is still scoped to
   identical environments and is not a cross-host guarantee.
4. **Canonical simulation-state hash tree.** **Not currently implemented.**
   Replay verifies serialized `StepResult` equality, not a state digest, so
   there is no canonical hash tree to compare against. The benchmark harness's
   FNV-1a step digest is an internal repeatability check — it anchors one
   warm-up iteration and proves later iterations did not go off-script — not a
   canonical simulation-state hash.

## 1. Target Support Matrix

### Operating systems and architectures

| Target | Release identifier | Notes |
| --- | --- | --- |
| Linux x64 | `lattice-linux-x64` | glibc-compatible; Ubuntu 22.04+ |
| macOS arm64 | `lattice-osx-arm64` | macOS 14+ (Apple silicon) |
| Windows x64 | `lattice-win-x64.exe` | Self-contained single-file executable. |

These are the targets built as self-contained, single-file binaries by
[`.github/workflows/release.yml`](../.github/workflows/release.yml). The test
matrix in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) exercises
Ubuntu, macOS, and Windows, and the benchmark regression workflow is
[`.github/workflows/benchmarks.yml`](../.github/workflows/benchmarks.yml).

### Runtime baseline

Every production assembly — `Environment`, `Generator`, `Agents`,
`Trajectories`, `Analytics`, `Visualization`, and `Cli` — targets `net8.0` and
depends only on the .NET 8 LTS base class library. There are no third-party
runtime package references anywhere in the production graph; assemblies
reference each other only through `ProjectReference`. The `Cli` and `Analytics`
assemblies set `RollForward=LatestMajor`, so the same binary can run on a newer
installed runtime. Committed benchmark and evaluation artifacts record the
runtime, operating system, architecture, and revision under which they were
produced, and those recorded values are the only provenance a result carries.

## 2. Operational Boundaries

### Supported use

- Official internal research use by the `candavere/lattice` maintainers and
  collaborators.
- Deterministic Dec-POMDP multi-agent benchmarking: seeded procedural map
  generation, pure step-contract simulation, and recorded, replayable
  trajectories.
- Offline paired policy evaluation: mirrored-seat MCTS-versus-Scout studies on
  the canonical dev and held-out seed suites, graded by the decision rule in
  Section 5.

### Scope exclusions

- **Production deployment is out of scope.** Lattice is not offered for
  production or critical-infrastructure deployment.
- **Critical-infrastructure use remains out of scope pending independent
  security reviews, soak testing, and formal fuzzing.** The engine is a
  zero-dependency, headless, local-execution library that binds no network
  sockets and stores no credentials; see [`../SECURITY.md`](../SECURITY.md) for
  the current disclosure process and supported release line.
- Any claim of an operating system, architecture, or runtime not listed in
  Section 1 is unsupported until it is added to the matrix and exercised by CI.

## 3. Known Limitations

- **Replay validation, not state-hash validation.** Replay validation operates
  on tick-by-tick serialized `StepResult` equality. A canonical
  simulation-state hash tree is not yet implemented, so a replay cannot be
  summarized by a single state digest.
- **Scenario-dependent policy behavior.** With 32 rollouts per action, the MCTS
  evaluation subject underperforms the `ScoutCollectorAgent` baseline on open
  collection topologies while outperforming it under capacity-1 procedural
  bottleneck contention. Concretely, the committed reference artifacts record a
  mean paired delta of −1.12 on the dev suite and −1.25 on the held-out suite
  for the standard scenario
  ([`../benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json)),
  against +1.42 and +1.38 for the bottleneck scenario
  ([`../benchmarks/bottleneck_evaluation_results.json`](../benchmarks/bottleneck_evaluation_results.json)).
  Policy quality is therefore topology-conditional and must not be described as
  uniformly better or worse.
- **Stepping is not zero-allocation.** Managed step allocations for the raw,
  facility, and dynamic-contention stepping workloads range from roughly
  3.2 KB to 7.1 KB per tick; the stress and MCTS workloads allocate
  substantially more (about 106 KB per tick and 11.5 MB per decision,
  respectively). The reference values and their per-run dispersion are in
  [`../benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md)
  and [`../benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json).

## 4. Trajectory Schema and Release Immutability Policy

### Schema v2 header requirements

Trajectories are JSONL with a fixed line grammar: exactly one `header` line
first, one `step` line per tick, and exactly one `final` line last. Every line
is independently parseable JSON and carries a `Kind` discriminator.

The header is everything needed to reconstruct the episode without re-running
the generator:

| Field | Required | Meaning |
| --- | --- | --- |
| `Kind` | yes | Always `"header"`. |
| `Seed` | yes | Generation seed for the recorded map. |
| `Map` | yes | The fully materialized map graph. |
| `SimulationConfig` | yes | Configuration used to rebuild the environment. |
| `DynamicRules` | no | The dynamic topology policy; omitted (null) for static maps. |
| `SchemaVersion` | yes | Wire format stamp; `2` is the current version. |
| `Scenario` | no | Demonstration-layer metadata; ignored by the replay core. |
| `AgentRoles` | no | Demonstration-layer roster metadata; ignored by the replay core. |

`SchemaVersion` is `2` for newly written files. Schema v2 records the episode's
dynamic topology policy (`DynamicRules`, timed portcullises and event locks) so
a replay recreates the exact choke-capacity schedule the recording was made
under. The simulation config is the required second half of that contract: a
replay with a different config is not a replay of the same episode.

### Structural and migration invariants

- The header must be the first line; a second header anywhere is rejected.
- Step numbers must be contiguous from `1`; gaps or repeats are rejected.
- Exactly one final line is permitted, and it must be last.
- The final line's `TotalSteps` must equal the number of recorded step lines.
- Action validity is checked at record time and again at verify time against
  `ActionSpace`, so a step outside the action space is rejected rather than
  replayed.
- Every non-blank line must declare a known `Kind`; unknown kinds fail loudly.
- A truncated or hand-corrupted recording is rejected instead of silently
  replaying wrong data.

### Backward and forward compatibility

- **Backward compatibility.** Files written before the `DynamicRules` field
  existed read back as schema version `0` — the static-map contract — and are
  still accepted. The absence of `DynamicRules` is interpreted as "static map,
  no dynamic rules", not as unknown data.
- **Forward compatibility is refused, not guessed.** A header whose
  `SchemaVersion` is newer than the version this library writes is rejected with
  an explicit error. Version skew fails loudly rather than replaying under the
  wrong contract.
- **Migration invariant.** Any new optional field must be nullable and omitted
  when absent (`JsonIgnoreCondition.WhenWritingNull`) so older recordings remain
  readable, and a writer always stamps `TrajectorySchema.CurrentVersion`. When
  the wire contract changes, the version is incremented and the reader's
  accepted-version rule is updated in the same change.

The golden fixtures used to pin these invariants are
[`../Tests/fixtures/golden_trajectory.jsonl`](../Tests/fixtures/golden_trajectory.jsonl)
(schema v2, seed 2024) and
[`../Tests/fixtures/golden_dynamic_rules.json`](../Tests/fixtures/golden_dynamic_rules.json).

### Release immutability policy

- **Published release tags are permanent and immutable.** A `v*` tag that has
  been pushed is never moved, re-pointed, or deleted.
- **Attached release assets are permanent and immutable.** Once a binary asset
  is attached to a published release, it is never replaced or re-uploaded under
  the same name.
- **Corrections supersede; they do not rewrite.** A defect in a published
  release is fixed by publishing a new, higher patch version with its own tag
  and its own freshly built assets. The prior release remains in place as part
  of the record.
- The release pipeline that publishes the Section 1 targets is
  [`.github/workflows/release.yml`](../.github/workflows/release.yml).

## 5. Step-by-Step Reproduction Procedures

### External reproduction challenge packet

Independent external evaluators should begin with the self-contained, turnkey
guide in [`reproduction_packet.md`](reproduction_packet.md). It anchors solely
to the published immutable release `v2.3.1` (commit
`c0e834266a0da8f482cfc327e72a988040a770a1`), lists the exact published SHA-256
checksums and download URLs for the three platform binaries, `SHA256SUMS.txt`,
and `sbom.json`, gives the pre-execution verification command, defines four
scripted reproduction experiments with pass/fail criteria (CLI parity,
per-step serialized `StepResult` replay equivalence, the standard-vs-bottleneck
"one policy, two conclusions" paired evaluation, and a structural benchmark
smoke), and provides a standardized reporting template for filing public
issues. In this section the procedures below are the maintainer-oriented
source-tree equivalents; the packet is the external-reviewer entry point.

The transition and perception laws those experiments exercise are pinned as
formal, implementation-agnostic invariants in
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md). An external
evaluator who wants to build an independent oracle or checker in any language
should work from that specification rather than from the source tree.

All commands run from a clean checkout of `candavere/lattice` at the repository
root, with the .NET 8 SDK installed. They are Release-configuration runs and
require no network access once the SDK and dependencies are restored.

### Full test suite

```sh
dotnet test Tests/Lattice.Tests.csproj -c Release
```

### Golden trajectory replay verification

```sh
dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify
```

A pass prints `replay verified` and exits `0`. This asserts per-step serialized
`StepResult` equivalence (Section 3), not a state hash.

### Five-workload throughput benchmark

```sh
dotnet run -c Release --project Cli -- benchmark
```

The default protocol is 10 measured iterations after a 50,000-step warm-up. To
regenerate the committed reference record instead of printing to stdout:

```sh
dotnet run -c Release --project Cli -- benchmark --out benchmarks/throughput_benchmark.json
```

Pass `--commit <sha>` and `--cpu "<model>"` to stamp provenance into the
artifact. The narrative reference record is
[`../benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md) and
the machine-readable record is
[`../benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json).

### Standard paired evaluation

```sh
dotnet run -c Release --project Cli -- evaluate --scenario standard --seeds 50 --rollouts 32
```

### Procedural bottleneck evaluation

```sh
dotnet run -c Release --project Cli -- evaluate --scenario bottleneck --seeds 30 --rollouts 32
```

Both `evaluate` commands default to the held-out suite. To reproduce both the
dev and held-out studies committed in the reference artifacts, add
`--seed-set dev,heldout` and direct the report with `--out`:

```sh
dotnet run -c Release --project Cli -- evaluate --seed-set dev,heldout \
  --scenario standard --seeds 50 --rollouts 32 \
  --out benchmarks/mcts_evaluation_results.json

dotnet run -c Release --project Cli -- evaluate --seed-set dev,heldout \
  --scenario bottleneck --seeds 30 --rollouts 32 \
  --out benchmarks/bottleneck_evaluation_results.json
```

### Expected artifact paths

| Artifact | Produced by | Contents |
| --- | --- | --- |
| `benchmarks/throughput_benchmark.json` | `benchmark --out ...` | Per-workload throughput, latency, allocation, and GC counters plus host metadata. |
| `benchmarks/throughput_summary.md` | narrative companion | Honest reading of the benchmark record. |
| `benchmarks/mcts_evaluation_results.json` | `evaluate --scenario standard ...` | Standard-scenario paired study, per suite. |
| `benchmarks/bottleneck_evaluation_results.json` | `evaluate --scenario bottleneck ...` | Bottleneck-scenario paired study, per suite. |

### Evaluation decision rule and reference verdicts

A study passes only when both conditions hold: the mean paired delta is positive
(Δ̄ > 0) **and** the lower bound of the 95% confidence interval is positive
(95% CI_lower > 0). In symbols: `Δ̄ > 0 ∧ 95% CI_lower > 0`.

| Scenario | Suite | Target | Baseline | Seeds | Rollouts | Mean Δ | 95% CI | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| standard | dev | MCTS | Scout | 50 | 32 | −1.12 | [−1.37, −0.87] | Fail |
| standard | held-out | MCTS | Scout | 50 | 32 | −1.25 | [−1.54, −0.96] | Fail |
| bottleneck | dev | MCTS | Scout | 30 | 32 | +1.42 | [+1.11, +1.73] | Pass |
| bottleneck | held-out | MCTS | Scout | 30 | 32 | +1.38 | [+1.07, +1.69] | Pass |

A negative delta is not a defect in the harness. It is committed baseline
empirical evidence that the evaluated policy lost to the Scout baseline under
that topology, and it is reported exactly as measured. Per the evidentiary
standard in
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md),
superseding a negative verdict requires clearing the same rule under the same
protocol on the same seed suites — not re-describing the result.

## References

- [`../README.md`](../README.md) — project overview and quick start.
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — contribution and evidence rules.
- [`../SECURITY.md`](../SECURITY.md) — supported versions and disclosure.
- [`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md) — governing thesis and evidentiary principles.
- [`adr-002.md`](adr-002.md) — step contract and runtime targets.
- [`adr-003.md`](adr-003.md) — evaluation and analytics decisions.
- [`ECOSYSTEM.md`](ECOSYSTEM.md) — assembly and data-flow map.
