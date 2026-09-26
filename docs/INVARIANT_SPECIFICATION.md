# Independent Invariant Specification

This document is a formal, implementation-agnostic specification of the
load-bearing transition rules and perception graph invariants of the Lattice
deterministic multi-agent environment. Its audience is an external auditor,
verification researcher, or clean-room implementer who wants to build an
independent oracle or checker — in any language (Python, Rust, C++, and
others) — for the engine's behavior **without consulting or referencing the
production implementation**.

Everything below is written in mathematics and procedural prose so that a
clean-room reimplementation can be derived from this document alone. No type
names, source files, or internal identifiers of the implementation are
referenced. Where the specification fixes a boundary between what an oracle
must and must not assume, it says so explicitly.

Scope notes:

- This specification pins the **transition and perception contract**. It does
  not pin performance figures, floating-point output formatting, or raw file
  byte identity across hosts.
- It is the companion to the reproduction challenge packet (see the
  reproducibility documentation): the packet verifies released binaries behave
  per contract; this document defines the contract an independent oracle
  checks.
- A canonical simulation-state hash **tree** is not part of the contract. What is
  specified is a single per-tick digest of the state, defined in Invariant 5
  below; there is no Merkle structure, no cross-recording root, and no
  cross-episode chaining claimed anywhere here.

---

## 1. Formal Environment & State Definitions

### 1.1 The topology graph

The environment is an undirected graph

```
G = (V, E)
```

- `V` — the set of zone nodes. Each zone `u ∈ V` carries:
  - a position embedding `pos(u) = (x_u, y_u) ∈ ℤ²`,
  - a capacity `capV(u) ∈ ℕ⁺ ∪ {∞}`, where `∞` means unbounded; `0` means
    the node rejects all entry for that tick.
- `E` — the set of choke edges (undirected). Each edge `e ∈ E` (between two
  zones `u, v`) carries a capacity `capE(e) ∈ ℕ⁺ ∪ {∞}`, where `∞` is
  unbounded and `0` means the edge is closed for that tick. Edge capacity
  gates **both** crossing directions of the same edge (a single-lane choke).
- A resource set `ρ` maps each resource `r` to exactly one hosting zone
  `home(r) ∈ V`.

The graph is a topology, not a grid: the coordinate embedding `pos` is used
only for the kinematic edge length below, never for adjacency or navigation.
Adjacency is the edge relation `E` alone.

### 1.2 The agent population

The environment hosts `N` agents, `N ∈ [2, 4]`. Each agent
`i ∈ { 0, …, N−1 }` has a kinematic state at any tick:

```
a_i = (u_i, s_i, κ_i)
```

- `u_i ∈ V` — the agent's zone of record.
- `s_i ∈ ℕ` — the agent's score (resources collected so far).
- `κ_i` — the transit state: `κ_i = ⊥` means "resident at node `u_i`";
  `κ_i = (from, to, k)` means "mid-edge travelling from `from` to `to` with
  `k` ticks remaining until arrival".

Occupancy rule: an agent with `κ_i = ⊥` is an occupant of node `u_i`. An agent
with `κ_i = (from, to, k)` is **not** an occupant of any node; while
transiting it still *reports* `u_i = from` (its departure node) but counts
only against the edge capacity of `{from, to}`, never against a node's
capacity, and it cannot act (Section 2.3).

### 1.3 Resources and claims

At tick `t`, a set `R̲(t) ⊆ ρ` records which resource ids have been claimed.
A resource is either claimed (`r ∈ R̲(t)`) or unclaimed; there is no third
state, and no resource can ever be claimed twice.

### 1.4 Configuration

The episode configuration is the tuple

```
config = ⟨ N, τ_max, Vh, s ⟩
```

- `N` — agent count (validated to `[2, 4]`).
- `τ_max ∈ ℕ⁺` — maximum tick budget.
- `Vh` — the perception horizon in graph hops: `∞` (unbounded) or an integer
  `≥ 1`.
- `s` — the transit speed: `0` (instantaneous traversal, the default) or an
  integer `≥ 1`.

### 1.5 The state tuple

The complete state at tick `t` is

```
S(t) = ⟨ G, A(t), R̲(t), t, config ⟩
```

