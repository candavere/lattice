# Case study — Automated map fairness profiling

**Question:** how can a procedural generator know whether a map is *fair*
before anyone plays it, deterministically, and reject it at generation time?

## Why fairness is not a yes/no

"Fair" for a two-spawn tactical map means neither starting position is a
structural advantage. Naively, you measure by playing: run a game, see who
wins, repeat. That conflates three confounds — the policies, the seating, and
the map — and no amount of repetition removes the first two. A deterministic
substrate can do better, because the same rules always produce the same game.

## The mirrored-seating arena

The environment binds spawn to agent id: `Simulation.CreateInitial` round-robins
agent `i` to zone `i % zoneCount` (adr-002). "The same two contestants seated
once each way" therefore cannot be expressed by swapping agent slots — the
environment would put agent 0 in spawn A both times. The fix (adr-003) is to
mirror the *map*, not the rules:

1. **Swap spawn territories.** Build a mirrored `MapGraph` exchanging zone 0
   and zone 1 — positions, `MaxOccupancy`, resident resources, and every
   incident choke endpoint — an exact transpose of the original.
2. **Play both assignments with the same seeded roster.** Agent 0 takes spawn
   A on the base map and spawn A's *node* on the mirrored map. Both runs step
   through the same pure `Simulation.Step` under the same seeded RNG, so each
   measurement is byte-identical for identical inputs.
3. **Attribute scores to territories, not agents.** A dominant agent skewing
   both games is canceled by averaging per territory across the pair:

   ```
   meanScore[spawnA] = (base.SpawnA + mirror.SpawnA) / 2
   meanScore[spawnB] = (base.SpawnB + mirror.SpawnB) / 2
   SpawnBiasIndex    = |meanScore[spawnA] − meanScore[spawnB]| / totalResources
   ```

   `SpawnBiasIndex` is exactly `0.0` for a perfectly balanced board and `1.0`
   for a wholly one-sided one, and it is a **single IEEE-exact double
   division** over integer quantities (`totalResources`, the two territory
   means) — no floating-point arena rule feeds the answer, so it cannot drift
   between machines.

The two runs are also the *minimum* for a mirrored measurement: an ascending-id
collect tie-break and a first-mover advantage land on both territories equally
across the pair, and the metric cancels them.

## Pinning the metric

None of this is trustworthy until it is pinned. The fairness suite uses
hand-built three-zone boards whose mirrored outcomes are hand-computable:

- a Y-symmetric board (two identical territories, a shared equidistant center
  stash) must report exactly `SpawnBiasIndex = 0.0`;
- an asymmetric board (near spawn three transit ticks from the stash, far
  spawn five) must report exact territory means `3.0/1.0` and bias `0.5` for
  the greedy policy;
- the MCTS policy measures the same board deterministically at a pinned bias.

Changing any resolution rule in the environment is therefore a *visible* test
change, not a silent re-tilt.

## Closing the loop: fairness as a generation constraint

The generator's discipline is "reject and retry, never patch" (adr-003).
Fairness plugs into that as a caller-supplied acceptance gate: the generator
itself stays BCL-only and has no idea agents exist. `MapGenerator.Generate`
takes an optional `MapAcceptanceGate` (`MapGraph -> bool`); a candidate whose
measured `SpawnBiasIndex` exceeds a threshold counts as one more failed
attempt in the retry loop (`"acceptance-gate"` in the failure diagnostics) and
is discarded, seeded-RNG fresh. The CLI exposes it directly:

```
lattice generate --seed 123 --min-fairness 0.3
```

Because the whole pipeline is deterministic, a seed whose retry budget yields
no fair map fails explicitly (`MapGenerationException` carrying the seed, the
attempt count, and the failed checks) instead of silently shipping a biased
map.

## Takeaway

Fairness profiling is not an analysis afterthought; it is a first-class
generation constraint with a pinned quantity. Measure on mirrored seatings,
report an integer-quantized bias index, and let generation reject rather than
patch — the same discipline that guarantees reachable, constraint-checked
maps also guarantees spawn-fair ones.