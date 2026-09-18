# Case study — Topological capacity vs. grid constraints

**Question:** why does Lattice model the world as a graph of zones and choke
points rather than a tile grid, and what does "capacity" mean on a graph?

## The grid instinct

Grids are the default mental model for tactical maps: a tile field, an
occupancy bitmap, visibility by rays or distance, movement by 4-neighbor
steps. Grids are comfortable because distance, line of sight, and occupation
all reduce to arithmetic over cell coordinates. They are also where hard
constraints get soft quickly — "no isolated pockets" becomes a flood-fill
afterthought, "the corridor should be traversable" becomes a paint job, and
two different designers' maps disagree on what a *passage* even is.

## The graph statement

Lattice separates the two things a tactical map actually needs from a grid:
*occupancy* and *connectivity*. `MapGraph` has three kinds of node/edge
objects and nothing else:

- **Zones** — nodes. A zone is a place with an optional position and an
  optional `MaxOccupancy`. "How many agents may stand here" is a property of
  the place, expressed as an integer, `0` meaning impassable.
- **ChokePoints** — edges. An edge carries its own `MaxOccupancy`
  (how many may be mid-crossing) and an optional `TransitSpeed` consequence.
- **Resources** — the collectable objects, each tied to a zone.

Constraining a map now means constraining a graph, and a graph has exactly one
way to be traversable: there is a path. Every generator checker
(`/Generator/ConstraintCheckers`) is a total function over zones/edges/
resources, so "no dead-locked paths" is not a heuristic — it is a reachability
computation with a unit test. The generative discipline is the Dungeon
Generator lineage: reject and retry against explicit checkers, never patch a
bad map after the fact.

## What capacity gates on a graph

Occupation is real state, applied at the same tick it matters:

- A zone's capacity bounds how many agents are counted at the node when moves
  resolve. A `MaxOccupancy = 1` zone full of one slow agent is a *door*.
- A choke point's capacity bounds how many agents are mid-crossing the edge.
  Because a transiting agent holds the edge slot for the whole crossing (see
  `Simulation.Step`, movement resolution), a capacity-1 choke with a
  multi-tick crossing is a *lane*: precisely one agent in the lane at a time.
- Contested entry resolves in ascending priority rank,
  `rank = (agentId + state.StepCount) % agentCount`, as does collection.
  At zero-based tick `t`, the first agent is `(-t mod agentCount)`
  (nonnegative modulo), not `t mod agentCount`. The outcome is a total
  function of the state — identical inputs produce the same outcome on every
  machine, without a permanent lowest-id contention advantage. An adversarial
  crossing of a capacity-1 lane grants passage to the first eligible agent
  and denies the other while the edge is occupied (see the operational tests:
  no tick ever carries both agents mid-crossing).
  Agent polling and pathfinder neighbor ordering remain ascending by agent id
  and zone id respectively; terminal score ties still select the lowest agent id.

## Why this beats a grid for the stated purpose

The product guarantees are determinism, byte-identical replay, and a
measurable notion of map fairness. All three are graph-native:

- The step loop allocates nothing that depends on the map's byte footprint; a
  permutation of zone ids cannot change behavior that is a pure function of
  connectivity.
- A grid's implicit neighbor rules are replaced by explicit edges, so replay
  cannot drift between machines that enumerate neighbors differently.
- Fairness (`SpawnBiasIndex`, adr-003) attributes node, resident-resource, and
  incident-edge sets to a spawn — a purely graph reading of "territory."

The footprint cost is real and acknowledged: positions are optional, and the
entire spatial model is `GridPoint` — a plain integer pair. Geometry exists to
make *rules* legible (Manhattan length feeds transit time:

$$\max(1, \lceil d/s \rceil)$$

for a choke of length `d` at `TransitSpeed s`, in integer arithmetic). It is
never allowed to smuggle in rules that connectivity should be carrying
(adr-001).

## Takeaway

A grid is a rendering choice; a graph is a contract. Lattice keeps the
contract first and lets the renderer be nothing but a consumer of
`MapGraph` — which is why the visualization can be ASCII frames in a terminal
or a CSS-animated SVG, produced from the same recording.