# Support and Reproducibility

Lattice is an auditable multi-agent research and benchmarking environment for
deterministic Dec-POMDP experiments under partial observability, dynamic
topology, and resource contention. This document names exactly what the project
supports, what it explicitly does not support, what it does not yet claim, and
the commands that reproduce every committed artifact. It is the operational
companion to the governing thesis in
[`adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md).

> **Release status: Research Preview — evaluation by maintainers and
> collaborators.** The support contract in this document targets the published
> **v2.3.2** release and its assets. The current source tree is at **3.0.0**,
> and the published **v3.0.0** release assets are what version 3.0.0 of the
> binaries corresponds to. So the two are separate things, and every reference
> below should be read against one of them: a `v2.3.2` reference describes the
> v2.3.2 release and its assets, not the current source, while the trajectory
> schema, perception and replay-verification sections describe what the source
> tree does today. Where a `v2.3.2` statement and a current-source statement
> differ, both are true of their own subject and the difference is the version
> gap, not a contradiction.
> Publishing v3.0.0 does not by itself qualify Lattice for production
> use; it remains a research instrument until independent security review,
> soak testing, and formal fuzzing are complete (see Section 2).

## Equivalence vocabulary

Five distinct guarantees are used throughout this repository. They are never
interchangeable, and no document may upgrade one into another:

1. **Engine transition determinism.** Under the stated runtime contract, the
   same `SimulationState` plus the same `AgentAction[]` produces the same next
   state. This is a property of the pure step contract
   ([`adr-0003`](adr/0003-simultaneous-actions-step-contract.md)).
2. **Per-step serialized `StepResult` replay equivalence.** Replaying a
   recording's actions from a fresh initial state reconstructs ticks whose
   serialized `StepResult`s equal the recorded ones. `TrajectoryReplay.Verify`
   asserts exactly this — plus field-by-field authentication of the final
   summary line's aggregates against the re-simulated run — on the tested CI
   platforms; the `replay --verify` command below exercises it.
3. **Same-host normalized JSONL byte identity.** Two fresh episodes recorded
   from the same seed and the same actions produce byte-identical JSONL only
   where line-ending and formatting normalization is verified on identical host
   environments, and only on hosts where that identity is explicitly tested.
   This guarantee is deliberately narrow: newlines are written as a bare `\n`
   on every platform, but a raw byte identity claim is still scoped to
   identical environments and is not a cross-host guarantee.
4. **Per-tick canonical simulation-state hash.** **Implemented (trajectory
   schema 3 and later).** Each step line records a SHA-256 digest of the
   complete simulation state at the end of that tick, computed from a canonical
   fixed-field-order, invariant-culture serialization
   (`Trajectories/SimulationStateHash.cs`), and `replay --verify` recomputes and
   compares it, naming the first mismatched tick. The serialization covers the
   zone and resource positions, per-zone occupancy, the per-tick choke capacities
   and the derived per-choke edge load, scores, claims, the episode seed and the
   tick. It does **not** cover the header's `SimulationConfig` or
   `DynamicMapRuleSet`, so it attests to the state each tick produced rather
   than to the whole episode configuration. A recording that predates the field
   still verifies on serialized `StepResult` equality and reports
   `no state hash: step-level verification only`; a recording that declares
   schema 3 or later but carries no digest is reported as a discrepancy, not a
   notice, so the hashes cannot be stripped to downgrade the check. The
   benchmark harness's FNV-1a step digest is an unrelated internal repeatability
   check — it anchors one warm-up iteration and proves later iterations did not
   go off-script.
5. **Recorded decision-time perception.** **Implemented (trajectory schema 4 and
   later).** Each step line carries, per agent slot, the `PartialObservation`
   that agent's own `PerceptionFilter` produced **inside** its `Decide` call, so
   what the agent saw when it chose is a record rather than a reconstruction.
   `replay --verify` reprojects every recorded perception independently through
   a `PerceptionFilter` built from the header's `AgentVision` over the world the
   step was decided from, and compares, naming the first offending tick and
   agent slot. This is a narrow claim and is worded to match: it says the
   recorded perceptions equal what the recorded agents' own filters produced
   inside `Decide`, and that an independent replay reproduces them. It does
   **not** say an independent party could have derived the same fog from the
   world alone — the filter's stale memory is per-agent internal state, so the
   fifth guarantee is replay-verified rather than an oracle law, and
   [`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) §2.5 states that
   boundary explicitly.

