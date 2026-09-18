# Lattice
<p align="center">
  <img src="assets/lattice-logo.svg" alt="LATTICE Logo" width="140" height="140" />
</p>

<h1 align="center">LATTICE</h1>

<p align="center">
  <strong>Deterministic Dec-POMDP Tactical Simulation Engine</strong>
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
  <img src="https://img.shields.io/badge/tests-344%20passing-brightgreen" alt="344 unit tests passing" />
  <img src="https://img.shields.io/badge/determinism-byte--identical-blue" alt="byte-identical determinism" />
  <img src="https://img.shields.io/badge/dependencies-BCL%20runtime%20only-blueviolet" alt="runtime dependencies: pure .NET 8 BCL" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8" />
  <a href="https://candavere.github.io/lattice/"><img src="https://img.shields.io/badge/live%20demo-GitHub%20Pages-2ea44f" alt="live demo" /></a>
</p>

> A deterministic, headless 2D tactical AI simulation substrate in pure C#
> (.NET 8). Built to validate, balance, and stress-test high-level game AI
> architectures (MCTS, Fog-of-War perception, procedural map fairness, and a
> tactical Dungeon Infiltration & Sentry Patrol scenario) at 0.9–3.5M
> single-threaded steps/second before game engine integration.

Think of Lattice as a digital board game engine running in memory without
graphics: units traverse a network of connected topological outposts over
multiple turns, competing for resources under Fog-of-War. Because every
transition is calculated using pure math rather than approximate continuous
physics, simulations run at millions of turns per second (measured mean
0.9–3.5M steps/s on a 2020 Apple M1) with byte-for-byte identical replay
across any platform.

<!--
Proposed GitHub topics for the maintainer (set these in the repo settings):
game-ai, tactical-ai, game-development, determinism, mcts, headless-simulation,
dotnet8, procedural-generation, fog-of-war, simulation-engine
-->

A deterministic game AI simulation in pure C#, built for tactical/strategic
systems design and engineering. Lattice is a headless tactical combat engine
for .NET: it runs graphics-free, has no engine or ML runtime dependencies
(pure .NET 8 BCL across every production assembly — development and test
projects rely exclusively on .NET, Microsoft.NET.Test.Sdk, and xUnit),
and ships a pure step-contract simulation core, a seeded procedural map
generator, a procedural map balance and spawn fairness tester, and a
recording/reporting toolchain. Agents are a thin demonstration layer — the
environment is the product.

## Who Is This For?

- **Game Designers & Systems Engineers:** Pre-balance procedural map seeds,
  detect choke-point congestion, and evaluate layout fairness before building
  3D environments.
- **AI & Systems Researchers:** Benchmark lookahead planners (MCTS, custom
  heuristics) under verifiable partial observability without state leakage.
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

- **BCL-only:** Base Class Library only; runs strictly on core standard .NET
  with zero external NuGet packages.
- **Headless:** Operates without a window, GPU context, or graphics thread,
  optimized for automated CI and high-speed batch evaluation.
- **Determinism:** Given the same seed and action sequence, simulations produce
  bit-for-bit identical state transitions across Windows, Linux, and macOS
  runtimes.
- **Topological Graph:** An environment modeled as discrete interconnected
  nodes (zones) and capacity-limited edges (chokes) rather than a continuous
  floating-point coordinate space.

</details>

Turn-based tactical play is a zero-dependency C# game state machine: every tick
is a pure function of the previous state and the recorded actions. The core
guarantee is repeatability — **same seed, same actions, same bytes.** Every run
on every machine reproduces an identical trajectory, because the simulation has
no hidden state, no singletons, and no ambient randomness. The built-in Monte
Carlo Tree Search (MCTS) agent demonstrates exactly this contract, pricing
candidate actions with deterministic BFS rollouts against the same pure `Step`
used by every other policy.

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

