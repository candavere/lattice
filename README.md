# Lattice

A deterministic, seedable simulation substrate for tactical/strategic game AI
design and systems engineering. Lattice runs headless and graphics-free: it
provides a pure simulation core, a seeded procedural map generator, and a
recording/reporting toolchain. Agents are a thin demonstration layer — the
environment is the product.

The core guarantee is repeatability: **same seed, same actions, same bytes.**
Every run on every machine reproduces an identical trajectory, because the
simulation has no hidden state, no singletons, and no ambient randomness. The
core libraries depend only on the .NET base class library.

## Architecture & Modules

```
/Environment    Step contracts + pure reset/step core (Observation, AgentAction,
                Reward, Info, StepResult, MapGraph, Simulation); spatial capacity,
                kinematic transit (InTransit), PerceptionFilter with
                Observed/Stale/Unknown projections for partial observability
/Generator      Seeded procedural map generator with hard-constraint checkers
                (retry, don't patch); optional caller-supplied acceptance gate
/Agents         Rule-based and tactical agents (Random, GreedyCollector,
                ScoutCollector, MctsAgent) + AgentBeliefMap + scenario runner
/Trajectories   JSONL trajectory read/write/replay/step utilities
/Analytics      Trajectory analysis (contention, pathing efficiency, heatmaps,
                Markdown reports) + MapFairnessEvaluator spawn-bias profiling
/Visualization  ASCII renderer + zero-dependency CSS-animated SVG exporter
/Cli            Driver: generate / simulate / render / analyze / benchmark
/Tests          Unit, determinism, replay, and benchmark tests (one per module)
/docs           ADR-style design-decision records (adr-001, adr-002, adr-003)
```

The behavioral contracts of each subsystem are documented as architecture
decision records in `/docs`; see [Design Decisions](#design-decisions).

## Key Mechanics

### Step contracts and replay determinism

Each tick takes one `AgentAction` per agent and produces an immutable
`StepResult`. Actions resolve in two fixed phases: all moves, then all
collects, each in ascending agent id. Invalid or missing actions degrade to
`Wait`, so every action array yields a valid next state and no agent can
crash or wedge the simulation. `Observation`, `Reward`, `Info`, and `StepResult`
are plain immutable records that serialize to JSONL without interpretation
logic; a recording is replayed by re-running the exact recorded action bytes.

### Topological graph space with capacity gates

Maps are graphs, not grids. `MapGraph` holds zones (nodes), choke points
(edges), and resources. Connectivity and degree are first-class, so every
validation checker and the spatial simulator reason about the same structure
(see adr-001). Zones and choke points carry a `MaxOccupancy` (default
unlimited; `0` = impassable), enforced as same-tick entry gates triaged in
ascending agent id. A zone is just a graph node with an optional position —
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
   belongs to the environment and tooling.
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
| `--out <file>`  | Write trajectory to a file instead of stdout |

```sh
dotnet run --project Cli -- simulate --seed 42
dotnet run --project Cli -- simulate --seed 42 --steps 40
dotnet run --project Cli -- simulate --seed 42 --steps 40 --agent mcts
```

Runs `GreedyCollectorAgent` vs `RandomAgent` and writes the trajectory (header,
one line per step, final metrics line) to stdout or file. A summary line —
steps recorded, termination reason, winner — goes to stderr.

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