The first four guarantees are formalized as an implementation-agnostic,
clean-room contract in
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) — the transition
laws an independent oracle or checker in any language must reproduce, plus
three falsifiable external challenge questions and the submission contract for
oracle verification reports. The fifth is checked by `replay --verify` and is
deliberately outside that contract; that document says why.

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
  the current disclosure process and supported release line. For calibration:
  the repository does contain an **in-process, seeded mutation/fuzz regression
  harness** — deterministic seeded fuzz suites under `Tests/Fuzz/` (parser,
  CLI arguments, dynamic rules) and pinned Stryker mutation analysis over the
  transition-bearing files (`stryker-config.json`) — and
  these run reproducibly in CI and locally. **External coverage-guided fuzzing
  remains deferred**: no libFuzzer-, AFL-, or OSS-Fuzz-style campaign has been
  run, and the "formal fuzzing" exclusion above refers to that external
  campaign, not to the committed in-process harness. The mutation analysis is
  summarized in
  [`../benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json).
- Any claim of an operating system, architecture, or runtime not listed in
  Section 1 is unsupported until it is added to the matrix and exercised by CI.

## 3. Known Limitations

- **Per-tick digests, not a single episode digest.** Replay validation operates
  on tick-by-tick serialized `StepResult` equality and, for schema-3
  recordings, on a per-tick state digest. An episode is still not reducible to
  one state digest: the digests are per tick, and a recording made before the
  field existed carries none and verifies on step results alone.
- **Scenario-dependent policy behavior.** With 32 rollouts per action, the MCTS
  evaluation subject underperforms the `ScoutCollectorAgent` baseline on open
  collection topologies while outperforming it under capacity-1 procedural
  bottleneck contention. Concretely, the committed reference artifacts record a
  mean paired delta of −1.12 on the dev suite and −1.25 on the held-out suite
  for the standard scenario
  ([`../benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json)),
  against +2.03 and +2.60 for the bottleneck scenario
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

### Schema v3 header requirements

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
| `SchemaVersion` | yes | Wire format stamp; the current version is `TrajectorySchema.CurrentVersion` (currently `4`). |
| `Scenario` | no | Demonstration-layer metadata; ignored by the replay core. |
| `AgentRoles` | no | Demonstration-layer roster metadata; ignored by the replay core. |
| `AgentVision` | no | Schema 4: the perception cone, in graph hops, each agent's own filter was built with, indexed by agent slot. Present exactly when the step lines carry `Perceptions`, and omitted otherwise. |

Newly written files carry `TrajectorySchema.CurrentVersion` (currently `4`);
this document cites that constant rather than a bare literal, so it cannot
drift out of step with the code. Schema 2 introduced the episode's dynamic
topology policy (`DynamicRules`, timed portcullises and event locks), which the
current schema still records, so a replay recreates the exact choke-capacity
schedule the recording was made under. Schema 4 adds the decision-time
perceptions, described below. The simulation config is the required
second half of that contract: a replay with a different config is not a replay
of the same episode.

#### What schema 3 adds

Schema 3 does not change the header. It changes the step lines.

- **`StateHash` is a step-line field, not a header field.** Each `step` line
  carries the SHA-256 digest of the complete simulation state at the *end* of
  that tick (`Trajectories/SimulationStateHash.cs`), computed from a canonical
  fixed-field-order, invariant-culture serialization. The header is unchanged
  apart from its `SchemaVersion` stamp.