where `A(t) = (a₀(t), …, a_{N−1}(t))`. The per-tick dynamic choke policy
(Section 1.6) is deterministic in `(t, R̲(t), G, policy)` and is folded into
the effective evaluation of `S(t)`.

### 1.6 Dynamic choke policy

A policy may override the base capacity of any edge per tick. Two
deterministic rule shapes are part of the contract:

1. **Timed oscillation.** An edge `e` cycles with period
   `cyc = T₀ + Tᶜ`: it holds capacity `c⁺` for the first `T₀` ticks of each
   cycle and capacity `c⁻` for the following `Tᶜ` ticks, repeating forever:

   ```
   C(e, t) = c⁺   if (t mod cyc) < T₀
           = c⁻   otherwise
   ```

   Constraints: `T₀ ≥ 1`, `Tᶜ ≥ 1`, `c⁺ ≥ 0`, `c⁻ ≥ 0`, and
   `cyc ≤ 2³¹ − 1` so the cycle arithmetic is exact.

2. **Event lock.** An edge `e` is governed by a trigger resource `r*`: once
   `r* ∈ R̲(t)`, the edge holds the locked capacity `c^lock ≥ 0` (default `0`)
   for all subsequent ticks; until then the base capacity applies.

Rules are evaluated **last-in-list-wins**: when several rules govern the same
edge, the one appearing last in declaration order determines `C(e, t)`. When
no rule governs an edge, `C(e, t) = capE(e)`. Rule parameters are validated
at binding time: non-negative choke and resource identifiers, non-negative
capacities, and windows whose sum fits a signed 32-bit integer. A rule
referencing an edge that does not exist in `G` is rejected at binding.

The state carries the capacity snapshot `C(e, t)` for every edge for the tick
being executed; the snapshot for the next tick is recomputed from the policy
against the updated claims.

---

## 2. Core Transition Invariant Families

The step transition is a pure, total function:

```
step : (S(t), actions) ⟼ (S(t+1), result(t+1))
```

It is **total**: every action vector yields a valid next state. Invalid or
missing entries in the action vector are treated as a no-op (`Wait`) — an
action vector can never crash or wedge the transition.

The action space for agent `i` at tick `t` is

```
Act = { Wait } ∪ { Move(v) : v ∈ N(u_i) } ∪ { Collect(r) : r ∈ ρ }
```

- `Move(v)` is meaningful only to an adjacent node `v ∈ N(u_i)` (exactly one
  edge away); a `Move` to a non-adjacent node is ignored (the agent stays).
- `Collect(r)` targets exactly one resource id and succeeds only under the
  conditions of Invariant 3.
- `Wait` is idle.

### 2.1 Invariant 1 — Legal kinematic transition & choke capacity

**Edge traversal delay.** Traversing edge `e = {u, v}` takes

```
delay(e) = 0                                  if s = 0   (instantaneous)
         = max(1, ⌈ manhattan(u, v) / s ⌉)    if s ≥ 1   (kinematic)
```

where `manhattan(u, v) = |x_u − x_v| + |y_u − y_v|` and the ceiling is exact
integer arithmetic

```
⌈ m / s ⌉ = (m + s − 1) div s
```

A `Move` granted with `delay ≤ 1` arrives the same tick. A `Move` granted
with `delay ≥ 2` places the agent into transit `κ_i = (u, v, delay − 1)`; it
holds the edge for the whole remaining crossing and arrives when the count
reaches zero. While `κ_i ≠ ⊥` the agent takes no further actions until
arrival.

**Arrival accounting.** `RemainingTicks = delay − 1` counts ticks *including*
the arrival tick: the agent arrives at the end of the tick in which its count
reaches `0`, and on that final tick its conveyance still consumes one edge
slot (the edge slot is held for the full tick; it is not freed mid-crossing).

**Instantaneous choke capacity.** Let

```
L_pre(e, t)  =  number of agents in transit on edge e at the start of tick t
```

and let `C(e, t)` be the effective capacity of the edge for tick `t`
(Section 1.6), which is always `≥ 0` by construction. The residual admission
capacity of the edge is

```
C_eff(e, t) = max(0, C(e, t) − L_pre(e, t))
```

A crossing into edge `e` is admitted at tick `t` only when the edge is open
and a residual slot exists:

