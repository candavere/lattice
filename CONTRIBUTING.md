# Contributing to Lattice

Thank you for considering a contribution to Lattice — a deterministic,
seedable 2D procedural strategy/tactical simulation environment with a clean
agent harness. This guide documents the engineering contract every change must
honor so the repository keeps reading as a coherent product rather than an
accumulation of patches.

Before anything else: read the project's engineering contract; it is the
source of truth for design principles and non-negotiable constraints. Work is
only *done* when the tests that pin the requested behavior pass, no principle
is violated, and no unrequested functionality was added.

## Philosophy & Design Tenets

Four ideas govern every change:

1. **Zero external runtime dependencies.** `Lattice.Environment`,
   `Lattice.Generator`, `Lattice.Trajectories`, `Lattice.Agents`, and
   `Lattice.Analytics` are pure .NET 8 BCL with no NuGet packages, no game
   engine references, no Newtonsoft.Json, no serialization frameworks — the
   production dependency graph is the standard library alone. `Visualization`
   and `Cli` may reference the core only through the public step-contract /
   recording types. Only development and test projects depend on external
   packages, exclusively `Microsoft.NET.Test.Sdk` and `xUnit`/`xunit.runner`.

2. **Strict platform determinism.** The core guarantee is **same seed + same
   actions → deterministic transitions under the pure .NET 8 BCL runtime, and
   verified per-step serialized StepResult equivalence on all supported CI
   targets**: .NET 8 on Linux, macOS, and Windows, on x64 and ARM64. There is
   no ambient randomness, no hidden mutable state, no `System.Random` without
   an explicit seeded instance, and no behavior that depends on `GetHashCode`,
   culture, or iteration order leaks. Determinism is tested, not assumed.

3. **High-throughput, allocation-conscious simulation loops.** The five-case
   workload matrix (raw stepping, facility, dynamic topology, stress, MCTS
   policy — see `Analytics/Benchmarking` and `benchmarks/throughput_benchmark.json`
   for the host-scoped reference run) measures the pure
   `Simulation.Step` core per case, e.g. ≈ 654k steps/s median for the 2-agent
   micro case on a 2020 Apple M1 (see `benchmarks/throughput_benchmark.json`). Methodology: warm-up that anchors a step
   digest, ≥10 measured iterations, per-step stopwatch + median/mean/std/p95,
   `GC.GetAllocatedBytesForCurrentThread`, and `GC.CollectionCount` for
   Gen0/1/2. Every measured iteration must reproduce the warm-up anchor's step
   digest. Keep the hot path free of per-step allocation churn, avoid string
   concatenation and LINQ in tight loops, and never move work such as logging
   or serialization into the step.

4. **Clear separation of concerns.** Environment state, topological graph
   definition, and agent decision logic are three distinct layers. The
   environment never knows about vision (perception is a pure projection), the
   map generator never knows agents or fairness exist (an acceptance gate is
   caller-supplied), and agents are a thin demonstration layer — the
   environment is the product.

## Governing Thesis & Evidentiary Standard

Before any change is accepted, it must answer to the governing product thesis
and the five evidentiary principles formalized in
[`docs/adr/0001-governing-product-thesis.md`](docs/adr/0001-governing-product-thesis.md):
Lattice is simulation-as-instrument — repeated, independently-verifiable
measurement under deterministic, contention-bearing conditions is the product,
and every claim must be traceable to committed, reproducible evidence.
Read that ADR before opening a PR; it is the decision authority for feature
intake.

The evidentiary standard makes three things mandatory for PRs:

1. **Performance claims ship evidence.** Any PR introducing performance claims
   must include an accompanying `benchmarks/*.json` artifact produced by the
   committed harness, with its host metadata (commit, runtime, OS, CPU, GC
   mode) intact. A claim without a committed artifact is rejected. If you
   cannot run the full protocol, say so and record the reference numbers you
   did measure; do not state a number you did not produce.
2. **Policies and search enhancements are evaluated, not asserted.** Any PR
   proposing a new policy, planner, or search enhancement must evaluate it on
   the dev and held-out seed sets under mirrored-seat pairings (the same
   protocol as `evaluate`), and the decision rule — mean paired delta > 0 with
   a CI lower bound > 0 — is the verdict. An improvement is demonstrated by
   clearing the rule, not by a narrative.
3. **Equivalence claims name their level.** Claims of state or replay
   equivalence must specify which of three tiers they assert:
   - **Transition determinism:** same state + same actions → same next state.
   - **Per-step serialized StepResult equivalence:** `TrajectoryReplay.Verify`
     passes across the supported CI targets (Ubuntu, macOS, and Windows).
   - **Normalized JSONL byte identity:** asserted only when explicitly verified
     on identical host configurations.
   Do not claim raw cross-platform file-byte identity or a canonical
   simulation-state hash unless an explicit canonical hasher was executed. Say
   which tier you changed and which tier your tests assert; the verifier's
   documented capability must match what the code actually enforces.

## Development Environment & Prerequisites

