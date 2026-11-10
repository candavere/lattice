# Results and negative results

This page holds the detailed results behind the README headline. Each row is one
committed benchmark claim, with the artifact behind it. The artifacts, not this
page, are the authority for every number.

The short version: the committed 32-rollout MCTS study **loses** to a
deterministic Scout heuristic on standard generated maps, and the **same policy
wins** when both agents are funneled through capacity-1 chokepoints into one
shared vault. The loss is the committed negative baseline; the win is
complementary evidence on a different map distribution. Neither verdict
describes the policy uniformly.

## Committed results

| Finding | Artifact |
| :--- | :--- |
| 32-rollout MCTS loses to the deterministic Scout heuristic on standard generated maps: mean paired delta -1.12, 95% CI [-1.37, -0.87] on the dev suite. This loss is the committed negative baseline. | [`benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json) |
| The same 32-rollout MCTS policy wins when both agents are funneled through capacity-1 chokepoints into one shared vault: +2.03, CI [+1.66, +2.41] on the dev suite. Topology changed the conclusion. | [`benchmarks/bottleneck_evaluation_results.json`](../benchmarks/bottleneck_evaluation_results.json) |
| 99.61% scoped mutation score on `Simulation.cs` + `PerceptionFilter.cs` only (252 killed / 1 timed out / 1 survived / 0 no-coverage). A historical local calibration at `053f3eb`, not a rerun at the current head, not whole-repository coverage, and not a CI gate. | [`benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json) |
| Five-workload throughput record on one host (tree `41530ef`, recorded in the artifact): median 14,916 to 899,075 steps/s depending on workload (the MCTS case reports decisions/s). Speed is measured, not advertised. | [`benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json), [`benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md) |

## The standard study: a committed loss

The reference result for the standard study is
[`benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json):
source revision `5783ca1`, .NET 10.0.10, MCTS 32 rollouts x depth 12, 2 agents /
200 ticks / transit speed 4, baseline `ScoutCollectorAgent` with unbounded
vision, mirror-seated per seed. The protocol facts are reproduced by
`Cli/CliApp.cs` (evaluation config) and `Agents/MctsAgent.cs` (default search
depth).

**dev (1001-1050)**
- Seeds: 50
- Mean delta: -1.12
- 95% CI: [-1.37, -0.87]
- Win: 15%
- Draw: 27%
- Loss: 58%
- Timeout: 0%
- Verdict: **FAIL**

**held-out (2001-2050)**
- Seeds: 50
- Mean delta: -1.25
- 95% CI: [-1.54, -0.96]
- Win: 18%
- Draw: 14%
- Loss: 68%
- Timeout: 0%
- Verdict: **FAIL**

The entire 95% CI sits below 0 on both suites, so the decision rule fails by a
wide margin. Every suite run terminates at `resources-exhausted` on at most 200
ticks; contention stays at 0, because generated maps never put both agents on the
same claim path. This measures policy speed on open layouts: the scout's
back-pressure-aware collection beats this budget's shallow lookahead.

**This failure is the committed baseline.** Any future search or learning policy
must clear the rule (mean paired delta > 0 and CI lower bound > 0) on the same
suites, seeds, and budget to supersede it. To challenge the result: raise the
budget (`--rollouts 64`), change the map distribution, or swap the baseline, and
every run records its own delta, CI, and verdict.

## The bottleneck study: the same policy, the other verdict

The reference result for the bottleneck study is
[`benchmarks/bottleneck_evaluation_results.json`](../benchmarks/bottleneck_evaluation_results.json):
source revision `28c89d3`, identical policy budget and baseline, procedural
contention topology family, first 30 dev + 30 held-out seeds.

**dev (1001-1030)**
- Seeds: 30
- Mean delta: +2.03
- 95% CI: [+1.66, +2.41]
- Win: 50%
- Draw: 3%
- Loss: 47%
- Timeout: 0%
- Contention: 16%
- Verdict: **PASS**

**held-out (2001-2030)**
- Seeds: 30
- Mean delta: +2.60
- 95% CI: [+2.20, +3.00]
- Win: 50%
- Draw: 18%
- Loss: 32%
- Timeout: 0%
- Contention: 16%
- Verdict: **PASS**

Each seed draws a distinct topology, while both spawn arms stay geometric mirror
images, so both agents reach the shared single-lane gate on the same tick and
actively contend. Under that pressure the same MCTS budget wins. Relative to the
pre-fix record (measured at `1c6fa80`), the re-anchored run's mean delta rose
while its match win rate fell: dev 0.55 → 0.50 (losses 22 → 28) and held-out
0.533 → 0.50 (losses 14 → 19), of 60 matches each, read from the artifacts. This
does **not** supersede the standard-suite negative baseline; the two are
complementary evidence on different map distributions.

## Performance, qualified

The committed
[`benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json)
records five workloads — raw stepping, mixed static facility, dynamic contention,
stress topology, and MCTS decisions — on a single reference host (.NET 10.0.12
Release, Workstation GC, tree `41530ef`, baseline re-anchored to a conservative
full-protocol session on the current runtime). Numbers come from a single
reference host; its full metadata (CPU, cores, OS, runtime) is recorded in the
artifact. Median throughput spans from 14,916 steps/s on the 30-zone stress case
to 899,075 steps/s on the micro case; the MCTS case reports decisions/s.

