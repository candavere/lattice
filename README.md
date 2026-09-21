# Lattice
<p align="center">
  <img src="assets/lattice-logo.svg" alt="LATTICE Logo" width="140" height="140" />
</p>

<h1 align="center">LATTICE</h1>

<p align="center">
  <strong>An auditable multi-agent research &amp; benchmarking environment for deterministic experiments</strong>
</p>

<p align="center">
  <a href="#quickstart">Quickstart</a> •
  <a href="#architecture">Architecture</a> •
  <a href="#dual-viewport">Visualizer</a> •
  <a href="#contributing">Contributing</a>
</p>

---
<p align="center">
  <a href="https://github.com/candavere/lattice/actions/workflows/ci.yml"><img src="https://github.com/candavere/lattice/actions/workflows/ci.yml/badge.svg" alt="CI build status" /></a>
  <img src="https://img.shields.io/badge/status-research%20preview-orange" alt="status: research preview" />
  <img src="https://img.shields.io/badge/tests-417%20passing-brightgreen" alt="417 unit tests passing" />
  <img src="https://img.shields.io/badge/determinism-verified--replay--equivalence-blue" alt="verified replay equivalence" />
  <img src="https://img.shields.io/badge/dependencies-BCL%20runtime%20only-blueviolet" alt="runtime dependencies: pure .NET 8 BCL" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8" />
  <a href="https://candavere.github.io/lattice/"><img src="https://img.shields.io/badge/live%20demo-GitHub%20Pages-2ea44f" alt="live demo" /></a>
</p>

> **Lattice** is an auditable multi-agent research and benchmarking environment
> for deterministic experiments under partial observability, dynamic topology,
> and resource contention. Every run is replayable, every comparison is seeded
> and statistically inspectable, and negative results are first-class evidence.
>
> Concretely, Lattice is a headless, zero-dependency C# (.NET 8) substrate: a
> pure step-contract simulation engine, a seeded procedural map generator, an
> MCTS lookahead policy, perception-filtered agents, and a paired statistical
> evaluation harness — instrumented end-to-end for reproducible research.
>
> **Status: Research Preview — evaluation by maintainers and collaborators.**
> Lattice is not yet qualified for production or critical-infrastructure use;
> the supported matrix, operational boundaries, and reproduction procedures
> live in
> [`docs/SUPPORT_AND_REPRODUCIBILITY.md`](docs/SUPPORT_AND_REPRODUCIBILITY.md).

### Development and Verification Method

Lattice is developed through AI-assisted implementation under human direction,
architectural specification, and review. Claims are accepted into the
repository only when they map to committed code, deterministic tests, CI
workflows, or benchmark artifacts. AI assistance is treated as an authoring
method, never as independent validation.

Independent external reviewers can reproduce the empirical claims of the
published immutable release through the turnkey challenge guide in
[`docs/reproduction_packet.md`](docs/reproduction_packet.md) — pinned to
release `v2.3.2` (commit `4f7816f`), with published SHA-256 checksums, four
scripted experiments, and a standardized reporting template. The governing
protocol for that replication — provenance requirements, pass/fail criteria,
and how results are ingested into the findings ledger — is defined in
[`docs/VALIDATION_PLAN.md`](docs/VALIDATION_PLAN.md).

The engine's load-bearing transition and perception laws are specified
formally, implementation-agnostically, for clean-room auditors and
independent oracles in
[`docs/INVARIANT_SPECIFICATION.md`](docs/INVARIANT_SPECIFICATION.md): the
kinematic/choke capacity gate, deterministic conflict resolution, resource
conservation and monotonicity, the graph-reachability perception boundary, and
the step-determinism vs. replay-equivalence contract, plus three falsifiable
external challenge questions and the oracle-report submission contract.

Governance of these claims is kept in two public records and inspected as a
routine part of verification:

- [`docs/FINDINGS_LEDGER.md`](docs/FINDINGS_LEDGER.md) — the append-only,
  transparent record of criticisms, edge cases, and resolved issues (replay
  claim calibration, benchmark-gate sensitivity, a release staging failure,
  parser guard findings, mutation-survivor gaps, and a preserved negative
  result), each with provenance, severity, disposition, and closing evidence.
- [`docs/CLAIM_CALIBRATION_MATRIX.md`](docs/CLAIM_CALIBRATION_MATRIX.md) —
  maps every public claim in README, `site/`, and the ADRs to its proving
  artifact, tested matrix, documented boundary, and wording status.

Lattice is simulation-as-instrument, not game middleware. Agents traverse a
topological graph of zones and capacity-limited chokes under partial
observation; a deterministic step contract advances every tick as a pure
function of the prior state and the recorded actions, so any run can be
replayed and verified per-step. Given identical seeds and action sequences,
simulations produce deterministic state transitions under the specified
.NET 8 BCL runtime contract. The engine, generator, and evaluation harness
exist to make deterministic experiments easy to run, audit, and
statistically inspect — stepping at **8.8k–661k mean steps/sec** (measured on a
2020 Apple M1 / 8 cores / 8 GiB RAM under .NET 10.0.10 Release / Workstation
GC, tree `b7459a1` — see the committed `benchmarks/throughput_benchmark.json`).
Replays guarantee per-step serialized `StepResult` equivalence against the
canonical golden trajectory across the tested Ubuntu, macOS, and Windows CI
matrix (`TrajectoryReplay.Verify`).