- **State hashes are required from schema 3 onward.** `StateHash` is mandatory
  per `TrajectorySchema.StateHashRequiredVersion` (currently `3`). A recording
  that declares schema 3 or later and carries no digest is reported as a
  **discrepancy, not a notice**, so the hashes cannot be stripped to downgrade
  the check; a recording that carries some but not all digests is likewise a
  discrepancy.
- **The notice path for older trajectories.** A recording written before
  schema 3 legitimately has no `StateHash` field at all. It still verifies on
  serialized `StepResult` equality and reports
  `no state hash: step-level verification only`. The same string is published
  as `TrajectoryReplay.NoStateHashNotice`.
- **Rewrites do not relabel.** `TrajectoryWriter.Write` re-emits the
  recording's own header version, so rewriting a pre-hash file cannot promote
  it to schema 3 with no digests present (see the migration invariant below).

#### What schema 4 adds

Schema 4 changes the header and the step lines, and it adds a fifth thing to
check: what each agent actually saw when it chose.

- **`Perceptions` is a step-line field.** Each `step` line may carry a
  `Perceptions` array with one `PartialObservation` per agent slot, indexed by
  slot, each naming the agent it belongs to. It is the masked, vision-bounded
  view that agent's own `PerceptionFilter` produced **inside** its `Decide`
  call, stamped with the tick it decided (`Perceptions[i].Tick` is the
  decision's tick, which is the *pre*-step world, not the world the step
  produced). It carries the three data tiers the filter defines: real-time
  detail inside the cone, last-known stale memory beyond it, and fully masked
  entries for everything never seen.
- **`AgentVision` is the header half of the same contract.** It declares the
  radius, in graph hops, each agent's filter was built with, so a reader can
  rebuild those filters. The writer derives it *from* the perceptions
  (`PerceptionProjector.DeclaredVision`) rather than accepting it as an
  independent claim, so the declared cone cannot disagree with the recorded fog.
- **The recording path never re-derives a perception.** `ScenarioRunner`
  captures what each agent's own filter produced
  (`IDecidesFromPerception.LastPerception`) immediately after that agent
  decides; `TrajectoryWriter.Record` records those values and nothing else. A
  second projection would be a second opinion about what the agent saw, not a
  record of it, and the two could be compared only if one of them were rebuilt
  anyway — which is exactly what verification does, separately.
- **`replay --verify` recomputes each perception.** Every recorded
  `PartialObservation` is reprojected through a `PerceptionFilter` built from
  the header's `AgentVision` over the world the step was decided from, and
  compared, naming the first offending tick and agent slot. A single edited
  zone status, `LastSeenTick`, or rival sighting fails. The filter is
  deterministic and its stale memory accumulates across the episode exactly as
  the agent's own did, so the comparison is a check of the recorded fog rather
  than a re-derivation of it from omniscient positions.
- **Coverage is all-or-nothing, like the state hash.** Every step line must
  carry a `Perceptions` array or none may; a partial block is a **discrepancy**,
  so the fog cannot be stripped from some steps to narrow the check. A header
  that declares an `AgentVision` no step backs, or a recording whose steps carry
  perceptions but whose header declares no cone, is likewise a discrepancy.
- **The notice path.** A recording that declares schema
  `TrajectorySchema.DecisionTimePerceptionVersion` (currently `4`) or later and
  carries neither perceptions nor a declared vision reports
  `no recorded perception: decision-time visibility not verified`, published as
  `TrajectoryReplay.NoPerceptionNotice`. It is a notice rather than a
  discrepancy because the fields are optional on the wire — an agent that
  carries no filter has no decision-time fog to record, and an external-agent
  match is exactly that — but it is never silence, because a reader that did
  not check the fog must not be able to report a pass that reads as though it
  had. A recording from **before** schema 4 is not nagged: the field never
  existed, so there is nothing it failed to carry. The committed
  `demo.jsonl` and the golden fixture are in that position and verify as they
  always did.