```
granted(e, t)  ⟺  C(e, t) > 0  ∧  L_pre(e, t) < C(e, t)
```

Equivalently, if `G(e, t)` is the number of crossings granted into `e` during
tick `t`:

```
L_pre(e, t) + G(e, t) ≤ C(e, t)        (the edge is never over capacity)
G(e, t) ≤ C_eff(e, t) = max(0, C(e, t) − L_pre(e, t))
```

Two enforced guarantees make the gate total and live:

1. **The residual is never negative.** Capacity is non-negative by validation
   and the admission test requires `L_pre(e, t) < C(e, t)`, so `C_eff(e, t) ≥
   0` always; a choke cannot be driven into a negative-capacity state.
2. **No livelock under opposition.** When two agents request the same
   capacity-1 choke from opposite sides in one tick, the higher-priority agent
   (Section 2.2) is admitted first and reserves the edge; the yielding agent
   remains stationary in its origin node and re-attempts on a later tick. The
   priority rotation guarantees a first resolver exists every tick, so two
   opposing crossings can neither deadlock nor livelock.

**Entry-permission gating vs. destination-node convergence.** These are two
distinct gates and an oracle must not conflate them:

- **Entry-permission (edge gate).** The admission test above controls access
  to the *edge*; it says nothing about merging on the far side.
- **Destination-node convergence (node gate).** Independent of the edge gate,
  a granted `Move` must also satisfy the destination zone's capacity in the
  same tick:

  ```
  nodeOpen(v)  ⟺  capV(v) > 0  ∧  load(v, t) < capV(v)
  ```

  where `load(v, t)` counts resident agents at `v` (agents not in transit) at
  the start of the tick, **plus** any entry reservations granted into `v`
  earlier in this tick's resolution (earlier grants reserve their slots).
  A `Move` is granted only when **both** gates pass:
  `travelOpen(e, t) ∧ nodeOpen(v)`.

  A crossing is therefore not granted merely because the edge is free: the
  destination node's residual capacity at the exact tick is a separate,
  conjunctive requirement. And the edge slot is consumed for the *full*
  crossing: an agent that begins tick `t` in transit on `e` counts against
  `L_pre(e, t)` even on its final (arrival) tick.

**Departure occupancy.** While transiting, the agent retains its departure
node's occupancy for the whole tick; the slot is freed only by the next tick's
recount from the new state.

### 2.2 Invariant 2 — Deterministic tie-breaking & conflict resolution

All contention in a tick — choke admission, node admission in resolution
order, and collection — resolves in one shared, deterministic order that is a
**pure function of the tick**. There is no random source and no
host-dependent jitter anywhere in resolution.

Assign agent `i` the priority

```
π_i(t) = (i + t) mod N        (nonnegative modulo)
```

at tick `t`. Resolution processes the agents in **ascending `π` order**: the
agent with `π = 0` first, then `1`, …, up to `N−1`. Equivalently, the
resolution sequence is the rotation of the agent ids starting at

```
first(t) = (−t) mod N
```

so the full sequence is

```
( (−t) mod N, (−t+1) mod N, …, (−t+N−1) mod N )
```

Properties an oracle must reproduce:

- **Injectivity.** For fixed `t`, `π_i(t)` is a bijection over
  `{ 0, …, N−1 }`. Two distinct agents never share the same priority in the
  same tick, so every simultaneous conflict has a unique, deterministic
  winner by construction.
- **Rotation, not privilege.** The order returns to `0, 1, …, N−1` whenever
  `t ≡ 0 (mod N)`. No agent index holds a permanent tie advantage; the first
  resolver rotates across ticks.
- **Host-invariance.** The order is a modular shift of the integer tick, so
  it is identical on every host and every runtime.
- **Seat-symmetry.** Mirroring the spawn seats of two agents swaps their ids
  deterministically before the episode; the priority rule itself is symmetric
  under that swap, so paired mirror-seated studies compare the same ordering
  law.

### 2.3 Invariant 3 — Resource claim conservation & monotonicity

**Conservation.** Let `|ρ|` be the total initial resource count. At every
tick

```
|R̲(t)| + ( |ρ| − |R̲(t)| ) = |ρ|
Σ over agents of s_i(t) = |R̲(t)|
```