- a recent [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (the solution
  also runs on later majors via `RollForward=LatestMajor`).
- no other tooling is required; there is no formatter, linter, or
  code-generator step. Everything compiles and tests with the SDK alone.

Build the solution (expect **0 warnings, 0 errors**):

```sh
dotnet build Lattice.sln -c Release
```

Run the full test suite (unit, determinism, replay, benchmark):

```sh
dotnet test Lattice.sln
```

A green build with a green suite in Release is the baseline bar for any merge.

## Determinism Testing Checklist (Mandatory for PRs)

Any change to action resolution, tie-breaking, kinematic transit counters,
capacity triage, or agent heuristics risk silently re-tilting a trajectory.
Run the determinism verification below **before** submitting and keep the
results in the PR description.

1. **Normalized JSONL byte identity on an identical host.** Record an episode
   to JSONL, then re-record the same seed/actions on a fresh process on the
   same host configuration and diff:

   ```sh
   dotnet run --project Cli -c Release -- simulate --seed 42 --steps 100 --out a.jsonl
   dotnet run --project Cli -c Release -- simulate --seed 42 --steps 100 --out b.jsonl
   cmp a.jsonl b.jsonl && echo "identical"
   ```

   If you changed resolution logic, do the same with `--agent mcts` (the
   search must still traverse deterministically). This asserts normalized
   file-byte identity on one host configuration; it is not evidence of raw
   cross-platform file-byte identity.

2. **Per-step serialized StepResult equivalence across the CI matrix.**
   `TrajectoryReplay.Verify` must pass on Ubuntu, macOS, and Windows (x64 and
   ARM64) — that is serialized-result equivalence against the recorded
   trajectory, not raw cross-platform file-byte identity. CI runs the suite on
   the full matrix; confirm it is green in your PR. If you cannot run all
   platforms locally, say so explicitly so reviewers know the matrix is the
   coverage.

3. **Floating-point hygiene.** No simulation rule may depend on IEEE
   double/float comparisons in tie-break or ordering decisions; tie-breaks
   must be integer-quantized (union/record order, id, distance). Call this out
   in the PR if your change touches scoring or search ranking.

4. **Determinism regression tests.** Add or extend a determinism test that
   pins the changed behavior — same seed + same actions producing identical
   transitions, and (where applicable) a passing per-step serialized
   `StepResult` replay — asserted rather than eyeballed. See `/Tests` for the
   existing suite; new deterministic behavior without a pinning test will be
   sent back.

## Pull Request Guidelines

- **Focused, atomic PRs.** One logical change per PR, branched from `main`
  with a descriptive, imperative subject. Split cross-cutting work; if the
  diff touches both environment and generator, it should typically be two PRs
  with the dependency stated in their descriptions.
- **100% test pass rate, zero compiler warnings.** The suite must pass as-is
  on `main` plus your branch; a change that turns a passing test red or adds a
  warning is not mergeable. If legacy behavior is intentionally changing, the
  PR must update the affected pins *in the same commit* with a rationale.
- **No new external packages in production assemblies.** Every production
  project (`Lattice.Environment`, `Lattice.Generator`, `Lattice.Trajectories`,
  `Lattice.Agents`, `Lattice.Analytics`, `Lattice.Visualization`,
  `Lattice.Cli`) remains pure .NET 8 BCL. Dependency proposals for the core
  are a design decision, not a merge decision — raise them in an issue first.
  Development and test projects may only add `Microsoft.NET.Test.Sdk` or
  `xUnit` packages; anything else is a design decision too.
- **Regression tests for bug fixes and topological edge cases.** Any fix —
  especially choke contention, capacity limits, transit bookkeeping, or
  generator constraint edge cases — must ship with a test that fails on the old
  behavior and passes on the new one. Favor adversarial fixtures (opposing
  agents on a capacity-1 choke, blocked first hops, exhausted retry budgets).
- **No hidden state or ambient randomness.** A contribution cannot introduce a
  static mutable field, a singleton, or an unseeded RNG. If an agent or
  subsystem needs per-episode state, make it instance state and document that
  the instance is bound to one episode.
- **Public types need `///` docs on the *why*.** Public step-contract records
  and functions must carry XML docs explaining their contract, not just their
  shape. Keep public documentation free of implementation-chronology hints;
  the repository reads as an authoritative product.

## Reporting Issues

Bugs reproduce deterministically — file them that way. Open a GitHub issue on
https://github.com/candavere/lattice/issues with the following, all of which
are required:

- **Seed number** — the exact `--seed` used (a `ulong`).
- **CLI command line** — the full invocation (command, flags, and values),
  e.g. `dotnet run --project Cli -- simulate --seed 42 --agent mcts --steps 100`.
  If the bug surfaces in the web replay viewer instead, say which file you
  loaded (`demo.jsonl` or a custom recording).
- **Step count** — how many ticks were recorded and whether the episode ended
  by exhaustion or tick limit (`Reason` from the final metrics line).
- **Target architecture** — OS and CPU (e.g. "macOS 15 / Apple silicon",
  "Linux / x64", "Windows 11 / ARM64"), and the .NET version you built with.
- **Expected vs. actual trajectory output** — paste (or attach) the
  differing `jsonl` lines, or the header/final lines that disagree, plus the
  exact bytes (or hex) of the divergence if it is not visible as text.

Include **one** focused problem per issue. Related but distinct symptoms should
be separate issues; the determinism guarantees make bisection trivial once the
seed and platform are recorded. If the bug is a performance regression, run
`dotnet run --project Cli -- benchmark --runs 3 --steps 20000` on your machine and report
the per-case `MedianThroughputPerSecond` (or the JSON artifact) alongside your hardware.