The per-workload latency and allocation breakdowns, the protocol, and the honest
reading notes are in [`benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md)
and [`docs/BENCHMARKING.md`](BENCHMARKING.md). Numbers vary with hardware and
build profile.

## Negative results, recorded rather than smoothed

Only claims the repository can back up are listed here.

- **The standard-suite loss is committed, not a bug.** The 32-rollout MCTS
  policy loses to the deterministic Scout heuristic on standard generated maps
  ([`benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json)).
  It is the baseline any future policy must beat under the identical protocol.
- **What the site calls "fog" is now recorded, for the infiltration run.**
  [`site/infiltration.jsonl`](../site/infiltration.jsonl) is trajectory schema
  v5: every step line carries a `Perceptions` entry per agent, holding the masked
  view that agent's *own* `PerceptionFilter` produced inside its `Decide` call at
  that tick, and the header declares the cone each filter was built with
  (`AgentVision: [2, 2]`, two graph hops apiece). The viewer draws that record,
  labels it "recorded perception", and `replay --verify` reprojects all 20 of
  them and fails on any edit. The core's `Vision` stays unbounded (`Vision = -1`
  in the header) by design — the fog belongs to the agents, not the simulation.
  The older recording still has no fog field, because it is pinned at schema v3:
  [`site/demo.jsonl`](../site/demo.jsonl) predates the perception field, so the
  viewer reconstructs its sightline from recorded positions with a 2-hop rule and
  labels that "reconstructed sightline" instead. The two chips are the honest
  answer to "where did this come from?" on a page that shows both.
- **The recorded fog's own tick disclosures.** Derived from
  [`site/infiltration.jsonl`](../site/infiltration.jsonl) (seed 42, 20 steps,
  `AgentVision [2, 2]`), reading `Perceptions` and nothing else. On the
  Infiltrator's view the Treasure Vault (zone 5) is not `Observed` at frame 0
  (`Unknown`), 9 (`Stale`) and 19 (`Stale`), and the terminal frame 20 shows the
  last decision-time view — step 20, where the vault is still `Stale`. On the
  Sentry's view the vault is not observed only at frames 0 and 1; what the guard
  actually loses is its own post and the entry hall, at frames 15 and 16, and at
  its final decision-time view (tick 20) it records every room as observed. The
  page's guided callout follows exactly these frames
  ([`site/index.html`](../site/index.html), [`site/infiltration.jsonl`](../site/infiltration.jsonl)).
- **The terminal frame is labelled, not dressed up as fresh.** Frame 20 is the
  world *after* the last step; no agent ever decided from it, so no perception
  was recorded for it. The viewer shows the last decision-time view the agents
  did act on and says so in the caption, the badge-adjacent text, the frame
  sentence and the canvas `aria-label` ("last decision-time view (tick 20)").
- **The re-recording is bigger, and that is the whole point.**
  [`site/infiltration.jsonl`](../site/infiltration.jsonl) went from **66,465
  bytes** (schema v3, SHA-256
  `fe213bd5ed2180942cba5334e350a974d3a36e17b319281d9ad8efe99d57f3f5`) to
  **114,983 bytes** (schema v4, SHA-256
  `22f9b0fe391fa7dd2e3f6e1975f4fa7582c9b84d314723c0260d4c7feabf4cfc`) —
  **+48,518 bytes, 1.73x**, for the same 20 ticks of the same episode. Strip the
  two new fields and the file is the old one byte for byte, which
  [`Tests/Trajectories/DecisionTimePerceptionTests.cs`](../Tests/Trajectories/DecisionTimePerceptionTests.cs)
  pins by digest: the extra bytes are evidence, not a behavioural change to the
  run. Reproduce with
  `dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out site/infiltration.jsonl`.
- **Schema 5 added one header field, and the pre-schema-5 file is kept.** The
  current committed [`site/infiltration.jsonl`](../site/infiltration.jsonl) is
  **115,067 bytes** (schema v5, SHA-256
  `d557e80f02fb4783866d77300916945ab2326501f85948dd9829e971836766a7`),
  re-recorded from the same command. The **+84 bytes** over the schema-4 file
  are the `ScenarioSha256` field and the version stamp, and nothing else: strip
  those two fields and it is the schema-4 file byte for byte, which
  [`Tests/Cli/ScenarioProvenanceTests.cs`](../Tests/Cli/ScenarioProvenanceTests.cs)
  pins field by field. The pre-schema-5 artifacts are **not** discarded — they
  are committed under [`Tests/fixtures/legacy/`](../Tests/fixtures/legacy) and
  are the standing proof that a schema 3 and a schema 4 recording still read,
  replay, and verify under the schema-5 reader.