Every command is seeded, so identical arguments reproduce identical bytes on
any machine. The full command set is `generate`, `simulate`, `render`,
`analyze`, and `benchmark` — see the [CLI Reference](#cli-reference) below.

## Architecture & Modules

| Directory | Responsibilities | Key Architectural Types |
| :--- | :--- | :--- |
| `/Environment` | Pure step-contract core — reset/step, spatial capacity, kinematic transit, perception projection, dungeon topology | `MapGraph`, `Observation`, `AgentAction`, `StepResult`, `PerceptionFilter`, `InTransit`, `Simulation`, `DungeonMapBuilder`, `DungeonRoles` |
| `/Generator` | Seeded procedural map generation with hard-constraint checkers (retry, don't patch); optional caller-supplied acceptance gate | `MapGenerator`, `ConstraintCheckers`, `MapGenerationException` |
| `/Agents` | Rule-based and tactical agent policies, belief maps, scenario runner | `IAgent`, `RandomAgent`, `GreedyCollectorAgent`, `ScoutCollectorAgent`, `MctsAgent`, `SentryPatrolAgent`, `InfiltratorAgent`, `AgentBeliefMap`, `ScenarioRunner`, `InfiltrationScenario` |
| `/Trajectories` | JSONL trajectory read/write/replay/step utilities | `TrajectoryModel`, `TrajectoryWriter`, `TrajectoryReader`, `TrajectoryReplay` |
| `/Analytics` | Trajectory analysis — contention, pathing efficiency, heatmaps, Markdown reports; spawn-bias fairness profiling; the five-case reproducibility benchmark harness | `TrajectoryAnalyzer`, `IncidentDetector`, `CounterfactualEvaluator`, `MapFairnessEvaluator`, `ReportGenerator`, `MapTraversal`, `WorkloadCatalog`, `BenchmarkHarness` |
| `/Visualization` | ASCII terminal renderer + dependency-free CSS-animated SVG exporter | `AsciiRenderer`, `SvgRenderer`, `SvgViewport`, `SvgTrajectoryExporter`, `TrajectoryPlayback` |
| `/Cli` | Driver: `generate` / `simulate` / `render` / `analyze` / `benchmark` / `evaluate` | `CliApp`, `Program` |
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
plain data, so mid-crossing frames record and replay byte-identically.
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
byte-identical replay verification the CLI runs over every recorded episode.
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

Identical parameters always reproduce identical trajectories — same room
sequence, same choke contention, same final verdict.

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
2. **Determinism first.** Same seed + same actions → byte-identical
   trajectory, asserted by a permanent determinism test suite.
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
replay a fixed script, and assert byte-identical trajectory JSON across runs.

## CLI Reference

`Lattice.Cli` exposes six commands (`dotnet run --project Cli -- <command>
...`, binary name `lattice`). **Every command is seeded** — identical
arguments always produce identical bytes. Exit status is `0` on success,
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
| `--agent <greedy\|random\|mcts>` | Policy for player 0 (default `greedy`); `mcts` is the rollout-based tactical agent |
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
verified byte-for-byte when the episode is re-run (`byte-identical replay
verified`).

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
(invariant culture, byte-identical).

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

Maps are generated from fixed seeds, so every host benchmarks the exact same
topologies. The protocol is the same for every case: a JIT-settling warm-up
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
GC** at commit `0ce60b8`:

| Case | Median throughput | p50 step lat. | p95 step lat. | Alloc / step |
| --- | --- | --- | --- | --- |
| `micro_raw_2agent` | 769k steps/s | 1.17 µs | 1.46 µs | 3.2 KB |
| `facility_static_4agent` | 405k steps/s | 2.21 µs | 3.92 µs | 4.6 KB |
| `dynamic_contention_4agent` | 243k steps/s | 3.79 µs | 6.42 µs | 7.1 KB |
| `stress_topology_4agent` | 9.0k steps/s | 100 µs | 146 µs | 104 KB |
| `policy_lookahead_mcts_32` | 463 decisions/s | 4.2 ms | 7.0 ms | 11.5 MB |

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
(`.github/workflows/benchmarks.yml`) as the reproducibility check — it
re-benchmarks the matrix and fails on a >20% regression against the committed
baseline when the host fingerprint matches, and shows a cross-host comparison
table otherwise.

### evaluate — mirrored-seat MCTS evidence

| Flag | Description |
| --- | --- |
| `--seed-set <dev\|heldout>` | Canonical suites: `dev` = 1001..1050, `heldout` = 2001..2050 (comma-separate to run both) |
| `--rollouts <n>` | MCTS rollouts per action; default 32 |
| `--seeds <n>` | Cap on seeds per suite (default 50; the decision rule needs ≥ 30) |
| `--commit <sha>` | Source revision recorded in the artifact |
| `--out <file>` | Write the JSON artifact to a file instead of stdout |

```sh
dotnet run --project Cli -- evaluate --seed-set dev,heldout --rollouts 32 --out benchmarks/mcts_evaluation_results.json
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
Scout heuristic** on both suites: the mean paired delta is negative and the
entire 95% CI sits below 0, so the decision rule fails by a wide margin
(~1 to 1.5 resource-equivalents per match; zero timeouts). This is a real,
reproducible finding — every suite run always terminates at
`resources-exhausted` on ≤ 200 ticks, contention stays at 0 (the maps never
put both agents on the same claim path), and the per-seed deltas repeat
byte-for-byte across runs. It is also a *working verdict*, not a bug: the
harness's whole point is that a rollout budget, map distribution, and
baseline family produce evidence; the evidence currently says the 1-tick
scout's back-pressure-aware collection beats this budget's shallow lookahead.
To challenge the result, raise the budget (`--rollouts 64`, the CLI's next
canonical config), change the map distribution, or swap the baseline — the
artifact and README table are the before/after record.

## Design Decisions

The decision records in `/docs` explain *why* the contracts are shaped the way
they are:

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