<!--
Proposed GitHub topics for the maintainer (set these in the repo settings):
deterministic-simulation, multi-agent, benchmarking, mcts, research-environment,
dotnet8, procedural-generation, partial-observability, simulation-engine, dec-pomdp
-->

Lattice frames multi-agent decision-making as a reproducible research
instrument: every production assembly is pure .NET 8 BCL, every run is seeded,
every trajectory is replay-verifiable, and every paired comparison reports a
confidence interval with negative results kept as first-class evidence. Agents
are a thin demonstration layer — the environment and the evaluation harness are
the product.

## Who Is This For?

- **Multi-Agent & Systems Researchers:** Run seeded, mirror-seated paired
  evaluations of decision policies (MCTS, heuristics) under verifiable partial
  observability, dynamic topology, and resource contention — with confidence
  intervals and negative results kept as evidence.
- **Reinforcement-Learning Engineers:** Use the step-contract environment and
  the procedural bottleneck suite as a deterministic, contention-bearing
  benchmark for lookahead planners before any distributed training loop.
- **.NET & Systems Programmers:** Study high-throughput, low-allocation C#
  systems programming operating on standard BCL primitives with zero
  third-party dependencies.

#### Reinforcement Learning & Simulation Interface Mapping

Coming from OpenAI Gym or PettingZoo? The step-contract surface maps almost
one-to-one onto the classic RL loop:

