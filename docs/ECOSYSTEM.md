# Lattice in an Ecosystem

Lattice is a headless simulation substrate, not an engine: it owns the tick,
the map, and the recording, and every other system in a game pipeline — Unity,
Godot, a server-side match service, an RL harness — owns itself. This page is
the integration guide: how to embed, what contracts to consume, and how the
project presents itself in broader collections.

## The seam that makes embedding cheap

Every exchange with the environment happens through four immutable records and
one pure function:

- `AgentAction(Kind, ZoneId, ResourceId)` — what one agent asks to do.
- `Observation(AgentId, Map, AgentStates, Claims)` — everything an agent can
  see this tick.
- `StepResult(Observations, Rewards, Info)` — one tick's full outcome.
- `Simulation.Step(state, actions, config) -> StepOutcome` — the only mutation
  of the world, and it takes an immutable `SimulationState` and returns a fresh
  one. Nothing inside `Lattice.Environment` touches the clock, the file
  system, randomness, or any global.

So a "game" using Lattice is just a loop:

```
state = Simulation.CreateInitial(map, config)
while not terminal:
    actions = [policy_i.decide(state) for policy_i in policies]
    state, result = Simulation.Step(state, actions, config)
```

The host supplies the policies, the termination policy is already in `Info`
(resources exhausted or tick budget), and the trajectory writer turns each
`StepResult` into exactly one JSONL line. There is no callback, no event
system, and nothing to unsubscribe from.

## Unity integration

- Reference `Lattice.Environment`, `Lattice.Generator`, and
  `Lattice.Trajectories` from an assembly; they are BCL-only, so they do not
  pull UnityEngine (or anything else) into the runtime.
- Run `Simulation.Step` anywhere — the core never touches `Time`, so tick
  cadence and frame rate are decoupled. Freeze/pause/replay are pure edits to
  which `Step` calls you issue, and there is no per-frame state to resync.
- Drive `Transform`s from `AgentState.ZoneId`/`Position` when a transit
  completes; mid-crossing frames read `InTransit` for interpolation. The
  position lattice (`GridPoint`) is the only geometry the core guarantees; let
  the renderer own whatever smoothing it wants.
- Ship deterministic rules to the client and let it predict. Because a seeded
  run on the same `SimulationConfig` produces byte-identical trajectories,
  replays, deathcams, and ghost data are exactly the recorded action bytes —
  no simulation traffic, no snapshots.
- Generate maps offline (or at build time) with the seeded generator, gate
  them with the fairness evaluator, and load them as `MapGraph` from the same
  JSON the CLI writes.

## Godot integration

The .NET build of Godot consumes Lattice the same way Unity does: project
references to the BCL-only core projects, a `Step` loop driven by a
`Timer`/`Process` of your choosing rather than the engine's physics tick, and
the SVG exporter (`Cli render --format svg`) for a zero-dependency browser-style
replay if you want a chart rather than a node tree.

## Server-side and batch use

`Lattice.Trajectories` and `Lattice.Analytics` are plain-file tooling: write
JSONL to disk, analyze byte-identical reports, run CI gates. The CLI is
scriptable (`generate`/`simulate`/`render`/`analyze`/`benchmark`/`evaluate`)
and every command is seeded, so a pipeline can regenerate and diff. There is no database,
service, or daemon layer to provision; a batch run is a shell loop.

## Presentations to broader collections

If you catalog Lattice somewhere community-facing, the same framing is used
everywhere in this repository:

- **awesome-game-ai / tactical-ai:** a deterministic, seedable tactical
  substrate with fog-of-war perception, capacity-gated terrain, and MCTS —
  the environment, not agents, is the product.
- **awesome-dotnet:** C# / .NET 8, nullable enabled, zero external *runtime*
  dependencies across every production assembly (pure BCL; only the test
  project references packages — `Microsoft.NET.Test.Sdk` and `xUnit`); step
  contracts are plain records.
- **awesome-procedural-generation:** a hard-constraint map generator with
  explicit checker functions, per-constraint unit tests, and a retry loop that
  rejects rather than patches; an optional acceptance gate bakes spawn-fairness
  measurement directly into generation.

Keep the pitch concrete (determinism is asserted, not assumed; the production
dependency graph is the BCL alone) and it will survive scrutiny — everything
above is enforced by
tests in `/Tests`.

## What this subsystem deliberately does not do

- It is not a networking or lockstep-transport layer. Determinism is a
  property the *host* preserves by calling the pure core; the repo does not
  ship a wire format for the state itself (trajectories record actions, and
  replay re-runs `Step`).
- The contiguous rendering of 3D scenes, ECS frameworks, and animation
  pipelines live outside the substrate. `PerceptionFilter` and
  `AgentBeliefMap` model *information*, not the raster behind it — a host that
  needs image-space sensing computes that itself and feeds the result through
  whatever contract it owns.
- Determinism is defined across machines for *identical* `SimulationConfig`
  inputs. Callers who mutate the config, hand agents ambient randomness, or
  patch maps after generation are outside the guarantee.