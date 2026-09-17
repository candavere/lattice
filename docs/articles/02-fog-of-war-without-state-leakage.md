# Case study — Fog of war without leaking state

**Question:** how does Lattice hide information from an observer without giving
that observer a privileged view into the simulation core?

## The naive approach leaks

A common design gives "fog" to the environment: the simulator tracks what each
agent can see, updates it as the agent moves, and emits a per-agent filtered
observation. This couples the core to the notion of an observer, threads
hidden memory through every `Step`, and breaks determinism the moment the
filtering bookkeeping is not itself a pure function of the same inputs. It
also makes replay shape-dependent: the recording's correctness depends on the
filter that produced it, not on the raw state.

## The projection contract

Lattice keeps the core vision-agnostic and immutable (adr-002). The simulator
always emits the complete state — `AgentState`, `Claims`, and the full
`MapGraph`. What an observer actually learns is computed *outside* the core by
`PerceptionFilter`, a pure projection with its own memory:

- A **vision horizon of `V` choke-edge hops** defines the set of zones visible
  this tick, computed as a BFS over choke edges expanding in ascending
  neighbor id. Because edges are the only adjacency there is, the cone is a
  Chebyshev-bounded hop cone:
  $$\| \text{zone} - \text{origin} \|_{\text{hops}} \le V$$
  — distance is measured in choke crossings, not Euclidean units, and every
  agent on the same map sees the same cone shape for the same `V`.
- Sights carry a **knowledge status**, not a partial copy:
  - `Observed` — inside the cone now; real-time data.
  - `Stale` — outside the cone now, but seen before; the last-known value and
    the tick it was recorded survive, timestamped.
  - `Unknown` — never seen; the record masks geometry entirely (`null`
    position, tick `-1`).
- The filter keeps its own last-seen tables, initialized and advanced only by
  the projections the observer is fed. It never references the simulation
  core; its inputs are what the host chose to project.

So the observation an agent reasons about is *derived*, never stored in the
world. The core stays a pure step machine and cannot be invalidated by a
filter bug.

## What the agents may not do

Because the filter is the only sensor, an agent's belief state is bounded by
its own projection stream. `AgentBeliefMap` accumulates what was seen, and
routing runs over exits the agent has actually observed. Two consequences are
enforced by tests:

- The scout cannot collect, move into, or route through anything it has not
  seen — its decisions are functions of `PartialObservation` projections and
  their memory, never of `MapGraph` reachable from the `Observation`'s claims.
- Beliefs can go **stale**: something believed unclaimed can be claimed by an
  unseen rival, and a choke believed traversable can be occupied. The correct
  response when the sensor finally refutes a belief is to re-plan within the
  same tick — the `AgentBeliefMap` records a *sensor surprise*
  (`ClaimedTarget` when a believed-unclaimed resource is observed claimed,
  `SaturatedChoke` when a believed-traversable choke is observed occupied), and
  the scout drops the refuted target or re-routes around the occupied edge
  before its next decision, never honoring the stale plan.

## Determinism, replay, and the boundary

A recording stores the *full* observations (`StepResult`), which is what makes
replay and analysis exact: any filter, run against the recorded bytes, must
reproduce the exact projections a live agent saw. Fog is therefore a property
of the module that consumes the raw state — a host can ship multiple observers
(fog-of-war scouts, omniscient scoring rigs, heatmap analyzers) against one
trajectory, all byte-identical.

The boundary rule is the load-bearing one: `Lattice.Environment` has no notion
of vision at all (`SimulationConfig.Vision` only sizes the filter), and
`PerceptionFilter` has no handle into any part of `Simulation`. That split is
what makes fog-of-war a projection and not a leak.

## Takeaway

Hidden state is where determinism dies. By pushing perception out of the core
and behind a pure projection, Lattice gets partial observability — bounded
cones, stale memory, surprise-driven replanning — with zero new moving parts
in the step loop and byte-identical replays for any observer.