# ADR-0004 — Spawn fairness is measured with mirrored seatings, gate via delegate

**Status:** accepted

## Context

The fairness profiler needs a deterministic, structurally meaningful
quantity: does a map favor the agent that starts in spawn A or spawn B? The
environment binds spawn
to agent id — `Simulation.CreateInitial` round-robins agent `i` to zone
`i % zoneCount` (adr-0003) — so "the same two contestants seated once each way"
cannot be expressed by swapping agent slots: the environment would put agent 0
in spawn A both times. Any fairness metric built on raw episodes would be
contaminated exactly this way. Two options were available:

- (a) add a "seat" concept to the environment (an explicit extra assignment,
  breaking `CreateInitial`'s id-binding), or
- (b) keep the environment untouched and express the mirror on the map.

## Decision

Mirror the **positions**, not the rules — option (b):

1. **Swap spawn territories.** Given a map, build its mirror by exchanging
   zone 0 and zone 1: positions, `MaxOccupancy`, every resident resource
   (their `ZoneId` flips), and every incident choke endpoint
   (`FromZoneId`/`ToZoneId` swap wherever the edge touches a spawn zone).
   Resources that were already in the *other* spawn zone move in the opposite
   direction, so the two maps are exact transposes of each other.
2. **Fixed two-agent roster.** The same two agents play both maps. In the
   base map (as-shipped), seat agent 0 at spawn A and agent 1 at spawn B
   (`AssignmentAB`). In the mirrored map, agent 0 physically occupies spawn B's
   node and agent 1 occupies spawn A's node (`AssignmentBA`), which is exactly
   the inverted seating the environment cannot spell itself.
3. **Attribute scores to territories, not agents.** `AssignmentBA.SpawnA` is
   the *mirrored run's* agent-1 score — the agent seated at spawn A's node.
   The metric then averages each territory's score across the two runs:

   ```
   meanScore[spawnA] = (AB.SpawnA + BA.SpawnA) / 2
   meanScore[spawnB] = (AB.SpawnB + BA.SpawnB) / 2
   SpawnBiasIndex    = |meanScore[spawnA] − meanScore[spawnB]| / totalResources
   ```

   `SpawnBiasIndex ∈ [0, 1]` (0 when there are no resources), with lower meaning
   fairer. Two runs are the minimum for a mirrored measurement, which also
   cancels non-map artifacts: a fixed ascend-by-id collect tie-break and
   first-mover advantage fall on both territories equally across the pair.
4. **The gate is caller-supplied.** `MapGenerator.Generate` takes an optional
   `MapAcceptanceGate` delegate (`MapGraph → bool`). A gate failure is just one
   more failed attempt in the existing retry loop (`"acceptance-gate"` in the
   failure diagnostics) — the generator never patches a biased map, consistent
   with the "regenerate, don't patch" discipline. The delegate keeps
   `Lattice.Generator` BCL-only: the generator has no idea agents or a fairness
   arena exist. `Lattice.Cli generate --min-fairness <0..1>` wires the real
   `MapFairnessEvaluator` (Greedy policy, 2 agents, 200 ticks, speed 8) into
   that gate.

## Rationale

Option (b) keeps the step contract unchanged (adr-0003's `CreateInitial`
contract stays true), so the arena is purely an analytics artifact consuming
only public step-contract types. Mirroring on the map is deterministic and
cheap: the swap is a pure record rewrite, and the two episodes replay under
the same seeded RNG, so the whole measurement reproduces the same per-step
serialized results for identical inputs. Territory attribution handles the real
confound (a fixed two-agent roster lets a dominant agent skew both seats) by
averaging per territory across the mirrored pair rather than per agent.

## Consequences

- `MapFairnessEvaluator.Evaluate(map, seed)` returns a `MapFairnessReport` with
  both assignments' scores, steps, and termination reasons plus
  `TotalResources`, the two territory means, and `SpawnBiasIndex` — pure data,
  JSON-serializable like every other step-contract record.
- Deterministic pins exist and are locked into tests: a symmetric three-zone
  board measures exactly 0.0 bias; the asymmetric fixture measures exactly 0.5
  with mean scores 3/1 on both assignments. Changing any resolution rule in the
  environment is therefore a *visible* test change, not a silent re-tilt.
- Generator gate behavior is pinned: a satisfied gate returns the identical
  plain map; a rejecting gate throws after the retry cap with `acceptance-gate`
  in the message.
- `Lattice.Analytics` now references `Lattice.Agents` (the arena plays real
  agents); `Lattice.Environment` and `Lattice.Generator` remain BCL-only.