That is, `Σ unclaimed + Σ collected = |ρ|` for the initial resource set, and
the sum of agent scores equals the number of claimed resources. Because every
claim adds exactly one to exactly one agent's score, total score grows by one
per new claim and never otherwise changes.

**Score monotonicity.** For every agent `i` and every tick transition

```
s_i(t+1) ≥ s_i(t)
```

A score never decreases; it increments by exactly `1` on a successful
`Collect` and is otherwise unchanged.

**Spatial prerequisite for a claim.** A `Collect(r)` by agent `i` succeeds at
tick `t` only when all of the following hold:

1. the agent is **resident** (not in transit) at the resource's hosting zone
   after movement this tick: `κ_i = ⊥` and `u_i = home(r)`, with `u_i` the
   post-move zone;
2. the resource is unclaimed: `r ∉ R̲(t)`;
3. the resource id `r` exists in `ρ`.

A transiting agent — even one whose departure node hosts `r` — cannot collect;
transit blocks all action until arrival.

**One claim per resource per tick.** When multiple agents issue
`Collect(r)` for the same unclaimed resource in the same tick, exactly the
highest-priority one (Section 2.2) wins: its score increments by `1`, the
resource is appended to `R̲(t+1)`, and it earns the tick's reward for that
resource. Every other contender earns no claim and no reward for that
resource. Because priorities are injective, the winner is unique.

**Rewards.** The per-tick reward for a successful claim is `+1`; every other
agent earns `0` for that tick. There is no negative reward and no reward for
anything other than a successful claim.

### 2.4 Invariant 4 — Graph-reachability perception boundary (Dec-POMDP)

Perception is defined strictly as a **graph reachability operator** over the
topology embedding, in **unweighted graph hops**:

Let `d_G(u, v)` be the shortest path length in edges between zones `u` and
`v` in the adjacency graph `G` (Section 1.1). An entity (zone, resource, or
agent) located in zone `u` is observable to an agent whose current position
records zone `v` in tick `t` if and only if

```
d_G(u, v) ≤ Vh
```

with the agent's own zone always observable (`d_G(v, v) = 0 ≤ Vh`). When
`Vh = ∞`, every zone is observable.

The operator is a breadth-first exploration of the adjacency graph from `v` to
depth `Vh`, with neighbors expanded in **ascending zone id order** at every
node, making the observable set deterministic and independent of input order.

An oracle must respect these boundary statements:

- **The reachability graph is the static adjacency relation.** The edge set
  used for perception is exactly the base topology `E`. Instantaneous
  capacity — including dynamic choke overrides (portcullises, locks) — is
  **never** consulted by the perception operator. Reachability for perception
  is invariant to gate state: the BFS cannot "see" that a gate is open or
  closed on any tick, including the exact tick of a state transition.
- **Euclidean distance is expressly not a valid oracle.** Neither the
  perception boundary nor any transition rule uses Euclidean distance. The
  only geometric uses are (a) the Manhattan length in the kinematic `delay`
  (Section 2.1) and (b) the position embedding for agent-facing tooling.
  Observability is hop-count over `E` and hop-count over `E` alone.
- **Capacity does not prune the perception graph.** A closed or saturated
  edge still transmits observability. If an independent implementation
  claims gate-aware perception, it does not implement this contract.

(An oracle builder should treat this boundary as a deliberately falsifiable
claim; challenge question 2 in Section 3 is exactly this test.)

### 2.5 Invariant 5 — State-step determinism vs. replay equivalence

**State-step determinism.** The transition is a pure total function:
identical `(S(t), actions)` produce identical `S(t+1)` and identical
`result(t+1)`. This is a static property of the transition law; it does not
depend on the host, the runtime, or any ambient randomness, because there is
no ambient randomness anywhere in the law.

**Replay equivalence.** A recorded episode is verified by a replay that
reconstructs `S(0)` from the episode header (map, seed-derived topology,
simulation configuration, and the dynamic choke policy), feeds each recorded
action vector through `step`, and compares the replayed output to the
recording. The comparison is **per-tick and two-layered**: the serialized
`StepResult` stream is compared for every tick, and — where the recording
carries one (schema 3 and later) — the SHA-256 digest of the complete
simulation state at the end of that tick is recomputed and compared as well.

