# Simulation mechanics

This page is the contract-level detail behind [How it works](../README.md#how-it-works)
on the landing page. Everything here is enforced by tests under
[`Tests/`](../Tests) and consolidated in
[`docs/INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md).

## Step resolution order

Each tick takes one `AgentAction` per agent and produces an immutable
`StepResult`. Actions resolve in two fixed phases — all moves, then all
collects — each resolved in ascending priority rank,
`rank = (agentId + state.StepCount) % agentCount`. At zero-based tick `t` the
first agent is `(-t mod agentCount)` (nonnegative modulo), not `t mod
agentCount`. Invalid or missing actions degrade to `Wait`, so every action
array yields a valid next state.

## Topological graph space

`MapGraph` holds zones, chokes, and resources; connectivity and degree are
first-class (adr-0002). Zones and chokes carry a `MaxOccupancy` (default
unlimited; `0` = impassable), enforced as same-tick entry gates in the same
tick-dependent order as collection. Coordinates are an optional embedding.

## Kinematic edge transit

With `TransitSpeed = s` and choke length `d` (Manhattan distance), a crossing
takes `max(1, ⌈d / s⌉)` ticks in integer arithmetic. A crossing agent carries
`InTransit(From, To, Remaining)`, counts as an occupant of the departure node,
and cannot move or collect until arrival. `Simulation.TransitTicks(map, from,
to, speed)` exposes the same arithmetic.

## Dynamic topology

A `DynamicMapRuleSet` carries `TimedPortcullisRule` (open/closed over an
`OpenTicks`/`ClosedTicks` cycle) and `EventLockedChokeRule` (locked until a
resource is collected). Rules apply at every tick boundary as
`DynamicMapOverrides`, serialize as JSON with a `ruleKind` discriminator, and
revalidate through their constructors, so a file cannot smuggle a degenerate
schedule past validation.

## Bounded perception and stale memory

A vision horizon of `V` choke-edge hops defines what an agent sees.
`Observed` = in cone this tick; `Stale` = seen before, last-known data plus
sighting tick; `Unknown` = never seen. Agents route through `AgentBeliefMap`
over exits they have actually seen.

## Immutable forking

`SimulationState` is one immutable record, so a snapshot is a shared
reference. `SimulationFork` steps a captured state through the same pure
`Simulation.Step`, so forking a recording at tick K and re-rolling an
alternative sequence can never mutate the source state, the recording, or a
sibling fork.

## Dungeon Infiltration & Sentry Patrol

Six rooms, capacity-1 chokes, a seeded vault chest count (2–3), a guard on a
fixed patrol that pivots to pursuit, and a belief-map infiltrator that
collects the vault then extracts. The deterministic outcome taxonomy —
`exfiltrated` / `intercepted` / `intercepted-after-exfil` / `timeout` — is an
emergent property of the topology and the choke arithmetic, not the agents'
internal logic.

## Map fairness and spawn bias

`MapFairnessEvaluator` plays the same policy twice in mirrored seatings (the
only way to invert the fixed spawn-to-id binding) and reports a normalized
`SpawnBiasIndex = |meanScore[spawnA] − meanScore[spawnB]| / totalResources`
(0 = balanced, 1 = one-sided). It can gate generation via `--min-fairness`.
See [`docs/adr/0001-governing-product-thesis.md`](adr/0001-governing-product-thesis.md)
and `docs/adr/0003-simultaneous-actions-step-contract.md` for the design record.