| Lattice (.NET 8 C#) | Gymnasium / PettingZoo Concept | Architectural Role |
| :--- | :--- | :--- |
| `Simulation.Step(actions)` | `env.step(actions)` | Advances active simulation state by exactly one tick |
| `Observation` | `observation` | Agent's egocentric, hop-bounded partial sensor horizon |
| `SimulationFork` | `copy.deepcopy(env)` | Allocation-conscious, immutable counterfactual rollout sandbox |
| `AgentAction` | `action` | Strongly-typed discrete action (`Move`, `Collect`, `Wait`) |
| `MapFairnessEvaluator` | N/A (Procedural Benchmark) | Automated symmetric seat-inversion balance profiler |

<details>
<summary><b>Glossary: Key Concepts & Terminology</b></summary>

- **BCL-only:** Base Class Library only — zero external runtime dependencies
  across every production assembly (pure .NET 8 BCL); development and test
  projects rely on the standard test SDKs (`Microsoft.NET.Test.Sdk`, xUnit).
- **Headless:** Operates without a window, GPU context, or graphics thread,
  optimized for automated CI and high-speed batch evaluation.
- **Determinism:** Given identical seeds and action sequences, simulations
  produce deterministic state transitions under the specified .NET 8 BCL
  runtime contract.
- **Topological Graph:** An environment modeled as discrete interconnected
  nodes (zones) and capacity-limited edges (chokes) rather than a continuous
  floating-point coordinate space.

</details>

The step contract is a zero-dependency C# state machine instrumented for
deterministic experiment: every tick is a pure function of the previous state
and the recorded actions. The core guarantee is repeatability under the
runtime contract — **same seed, same actions, same transitions.** Trajectory
verification asserts tick-by-tick serialized payload equality. A canonical
simulation-state hash tree is not yet implemented; raw file-byte identity is
not asserted across heterogeneous hosts. The simulation has no hidden state,
no singletons, and no ambient randomness. The built-in Monte Carlo Tree Search
(MCTS) agent demonstrates exactly this contract, pricing candidate actions with
deterministic BFS rollouts against the same pure `Step` used by every other
policy, and the paired evaluation harness turns those runs into statistically
inspectable comparisons.

## Quickstart

Run the Dungeon Infiltration & Sentry Patrol scenario — a patrolling guard
chases a heister through a capacity-gated dungeon under partial observation:

```sh
dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl
dotnet run --project Cli -- render --trajectory infiltration.jsonl --format svg --out infiltration.svg
```

Or the classic two-agent sampling run (MCTS vs Random):

```sh
dotnet run --project Cli -- simulate --seed 42 --agent mcts --steps 30 --out demo.jsonl
dotnet run --project Cli -- render --trajectory demo.jsonl
```

Every command is seeded, so identical arguments produce identical per-step
serialized output under the specified .NET 8 BCL runtime contract. The full
command set is `generate`, `simulate`, `render`,
`analyze`, `replay`, `benchmark`, and `evaluate` — see the
[CLI Reference](#cli-reference) below.

To verify the claims of the published release asset-for-asset without building
anything, work through the
[Reproduction Challenge Packet](docs/reproduction_packet.md).

## Architecture & Modules

| Directory | Responsibilities | Key Architectural Types |
| :--- | :--- | :--- |
| `/Environment` | Pure step-contract core — reset/step, spatial capacity, kinematic transit, perception projection, dungeon topology | `MapGraph`, `Observation`, `AgentAction`, `StepResult`, `PerceptionFilter`, `InTransit`, `Simulation`, `DungeonMapBuilder`, `DungeonRoles` |
| `/Generator` | Seeded procedural map generation with hard-constraint checkers (retry, don't patch); optional caller-supplied acceptance gate | `MapGenerator`, `ConstraintCheckers`, `MapGenerationException` |
| `/Agents` | Rule-based and tactical agent policies, belief maps, scenario runner | `IAgent`, `RandomAgent`, `GreedyCollectorAgent`, `ScoutCollectorAgent`, `MctsAgent`, `SentryPatrolAgent`, `InfiltratorAgent`, `AgentBeliefMap`, `ScenarioRunner`, `InfiltrationScenario` |
| `/Trajectories` | JSONL trajectory read/write/replay/step utilities | `TrajectoryModel`, `TrajectoryWriter`, `TrajectoryReader`, `TrajectoryReplay` |
| `/Analytics` | Trajectory analysis — contention, pathing efficiency, heatmaps, Markdown reports; spawn-bias fairness profiling; the five-case reproducibility benchmark harness | `TrajectoryAnalyzer`, `IncidentDetector`, `CounterfactualEvaluator`, `MapFairnessEvaluator`, `ReportGenerator`, `MapTraversal`, `WorkloadCatalog`, `BenchmarkHarness` |
| `/Visualization` | ASCII terminal renderer + dependency-free CSS-animated SVG exporter | `AsciiRenderer`, `SvgRenderer`, `SvgViewport`, `SvgTrajectoryExporter`, `TrajectoryPlayback` |
| `/Cli` | Driver: `generate` / `simulate` / `render` / `analyze` / `replay` / `benchmark` / `evaluate` | `CliApp`, `Program` |
| `/Tests` | Unit, determinism, replay, and benchmark tests (one per module) | — |
| `/docs` | ADR-style design-decision records (`adr-001`, `adr-002`, `adr-003`) | — |

The behavioral contracts of each subsystem are documented as architecture
decision records in `/docs`; see [Design Decisions](#design-decisions).

## Key Mechanics

### Step contracts and replay determinism

Each tick takes one `AgentAction` per agent and produces an immutable
`StepResult`. Actions resolve in two fixed phases: all moves, then all
collects, each resolved in ascending priority rank, where
`rank = (agentId + state.StepCount) % agentCount`. At zero-based tick `t`,
the first agent is `(-t mod agentCount)` (nonnegative modulo), not
`t mod agentCount`. Agent polling remains in ascending agent id, and pathfinder
neighbors remain in ascending zone id. Terminal score ties still select the
lowest agent id. Invalid or missing actions degrade to `Wait`, so every action
array yields a valid next state and no agent can
crash or wedge the simulation. `Observation`, `Reward`, `Info`, and `StepResult`
are plain immutable records that serialize to JSONL without interpretation
logic; a recording is replayed by re-running the exact recorded action bytes.

### Topological graph space with capacity gates

Maps are graphs, not grids. `MapGraph` holds zones (nodes), choke points
(edges), and resources. Connectivity and degree are first-class, so every
validation checker and the spatial simulator reason about the same structure
(see adr-001). Zones and choke points carry a `MaxOccupancy` (default
unlimited; `0` = impassable), enforced as same-tick entry gates triaged in
ascending priority rank using the same tick-dependent order as collection.
A zone is just a graph node with an optional position —
the coordinate embedding exists only where agents need it.

### Kinematic edge transit

Agents may cross a choke edge over multiple ticks instead of instantly. With
`TransitSpeed = s` and choke length `d` (Manhattan distance between the two
zones), a crossing takes

    max(1, ⌈d / s⌉)

ticks, computed entirely in integer arithmetic. While crossing, the agent
carries an `InTransit(From, To, Remaining)` state and counts as an occupant of
the departure node; it cannot move or collect until arrival. Transit state is
plain data, so mid-crossing frames record and replay deterministically.
`Simulation.TransitTicks(map, from, to, speed)` exposes the same arithmetic for
tooling.

### Dynamic topology

Choke capacity need not be static. A `DynamicMapRuleSet` carries a list of
`IDynamicMapRule`s — `TimedPortcullisRule` (a choke oscillates between open and
closed capacity over a `OpenTicks`/`ClosedTicks` cycle) and
`EventLockedChokeRule` (a choke stays at a locked capacity until a resource is
collected). The engine applies the rules at every tick boundary as
`DynamicMapOverrides` (see adr-002 addenda), and the same policy flows through
rollouts, trajectory headers as `DynamicRules`, contention reporting, and the
per-step replay verification the CLI runs over every recorded episode.
Rules are serializable JSON (`ruleKind` discriminator) and revalidate through
their constructors, so file, programmatic API, and recorded headers can never
drift apart.

### Bounded perception and stale memory

Observation is a projection of a full state, not a core change: the simulator
always emits complete information, and `PerceptionFilter` masks it per
observer. A vision horizon of `V` choke-edge hops defines what an agent can see
this tick. Sights carry one of three knowledge states:

- `Observed` — inside the cone this tick; real-time data.
- `Stale` — seen before, out of cone now; last-known data plus the tick of
  that sighting survive, timestamped.
- `Unknown` — never observed; fully masked (`null` geometry).

Agents consume these projections through `AgentBeliefMap`, so routing happens
over exits an agent has actually seen rather than omniscient map knowledge.

### Immutable forking and rollout lookahead

`SimulationState` is one immutable record, so a snapshot is a shared reference —
no deep copy, no side effects. `SimulationFork` wraps a captured state plus its
config and steps it through the same pure `Simulation.Step`, giving a
provably-isolated sandbox for what-if analysis: forking a recorded trajectory
at tick K and re-rolling an alternative action sequence can never mutate the
source state, the recording, or a sibling fork. The `MctsAgent` builds on this
by pricing candidate actions with deterministic BFS rollouts against a greedy
opponent model, evaluated by `CounterfactualEvaluator`.

### Dungeon Infiltration & Sentry Patrol scenario

`InfiltrationScenario` uses the fixed hand-authored `DungeonMapBuilder`:
six rooms connected by capacity-1 choke points, seeded chest count in the
Treasure Vault (2–3), and an extraction objective in the Entry Hall. The
sentry (`SentryPatrolAgent`) walks a fixed patrol loop and pivots to pursuit
on a Chebyshev-bounded hop cone; the infiltrator (`InfiltratorAgent`) builds
a fog-of-war belief map, steers through a least-risk goal-priority heuristic,
and collects the vault before racing back to extract.

The deterministic outcome taxonomy (all `XOR`-exclusive verdicts):

| Status | Meaning |
| :--- | :--- |
| `exfiltrated` | Every resource claimed, no co-location capture at any tick |
| `intercepted` | Sentry and Infiltrator share a zone with no active transit — the guard physically corners the rogue |
| `intercepted-after-exfil` | Infiltrator completes the haul, but is caught at the exit the same tick the episode closes |
| `timeout` | Budget exhausted before the vault is raided or captured |

Identical parameters produce the same deterministic trajectory under the
runtime contract — same room sequence, same choke contention, same final
verdict.

### Map fairness and spawn bias

`MapFairnessEvaluator` measures whether a map structurally favors one spawn
over the other by playing the same policy twice in mirrored seatings: the two
spawn territories (nodes, resident resources, incident choke endpoints) swap,
which is the only way to invert the fixed spawn-to-id binding. Territory means
are averaged across both seats (see adr-003) into a normalized
`SpawnBiasIndex`:

    SpawnBiasIndex = |meanScore[spawnA] − meanScore[spawnB]| / totalResources

A perfectly balanced map scores `0.0`; a fully one-sided map scores `1.0`.
The index can be wired into generation as a retry-loop acceptance gate (see
`--min-fairness` below).

## Design principles

1. **Pure logic, no hidden state.** No singletons, no static mutable state, no
   ambient randomness. Seeded instances only.
2. **Determinism first.** Same seed + same actions → per-step
   `StepResult`-equivalent trajectory, asserted by a permanent determinism
   test suite.
3. **Step contracts are data.** Records serialize to JSONL with no behavior and
   no interpretation logic.
4. **Hard constraints over probabilistic generation.** The generator rejects
   and retries; constraint checkers are explicit and unit-tested.
5. **Environment over agents.** Agents stay intentionally simple; the budget
   belongs to the environment and tooling. The infiltration scenario's
   deterministic outcome is an emergent property of the graph topology and
   the capacity-gated choke points, not the agents' internal logic.
6. **No heavy dependencies in the core.** `/Environment` and `/Generator` are
   BCL-only; everything else consumes them through public step contracts.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (Lattice also runs on
  .NET 9/10 via `RollForward=LatestMajor`).

## Build & test

```sh
dotnet build Lattice.sln      # expect 0 warnings, 0 errors
dotnet test  Lattice.sln      # unit + determinism + replay + benchmark tests
```

The determinism tests are permanent gates: they regenerate a map from a seed,
replay a fixed script, and assert serialized-equivalent trajectory JSON across
runs on the same host.

## CLI Reference

`Lattice.Cli` exposes seven commands (`dotnet run --project Cli -- <command>
..., binary name `lattice`). **Every command is seeded** — identical
arguments produce identical deterministic output under the specified .NET 8
BCL runtime contract. Exit status is `0` on success,
non-zero on any bad argument or runtime error; `--help`/`-h` prints usage.

### generate — write a valid map

| Flag | Description |
| --- | --- |
| `--seed <ulong>` | Required RNG seed; same seed → same map |
| `--min-fairness <0..1>` | Optional gate: reject any candidate whose measured `SpawnBiasIndex` exceeds the threshold and retry; see below |
| `--out <file>` | Write JSON to a file instead of stdout |

```sh
dotnet run --project Cli -- generate --seed 123
dotnet run --project Cli -- generate --seed 123 --out map.json
dotnet run --project Cli -- generate --seed 123 --min-fairness 0.3
```

Output is compact PascalCase JSON, the same shape the trajectory header
embeds:

```json
{"Zones":[{"Id":0,"Position":{"X":575,"Y":446}}, ...],"Resources":[{"Id":0,"ZoneId":0,"Position":{"X":599,"Y":744}}, ...],"ChokePoints":[{"Id":0,"FromZoneId":0,"ToZoneId":1}, ...]}
```

With `--min-fairness`, every structurally-valid candidate runs through the
mirrored fairness arena (greedy policy, two agents, 200 ticks, transit speed
8). Candidates above the threshold are rejected and regenerated like any
other failed constraint — the generator never patches a biased map. On
acceptance, `spawn bias index X <= fair-threshold Y; map accepted` goes to
stderr; a seed whose retry budget yields no fair map exits non-zero.

### simulate — record an episode as JSONL

| Flag | Description |
| --- | --- |
| `--seed <ulong>` | Required RNG seed for map generation and agents |
| `--steps <n>`   | Tick budget; default 100. Episode ends on budget or when all resources are claimed |
| `--agent <greedy\|random\|mcts>` | Policy for player 0 (default `greedy`); `mcts` selects the MCTS evaluation subject |
| `--scenario <infiltration>` | Run the fixed Dungeon Infiltration & Sentry Patrol scenario; `--agent` is forbidden (roster is fixed) |
| `--rules <file>` | Load a JSON `DynamicMapRuleSet` (timed portcullises / event-locked chokes) into the episode — see [Dynamic topology](#dynamic-topology) |
| `--out <file>`  | Write trajectory to a file instead of stdout |

```sh
dotnet run --project Cli -- simulate --seed 42
dotnet run --project Cli -- simulate --seed 42 --steps 40
dotnet run --project Cli -- simulate --seed 42 --steps 40 --agent mcts
dotnet run --project Cli -- simulate --seed 42 --rules rules.json --steps 60 --out dynamic.jsonl
dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl
```

Without `--scenario`, runs `GreedyCollectorAgent` vs `RandomAgent` on a
procedurally generated map. With `--scenario infiltration`, the fixed
dungeon is used and the roster is hard-wired to Sentry vs Infiltrator. A
summary line — steps recorded, termination reason, outcome — goes to
stderr.

A `--rules` file is a JSON `DynamicMapRuleSet`; rules serialize with a
`ruleKind` discriminator and are rehydrated through the same validating
constructors the programmatic API enforces, so a file cannot smuggle a
degenerate schedule (zero-length cycle, negative capacity) past validation.
Rules participate in the whole toolchain: they bind to the MCTS agent's
internal rollout model, are recorded into the trajectory header as
`DynamicRules`, drive choke contention reporting, and are replayed and
verified per-step against the recorded trajectory when the episode is re-run.

### render — replay a recorded trajectory

| Flag | Description |
| --- | --- |
| `--trajectory <file>` | Required JSONL trajectory |
| `--format <ascii\|svg>` | `ascii` (default) or `svg` |
| `--out <file>` | Write output to a file instead of stdout |

```sh
dotnet run --project Cli -- render --trajectory out/trajectory.jsonl
dotnet run --project Cli -- render --trajectory out/trajectory.jsonl --format svg --out frame.svg
```

`ascii` streams terminal frames; `svg` emits one self-contained,
CSS-animated, dependency-free SVG.

### analyze — report on a recorded trajectory

| Flag | Description |
| --- | --- |
| `--trajectory <file>` | Required JSONL trajectory |
| `--out <file>` | Write the full Markdown report to a file instead of the terminal view |

```sh
dotnet run --project Cli -- analyze --trajectory out/trajectory.jsonl
dotnet run --project Cli -- analyze --trajectory out/trajectory.jsonl --out report.md
```

The report covers contention events, turning points, per-agent pathing
efficiency with archetypes and ratings, resource acquisition timelines,
zone/edge heatmaps, and a per-agent steps timeline. Analysis is a pure
function of the trajectory: the same file always produces the same report
(invariant culture, same-host identical).

### replay

Replay recorded trajectory files and optionally verify per-step serialized
equivalence against the simulation engine.

```bash
# Replay a trajectory interactively or headless
dotnet run -c Release --project Cli -- replay <path-to-trajectory.jsonl>

# Replay with strict per-step serialized StepResult verification
dotnet run -c Release --project Cli -- replay <path-to-trajectory.jsonl> --verify
```

Flags:

- `<file>`: Path to a valid Schema v2 `.jsonl` trajectory.
- `--verify`: Reconstructs the simulation state and dynamic topology rules from
  the header, steps the engine identically, and asserts tick-by-tick serialized
  `StepResult` equality.
- `--out <file>`: Write the re-serialized recording to a file instead of stdout
  (interactive mode only).

`--verify` checks **per-step serialized-result equivalence**, not raw
file-byte identity: `TrajectoryReplay.Verify` rebuilds a fresh simulation from
the header (seed + map + simulation config + `DynamicRules`), feeds each
recorded turn's actions through the identical engine, and compares every
replayed `StepResult`'s JSON serialization against the recorded one. Without
`--verify` the recording is re-serialized to stdout so a caller can inspect or
re-host it; with it, the exit code is `0` only when every recorded tick
reproduces the recorded serialized `StepResult` and every recorded action is
in-space, else a non-zero exit with the divergence on stderr.

Trajectory replay validation enforces tick-by-tick serialized `StepResult`
equivalence against the canonical golden trajectory across the tested Ubuntu,
macOS, and Windows CI matrix (`TrajectoryReplay.Verify`). The repository names
four distinct guarantees and never conflates them:

1. **Engine transition determinism** — under the stated .NET 8 BCL runtime
   contract, the same state plus the same actions yields the same next state.
2. **Per-step serialized `StepResult` replay equivalence** — a replay's
   reconstructed ticks match the recorded ticks' serialized results
   (`TrajectoryReplay.Verify` asserts exactly this).
3. **Same-host normalized JSONL byte identity** — two fresh runs from the same
   seed and actions produce byte-identical files only where line-ending and
   formatting normalization is verified on identical host environments and that
   identity is explicitly tested there.
4. **No canonical simulation-state hash tree currently exists** — replays verify
   serialized `StepResult` equality, never a state digest. The benchmark
   harness's FNV-1a step digest anchors a warm-up iteration as an internal
   repeatability check; it is not a canonical simulation-state hash.

### benchmark — measure the five-case workload matrix

| Flag | Description |
| --- | --- |
| `--runs <n>` | Measured iterations per workload; default 10 |
| `--warmup <n>` | Warm-up budget in ticks (realized as 1–2 full iterations); default 50000 |
| `--steps <n>` | Per-iteration ticks for the raw cases (for smoke passes); raw cases default 100000, the MCTS case keeps its own 100-tick catalog budget |
| `--commit <sha>` | Source revision to record in the JSON artifact (provenance) |
| `--cpu <model>` | CPU model string to record in the JSON artifact (provenance) |
| `--out <file>` | Write the JSON artifact to a file instead of stdout |

```sh
dotnet run --project Cli -- benchmark --runs 10 --warmup 50000 --out benchmarks/throughput_benchmark.json
```

The benchmark runs a reproducible five-case workload matrix through one
harness protocol (see `Analytics/Benchmarking`), not a single hand-picked
sample:

| Case | Map | Roster | What it measures |
| --- | --- | --- | --- |
| `micro_raw_2agent` | 3 zones / 2–5 chokes | 2 seeded `RandomAgent`s | Raw engine stepping |
| `facility_static_4agent` | 10 zones / ≥9 chokes | 2 `GreedyCollectorAgent` + 2 seeded `RandomAgent` | Mixed static facility |
| `dynamic_contention_4agent` | 10 zones | 4 seeded `RandomAgent`s under a `TimedPortcullisRule` + `EventLockedChokeRule` | Dynamic topology |
| `stress_topology_4agent` | 30 zones / ≥29 chokes | 2 `GreedyCollectorAgent` + 2 `ScoutCollectorAgent` | Large-map contention |
| `policy_lookahead_mcts_32` | 3 zones | 2 `MctsAgent`s, 32 rollouts / depth 12 | Rollout-search decisions |

Maps are generated from fixed seeds, so the same catalog drives identical
topologies run over run. The protocol is the same for every case: a JIT-settling warm-up
that anchors an FNV-1a step digest, then `--runs` measured iterations with a
forced GC sweep before each, per-step latency sampled into one histogram
(`Stopwatch.GetTimestamp` per tick), throughput per iteration, managed
allocation via `GC.GetAllocatedBytesForCurrentThread`, and process-wide
`GC.CollectionCount` deltas for Gen0/1/2 across the measured window. Every
measured iteration must reproduce the warm-up anchor's step digest exactly —
if the episode ever went off-script the run fails loudly instead of reporting
timings.

Raw cases report **steps/sec**; the MCTS case reports **decisions/sec** (each
decision runs rolloutsPerAction × depth fork steps). The reference record
committed at `benchmarks/throughput_benchmark.json` was produced on
**Apple M1 / 8 cores / macOS 27.0.0 / .NET 10.0.10 / Release / Workstation
GC** (baseline re-anchored on tree `b7459a1` to a conservative full-protocol
session, so a matching-host pass has real headroom):

| Case | Median throughput | Mean throughput | p50 step lat. | p95 step lat. | Alloc / step |
| --- | --- | --- | --- | --- | --- |
| `micro_raw_2agent` | 654k steps/s | 661k steps/s | 1.33 µs | 1.96 µs | 3.2 KB |
| `facility_static_4agent` | 359k steps/s | 356k steps/s | 2.50 µs | 3.33 µs | 4.6 KB |
| `dynamic_contention_4agent` | 233k steps/s | 229k steps/s | 4.00 µs | 5.33 µs | 7.1 KB |
| `stress_topology_4agent` | 9.1k steps/s | 8.8k steps/s | 100 µs | 151 µs | 104 KB |
| `policy_lookahead_mcts_32` | 394 decisions/s | 382 decisions/s | 4.9 ms | 6.7 ms | 11.5 MB |

Every value above is the exact field `MedianThroughputPerSecond` /
`MeanThroughputPerSecond` / `MedianStepLatencyMicros` / `P95StepLatencyMicros` /
`AllocationsPerStepBytes` in `benchmarks/throughput_benchmark.json`, produced
by one protocol on one host: workload `Workloads[].Name`, statistics median
and mean over 10 measured iterations, host **Apple M1 / 8 cores / 8 GiB RAM /
macOS 27.0.0**, runtime **.NET 10.0.10**, **Release** configuration, Workstation
GC, reference tree **`b7459a1`**.

Two honest notes on what the numbers do and do not say:

- **The stepping core is allocation-light, but not zero-allocation.** Each tick
  the pure step contract returns an immutable `StepResult` (observations,
  rewards, info) and the harness rebuilds per-agent `Observation`s — that is
  measured reality, reported as measured (roughly KB/tick for raw cases, far
  more under stress/MCTS search). The committed record shows only ephemeral
  Gen0-dominated reclamation (Gen2 = 20 over the whole run in raw cases), and
  the regression suite still enforces that allocation grows **linearly**, never
  per-episode. Claiming "0 bytes" would be fabrication; the README reports the
  verified numbers instead.
- **The stress case runs 4 agents, not a nominal 16.** `SimulationConfig`
  validates agent count inclusive 2..4, so Workload D exercises the contract
  ceiling on the 30-zone map rather than an impossible 16 — an honest
  adaptation, not a silent change.

Numbers vary with hardware and build profile; treat the committed record as
host-scoped evidence (its `metadata` block carries the commit, timestamp,
runtime, OS, CPU, cores, RAM, and GC mode) and the CI regression gate
(`.github/workflows/benchmarks.yml`) as the reproducibility check. The gate
installs the same .NET 10 runtime the baseline was recorded under, re-benchmarks
the matrix, and fails on a >20% regression when the host fingerprint (OS
family + architecture + .NET runtime major) matches. On any fingerprint
mismatch it prints a cross-host comparison table instead of failing — a
cross-runtime delta is a measurement artifact, not a regression — and the
bounded cross-host smoke pass (ubuntu x64, .NET 8) is classified structurally
(`--smoke`): the comparator verifies the workload matrix and positive medians
but never adjudicates a throughput ratio from a run with a shortened budget.

### evaluate — mirrored-seat MCTS evidence

| Flag | Description |
| --- | --- |
| `--seed-set <dev\|heldout>` | Canonical suites: `dev` = 1001..1050, `heldout` = 2001..2050 (comma-separate to run both) |
| `--rollouts <n>` | MCTS rollouts per action; default 32 |
| `--seeds <n>` | Cap on seeds per suite (default 50; the decision rule needs ≥ 30) |
| `--scenario <standard\|bottleneck>` | Map topology: `standard` = generated maps (default), `bottleneck` = Seeded procedural contention topology family with capacity-1 choke bottlenecks |
| `--commit <sha>` | Source revision recorded in the artifact |
| `--out <file>` | Write the JSON artifact to a file instead of stdout |

```sh
dotnet run --project Cli -- evaluate --seed-set dev,heldout --rollouts 32 --out benchmarks/mcts_evaluation_results.json
dotnet run --project Cli -- evaluate --seed-set dev,heldout --rollouts 32 --seeds 30 --scenario bottleneck --out benchmarks/bottleneck_evaluation_results.json
```

Runs the empirical evaluation protocol: for every seed, two matches **with
mirrored seats** — MCTS at seat 0 vs the `ScoutCollectorAgent` baseline at
seat 1, and the baseline at seat 0 vs MCTS at seat 1 — on the standard
generated 2-agent / 200-tick map. The per-seed paired delta
Δ = avg((score_MCTS − score_Scout) at seat 0, (score_Scout − score_MCTS) at
seat 1) cancels positional spawn bias, which is why the difference can never
be explained by "who spawned first". The report covers per-seed rows, mean /
median / std / IQR of Δ, a 95% confidence interval for the mean on the
t-distribution, win/draw/loss/timeout rates, mean choke-contention
saturation, and a decision-rule verdict: **pass** only if mean Δ > 0 and the
CI lower bound > 0 (studies under 30 seeds are reported as not graded).
`--out` writes the full artifact; the summary lands on stderr.

### MCTS Empirical Evaluation

Reference result committed at `benchmarks/mcts_evaluation_results.json`
(source revision `5783ca1`, Apple M1 / 8 cores, MCTS budget 32 rollouts ×
depth 12, 2 agents / 200 ticks / transit speed 4, baseline
`ScoutCollectorAgent` with unbounded vision, mirror-seated per seed):

| Suite | Seeds | Mean Δ | 95% CI | Win | Draw | Loss | Timeout | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| dev (1001–1050) | 50 | −1.12 | [−1.37, −0.87] | 15% | 27% | 58% | 0% | FAIL |
| held-out (2001–2050) | 50 | −1.25 | [−1.54, −0.96] | 18% | 14% | 68% | 0% | FAIL |

The 32-rollout MCTS policy **loses the paired comparison to the deterministic
Scout heuristic** on both standard suites (the open generated facility
layouts): the mean paired delta is negative and the
entire 95% CI sits below 0, so the decision rule fails by a wide margin
(~1 to 1.5 resource-equivalents per match; zero timeouts). This is a real,
reproducible finding — every suite run always terminates at
`resources-exhausted` on ≤ 200 ticks, contention stays at 0 on the default
maps (the generated topologies never put both agents on the same claim path),
and the per-seed deltas are reproducible run over run. It is also a
*working verdict*, not a bug: the harness's whole point is that a rollout
budget, map distribution, and baseline family produce evidence; the evidence
currently says the 1-tick scout's back-pressure-aware collection beats this
budget's shallow lookahead.

**This failure is the committed baseline.** It is not a dead-end to be hidden
— it is the reference any future search or learning policy must beat under
the identical protocol (mirror-seated, 32-rollout budget, 2 agents / 200
ticks / transit speed 4, `ScoutCollectorAgent` baseline, the same 50-seed
dev + held-out suites). A policy that clears the rule (mean paired delta &gt; 0
and CI lower bound &gt; 0) on these same suites, seeds, and budget supersedes
this record; the artifact and table below are the before/after comparison.
To challenge the result, raise the budget (`--rollouts 64`), change the map
distribution, or swap the baseline — every run records its own delta, CI, and
verdict.

#### Contention-bearing evaluation (procedural bottleneck maps)

The default generated maps above saturate contention at 0, so they measure
policy *speed*, not policy *pressure*. The paired harness also ships a
seeded procedural contention topology family (`--scenario bottleneck`,
`ProceduralBottleneckGenerator`) that funnels both agents through capacity-1
single-lane chokes into a shared vault. Each trial seed draws a distinct
topology — choke placement and corridor layout, transit geometry, and the
vault's resource count and distribution across one or two capacity-gated
vault zones all vary — while preserving the invariant that both spawn arms are
geometric mirror images, so both agents always reach the shared single-lane
gate on the same tick and actively contend. On these maps, transit denials
(two agents requesting the same capacity-1 choke in one tick) and claim races
are exercised and tracked: `ScenarioMetrics`/`MatchResult` count a tick as
contended on either a same-resource Collect race or a same-capacity-1-choke
transit denial, and `PairedStudyStatistics.MeanContentionSaturation` reports
the mean.

Reference procedural-bottleneck result committed at
`benchmarks/bottleneck_evaluation_results.json` (source revision `1c6fa80`,
Apple M1 / 8 cores / .NET 10.0.10, MCTS budget 32 rollouts × depth 12, 2
agents / 200 ticks / transit speed 4, baseline `ScoutCollectorAgent`, the
first 30 dev + 30 held-out seeds, mirror-seated per seed):

| Suite | Seeds | Mean Δ | 95% CI | Win | Draw | Loss | Timeout | Contention | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| dev (1001–1030) | 30 | +1.42 | [+1.11, +1.73] | 55% | 8% | 37% | 0% | 22% | PASS |
| held-out (2001–2030) | 30 | +1.38 | [+1.07, +1.69] | 53% | 23% | 23% | 0% | 27% | PASS |

Under funnel pressure the result **inverts**: the same 32-rollout MCTS policy
that loses on the empty standard maps *wins* the paired comparison to the Scout
baseline on the procedural bottleneck family — the mean paired delta is
positive and the entire 95% CI sits above 0 on both suites, with non-zero
choke-contention saturation (22–27%) and non-zero statistical dispersion
(stddev ≈ 0.83, IQR = 1). This is a real, reproducible finding of the
environment, not a tuned parameter: rollouts, depth, step budget, and baseline
are identical to the standard suite; only the topology distribution changed.
It does **not** supersede the standard-suite negative baseline — the two are
complementary evidence on different map distributions (empty-map latency vs.
contention-bearing pressure), and a policy must still clear the rule on the
original standard suites to replace that record.

## Design Decisions

The decision records in `/docs` explain *why* the contracts are shaped the way
they are:

- **adr-0001 — Governing product thesis and evidentiary standards.** Why Lattice
  is an instrument for reproducible research, how the five evidentiary
  principles (auditability, policies as evaluation subjects, negative results,
  empirical provenance, contractual terminology) gate feature intake, and which
  claims need which committed evidence. See
  `docs/adr/0001-governing-product-thesis.md`.
- **adr-001 — MapGraph is a graph over zones, not a grid.** Checkers and
  capacity/transit logic operate on nodes and edges; coordinates are an
  optional embedding.
- **adr-002 — Simultaneous actions, two-phase resolution; full-observer step
  contract.** Includes addenda on perception filtering as a projection,
  kinematic transit and capacity, immutable forking, and mirrored fairness
  seatings.
- **adr-003 — Spawn fairness is measured with mirrored seatings.** Why spawn
  bias uses position swaps and territory attribution, and why fairness gates
  are injected into the generator as a caller-supplied delegate.