- **A recording without the fields is byte-identical to one written before
  them.** Both new fields are nullable and omitted when absent
  (`JsonIgnoreCondition.WhenWritingNull`), so the schema-4 writer's output for a
  perception-free episode differs from the pre-schema-4 output only in the
  header's own version stamp. `Tests/Trajectories/DecisionTimePerceptionTests.cs`
  pins that: stripping the fields from the committed infiltration episode
  reproduces its pre-change SHA-256 exactly.

### Structural and migration invariants

- The header must be the first line; a second header anywhere is rejected.
- Step numbers must be contiguous from `1`; gaps or repeats are rejected.
- Exactly one final line is permitted, and it must be last.
- The final line's `TotalSteps` must equal the number of recorded step lines.
- Action validity is checked at record time and again at verify time against
  `ActionSpace`, so a step outside the action space is rejected rather than
  replayed.
- Every non-blank line must declare a known `Kind`; unknown kinds fail loudly.
- A `Perceptions` array must hold exactly one non-null entry per agent slot,
  each entry naming the slot it sits in; a header's `AgentVision` must hold
  exactly one radius per declared agent, and a radius must be
  `SimulationConfig.UnboundedVision` or at least 1. The rule is stated once, in
  `Trajectories/PerceptionProjector.cs`, and the reader, the writer and
  verification all call it.
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
  readable. `TrajectoryWriter.Record` stamps
  `TrajectorySchema.CurrentVersion` on the recordings it mints, while
  `TrajectoryWriter.Write` re-emits the recording's *own* header version: a
  rewrite of a pre-hash file must not relabel it as schema 3 with no digests
  present. When the wire contract changes, the version is incremented and the
  reader's accepted-version rule is updated in the same change.

The golden fixtures used to pin these invariants are
[`../Tests/fixtures/golden_trajectory.jsonl`](../Tests/fixtures/golden_trajectory.jsonl)
(schema v3, seed 2024) and
[`../Tests/fixtures/golden_dynamic_rules.json`](../Tests/fixtures/golden_dynamic_rules.json).
The v3 fixture stays at v3 on purpose: it is the standing proof that a
pre-perception recording is still read, still verified, and still emits no
perception notice. The committed
[`../site/infiltration.jsonl`](../site/infiltration.jsonl) is the schema-4
counterpart (seed 42, 20 steps, per-agent vision `[2, 2]`), and
`../site/demo.jsonl` remains a schema-3 recording with no perceptions, which is
why the viewer labels its sightline a reconstruction rather than a record.

### Viewer claim semantics

The replay viewer's loot diamonds and the claim metric in its metrics panel are
two readings of one question — *which resources does this view say are already
taken* — so they are filled from one set and labelled by one decision. The
count and the words that describe it are written together, because a number
taken from one set under a label borrowed from another is the failure the
pairing exists to prevent.

| View | Counted set | Label | Basis |
| --- | --- | --- | --- |
| Ground truth | the world's claim list, `Result.Observations[].Claims` for the frame being painted | `claimed in world` | that is what the view is |
| Agent view, schema 4 | that observer's own `VisibleClaims`, for the perception painted with it | `claims seen` | the recording's `Perceptions[i].VisibleClaims` |
| Agent view, schema 3 | the world's claims **held to the rooms the derived sightline reached** | `claims in derived view` | this page's own derivation; nothing was recorded |
| Agent view, no recorded perception for the frame | none reported (`—`) | `claims seen` | the view masks nothing and the file holds no per-agent claim set, so no count is invented |

Three consequences are worth stating because they are the ones a reader is most
likely to check:

- **The terminal ego frame is not an exception.** The last frame has no
  decision of its own, so the ego view paints the last decision-time
  perception (`fresh: false`) over the world that decision was made from
  (frame *last*−1). The metric counts the same perception's `VisibleClaims`, not
  the post-step terminal world's claims. In
  [`../site/infiltration.jsonl`](../site/infiltration.jsonl) those differ: at the
  terminal frame the world has claims `[1, 2, 0]`, the Sentry's recorded
  perception has `[1, 2]`, and the Infiltrator's has `[]`.
- **A schema-3 ego view is a derivation and is never labelled "seen."** There is
  no recorded per-agent claim set to narrow the world's list, so the page holds
  it to the rooms its reconstructed 2-hop walk reached: a chest behind an
  unexplored door stays unclaimed, exactly as the room around it is drawn. The
  fog chip, the map caption, the canvas label, the provenance row and the metric
  label all say "derived by this page" on that view; none of them claims a
  recording.
- **The zone table's loot column follows the same set.** The per-room
  "unclaimed loot" counts are the same claim set the diamonds were filled from,
  not the world's, so the panel cannot contradict the map above it.

The diamonds themselves are drawn from the same sets, on top of the room card
they live in. That layer order is load-bearing: a card is an opaque fill in an
observed room, so painting loot first buries every chest under the card it is in
and no chest is visible in any view, at any DPR. `ui_tests/test_loot_pixels.py`
reads the actual canvas pixels with `getImageData` for one desktop frame of the
schema-4 recording, on every perspective, and fails when a diamond is absent or
in the wrong colour; `ui_tests/test_agent_view_honesty.py` covers the same claim
semantics across every frame of every view from the geometry probe.

### Viewer presence semantics

Who is standing in each room is a second such question, asked by the zone
table's occupancy column and by the small badge painted in the bottom-right of
every room card. Both are read out of the *same fog object the canvas was
painted from*, so the table, the badge and the tokens on the map cannot describe
two different worlds. The count and the words go together, as with the claim
metric.

| View | Counted occupants | Label | Basis |
| --- | --- | --- | --- |
| Ground truth | the world's agents, standing in rooms, in the world the map was painted from | `agents present in world` | that is what the view is |
| Agent view, schema 4 | the observer, plus each rival only where that observer's recorded perception reports it **currently observed** | `agents observed in this view` | `Perceptions[i].Agents[].Status` |
| Agent view, schema 3 | the observer, plus the rivals this page's derived sightline currently places in a room | `agents in derived view` | this page's own derivation; nothing was recorded |
| Agent view, no recorded perception for the frame | none reported (`—` per room) | `presence unavailable` | the file records perceptions but has none for this frame, so no count is invented from `frame.agents` |

Four consequences, each of which the tests above check against the painted
tokens rather than against another helper:

- **A remembered ghost is not an occupant.** A rival the observer has only a
  memory of is drawn as a faded age stamp in the room it was last seen in, and a
  rival it has never reached is not drawn at all. Neither is counted in any
  room: counting a ghost would claim somebody standing in a room the canvas
  deliberately declined to draw a token in.
- **A corridor traveller occupies no room.** An agent mid-transit is painted
  *between* rooms, so it is not an occupant of either. Its crossing is reported
  in the agents table above the zone table, with destination and remaining
  ticks. The world count this replaces did attribute a mid-transit agent to the
  room it had left, which put "1 agent" under a room with no token in it.
- **A room the view has not looked at is not "empty."** A stale room reads
  `last known` and an unexplored one `unexplored` in the table — the canvas's own
  words for those two cards — and carries no count and no badge. "empty" is a
  claim about a room, and this view has made no claim about that one.
- **The terminal ego frame is not an exception here either.** As with the claim
  metric, the occupancy column is taken from the world the last decision was made
  from (frame *last*−1), which is the world the tokens were painted from.