The precise contract:

- equivalence is evaluated over the per-step serialized `StepResult` stream
  (each recorded tick's serialized result equals the replayed tick's
  serialized result);
- where a per-tick state hash is present, the replayed state's digest must
  equal the recorded digest, so the tick's world — zone and resource positions,
  occupancy, per-tick choke capacities and the derived per-tick edge load,
  scores, claims, the episode seed and the tick — is authenticated, not just
  the results. The digest covers the **post**-step state, which is the state the
  tick's own observation describes, so the final recorded tick's hash also
  pins `S(T)`;
- it is **not** raw file-byte identity across hosts;
- a recording made before the digest field existed verifies on the serialized
  `StepResult` stream alone and says so explicitly
  (`no state hash: step-level verification only`), so a pre-hash file is
  never mistaken for a fully state-verified one. A recording that *declares*
  schema 3 or later but carries no digest is reported as a discrepancy instead,
  so deleting the digests cannot be used to downgrade the check.

The state digest is a SHA-256 over a canonical serialization with fixed field
order, invariant-culture integer formatting and no floating-point values, so it
is stable across locales and hosts. It covers the zone and resource
`GridPoint` X/Y positions, because they are state and not rendering:
`TransitTicks` reads `Zone.Position` for a crossing's kinematic length, and the
perception filter reads both positions to build the observations a step line
carries. What it excludes is the demonstration-layer `Role` label on zones,
resources and choke points, which the step contract never reads, and the
header's `SimulationConfig` and `DynamicMapRuleSet` — so the digest attests to
the state each tick produced, not to the whole episode configuration.

Terminal states: an episode is terminal at the first tick where either (a)
`|ρ| > 0` and every resource is claimed (`|R̲(t)| = |ρ|`), or (b)
`t ≥ τ_max`. The winner is the agent with the strictly greatest score
(`s_i`); ties select the lowest agent id.

---

## 3. Clean-Room Verification Guide & External Challenge Questions

An independent oracle is a program that implements the transition law of
Section 2 and checks the invariants as predicates on its own output. It must
be derivable from this document alone. A minimal oracle computes, for any
`(S(t), actions)`:

1. `S(t+1)` per Sections 2.1–2.2 (movement, transit, admission, claims);
2. the `result(t+1)` stream (per-agent reward, step metadata) per Section 2.3;
3. the perception projections per Section 2.4.

Then it verifies that its own output satisfies every invariant — or, against a
recorded Lattice episode, that its `step` output serializes to per-step
results equal to the recording's (Section 2.5).

### 3.1 Challenge question 1 — choke admission: deadlock or negative capacity?

**Question.** Can the choke admission gate of Section 2.1 be forced into a
deadlock or a negative-capacity state under cyclic or opposing multi-agent
movement — e.g. two agents perpetually crossing a capacity-1 choke in
opposite directions, or a closed event-lock sealing agents behind it?

**Anchored expectation.** No. The residual is clamped `C_eff(e, t) = max(0,
C(e, t) − L_pre(e, t)) ≥ 0` at every tick, admission requires
`L_pre(e, t) < C(e, t)`, and opposing crossings resolve in the priority order
of Section 2.2 — the first resolver is granted, the other yields in place and
re-attempts later — so the toy scenario terminates in a boundary crossing
within a bounded number of ticks. The transition is total: no tick can render
the state machine unreachable.

**How to falsify.** Produce a finite state/topology/config/action-sequence in
which the transition law yields (a) an edge with instantaneous residual
below `0`, or (b) an agent with no legal action progress and no clock
advance, or (c) a cyclic movement pattern where the priority rotation grants
*nobody* passage for `2N` consecutive ticks.

### 3.2 Challenge question 2 — perception reachability across gate transitions

**Question.** Does the Section 2.4 BFS reachability operator leak state
information across dynamic choke transitions on the exact tick of gate
closure — i.e. does an agent observe zones *through* a portcullis/lock edge
on the tick the gate seals, or observe stale information inconsistent with the
operator's own boundary?

