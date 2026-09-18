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
  <img src="https://img.shields.io/badge/tests-323%20passing-brightgreen" alt="323 unit tests passing" />
  <img src="https://img.shields.io/badge/determinism-byte--identical-blue" alt="byte-identical determinism" />
  <img src="https://img.shields.io/badge/dependencies-BCL%20only-blueviolet" alt="zero dependencies — BCL only" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8" />
  <a href="https://candavere.github.io/lattice/"><img src="https://img.shields.io/badge/live%20demo-GitHub%20Pages-2ea44f" alt="live demo" /></a>
</p>

> A deterministic, headless 2D tactical AI simulation substrate in pure C#
> (.NET 8). Built to validate, balance, and stress-test high-level game AI
> architectures (MCTS, Fog-of-War perception, procedural map fairness, and a
> tactical Dungeon Infiltration & Sentry Patrol scenario) at >400k steps/second
> before game engine integration.

Think of Lattice as a digital board game engine running in memory without
graphics: units traverse a network of connected topological outposts over
multiple turns, competing for resources under Fog-of-War. Because every
transition is calculated using pure math rather than approximate continuous
physics, simulations run at 400,000+ turns per second with byte-for-byte
identical replay across any platform.

<!--
Proposed GitHub topics for the maintainer (set these in the repo settings):
game-ai, tactical-ai, game-development, determinism, mcts, headless-simulation,
dotnet8, procedural-generation, fog-of-war, simulation-engine
-->

A deterministic game AI simulation in pure C#, built for tactical/strategic
systems design and engineering. Lattice is a headless tactical combat engine
for .NET: it runs graphics-free, has zero engine or ML dependencies (BCL only),
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
| `/Analytics` | Trajectory analysis — contention, pathing efficiency, heatmaps, Markdown reports; spawn-bias fairness profiling | `TrajectoryAnalyzer`, `IncidentDetector`, `CounterfactualEvaluator`, `MapFairnessEvaluator`, `ReportGenerator`, `MapTraversal` |
| `/Visualization` | ASCII terminal renderer + dependency-free CSS-animated SVG exporter | `AsciiRenderer`, `SvgRenderer`, `SvgViewport`, `SvgTrajectoryExporter`, `TrajectoryPlayback` |
| `/Cli` | Driver: `generate` / `simulate` / `render` / `analyze` / `benchmark` | `CliApp`, `Benchmark`, `Program` |
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

`Lattice.Cli` exposes five commands (`dotnet run --project Cli -- <command>
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
| `--out <file>`  | Write trajectory to a file instead of stdout |

```sh
dotnet run --project Cli -- simulate --seed 42
dotnet run --project Cli -- simulate --seed 42 --steps 40
dotnet run --project Cli -- simulate --seed 42 --steps 40 --agent mcts
dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl
```

Without `--scenario`, runs `GreedyCollectorAgent` vs `RandomAgent` on a
procedurally generated map. With `--scenario infiltration`, the fixed
dungeon is used and the roster is hard-wired to Sentry vs Infiltrator. A
summary line — steps recorded, termination reason, outcome — goes to
stderr.

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

### benchmark — measure core throughput

| Flag | Description |
| --- | --- |
| `--ticks <n>` | Simulation ticks to pump; default 1000 |

```sh
dotnet run --project Cli -- benchmark --ticks 1000
```

Measures a pure `Simulation.Step` loop with no agent, episode, or
serialization overhead:

```
ticks=1000
elapsed_ms=2.306
steps_per_second=433621.9
allocated_bytes=704192
bytes_per_tick=704.2
```

The printed figure is a sample from a typical developer workstation; the
`>400k steps/second` tagline reflects this workload class (roughly
half a million pure steps per second, single-threaded, on the .NET 8
runtime). Absolute numbers vary with hardware and build profile — the
guarantee the benchmark pins is not a headroom claim but that the loop
is low-allocation (no per-step logging or serialization in the hot path)
and never touches the disk or a network until the caller asks it to.

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