`ui_tests/test_agent_view_honesty.py` asserts this three ways at once for every
frame of every view of both committed recordings: the zone-table cell, the
painted room badge, and the live agent tokens read back out of the geometry
probe, with each token assigned to the room box that contains it. Neither
committed file contains a rival an observer has lost — every recorded rival
sighting in `infiltration.jsonl` is currently observed, and `demo.jsonl`'s four
rooms all sit inside the page's two-hop cone — so the ghost and unknown-rival
cases, and the missing-perception case, run on controlled variants of the
schema-4 file written to a temp directory and loaded through the page's own file
input. The committed recordings are immutable evidence and are not edited to
make a test pass.

### Release immutability policy

- **`immutable: true` is scoped to `v2.3.1`, `v2.3.2`, and `v3.0.0`, all
  published.** Only these three lines carry the immutable publishing policy
  below: permanently pinned tags, checksummed permanently attached assets, and
  corrections-by-supersession. `v2.3.1` is published at commit `c0e8342`;
  `v2.3.2` is published and immutable at commit `4f7816f` (tag `v2.3.2` →
  commit `4f7816fa5594f7097d6b2978c6c626553d075326`), superseding `v2.3.1`
  with the parser hardening, property suites, and fuzz fixtures; `v3.0.0` is
  published and immutable at commit `c39d79b` (tag `v3.0.0` →
  commit `c39d79b746e0f3aebce536dbe1cde387bd4e7991`), superseding `v2.3.2`
  with schema 3 per-tick state authentication.
- **`v2.3.0` is historical, untouched, but was published under
  `immutable: false`.** It predates the immutable publishing policy, remains in
  place as part of the record, and is never modified, retagged, or deleted —
  but its assets carry no immutability or checksum-permanence promise.
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
guide in [`reproduction_packet.md`](reproduction_packet.md). Its current section
anchors to `v3.0.0` (commit `c39d79b746e0f3aebce536dbe1cde387bd4e7991`),
published and immutable; the `v2.3.2` section alongside it anchors to `v2.3.2`
(commit `4f7816fa5594f7097d6b2978c6c626553d075326`), also published and
immutable, and `v2.3.1` remains historical under
corrections-by-supersession. It lists the exact published SHA-256
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

Two companion records keep the audit chain honest:

- [`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md) — the public, append-only findings
  ledger documenting criticisms, edge cases, and resolved issues (including
  replay-claim calibration, benchmark-gate sensitivity, a release staging
  failure, parser guard gaps, mutation-survivor coverage, and a preserved
  negative result), each with provenance, severity, disposition, and closing
  evidence link.
- [`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md) — the audit
  matrix mapping every public claim in the README, the published site, and the
  architectural decision records to its proving artifact, tested matrix,
  documented boundary, and wording status.

All commands run from a clean checkout of `candavere/lattice` at the repository
root, with the .NET 8 SDK installed. They are Release-configuration runs and
require no network access once the SDK and dependencies are restored.

### Full test suite

```sh
dotnet test Tests/Lattice.Tests.csproj -c Release
```

The test suite's results are interpreted against the public governance
records — historical defects and closings in
[`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md) and the claim-to-evidence audit
matrix in [`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md).

### Golden trajectory replay verification

```sh
dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify
```

A pass prints `replay verified` and exits `0`. This asserts per-step serialized
`StepResult` equivalence (Section 3), per-tick state-digest equality where the
recording carries one, and field-by-field authentication of the final summary
line's aggregates.

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
| bottleneck | dev | MCTS | Scout | 30 | 32 | +2.03 | [+1.66, +2.41] | Pass |
| bottleneck | held-out | MCTS | Scout | 30 | 32 | +2.60 | [+2.20, +3.00] | Pass |

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
- [`adr-0003`](adr/0003-simultaneous-actions-step-contract.md) — step contract and runtime targets.
- [`adr-0004`](adr/0004-spawn-fairness-mirrored-seatings.md) — evaluation and analytics decisions.
- [`ECOSYSTEM.md`](ECOSYSTEM.md) — assembly and data-flow map.