- **`site/demo.jsonl` is deliberately pinned at trajectory schema v3, and that
  is load-bearing.** It is **84,090 bytes**, SHA-256
  `f6da7cc39e56783b210bb2c936b051bf8eea80f99053cf2c4aecab1550573981`. It stays
  at v3 because it is the committed proof that a **pre-perception** recording is
  read, replayed and verified *without* the reader nagging it about fields that
  did not exist yet: `TrajectoryReplay.NoPerceptionNotice` is gated on
  `DecisionTimePerceptionVersion = 4`, so a recording with at least one step at
  schema 4 or later that declares neither recorded perceptions nor a declared
  `AgentVision` is *required* to be flagged, and re-recording this one would
  make it exactly that. Re-recording it would replace a contract test with a
  nagged file, which is a regression dressed as tidying.

  It is also **not** true, as an earlier version of this file claimed, that no
  current invocation regenerates it. Measured against the file, every one of its
  27 step lines and its final metrics line are byte-identical to a fresh
  `simulate --seed 42 --agent mcts --steps 30`; only the header differs, by the
  `ScenarioSha256` and `SchemaVersion` that migration would add. The *episode*
  was always reproducible. It is pinned for the reason above, not because it
  drifted.

  [`scripts/regenerate-site-demos.sh`](../scripts/regenerate-site-demos.sh)
  encodes exactly that split. `--check` re-derives the regenerable pair
  ([`site/infiltration.jsonl`](../site/infiltration.jsonl) and its SVG) into a
  scratch directory and byte-compares them, renders `demo.svg` from the
  committed recording, and asserts the pinned `demo.jsonl` still replays with
  **no** notice — **without** rewriting anything. CI runs it, because
  `replay --verify` alone cannot catch drift: a stale recording replays cleanly
  forever.
- **Per-tick state hash.** `replay --verify` recomputes a SHA-256 digest of the
  full simulation state at every tick — zone and resource positions, occupancy,
  the per-tick choke capacities and derived edge load, scores, claims, the
  episode seed and the tick — from a canonical, fixed-field-order,
  invariant-culture serialization
  ([`Trajectories/SimulationStateHash.cs`](../Trajectories/SimulationStateHash.cs)),
  and compares it to the digest recorded on each step line. Positions are
  included because they are state, not rendering: `Simulation.TransitTicks`
  reads `Zone.Position` for a crossing's kinematic length, and the perception
  filter reads both positions to build the observations a step line carries.
  Only the demonstration-layer `Role` labels are excluded, since the step
  contract never reads them. The digest does not cover the header's simulation
  config or dynamic-rule set, so it attests to the state each tick produced
  rather than to the whole episode configuration. The benchmark harness's FNV-1a
  step digest remains an internal warm-up anchor, unrelated to this canonical
  hash.
- **Stepping is not zero-allocation.** Managed step allocations range from
  roughly 3.2 KB to 7.1 KB per tick on the raw, facility, and dynamic workloads,
  and substantially more on the stress and MCTS workloads;
  [`benchmarks/throughput_summary.md`](../benchmarks/throughput_summary.md)
  carries the measured values. No "0 bytes allocated" claim is made.
- **Policy quality is topology-conditional.** MCTS wins the bottleneck study and
  loses the standard one; neither verdict describes the policy uniformly. The
  Python example agent does the same, in
  [Bring your own agent](../README.md#bring-your-own-agent).
- **Site recordings are re-recorded, not historical.**
  [`site/demo.jsonl`](../site/demo.jsonl) and
  [`site/infiltration.jsonl`](../site/infiltration.jsonl) were re-recorded on the
  current engine from the seed and config their own headers and the page's
  reproduce commands name, so `replay --verify` passes on both, state hashes
  included, and a CI job gates every recording under `site/`. They are not the
  files the page first shipped: engine changes since 2026-09-17 (the
  `Observation.StepNumber` field, the tick-rotated choke arbitration, and the
  instant/one-tick crossing gate) moved both episodes earlier — the demo now
  ends at tick 27 instead of hitting the 30-tick cap, and the infiltration run at
  tick 20 instead of 23. Re-running the documented commands on any later engine
  revision can move them again, and the CI job is what catches it.

## Where the numbers come from

- [Benchmarking](BENCHMARKING.md) — the throughput protocol, host provenance,
  and the regression gate.
- [Support and reproducibility](SUPPORT_AND_REPRODUCIBILITY.md#5-step-by-step-reproduction-procedures)
  — the evaluation decision rule and the exact reproduction commands.
- [Claim calibration matrix](CLAIM_CALIBRATION_MATRIX.md) — each public claim
  mapped to its proving artifact, tested matrix, and boundary.
- [Verification](VERIFICATION.md) — the full claims-and-proofs table, the gates,
  and the test evidence.
