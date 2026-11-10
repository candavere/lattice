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
  run on the same `SimulationConfig` produces deterministic trajectories,
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
JSONL to disk, analyze reports, and rely on verified replay equivalence at CI
gates. The CLI is scriptable
(`generate`/`simulate`/`render`/`analyze`/`benchmark`/`evaluate`)
and every command is seeded, so a pipeline can regenerate and diff. There is no database,
service, or daemon layer to provision; a batch run is a shell loop.

## Reinforcement Learning & Simulation Interface Mapping

The step-contract surface maps almost one-to-one onto the classic RL loop:

| Lattice (.NET 10 C#) | Gymnasium / PettingZoo Concept | Architectural Role |
| :--- | :--- | :--- |
| `Simulation.Step(actions)` | `env.step(actions)` | Advances active simulation state by exactly one tick |
| `Observation` | `observation` | Agent's egocentric, hop-bounded partial sensor horizon |
| `SimulationFork` | `copy.deepcopy(env)` | Allocation-conscious, immutable counterfactual rollout sandbox |
| `AgentAction` | `action` | Strongly-typed discrete action (`Move`, `Collect`, `Wait`) |
| `MapFairnessEvaluator` | N/A (Procedural Benchmark) | Automated symmetric seat-inversion balance profiler |

A host RL loop consumes the same four records (`Simulation.Step` returns a
fresh `SimulationState` plus `Observations`, `Rewards`, `Info`), so a trainer
sees the classic `obs, reward, done, info` surface while the environment keeps
its determinism, replay, and contention contracts.

## Presentations to broader collections

If you catalog Lattice somewhere community-facing, the same framing is used
everywhere in this repository:

- **awesome-game-ai / tactical-ai:** a deterministic, seedable tactical
  substrate with fog-of-war perception, capacity-gated terrain, and MCTS —
  the environment, not agents, is the product.
- **awesome-dotnet:** C# / .NET 10, nullable enabled, zero external *runtime*
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

## Architecture and data flow

The data flow, drawn from the committed tools:

```
simulate -> recording (.jsonl) -> replay --verify / render / analyze / site
benchmark -> throughput JSON -> throughput gate
evaluate  -> evaluation JSON   evaluate --agent-cmd -> your agent
```

The full diagram is [`architecture.svg`](architecture.svg), and a fixed-size
raster of the same rendering is [`architecture.png`](architecture.png):

<p align="center">
  <img src="architecture.svg" alt="Lattice architecture, drawn from the committed tools. A seeded lattice simulate run writes one schema 5 JSONL recording, and four readers consume it: lattice replay --verify, lattice render, lattice analyze, and the site replay viewer. The golden fixture is itself a recording, so the test suite pins the reader, and CI runs that suite on Ubuntu, Windows and macOS. Independently of any recording, lattice benchmark writes a host-scoped throughput JSON that is gated only on a full host match, and lattice evaluate writes an evaluation JSON from a mirrored-seat paired study of MCTS against the Scout baseline. Seated instead of the in-process MCTS candidate, lattice evaluate --agent-cmd runs an external agent process of any language, in a separate OS process, that speaks protocol 1 over stdin and stdout." />
</p>

Where each box in that diagram is implemented, at the commit that introduced
this table:

| Box | Implementation |
| --- | --- |
| `lattice simulate` | `Cli/CliApp.cs:219`; the header is stamped with the current schema at `Trajectories/TrajectoryWriter.cs:117` |
| `recording .jsonl` (schema 5) | `Trajectories/TrajectoryModel.cs`; one header line, one line per tick, one final line. Schema 5 adds the header's `ScenarioSha256`, the digest of the scenario descriptor's exact bytes; schema 4's decision-time `Perceptions` and `AgentVision` and schema 3's per-tick state hash are unchanged |
| `lattice replay --verify` | `Cli/CliApp.cs:222`; per-tick result, state-digest and decision-time perception comparison at `Trajectories/TrajectoryReplay.cs` |
| `lattice render` | `Cli/CliApp.cs:220`, over `Visualization/` |
| `lattice analyze` | `Cli/CliApp.cs:221`, over `Analytics/` |
| `site/ replay viewer` | `site/app.js`, deployed by `.github/workflows/pages.yml` |
| `lattice benchmark` | `Cli/CliApp.cs:223`; five workloads on fresh simulations, never on a recording |
| `throughput JSON` | the host is sampled for the artifact at `Analytics/Benchmarking/BenchmarkMetadata.cs:53`, so the record names its own commit, runtime, OS, CPU, cores and RAM |
| `throughput gate` | the strict gate arms only on a full host match, at `.github/workflows/compare_benchmarks.py:518-543`; which hosted-runner workloads are armed, and which is a permanent non-verdict, is stated once in [`FINDING-014`](FINDINGS_LEDGER.md#finding-014--the-mcts-decision-throughput-case-cannot-be-gated-on-this-host-class-and-four-of-five-workloads-can) |
| `lattice evaluate` | `Cli/CliApp.cs:224`; the pass/fail verdict is composed at `Agents/PairedEvaluation.cs:145-150` |
| `evaluation JSON` | the per-seed rows and statistics are written at `Cli/CliApp.cs:1450`; an external agent's failures are tallied per reason code at `Cli/CliApp.cs:1416` |
| `lattice evaluate --agent-cmd` | the candidate seat, with the command line split without a shell at `Cli/AgentCommandLine.cs:49`; the child process is launched from `Agents/External/ExternalAgentLaunch.cs:84` |
| `your agent, any language` | a separate OS process reached only over protocol 1; a CR anywhere in a line is refused as `malformed_json` at `Protocol/ProtocolFraming.cs:65` |
| `tests + golden fixture` → `CI on 3 operating systems` | the golden-trajectory replay gate runs on every matrix OS at `.github/workflows/ci.yml:42-43`; every committed `site/*.jsonl` is re-verified by the gate at `.github/workflows/ci.yml:221-236` |

The viewer reports what a recording actually supports, and distinguishes the
three counts that are easy to conflate: `claimed in world`, the observer's own
`claims seen` (schema 4), and `claims in derived view` where nothing was
recorded. A frame with no recorded perception reports no count rather than
inventing one, and zone-table presence follows the painted ego view. The
detailed table of claim and presence semantics, and the pixel and geometry
tests that pin them, are in
[`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md#viewer-claim-semantics).

## Repository layout

- `Cli/` - the `lattice` command surface: `generate`, `simulate`, `render`,
  `analyze`, `replay`, `benchmark`, `evaluate`.
- `Environment/` - the pure step contract: `Simulation`, `PerceptionFilter`,
  dynamic topology rules.
- `Generator/` - seeded procedural maps with a caller-supplied fairness gate.
- `Agents/` - evaluation subjects (MCTS, Scout), the mirrored-seat paired
  evaluation, and the external-agent match runner.
- `Protocol/` - the external-agent wire types, a leaf that references nothing in
  this repository.
- `Trajectories/` - the JSONL recording model, writer, reader, and replay
  verifier.
- `Analytics/` - the benchmark harness and analysis.
- `Visualization/` - the SVG/ASCII renderer.
- `Tests/` - unit, determinism, property, and fuzz suites plus the golden
  fixtures.
- `benchmarks/` - the committed JSON artifacts every number above comes from.
- `docs/` - ADRs, the invariant specification, protocols, the findings ledger,
  and the claim calibration matrix.
- `examples/` - worked external agents; `examples/python/` is a conformant
  standard-library agent.
- `site/` - the browser replay viewer deployed to GitHub Pages; it reads
  committed `.jsonl` recordings.
- `ui_tests/` - the tracked Playwright regression for the demo page.

The engine mechanics and their enforcement tests are in
[`MECHANICS.md`](MECHANICS.md). The command surface is in
[`CLI.md`](CLI.md).

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
- Determinism is defined for *identical* `SimulationConfig` inputs as
  transition determinism under the specified runtime contract plus per-step
  serialized `StepResult` replay equivalence across the tested CI platforms.
  Callers who mutate the config, hand agents ambient randomness, or patch maps
  after generation are outside the guarantee.