**Anchored expectation.** No, and this is the sharpest boundary to test. The
perception operator is a BFS over the **static** adjacency relation `E`; it
never consults instantaneous capacity. Consequently the observable zone set
of an agent is *identical* on the tick before, the tick of, and the tick
after a gate transitions: the operator is invariant to gate state. There is
no flow "across" a closed gate in the perception sense, because the operator
does not model gate state at all. If a reviewer's experiment shows the
observable set *changing* with gate state, either the reviewer's oracle
consulted capacity (and therefore does not implement this contract) or a
genuine divergence exists — which is a finding to report, not to explain
away.

**How to falsify.** Build a tiny graph: zones `a—b—c`, an event lock on edge
`b—c` with trigger `r` in `a`, perpendicular or parallel paths placing agent
`X` in `a` and resource `r` in `b`. Confirm `X` sees `c` if and only if
`d_G(a, c) ≤ Vh`, and that claiming `r` (sealing `b—c`) does not change `X`'s
observable set in the same tick or later ticks.

### 3.3 Challenge question 3 — simultaneous Collect consistency

**Question.** Can two (or more) agents issuing `Collect(r)` for the same
unclaimed resource `r` in the same tick — under any priorities — produce an
inconsistent scoring state (double claim, negative score, conservation
violation, or a claim without a count)?

**Anchored expectation.** No. Priorities `π_i(t) = (i + t) mod N` are a
bijection, so the contenders are totally ordered and exactly the first one
wins; `R̲` grows by exactly one element, exactly one score increments by `1`,
and conservation (`Σ s_i = |R̲|`) holds at every tick. "Identical priorities"
is impossible by construction. Only a resident (non-transiting) agent
occupying `home(r)` after movement can even attempt the claim, so the spatial
prerequisite can never be bypassed.

**How to falsify.** Construct any `N ∈ [2,4]`, any zone/resource layout, and
any action vector in which two agents `Collect` the same valid, unclaimed
resource in one tick and the resulting state violates conservation,
monotonicity, or the one-claim-per-resource rule.

### 3.4 Submitting an independent reimplementation or oracle report

Clean-room verification reports are submitted through the public issue
tracker:

```
https://github.com/candavere/lattice/issues/new
```

Title convention: `Independent oracle report — <invariant or scope>`.

A report should contain, at minimum:

1. **Method.** The language and data structures of the oracle, and a
   statement that it was derived from this specification alone (no production
   source consulted), or an explicit disclosure if the implementation was
   validated against release outputs.
2. **Corpus.** The test corpus: hand-built topologies plus, if used, commands
   run against the published release (see the reproduction challenge packet
   for the asset-only procedure).
3. **Per-invariant verdicts.** For each of Invariants 1–5: `holds` / `fails`,
   with the smallest counterexample when `fails` (state, topology, config,
   action vector, tick).
4. **Challenge question verdicts.** Answers to questions 1–3 with the exact
   constructions used.
5. **Divergences.** Any place where the oracle's `step` output differs from a
   release binary's recorded per-step serialized results, with the minimal
   reproducing input.

Maintainers will treat any reported counterexample or divergence as an
evidence item and respond with either a corrected specification, a corrected
implementation, or a documented reproduction — never by retagging or
rewriting a published release.

---

## 4. Normative summary

| # | Invariant | Core law |
| --- | --- | --- |
| 1 | Kinematic transition & choke capacity | `delay = max(1, ⌈manhattan/s⌉)`; admit iff `C > 0 ∧ L_pre < C`; `C_eff = max(0, C − L_pre)`; edge and node gates both required; no over-capacity, no deadlock. |
| 2 | Deterministic conflict resolution | `π_i(t) = (i + t) mod N`, ascending order; injective, rotating, host-invariant. |
| 3 | Resource conservation & score monotonicity | `Σ s_i = |R̲|`, `s_i(t+1) ≥ s_i(t)`, claim requires residency at `home(r)` post-move; one claim per resource per tick. |
| 4 | Graph-reachability perception | observable ⟺ `d_G(u, v) ≤ Vh` over static `E`; Euclidean distance is not an oracle; gate state never enters the operator. |
| 5 | Determinism vs. replay equivalence | identical `(S, actions) →` identical next state and result; equivalence is per-step serialized `StepResult` equality plus, where recorded, a per-tick SHA-256 state-digest equality over the canonical state serialization. |

End of specification.