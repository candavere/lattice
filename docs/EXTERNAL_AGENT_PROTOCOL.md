# External Agent Protocol v1

**Status:** normative specification, committed before implementation.
**Scope:** how a process that is not part of this repository plays Lattice as an
agent, and what Lattice owes it in return.

This document is the contract. Every requirement is written in MUST / MUST NOT
language and is binding on both sides: Lattice (the host) and any external
agent implementation. Where this document and the implementation disagree, the
implementation is non-conforming until this document or the implementation is
changed — never silently reconciled.

The normative source for the *step contract* itself remains
[`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md) and
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md). This document adds
only the process boundary: framing, handshake, message shapes, limits, failure
accounting. The simulation rules are unchanged by anything written here.

Design rationale for the load-bearing choices lives in
[`adr/0005-external-agent-wire-contract.md`](adr/0005-external-agent-wire-contract.md).

---

## 1. Transport

Lattice MUST run the external agent as a child process and MUST communicate
with it exclusively over that process's **stdin** and **stdout**.

- The encoding MUST be **UTF-8** with **no byte-order mark** on either stream.
- The framing MUST be **newline-delimited JSON**: exactly one JSON value per
  line, terminated by a single **LF** (`0x0A`).
- A **CR** (`0x0D`) MUST NOT be used as a terminator and MUST NOT appear at the
  end of a line. A trailing CR is a framing violation, not whitespace to be
  trimmed.
- A JSON string value MUST NOT contain a raw `LF` or `CR`; both MUST be escaped
  (`\n`, `\r`). This is a direct consequence of one-message-per-line framing and
  is not optional.
- Lines MUST NOT be padded, indented for alignment, or joined. Lattice MUST NOT
  emit a blank line, and an agent that reads a blank line MUST treat it as a
  framing violation rather than skipping it.
- There MUST be no other channel. In particular Lattice MUST NOT pass a socket,
  a shared file handle, a named pipe, or an environment-injected control channel
  to the agent for the purpose of exchanging protocol messages.

### 1.1 stderr

The agent's **stderr is diagnostic only**. Lattice MUST NOT parse it, MUST NOT
interpret it as protocol, and MUST NOT allow its contents to influence the
action stream in any way. Lattice MUST capture stderr into a **bounded ring
buffer** so that the tail of an agent's diagnostics survives into the failure
report.

The ring capacity is a constant fixed in stage 3 and is **not settled by this
document** (see §14, U-4). No number is asserted here.

An agent that writes to stderr MUST NOT block as a result: Lattice MUST drain
stderr continuously for the life of the process, and MUST terminate the process
if draining it is the only thing keeping it alive.

### 1.2 Direction of flow

Lattice is the driver. At every step Lattice writes one `observation` line to
each connected agent process and reads exactly one `action` line back. The
agent MUST NOT write to stdout at any other time, and MUST NOT write more than
one line per `observation` it receives.

When more than one agent slot is played by an external process, each slot is a
**separate process** with its own stdin/stdout pair, and Lattice MUST poll them
in ascending agent-slot id order within a step, so that any interleaving of
process scheduling cannot change the order in which actions are collected. This
mirrors the in-process rule that agents are polled in ascending id
(`Agents/ScenarioRunner.cs:53-56`).

---

## 2. Protocol version

The wire contract is versioned by a single **integer**, carried as
`"protocol": 1` in the handshake. The version is an integer, not a string, so
that `1` and `"1"` are distinguishable and a string form is a schema violation.

**Lattice is authoritative.** Negotiation is an **exact match**:

- Lattice sends `hello` with `"protocol": 1`.
- The agent MUST reply `hello_ack` with `"protocol": 1`.
- Any other **value** — a different integer, a string, a float, or a missing
  `protocol` field — MUST be **refused**. Lattice MUST terminate the match and
  record the reason `protocol_mismatch`.
- A `hello_ack` that **never arrives** is a **timeout**, not a mismatch, and
  has its own reason code: `timeout_handshake`, raised when no valid `hello_ack`
  is readable within `step_timeout_ms` of `hello` being written. It is scored as
  an **agent failure** (§9), on the same footing as `timeout_step`. An *invalid*
  `hello_ack` is not affected by this code: a `hello_ack` that arrives and
  carries a wrong `protocol` value is `protocol_mismatch` (§2), and one that
  violates the message schema is `schema_violation` (§8).
- There is **no downgrade path**. Lattice MUST NOT fall back to an older
  protocol, MUST NOT offer a list of acceptable versions, and MUST NOT accept a
  version the agent chose unilaterally.

A protocol mismatch is scored as an **agent failure** (§9), not as a host
misconfiguration.

---

## 3. Handshake sequence

```
Lattice                                   Agent
   |                                          |
   |  ---- hello ---------------------------->|
   |                                          |
   |  <--- hello_ack ------------------------ |
   |                                          |
   |  ---- observation (step 0) ------------->|
   |  <--- action (step 0) -------------------|
   |                                          |
   |          ... one exchange per step ...  |
   |                                          |
   |  ---- observation (step N-1) ---------->|
   |  <--- action (step N-1) -----------------|
   |                                          |
   |  (on failure, optionally:)              |
   |  ---- error --------------------------->|
   |                                          |
   X  close stdin, wait, terminate            X
```

Requirements:

- **Process lifetime is one process per match.** Lattice MUST launch a fresh
  agent process for every match, send it `hello` exactly once, and terminate it
  at the end of that match. A process MUST NOT be reused across matches, across
  seed pairings, or across the mirrored seatings of one seed. This is what
  guarantees that no state an agent accumulated — a cache, an RNG stream, a
  learned table — can carry over from one experiment into the next, and it
  mirrors the in-process factory contract, which already builds a fresh agent
  per (pairing, seed) precisely so that RNG streams cannot leak between matches
  (`Agents/IAgentFactory.cs:12-23`, `Agents/EvaluationHarness.cs:175`). It also
  makes isolation unconditional: one agent's crash, hang, or memory growth can
  never affect another match. A suite-scoped process would need a reset message,
  and the five-type catalogue (§4) has no such type; that would be a protocol-2
  change. See §14, U-8.
- Lattice MUST send `hello` exactly once per process, before any `observation`.
- The agent MUST send `hello_ack` exactly once, before any `action`, and MUST
  send nothing else until it receives its first `observation`.
- Lattice MUST NOT send an `observation` before a valid `hello_ack`.
- Lattice MUST read and consume the final `action` line of the last step before
  it closes the stream, so that an agent which always answers is never reported
  as having exited.
- Lattice MUST send an `error` line before terminating **only when it has
  detected a failure**. A match that ends normally MUST be terminated by
  closing the stream, not by an `error`.
- The agent has no `error` message of its own. The `error` type is
  Lattice-to-agent only.

### 3.1 `hello`

| Field | Type | Required | Meaning |
| :--- | :--- | :--- | :--- |
| `type` | string | yes | Exactly `"hello"`. |
| `protocol` | integer | yes | Exactly `1`. |
| `scenario` | string | yes | The evaluation scenario family that selected the map. In v3.0 the closed set is `"standard"` and `"bottleneck"` (`Cli/CliApp.cs:736-742`). |
| `seed` | integer | yes | The run seed, an unsigned 64-bit value (`Agents/EvaluationHarness.cs:29`). MUST be a JSON number with no fraction, no exponent, no leading `+`, and no leading zeros. |
| `agent_slot` | integer | yes | The agent slot this process plays, in `0..AgentCount-1` (`Environment/Simulation.cs:58-60`). In the `evaluate` path this is exactly `0` or `1`, because evaluation pairings are head-to-head (`Agents/EvaluationHarness.cs:112-117`). |
| `max_ticks` | integer ≥ 1 | yes | The match's tick budget, from `SimulationConfig.MaxTicks` (`Environment/Simulation.cs:28`). It is the **same value** as `EvaluationSimulationConfig.MaxTicks`, which is the per-match `MaxSteps` the harness passes down (`Agents/EvaluationHarness.cs:147-148`). This is the horizon an agent plans against; §7's `match_timeout_ms ≥ step_timeout_ms × MaxTicks` constraint is computed from this number. |
| `agent_count` | integer ≥ 2 | yes | The number of agents in the match, from `SimulationConfig.AgentCount` (`Environment/Simulation.cs:25`), which is bounded to 2..4 (`Environment/Simulation.cs:58-60`). It is always `2` on the `evaluate` path (`Agents/EvaluationHarness.cs:112-117`); the field is carried so an agent can size its model of the episode without inferring the count from `agent_states[]` length. |
| `limits` | object | yes | The two named time limits for this match (§7). |

`limits` has exactly two fields and no others:

| Field | Type | Meaning |
| :--- | :--- | :--- |
| `step_timeout_ms` | integer ≥ 1 | Wall-clock milliseconds Lattice will wait for one `action` line after writing an `observation`. |
| `match_timeout_ms` | integer ≥ 1 | Wall-clock milliseconds Lattice will allow for the whole match, measured from the moment `hello` is written. |

The **values** of `step_timeout_ms` and `match_timeout_ms` are chosen in stage 3
and recorded there. This document fixes the field names, types, and semantics
and asserts no number. See §14, U-2.

`scenario` is the closed v3.0 set. The `infiltration` scenario is **not**
reachable through the external-agent path in v3.0: it is a `simulate`-only
roster and is explicitly separate from the `evaluate` path
(`Cli/CliApp.cs:291-293`, `Cli/CliApp.cs:736-742`).

`hello` carries **no map**. The map arrives with the first `observation` (§5.3),
which is re-sent in full on every step. What `hello` does carry beyond its
identity fields is the two numbers an agent cannot otherwise learn:
`max_ticks`, the horizon it is playing against, and `agent_count`, the size of
the episode. Both are **required** — under the strictness rule in §8.3 a
protocol-1 agent that omits either is refused with `schema_violation`, and one
that sends either under a different name is refused with `unknown_field`. This
closes the usability gap recorded as §14, U-3: an agent that plans a horizon now
knows the horizon from the handshake rather than by running out of steps.

---

## 4. Message catalogue

There are exactly **five** message types. The set is closed: a `type` outside
it is a schema violation (§8).

| `type` | Direction | Purpose |
| :--- | :--- | :--- |
| `hello` | Lattice → agent | Opens the session; declares protocol, scenario, seed, slot, limits. |
| `hello_ack` | agent → Lattice | Confirms the exact protocol version. |
| `observation` | Lattice → agent | Everything the agent may see at one tick. |
| `action` | agent → Lattice | The agent's request for that tick. |
| `error` | Lattice → agent | Optional; announces a named failure before termination. |

### 4.1 Example lines

The numeric limit values in the `hello` example are **illustrative only**; the
normative values are fixed in stage 3 (§14, U-2). The same is true of
`max_ticks` and `agent_count`, which are shown with a plausible but not
normative pair. Every other value in these examples is normative and matches
the field tables that follow.

**`hello`**

```json
{"type":"hello","protocol":1,"scenario":"standard","seed":1001,"agent_slot":0,"max_ticks":500,"agent_count":2,"limits":{"step_timeout_ms":5000,"match_timeout_ms":1200000}}
```

**`hello_ack`**

```json
{"type":"hello_ack","protocol":1}
```

**`observation`** (step 0, two zones, one resource, one capacity-1 choke; the
scenario-specific `role` fields are absent because they are null)

```json
{"type":"observation","step":0,"agent_id":0,"map":{"zones":[{"id":0,"position":{"x":0,"y":0},"max_occupancy":2147483647},{"id":1,"position":{"x":3,"y":0},"max_occupancy":2147483647}],"resources":[{"id":0,"zone_id":1,"position":{"x":3,"y":0}}],"choke_points":[{"id":0,"from_zone_id":0,"to_zone_id":1,"max_occupancy":1}]},"agent_states":[{"agent_id":0,"zone_id":0,"score":0},{"agent_id":1,"zone_id":1,"score":0}],"claims":[],"step_number":0}
```

**`action`**

```json
{"type":"action","step":0,"kind":"Move","zone_id":1}
```

**`error`**

```json
{"type":"error","reason":"step_mismatch","detail":"expected step 7, received step 3"}
```

---

## 5. Observation

### 5.1 Envelope

| Field | Type | Required | Provenance |
| :--- | :--- | :--- | :--- |
| `type` | string | yes | Exactly `"observation"`. |
| `step` | integer ≥ 0 | yes | The step this observation decides. **0-based** (§5.4). |

### 5.2 Flattening rule

The body is a **flattened projection of `Lattice.Environment.Observation`**
(`Environment/StepContracts.cs:62-67`). The rule is mechanical and total:

1. Every record member becomes a wire field.
2. The member name is converted from `PascalCase` to `snake_case`; no other
   renaming, abbreviation, or reordering occurs.
3. A nested record becomes a nested JSON object, flattened by the same rule.
4. An array of records becomes a JSON array of objects, flattened by the same
   rule. Array order is preserved exactly as the source array has it.
5. A `string?` member that is null MUST be **omitted** from the object, never
   emitted as `null`. This matches the repository's migration invariant that
   new optional fields are nullable and omitted when absent
   (`docs/SUPPORT_AND_REPRODUCIBILITY.md:232-239`) and the existing
   `JsonIgnoreCondition.WhenWritingNull` attributes on the source records
   (`Environment/MapData.cs:43`, `Environment/MapData.cs:59`,
   `Environment/MapData.cs:78`).
6. **No field is added, removed, renamed, defaulted, or derived.** The wire
   projection carries exactly the members below.

`ObservationView` (`Agents/ObservationView.cs:11`) stays `internal` and
**unchanged** by this protocol. It is cited here only to fix the meaning of two
derived quantities that an agent MUST be able to compute from the wire body
alone, because in-process agents depend on exactly these derivations
(`Agents/ObservationView.cs:18-29`, `Agents/ObservationView.cs:35-38`):

- **My zone** is the `agent_states[]` entry whose `agent_id` equals the
  observation's own `agent_id`. If no such entry exists the observation is
  inconsistent and the agent MUST treat it as a fatal error rather than
  guessing.
- **Unclaimed resources** are the `resources[]` entries whose `id` does not
  appear in `claims`, **in ascending `id` order**. The ordering is normative:
  it is what makes the in-process decision path deterministic, and an agent
  that iterates in a different order can produce a different action on the same
  observation.

These are semantics, not fields. They add nothing to the wire shape.

### 5.3 Field tables

**`Observation` body** — source: `Environment/StepContracts.cs:62-67`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `agent_id` | integer | yes | `int AgentId` | `Environment/StepContracts.cs:63` |
| `map` | object | yes | `MapGraph Map` | `Environment/StepContracts.cs:64` |
| `agent_states` | array of objects | yes | `AgentState[] AgentStates` | `Environment/StepContracts.cs:65` |
| `claims` | array of integers | yes | `int[] Claims` | `Environment/StepContracts.cs:66` |
| `step_number` | integer | yes | `int StepNumber` | `Environment/StepContracts.cs:67` |

**`map`** — source: `MapGraph`, `Environment/MapData.cs:85-88`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `zones` | array of objects | yes | `Zone[] Zones` | `Environment/MapData.cs:86` |
| `resources` | array of objects | yes | `ResourceNode[] Resources` | `Environment/MapData.cs:87` |
| `choke_points` | array of objects | yes | `ChokePoint[] ChokePoints` | `Environment/MapData.cs:88` |

**`zones[]`** — source: `Zone`, `Environment/MapData.cs:39-43`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `id` | integer | yes | `int Id` | `Environment/MapData.cs:40` |
| `position` | object | yes | `GridPoint Position` | `Environment/MapData.cs:41` |
| `max_occupancy` | integer | yes | `int MaxOccupancy` | `Environment/MapData.cs:42` |
| `role` | string or absent | no | `string? Role` | `Environment/MapData.cs:43` |

`max_occupancy` is an explicit capacity, never omitted. Its generator default is
`MapLimits.Unlimited` = `int.MaxValue` = `2147483647`
(`Environment/MapData.cs:14`); `0` means no entry is allowed
(`Environment/MapData.cs:31-32`).

**`resources[]`** — source: `ResourceNode`, `Environment/MapData.cs:55-59`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `id` | integer | yes | `int Id` | `Environment/MapData.cs:56` |
| `zone_id` | integer | yes | `int ZoneId` | `Environment/MapData.cs:57` |
| `position` | object | yes | `GridPoint Position` | `Environment/MapData.cs:58` |
| `role` | string or absent | no | `string? Role` | `Environment/MapData.cs:59` |

**`choke_points[]`** — source: `ChokePoint`, `Environment/MapData.cs:73-78`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `id` | integer | yes | `int Id` | `Environment/MapData.cs:74` |
| `from_zone_id` | integer | yes | `int FromZoneId` | `Environment/MapData.cs:75` |
| `to_zone_id` | integer | yes | `int ToZoneId` | `Environment/MapData.cs:76` |
| `max_occupancy` | integer | yes | `int MaxOccupancy` | `Environment/MapData.cs:77` |
| `role` | string or absent | no | `string? Role` | `Environment/MapData.cs:78` |

**`position`** — source: `GridPoint`, `Environment/MapData.cs:25`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `x` | integer | yes | `int X` | `Environment/MapData.cs:25` |
| `y` | integer | yes | `int Y` | `Environment/MapData.cs:25` |

**`agent_states[]`** — source: `AgentState`, `Environment/StepContracts.cs:50`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `agent_id` | integer | yes | `int AgentId` | `Environment/StepContracts.cs:50` |
| `zone_id` | integer | yes | `int ZoneId` | `Environment/StepContracts.cs:50` |
| `score` | integer | yes | `int Score` | `Environment/StepContracts.cs:50` |
| `transit` | object or absent | no | `InTransit? Transit` | `Environment/StepContracts.cs:50` |

**`transit`** — source: `InTransit`, `Environment/StepContracts.cs:41`

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `from_zone_id` | integer | yes | `int FromZoneId` | `Environment/StepContracts.cs:41` |
| `to_zone_id` | integer | yes | `int ToZoneId` | `Environment/StepContracts.cs:41` |
| `remaining_ticks` | integer ≥ 1 | yes | `int RemainingTicks` | `Environment/StepContracts.cs:41` |

### 5.4 Observation semantics

These are properties of the step contract, not new rules. They are stated here
because an external agent reads them off the wire and must not have to guess.

- **Full observability.** `agent_states` lists **every** agent in the episode,
  not just the observer, and `map` is the whole map. This is the existing
  full-observability design (`Environment/StepContracts.cs:52-60`) and it is
  what keeps rule-based agents simple and deterministic given the state. An
  external agent gets exactly what an in-process agent gets; it is not a
  reduced view.
- **The same body for every slot.** The per-agent `Observation` differs only
  in its own `agent_id`; the shared state is handed to every agent unchanged
  (`Agents/ScenarioRunner.cs:241-244`).
- **`agent_states` while in transit.** An agent that is mid-edge is **not** at
  a node. Its `zone_id` remains the **departure** node and `transit` describes
  the crossing (`Environment/StepContracts.cs:45-49`). An agent with
  `transit` absent is at a node and may act. An agent that ignores `transit`
  and re-plans from `zone_id` while it is crossing is acting on stale
  information, and will get the same result an in-process agent would.
- **`remaining_ticks` is never `0`.** A value of `1` means "arrives at the end
  of this tick"; a value of `0` never appears on an `AgentState`
  (`Environment/StepContracts.cs:35-40`).
- **`claims` is the list of claimed resource ids, not a per-resource mask**
  (`Environment/Simulation.cs:95-99`; `Environment/StepContracts.cs:57`).
  An empty array is `[]`, never absent.
- **The map is re-sent in full on every observation**, because `Observation`
  carries the whole `MapGraph` (`Environment/StepContracts.cs:64`). The wire is
  therefore O(map) per step. This is a cost the protocol accepts in exchange
  for the agent needing no out-of-band state.
- **Wire step numbers are 0-based; trajectory step numbers are 1-based.**
  The wire `step` is the loop index (`Agents/ScenarioRunner.cs:50`) and
  `step_number` is `Observation.StepNumber` (`Environment/StepContracts.cs:67`).
  Recorded trajectories are a different, 1-based numbering that must be
  contiguous from `1` (`docs/SUPPORT_AND_REPRODUCIBILITY.md:212`). The two MUST
  NOT be conflated. A recorded step `n` came from wire step `n - 1`.
- **`step` and `step_number` are always equal.** `step_number` is
  `SimulationState.StepCount` (`Agents/ScenarioRunner.cs:244`) and the runner's
  loop index advances in lockstep with it, one increment per
  `Simulation.Step` (`Environment/Simulation.cs:342`). An agent MAY read
  either; the value it MUST echo in its action is the envelope's `step`.

---

## 6. Action

### 6.1 Envelope and fields

The action maps **1:1** onto `Lattice.Environment.AgentAction`
(`Environment/StepContracts.cs:31`), which is
`AgentAction(ActionKind Kind, int ZoneId = -1, int ResourceId = -1)`.

| Wire field | Type | Required | Source member | Provenance |
| :--- | :--- | :--- | :--- | :--- |
| `type` | string | yes | — | Exactly `"action"`. |
| `step` | integer ≥ 0 | yes | — | MUST equal the `step` of the `observation` being answered. |
| `kind` | string | yes | `ActionKind Kind` | `Environment/StepContracts.cs:31` |
| `zone_id` | integer | conditional | `int ZoneId` | `Environment/StepContracts.cs:31` |
| `resource_id` | integer | conditional | `int ResourceId` | `Environment/StepContracts.cs:31` |

**`kind` strings.** The permitted values are exactly the `ActionKind` member
names, with that exact casing:

| Wire value | Underlying member | Provenance |
| :--- | :--- | :--- |
| `"Wait"` | `ActionKind.Wait` | `Environment/StepContracts.cs:12` |
| `"Move"` | `ActionKind.Move` | `Environment/StepContracts.cs:15` |
| `"Collect"` | `ActionKind.Collect` | `Environment/StepContracts.cs:18` |

No other string, and no other casing, is accepted. `"move"`, `"MOVE"`, `"Idle"`,
and the integer `1` are all schema violations. This keeps the `ActionKind`
unknown-value branch in `ActionSpace.Validate`
(`Environment/ActionSpace.cs:22-25`) unreachable from the wire; it stays as
defence in depth for in-process callers.

**Conditional fields.** The 1:1 mapping to `AgentAction` fixes the presence
rule, because the target fields default to `-1` and are otherwise ignored
(`Environment/StepContracts.cs:29-30`):

- `zone_id` is **required** when `kind` is `"Move"`.
- `zone_id` **MUST NOT** be present when `kind` is `"Wait"` or `"Collect"`.
- `resource_id` is **required** when `kind` is `"Collect"`.
- `resource_id` **MUST NOT** be present when `kind` is `"Wait"` or `"Move"`.
- An omitted conditional field maps to the record's `-1` default. `-1` is
  **not** a legal explicit value: it is the sentinel, not an addressable zone or
  resource, and an agent that sends it explicitly is violating the schema.

This is why the shape is uniform across kinds in the record
(`Environment/StepContracts.cs:22-24`) while the wire omits what does not apply:
the wire carries no field the record would ignore.

### 6.2 Step echo

The `step` in the action MUST equal the `step` in the `observation` it answers.
A mismatch is refused with reason `step_mismatch` (§8). Lattice MUST NOT accept
a stale or skipped step, and MUST NOT renumber an action to make it fit.

### 6.3 Action validity

Beyond the schema, an action MUST be inside the action space for the map.
`ActionSpace.Validate` checks exactly three things
(`Environment/ActionSpace.cs:18-38`):

| Check | Source |
| :--- | :--- |
| `kind` is a defined `ActionKind` value | `Environment/ActionSpace.cs:22-25` |
| `Move` targets a zone id in `0..Zones.Length-1` | `Environment/ActionSpace.cs:27-30` |
| `Collect` targets a resource id in `0..Resources.Length-1` | `Environment/ActionSpace.cs:32-35` |

`ActionSpace.Validate` checks **shape only**. It does not check whether a move
is to an adjacent zone, whether a choke is passable, or whether the agent is
currently in transit — the step function tolerates those as no-ops
(`Environment/ActionSpace.cs:6-9`). Lattice MUST NOT refuse an in-shape but
ineffective action; it MUST apply it, and the step function MUST resolve it
exactly as it does for an in-process agent.

An action that fails the table above is refused with reason `illegal_action`
(§8). Lattice MUST validate against `ActionSpace` at receipt, for the same
reason trajectories validate at record time and again at verify time
(`docs/SUPPORT_AND_REPRODUCIBILITY.md:215-217`): a step outside the action space
is rejected rather than replayed.

---

## 7. Limits

Four limits are normative in protocol v1. The first two are **constants of the
protocol**; the second two are **named, per-match values carried in
`hello.limits`** (§3.1).

| Limit | Value | Applies to | Enforced by | Reason on breach |
| :--- | :--- | :--- | :--- | :--- |
| `max_line_bytes` | `1048576` (1 MiB), **excluding** the terminating LF | every line, in both directions | Lattice | `line_too_long` (agent's line) / `host_limit` (Lattice's own line) |
| `max_json_depth` | `32` | every JSON value Lattice parses | Lattice | `depth_exceeded` |
| `step_timeout_ms` | stage-3 constant, carried in `hello.limits` | the wait for one `action` after one `observation`, and the wait for one `hello_ack` after one `hello` | Lattice | `timeout_step` (after an `observation`) / `timeout_handshake` (after `hello`) |
| `match_timeout_ms` | stage-3 constant, carried in `hello.limits` | the whole match, from `hello` written to termination | Lattice | `timeout_match` |

Notes that are binding:

- **`max_line_bytes` is measured excluding the LF.** A line of exactly
  `1048576` payload bytes is accepted; `1048577` is not. Lattice MUST count
  bytes, not characters: a multi-byte UTF-8 sequence counts as its encoded
  length. Lattice MUST refuse an over-long line and MUST NOT truncate it.
- **A conforming `observation` is nowhere near the depth cap.** The deepest
  path in the flattened shape is
  `observation → map → zones[] → position → x`: five nested containers with the
  scalar value at level 6. The other deep path,
  `observation → agent_states[] → transit → from_zone_id`, is four containers
  with its scalar at level 5. `32` is therefore headroom against a hostile or
  broken agent, not a constraint on legitimate traffic: `depth_exceeded` is
  reachable only from the agent's side.
- **`max_line_bytes` is a real ceiling on map size, not a formality.** Because
  the whole map is re-sent every step (§5.4) and the generator's zone and
  resource bounds are caller-configured with no upper ceiling
  (`Generator/MapGenerator.cs:13-25`), a sufficiently large generated map
  produces an `observation` line that exceeds 1 MiB. Lattice MUST refuse to
  start such a match rather than emit a line it has itself violated. That
  refusal is `host_limit` (§8): a **host** fault, recorded as a **void** run,
  and explicitly **not** a loss for the external agent. The reasoning is
  fairness, and it is the whole point of the code: the agent had no opportunity
  to influence how large Lattice's own map is, so charging it a loss would let a
  map-size choice in the host decide the agent's measured score. See §9.1, §9.3,
  and §14, U-7.
- **Lattice MUST check before it sends.** The `host_limit` check runs over the
  `observation` Lattice is about to write — specifically, over its encoded byte
  length against `max_line_bytes` and its nesting against `max_json_depth` —
  and it MUST run before any byte reaches the agent. Lattice MUST NOT write a
  line it has already determined exceeds a limit and then report the breach: the
  refusal exists precisely so that the agent's stream never sees a line Lattice
  considers illegal. The check is the mirror of the inbound enforcement Lattice
  applies to the agent's lines, and it uses the same two constants.
- **`match_timeout_ms` MUST be at least `step_timeout_ms × MaxTicks`**, where
  `MaxTicks` is the match's tick budget — the same number Lattice sends as
  `hello.max_ticks` (`Environment/Simulation.cs:28`,
  `Agents/EvaluationHarness.cs:147-148`). Otherwise the whole-match limit is
  guaranteed to fire before a single match could complete, and every external
  agent would be scored a loss for a limit Lattice itself mis-set.
- **An agent MUST honour `step_timeout_ms` in both directions.** An agent that
  needs longer to answer an `observation` — or to answer `hello` with a
  `hello_ack` — MUST fail; it MUST NOT hold the stream. Lattice MUST NOT extend
  a step timeout, and MUST NOT retry a step or a handshake (§9).

---

## 8. Error codes

The reason code set is **closed and machine-readable**. Lattice MUST use one of
these strings as `error.reason` and MUST NOT invent, extend, or re-purpose one.

The set is **fourteen** codes, and it is partitioned by **who is at fault**,
because that partition determines scoring (§9.3) and it must not be inferred
case by case. Thirteen are **agent-attributable** — the agent sent something
wrong, sent nothing in time, or stopped existing — and every one of them is
scored as a **loss** for the external agent. One is **host-attributable**:
`host_limit`, which fires when Lattice's own outbound message would breach a
limit and is recorded as a **void** run instead.

| `reason` | Fault | Meaning |
| :--- | :--- | :--- |
| `protocol_mismatch` | agent | The handshake version is not exactly `1` (§2). |
| `malformed_json` | agent | The line is not a single well-formed JSON value. |
| `schema_violation` | agent | The JSON is well formed but violates the message schema: a missing required field, a field present where the schema requires it absent, a wrong JSON type, an unknown `type` value, or an unknown `kind` value (§6.1). |
| `unknown_field` | agent | The object carries a member that is not in the schema for that type. |
| `line_too_long` | agent | A line the agent sent exceeded `max_line_bytes` (§7). |
| `depth_exceeded` | agent | A JSON value the agent sent exceeded `max_json_depth` (§7). |
| `step_mismatch` | agent | The action's `step` did not equal the `step` it answers (§6.2). |
| `illegal_action` | agent | The action is well formed but outside `ActionSpace` for this map (§6.3). |
| `timeout_handshake` | agent | No valid `hello_ack` arrived within `step_timeout_ms` of `hello` (§2, §7). |
| `timeout_step` | agent | No `action` arrived within `step_timeout_ms` of an `observation` (§7). |
| `timeout_match` | agent | The match exceeded `match_timeout_ms` (§7). |
| `agent_exited` | agent | The process closed its stdout, or exited with code `0`, before the exchange completed. |
| `agent_crashed` | agent | The process was terminated by a signal, or exited with a non-zero code, before the exchange completed. |
| `host_limit` | **host** | Lattice's own outbound message would have breached `max_line_bytes` or `max_json_depth`, so Lattice refused the match before sending (§7). **Void run; not a loss** (§9.1, §9.3). |

A `reason` is never attributed to the agent for a fault the agent did not
control, and the one place that could be argued — an outbound line that is too
long — is settled by the explicit `host_limit` code rather than by argument. The
`Fault` column is the normative statement; it is not a commentary.

### 8.1 The `error` message

| Field | Type | Required | Meaning |
| :--- | :--- | :--- | :--- |
| `type` | string | yes | Exactly `"error"`. |
| `reason` | string | yes | One of the fourteen codes above. |
| `detail` | string | yes | Human-readable text. **Diagnostic only: an agent MUST NOT parse it and MUST NOT branch on it.** It exists so a human debugging an agent can see what happened. |

Lattice MUST send `error` **before** terminating, whenever it has detected a
failure, and MUST NOT send it on a normal termination (§3). Sending `error` is
permitted, not required, on process-level failures where the stream is already
gone. It is likewise not required for `host_limit`: that refusal is detected
before `hello` is written, so in the ordinary case there is no exchange to
report into and the code appears in the run record and the reported void count
(§9.3) rather than on the wire.

### 8.2 Which code, when

The codes are distinguished so that a failure report says what kind of mistake
was made:

- **Framing** — `line_too_long`, `malformed_json`, `depth_exceeded`. The bytes
  were wrong before any meaning could be read.
- **Schema** — `schema_violation`, `unknown_field`. The bytes were JSON, but the
  document is not this contract. `unknown_field` is separated from
  `schema_violation` because a forward-incompatible agent that sends a field
  from a hypothetical protocol 2 is making a *different* mistake from one that
  omits a required field, and the two should be countable separately.
- **Sequence** — `protocol_mismatch`, `step_mismatch`. The document parsed and
  validated, but it is the wrong message, or the wrong step.
- **Semantics** — `illegal_action`. The document is a valid action that the map
  does not permit.
- **Timing** — `timeout_handshake`, `timeout_step`, `timeout_match`. Nothing
  arrived in time. The handshake is separated from the step loop because they are
  different exchanges with different causes, and a study that reports "how often
  did the agent fail to start" should not have to subtract that out of "how often
  did the agent stall mid-match".
- **Process** — `agent_exited`, `agent_crashed`. The peer stopped existing.
- **Host** — `host_limit`. Lattice refused its own message before sending. The
  only code in this list that is not the agent's fault, and the only one that
  does not become a loss (§9.3).

### 8.3 Strictness

This follows the repository's existing forward-compatibility policy: **forward
compatibility is refused, not guessed** (`docs/SUPPORT_AND_REPRODUCIBILITY.md:228-231`).
Accordingly:

- Lattice MUST refuse a message carrying a **field that is not in the schema**.
  It MUST NOT ignore it, MUST NOT log-and-continue, and MUST NOT treat it as a
  hint. Reason `unknown_field`.
- Lattice MUST refuse a message with a **missing required field**. It MUST NOT
  substitute a default. Reason `schema_violation`.
- Lattice MUST refuse a message whose **`type` is not one of the five**. Reason
  `schema_violation`.
- Lattice MUST refuse a message whose **`kind` is not one of the three member
  names**. Reason `schema_violation`.
- Lattice MUST refuse a **conditional field that the schema says must be
  absent**. Reason `schema_violation`.
- There is **no silent tolerance anywhere on the wire.** If an agent wants to
  be resilient to a future Lattice, the correct response to a new field is to
  fail loudly and loudly report which version it wants — not to be handed a
  document Lattice did not agree to.

### 8.4 Reason precedence

Exactly one reason is reported per failed match. When more than one condition
holds, the **first detected** is the one reported, in this precedence order:

1. `host_limit` — detected by Lattice over its own outbound message, before any
   byte is sent and therefore before the handshake exists.
2. `protocol_mismatch` — detected during the handshake, before any step.
3. `timeout_handshake` — the handshake's wait elapsed with no valid `hello_ack`.
4. Framing codes (`line_too_long`, `malformed_json`, `depth_exceeded`).
5. Schema codes (`schema_violation`, `unknown_field`).
6. `step_mismatch`, then `illegal_action`.
7. `timeout_step`, then `timeout_match`.
8. `agent_crashed`, then `agent_exited`.

A detected protocol violation therefore **outranks** the process-level
consequence of that violation. When Lattice detects a violation, writes
`error`, and then terminates the process, the reason is the violation — not
`agent_exited`. `agent_exited` and `agent_crashed` apply only when **no**
protocol violation was detected, i.e. the process died on its own. Between
those two, a signal or non-zero exit is `agent_crashed`; a clean exit or an EOF
on stdout is `agent_exited`.

`host_limit` is first because it is the only code for which the failing party
is Lattice, and because a match refused on the host's own limits never reaches
the wire — there is no agent behaviour for it to outrank. The three handshake
codes sit together at the top of the agent-attributable list because they are
the only ones that can fire before step 0.

---

## 9. Failure is a result, never a retry

### 9.1 The rule

Every **agent-attributable** failure listed in §8 — that is, every code except
`host_limit` — **MUST be recorded as a result and scored as a loss for the
external agent.** Specifically:

- Lattice MUST NOT silently retry a failed handshake, a failed step, or a
  crashed process. There is no second attempt, no backoff, and no
  "try again with a fresh process".
- A failed match MUST produce a match row in the batch evaluation's
  `MatchResult` array (`Agents/EvaluationHarness.cs:26-37`), carrying the reason
  code in `TerminationReason`.
- The reason code string MUST be the value recorded in `TerminationReason`,
  alongside the environment's own termination reasons `"resources-exhausted"`
  and `"tick-limit"` (`Environment/Simulation.cs:347`) — the same nullable
  string field, so a reader can tell a protocol failure from a finished
  episode without a second channel.
- A failed match MUST still occupy **both mirrored seatings** for its seed. The
  paired-study analyzer requires exactly one match pairing the target at seat 0
  and one with the seats swapped, and throws if either is missing
  (`Agents/PairedEvaluation.cs:100-105`). Dropping a failed match would break
  the mirror and abort the whole suite; the correct behaviour is to record the
  loss in both rows and continue.
- The failure MUST appear in the reported statistics, not be filtered out
  (§9.3).

**`host_limit` is the one exception, and it is a hard exception.** When Lattice
would breach `max_line_bytes` or `max_json_depth` on its own outbound message
(§7), it MUST refuse the match before sending anything, MUST record the run as
**void / invalid**, and MUST NOT score it as a loss for the external agent. The
agent did not choose the map size, cannot see the line Lattice declined to
write, and had no action that would have changed the outcome; a loss here would
be a host decision masquerading as agent behaviour. Void runs are still
**counted and reported** — a run that silently disappeared would be worse — but
they are **excluded from the paired statistics** (§9.3), because a seed with no
completed match contributes no delta, no win, and no loss.

The failure-is-a-loss rule therefore has a precise boundary: it covers every
fault the agent could have avoided, and it stops at the first fault only Lattice
could have avoided.

### 9.2 Why a failure is a loss and not an abort

An external agent is a competitor in a scored study, not a dependency Lattice
can retry. A protocol failure is indistinguishable, from the environment's
point of view, from an agent that chose to stand still: Lattice received no
usable action for that step. Scoring it as anything other than a loss would let
an agent improve its measured score by crashing, which is the exact failure mode
a public benchmark must not have. It is also the reason there is no retry: a
retry rule would make the reported numbers depend on Lattice's internal policy
rather than on the agent's behaviour.

The rule is deliberately one-sided, and `host_limit` (§9.1) is the single
carve-out. The harshness is a feature for every fault the agent controls — a
crash must never be a scoring strategy. It would be a defect for the one fault
it does not, and a host-side refusal is recorded as a void run precisely so that
Lattice can be strict about the agent without also being unfair to it.

### 9.3 Failure-to-score mapping

| Reason | Fault | Recorded `TerminationReason` | Match classified as | External agent's policy outcome |
| :--- | :--- | :--- | :--- | :--- |
| `protocol_mismatch` | agent | the reason code | win for the baseline side | loss |
| `malformed_json` | agent | the reason code | win for the baseline side | loss |
| `schema_violation` | agent | the reason code | win for the baseline side | loss |
| `unknown_field` | agent | the reason code | win for the baseline side | loss |
| `line_too_long` | agent | the reason code | win for the baseline side | loss |
| `depth_exceeded` | agent | the reason code | win for the baseline side | loss |
| `step_mismatch` | agent | the reason code | win for the baseline side | loss |
| `illegal_action` | agent | the reason code | win for the baseline side | loss |
| `timeout_handshake` | agent | the reason code | win for the baseline side | loss |
| `timeout_step` | agent | the reason code | win for the baseline side | loss |
| `timeout_match` | agent | the reason code | win for the baseline side | loss |
| `agent_exited` | agent | the reason code | win for the baseline side | loss |
| `agent_crashed` | agent | the reason code | win for the baseline side | loss |
| `host_limit` | **host** | the reason code | **void — not a match result** | **not scored** |

"Win for the baseline side" resolves to `MatchOutcome.TeamAWin` or
`MatchOutcome.TeamBWin` according to which seat the external agent occupied in
that match; the external agent's own policy outcome is always a **loss**
(`Agents/PairedEvaluation.cs:181-195`,
`Agents/EvaluationHarness.cs:200-213`).

**Void runs.** A `host_limit` run produces no `MatchOutcome` at all: it is not a
win, a loss, a draw, or a timeout. It MUST be reported as a **count** —
`VoidRuns`, alongside the reason breakdown — so that a reader can see that a
match did not happen, rather than inferring it from a total that is short. Void
runs MUST be **excluded from the paired statistics**: they contribute to no
delta, no per-outcome rate, and no seed in the grading denominator, because
neither agent played. A seed whose only two mirror matchings were both void does
not count toward the 30-seed floor in §9.4. This is the whole point of the
carve-out — excluding the run is what makes "not a loss" true — and reporting the
count is what stops the exclusion from being invisible.

**`AgentFailures`.** A protocol failure is **not** reported through the
`Timeout` bucket (`Agents/EvaluationHarness.cs:12-18`). `Timeout` means the
episode burned its step budget without a terminal tick
(`Agents/EvaluationHarness.cs:200-205`) — a legitimate outcome. A protocol
failure is a different event and MUST be counted separately, so that an agent
cannot be timed out by the study and mislabelled as having merely run long.

The reported statistics therefore carry an **`AgentFailures`** count, separate
from `MatchOutcome.Timeout` and separate from `VoidRuns`: it is incremented once
per match whose `TerminationReason` is one of the thirteen agent-attributable
codes in the table above. `AgentFailures` is a **report-only** field. It MUST
NOT change scoring, MUST NOT change the outcome of any match, and MUST NOT
enter the paired delta, the confidence interval, or the decision rule — those
remain exactly as §9.4 specifies. It exists so that a study can answer two
questions that one number cannot: *how badly did it play* (the paired
statistics, in which every failure is a loss) and *how often did its plumbing
break* (the `AgentFailures` count, in which a crash and a stall are visible as
such and not laundered into a single loss). The two are reported side by side and
are never merged. See §14, U-6.

### 9.4 Scoring is through the existing paired evaluation

An external agent is scored with **identical** statistics and identical floors
to an in-process agent. There is no external-agent-specific statistic, no
adjusted floor, and no separate report. Specifically, an external agent run
through `evaluate` MUST use the same:

- mirrored-seat pairings `(0, 1)` and `(1, 0)` (`Cli/CliApp.cs:750`);
- canonical seed suites, dev `1001..1050` and held-out `2001..2050`
  (`Cli/CliApp.cs:753-756`);
- per-match tick budget `EvaluationSimulationConfig.MaxTicks`
  (`Cli/CliApp.cs:765-770`);
- paired-delta definition, Δ = avg((policy − baseline) at seat 0,
  (baseline − policy) at seat 1) (`Agents/PairedEvaluation.cs:107-115`);
- dispersion statistics, 95% confidence interval, and per-outcome rates
  (`Agents/PairedEvaluation.cs:120-142`);
- decision rule — graded at **≥ 30 seeds**, passing only when mean Δ > 0 **and**
  the CI lower bound > 0 (`Agents/PairedEvaluation.cs:144-150`).

An external agent that clears fewer than 30 seeds is reported as
**"Not graded"**, exactly as an in-process agent is.

### 9.5 Flag collision: `--agent`

`simulate` already has `--agent <greedy|random|mcts>`, an **in-process policy
selector** (`Cli/CliApp.cs:206-222`, `Cli/CliApp.cs:1029-1031`,
`docs/CLI.md:34`). That selector is **unchanged** by this protocol.

The external-agent flag on `evaluate` is a different kind of thing: it names a
**process to launch**, not a policy out of a closed enum. Reusing the spelling
`--agent` on `evaluate` would put a free-form command line behind a flag whose
established meaning in this CLI is a three-value enum, and would make
`--agent "python my_agent.py"` and `--agent mcts` syntactically identical and
semantically unrelated. The two cannot share a name without a reader having to
know which command they are under to know what the value means.

**Resolution: the external-agent flag on `evaluate` is `--agent-cmd`, and it
takes a command line, not an enum.** `--agent-cmd "python3 my_agent.py"` (and
`--agent-cmd-cwd`, `--agent-cmd-slot`, if stage 3 needs them) cannot be confused
with `simulate --agent mcts` because the flag names differ and because
`--agent-cmd`'s value is an arbitrary command line rather than one of three
words. `simulate --agent` keeps its exact current spelling, values, and
validation. The rationale is recorded in
[`adr/0005-external-agent-wire-contract.md`](adr/0005-external-agent-wire-contract.md).

---

## 10. Determinism and replay

### 10.1 Lattice's side is deterministic

Given the seed and the sequence of actions an agent produced, Lattice's side of
the match is deterministic. Lattice MUST NOT introduce any source of
nondeterminism that a recorded action sequence could expose: no ambient
randomness, no wall-clock in the step path, no hash-order iteration, no
thread-scheduling-dependent resolution. The in-process contract is the reference
and is unchanged — agents are polled in ascending id
(`Agents/ScenarioRunner.cs:53-56`) and the step function resolves every race
(`Environment/StepContracts.cs:9-19`).

### 10.2 External actions are recorded exactly like in-process actions

An action received over the wire is recorded into the trajectory in exactly the
form an in-process agent's action is recorded: as the same `AgentAction` values
in the same per-step turn array (`Trajectories/TrajectoryModel.cs:68-72`,
`Agents/ScenarioRunner.cs:58`). There is no "external" marker in the recorded
step, no separate action log, and no re-derivation.

The consequences MUST hold:

- **`replay --verify` works unchanged.** Replay reconstructs the episode from
  the header and asserts tick-by-tick serialized `StepResult` equivalence
  (`docs/SUPPORT_AND_REPRODUCIBILITY.md:212-220`,
  `Trajectories/TrajectoryModel.cs:55-72`). A match with an external agent
  produces a recording of the same schema as any other, so the existing
  verification path applies with no special case.
- **State hashes work unchanged.** A schema-3 recording carries a SHA-256
  digest of the state at every step (`Trajectories/TrajectoryModel.cs:6-31`).
  A digest attests to the state the recorded actions produced; it makes no
  statement about who chose those actions.
- **The trajectory schema is not bumped.** This protocol adds no field to
  `TrajectoryHeader`, `TrajectoryStep`, or `TrajectoryFinal`
  (`Trajectories/TrajectoryModel.cs:46-85`), so `TrajectorySchema.CurrentVersion`
  stays `3` (`Trajectories/TrajectoryModel.cs:21`) and no existing golden
  fixture changes.

### 10.3 Replay never re-runs the external process

This is the load-bearing rule. `replay` MUST NOT spawn, re-execute, or consult
the external agent process. The recording carries the **actions**, not a
reference to the thing that produced them, so a trajectory containing external
actions is replayable forever, on any machine, with the agent binary absent.
This mirrors the existing "the seed and map are captured together so replay
never needs the generator again" property
(`Trajectories/TrajectoryModel.cs:33-45`).

An agent process that is nondeterministic therefore produces **different
trajectories on different runs from the same seed**, because it chooses
different actions. That is a property of the agent, not a defect in Lattice,
and it is **reported, not hidden**: the trajectory is still internally
consistent, and `replay --verify` on it succeeds. A second run from the same
seed is a different experiment and MUST NOT be claimed to reproduce the first.
Lattice MUST NOT add any mechanism that would paper over this, and MUST NOT
present an external agent's per-seed results as replay-equivalent unless the
agent's own action stream is reproducible.

---

## 11. Architecture boundary

### 11.1 Two layers, one direction of dependency

The protocol is split so that the wire format has no stake in the simulation:

**The protocol project** is `Lattice.Protocol`, in the repository directory
`Protocol/`. It holds, and only holds:

- plain wire types — records describing `hello`, `hello_ack`, `observation`,
  `action`, `error` as data, with no behaviour and no environment semantics;
- a **parser** that turns a line of bytes into those types, or reports one of
  the §8 reasons;
- a **writer** that turns those types into lines of bytes, enforcing the
  framing rules in §1.

It MUST have **no `ProjectReference`** to `Lattice.Environment` or to
`Lattice.Agents`. This is a hard constraint, not a preference: it is what lets
the wire contract be unit-tested, fuzzed, and reasoned about without booting a
simulation, and it is what keeps `Lattice.Environment` and `Lattice.Generator`
BCL-only in the existing layering (`Environment/Lattice.Environment.csproj`,
`Generator/Lattice.Generator.csproj`).

The project's tests live in `Tests/Protocol/`, in the existing `Lattice.Tests`
assembly, and its fixtures live in `Tests/fixtures/protocol/`. The test
assembly is allowed to reference `Lattice.Protocol` — and already references
`Lattice.Environment` — because the asymmetry is deliberate and one-directional:
the tests need both vocabularies to assert that the wire projection is faithful,
while the protocol project needs neither. `Lattice.Protocol` is a leaf: it
references nothing in this repository at all.

### 11.2 Where the mapping lives

Mapping wire ↔ `Observation` / `AgentAction` lives in the **stage-3 runner**,
which already references both sides. The mapping is the only place that knows
both vocabularies, and it is a small, explicit, testable translation:

- wire `action` → `AgentAction(Kind, ZoneId, ResourceId)`, with the §6.1
  presence rule supplying the `-1` defaults;
- `Observation` → wire `observation`, by the §5.2 flattening rule.

The protocol project MUST NOT contain that mapping, and the stage-3 runner
MUST NOT re-implement the parser or the writer.

`Agents/ObservationView.cs` stays `internal` and **unchanged**
(`Agents/ObservationView.cs:11`). It is not moved, not made public, and not
reimplemented in the protocol project. Its two derivations are documented in
§5.2 so that an external agent can reproduce them, but the type itself stays
where it is.

### 11.3 Why the boundary is drawn here

`Lattice.Environment` is the audited core: it is BCL-only, it carries the step
contract, and it is the thing the golden fixtures and the state hashes attest
to. A wire format that could see `Observation` directly would put a
serialization concern inside the boundary that the determinism claims rest on,
and would make "the protocol is just data" unfalsifiable. Splitting it means the
protocol can be fuzzed as pure bytes, and the mapping can be fuzzed as a pure
function, with no simulation in either loop.

---

## 12. Versioning policy

The protocol version is the integer `1`, and it is bumped by the same discipline
the trajectory schema already uses
(`Trajectories/TrajectoryModel.cs:6-31`,
`docs/SUPPORT_AND_REPRODUCIBILITY.md:222-239`).

1. **The version is an integer, and negotiation is exact.** Any value other than
   the one Lattice sent is refused with `protocol_mismatch` (§2). There is no
   downgrade and no negotiation.
2. **Forward compatibility is refused, not guessed.** An agent built for a
   newer protocol is a failure, not something Lattice half-satisfies
   (`docs/SUPPORT_AND_REPRODUCIBILITY.md:228-231`).
3. **Backward compatibility is opt-in per version, never automatic.** A new
   protocol version may still accept `1` if and only if the Lattice release
   states that it does. It MUST NOT accept `1` by accident, by ignoring fields,
   or by "being liberal in what it accepts".
4. **Any wire change is a version change.** A new field, a changed meaning, a
   new `type`, a new `kind`, a new `reason`, or a changed limit is a new
   integer. Two changes that alter what a conforming agent can observe or
   produce MUST NOT share a version number, and the version bump and the
   reader's accepted-version rule MUST land in the same change
   (`docs/SUPPORT_AND_REPRODUCIBILITY.md:232-239`).
5. **A new optional field is nullable and omitted when absent**, so that a
   protocol-1 agent is not broken by its mere presence — and, symmetrically, a
   protocol-2 agent that sends it to a protocol-1 host is refused with
   `unknown_field` rather than ignored.
6. **The limit values are not the version.** `step_timeout_ms` and
   `match_timeout_ms` are carried per match, so changing them is not a
   breaking wire change. `max_line_bytes` and `max_json_depth` are protocol
   constants and changing either **is** a version change.
7. **The closed reason set is versioned as a unit.** A new failure mode gets a
   new code in a new version, or is folded into an existing code with its
   meaning stated — never a new string within version `1`.
8. **Rule 7 binds only from the first release.** Version `1` is not yet
   released: the codes `timeout_handshake` and `host_limit` were added while the
   version was still in draft, which is why this document is version `1` with
   fourteen codes rather than a later version with two more. Once a
   Lattice release has shipped protocol `1` with a given reason set, rule 7
   applies without exception, and any further code is a version bump. The same
   holds for the `hello` field list: `max_ticks` and `agent_count` (§3.1) were
   added before the first release, so no agent has ever seen a `hello` without
   them. See §14, U-3, U-5, U-7.

---

## 13. Non-goals for v3.0

The following are explicitly **out of scope** and MUST NOT be added under
protocol `1`:

- **Custom scenario files.** Scenario selection in v3.0 is the existing
  `evaluate --scenario standard|bottleneck` switch
  (`Cli/CliApp.cs:736-742`). A user-authored scenario file is a later
  protocol.
- **In-process plugins.** An `IAgent` loaded into the harness
  (`Agents/IAgent.cs:14-27`) is the existing path and is unchanged. The wire
  protocol is an addition, not a replacement.
- **External agents through `simulate`.** `simulate` MUST NOT accept an
  external agent in v3.0. It runs one episode, prints one result, and has no
  paired-study machinery, no mirrored seatings, and no seed suite — the
  apparatus §9.4 scores external agents with. Running an external process
  through `simulate` would produce a number that looks like a measurement and is
  not one: a single episode has no mirror, no confidence interval, and no
  grading floor, so a result from it could not be compared with anything
  published here. `simulate --agent greedy|random|mcts` keeps its exact
  meaning — an in-process policy enum (`Cli/CliApp.cs:206-222`) — and MUST NOT
  grow an external-agent spelling. The external path is `evaluate
  --agent-cmd` only (§9.5). See §14, U-10.
- **Network transports.** Loopback sockets, TCP, and shared memory are out.
  stdin/stdout is the transport.
- **Sandboxing beyond process isolation plus limits.** See below.

### 13.1 Security note, stated plainly

The external agent is **arbitrary user-supplied code**, and Lattice executes it
**with the user's own permissions, in the user's own environment, on the user's
own machine**. Lattice does **not** sandbox it.

Concretely, and stated as a limitation rather than a warning to be skimmed:

- The process inherits the launching user's identity, filesystem access, network
  access, and environment.
- Lattice's only controls are **process isolation** (a separate OS process) and
  the **limits** in §7: line length, JSON depth, and two timeouts. None of these
  is a security boundary. A limit bounds cost, not authority.
- Lattice MUST NOT be described as running untrusted code safely. It runs
  user-chosen code with user-chosen permissions, and the user is the trust
  boundary.
- Anyone running an external agent they did not write is executing someone
  else's code with their own privileges. That is the user's decision to make,
  and this document exists partly to make the decision informed.
- Additional confinement — containers, seccomp, job objects, resource limits,
  a restricted environment — is a separate piece of work with a separate
  threat model, and is not in v3.0.

---

## 14. Unsettled points

Points that stage 2 settled are recorded here with their resolution, so the
reasoning survives the fact that the answer is now in the normative text above.
Points that remain open are still open, and this document does **not** invent
answers for them. Each is a real gap that stage 3 must close, or that the
orchestrator must settle before stage 3 can proceed.

| # | Unsettled | Why it matters | Status in this document |
| :--- | :--- | :--- | :--- |
| U-1 | The protocol project's assembly name and directory. | §11 requires a project with a hard no-`ProjectReference` rule; the name is a stage-3 mechanical choice. | **Resolved.** The project is `Lattice.Protocol`, in `Protocol/`, a leaf that references nothing in this repository. Tests are in `Tests/Protocol/`; fixtures are in `Tests/fixtures/protocol/`. §11.1 states the name, the directory, the leaf property, and the one-way test reference. |
| U-2 | The values of `step_timeout_ms` and `match_timeout_ms`. | Decision 5 defers these to stage 3 explicitly. | **Open.** Fields and semantics defined (§3.1, §7); no value asserted. The §7 constraint `match_timeout_ms ≥ step_timeout_ms × MaxTicks` is derived, not assumed. Stage 3 sets the constants and records them here. |
| U-3 | **`hello` carried no tick budget or agent count.** | A conforming v1 agent could learn its slot, its seed, and its map, but **not** `MaxTicks` and **not** the agent count up front. An agent that plans a horizon had no way to know the horizon except by running out of steps, and `MaxTicks` is the input to any time-aware plan. This was the largest usability gap in the contract. | **Resolved.** `hello` gains two **required** integer fields: `max_ticks` (from `SimulationConfig.MaxTicks`, `Environment/Simulation.cs:28`, the same value the harness passes as `MaxSteps`, `Agents/EvaluationHarness.cs:147-148`) and `agent_count` (from `SimulationConfig.AgentCount`, `Environment/Simulation.cs:25`, bounded 2..4 at `Environment/Simulation.cs:58-60`). Both are additions to a `hello` that no released agent has ever seen, so no version bump is due (§12.8). The `hello` field list, the §4.1 example line, the §12 versioning rules, and the §7 `match_timeout_ms` constraint (which is now computed from the number Lattice actually sends) are all updated to match. |
| U-4 | The stderr ring-buffer capacity. | §1.1 requires a bounded ring but no bound is given. | **Open.** Semantics fixed; no number asserted. Stage 3 sets the capacity and records it here. |
| U-5 | **There was no handshake-timeout reason code.** | The closed set had `timeout_step` and `timeout_match` but nothing for "the agent never sent `hello_ack`". Assigning it to `timeout_step` treated the handshake as step 0's exchange, which was defensible but an interpretation, not a decision. An *invalid* `hello_ack` was not affected — a wrong `protocol` value is `protocol_mismatch` (§2). | **Resolved.** A thirteenth agent code, `timeout_handshake`, is added: no valid `hello_ack` within `step_timeout_ms` of `hello`. It is scored as an **agent failure** like every other agent code (§9.1, §9.3). It is distinct from `protocol_mismatch` (a wrong `protocol` value, which arrived) and from `schema_violation` (a malformed `hello_ack`, which also arrived). It sits in the Timing group in §8.2 and second among the agent codes in the §8.4 precedence, immediately after `protocol_mismatch`. §2, §7, §8, §8.1, §8.2, §8.4, and §9.3 are updated. |
| U-6 | **A protocol failure had no reported count of its own** — it was a loss and nothing more. | §9.3 scores failures as losses per decision 6, which is right for the statistics but means a study cannot report "how often did the external agent's plumbing break" separately from "how badly did it play". | **Resolved.** The reported statistics carry an **`AgentFailures`** count, incremented once per match whose `TerminationReason` is one of the thirteen agent-attributable codes, kept **separate** from `MatchOutcome.Timeout` (`Timeout` remains "budget burned without a terminal tick") and separate from `VoidRuns`. It is **report-only**: it MUST NOT change scoring, the paired delta, the confidence interval, or the decision rule, all of which stay exactly as §9.4 specifies. Agent failures still count as losses. §9.3 states the boundary. |
| U-7 | Whether an over-long **outbound** line is a loss for the external agent. | §7 requires Lattice to refuse a match whose `observation` would exceed 1 MiB. No reason code covered that, and under decision 6's literal wording ("every failure ... is scored as a loss for the external agent") the agent was blamed for a limit Lattice's own map generation caused — a map-size choice the agent could neither see nor influence. | **Resolved.** A fourteenth code, `host_limit`, records the refusal. Lattice checks its own outbound `observation` against **both** `max_line_bytes` and `max_json_depth` **before writing any byte**, refuses the match if it would breach either, records the run as **void / invalid**, and does **NOT** score it as a loss. Void runs are **reported as a count** (`VoidRuns`) and **excluded from the paired statistics** — from the delta, the per-outcome rates, the confidence interval, the decision rule, and the 30-seed grading denominator — because neither agent played. The §8 and §9.3 tables carry an explicit **`Fault`** column (`agent` vs `host`) so the partition is normative rather than inferred. §8.4 puts `host_limit` first, since it is detected before any wire exchange exists. An *inbound* over-long line is unaffected and remains `line_too_long`, an agent loss. |
| U-8 | Process lifetime scope: one process per match, or one long-lived process across a whole evaluation suite. | A per-match process is simpler and matches the in-process factory contract, which builds a fresh agent per (pairing, seed) so RNG streams cannot leak between matches (`Agents/IAgentFactory.cs:12-23`, `Agents/EvaluationHarness.cs:175`). A long-lived process would need a reset message that does not exist in the five-type catalogue. | **Resolved: one agent process per match.** Stated normatively in §3 as the first handshake requirement — Lattice MUST launch a fresh process per match, send `hello` exactly once, and MUST NOT reuse a process across matches, seed pairings, or the mirrored seatings of one seed. This buys unconditional isolation (one agent's crash, hang, or memory growth cannot affect another match) and makes state carryover between seeds impossible by construction. A suite-scoped process would need a reset message, and the closed five-type catalogue (§4) has none; that remains a protocol-2 change. **Semantics only here** — process launch, the stdin/stdout pumps, and the timeout enforcement are stage 3, not stage 2. |
| U-9 | Process launch details: argv, environment, working directory, and how `--agent-cmd` is split into program and arguments. | §9.5 names the flag and its value shape; the launch contract is not written anywhere. | **Open.** Flag name and value type are fixed (§9.5); the launch contract is not. Stage 3 writes it. |
| U-10 | Whether the external agent may also be run through `simulate`. | Decision 9 scopes external agents to `evaluate` and says `simulate --agent greedy|random|mcts` is unchanged. Whether a *separate* `simulate` flag for external agents is wanted was unaddressed. | **Resolved: no.** `simulate` MUST NOT accept an external agent in v3.0; it is listed as an explicit **non-goal** in §13. The reason is commensurability, not convenience: `simulate` runs one episode and has no mirror, no confidence interval, and no grading floor, so a number produced there could not be compared with any published result. `simulate --agent` keeps its exact in-process-enum meaning; the external path is `evaluate --agent-cmd` only (§9.5). |

Points U-2, U-4, and U-9 remain open and are stage 3's to close. Nothing in the
resolved rows above is provisional: each is normative in the body of this
document, and where a row says a value is fixed in stage 3, the semantics it
constrains are fixed here.

---

## Provenance of every citation in this document

All `file:line` references were verified at commit
`5fa952604b0fc108c6db83ccfc3d77d932717b34`, which is the parent of the commit
that added this document. No cited source line moved between that commit and the
one that settled the §14 points, because none of those commits touched a cited
file.

| Source | What it fixes |
| :--- | :--- |
| `Environment/StepContracts.cs:9-19` | The `ActionKind` enum and its three member names. |
| `Environment/StepContracts.cs:31` | `AgentAction(ActionKind, int = -1, int = -1)` — the 1:1 action target. |
| `Environment/StepContracts.cs:41` | `InTransit(FromZoneId, ToZoneId, RemainingTicks)`. |
| `Environment/StepContracts.cs:50` | `AgentState(AgentId, ZoneId, Score, InTransit?)`. |
| `Environment/StepContracts.cs:52-60` | Full observability and the meaning of `StepNumber`. |
| `Environment/StepContracts.cs:62-67` | The `Observation` record — the projection's source. |
| `Environment/MapData.cs:14` | `MapLimits.Unlimited` = `int.MaxValue`. |
| `Environment/MapData.cs:25` | `GridPoint(X, Y)`. |
| `Environment/MapData.cs:39-43` | `Zone`. |
| `Environment/MapData.cs:55-59` | `ResourceNode`. |
| `Environment/MapData.cs:73-78` | `ChokePoint`. |
| `Environment/MapData.cs:85-88` | `MapGraph`. |
| `Environment/ActionSpace.cs:18-38` | Action validity: kind, `Move` zone range, `Collect` resource range. |
| `Environment/Simulation.cs:28` | `SimulationConfig.MaxTicks` — the tick budget, sent as `hello.max_ticks` (§3.1). |
| `Environment/Simulation.cs:25` | `SimulationConfig.AgentCount` — the agent count, sent as `hello.agent_count` (§3.1). |
| `Environment/Simulation.cs:58-60` | `AgentCount` is bounded to 2–4. |
| `Environment/Simulation.cs:95-99` | `SimulationState`, including `int[] Claims` and `StepCount`. |
| `Environment/Simulation.cs:342` | `StepCount` advances by one per `Step`. |
| `Environment/Simulation.cs:347` | Termination reason strings `"resources-exhausted"` / `"tick-limit"`. |
| `Agents/ObservationView.cs:11` | `internal static class ObservationView` — stays internal and unchanged. |
| `Agents/ObservationView.cs:18-29` | "My zone" derivation. |
| `Agents/ObservationView.cs:35-38` | "Unclaimed", ascending by id. |
| `Agents/ScenarioRunner.cs:50` | The 0-based step loop. |
| `Agents/ScenarioRunner.cs:53-56` | Agents polled in ascending id. |
| `Agents/ScenarioRunner.cs:58` | Actions recorded per step as a turn array. |
| `Agents/ScenarioRunner.cs:241-244` | Per-agent `Observation` built from shared state. |
| `Agents/IAgent.cs:14-27` | The existing in-process agent contract, unchanged. |
| `Agents/IAgentFactory.cs:12-23` | Fresh agent per (pairing, seed). |
| `Agents/EvaluationHarness.cs:12-18` | `MatchOutcome`, including `Timeout`. |
| `Agents/EvaluationHarness.cs:26-37` | `MatchResult`, including `TerminationReason`. |
| `Agents/EvaluationHarness.cs:112-117` | Evaluation pairings are head-to-head: `AgentCount == 2`. |
| `Agents/EvaluationHarness.cs:147-148` | `MaxSteps` is the per-match tick budget. |
| `Agents/EvaluationHarness.cs:175` | Fresh agents per (pairing, seed). |
| `Agents/EvaluationHarness.cs:200-213` | Match classification; `Timeout` = budget burned without terminal. |
| `Agents/PairedEvaluation.cs:100-105` | Both mirrored seatings required per seed. |
| `Agents/PairedEvaluation.cs:107-115` | The paired-delta definition. |
| `Agents/PairedEvaluation.cs:120-142` | Dispersion, CI, and per-outcome rates. |
| `Agents/PairedEvaluation.cs:144-150` | Decision rule and the 30-seed grading floor. |
| `Agents/PairedEvaluation.cs:181-195` | Policy outcome from seat and `MatchOutcome`. |
| `Cli/CliApp.cs:206-222` | `simulate --agent greedy\|random\|mcts` — the existing selector. |
| `Cli/CliApp.cs:291-293` | `infiltration` forbids `--agent`. |
| `Cli/CliApp.cs:708` | The `evaluate` command. |
| `Cli/CliApp.cs:736-742` | `evaluate --scenario standard\|bottleneck`. |
| `Cli/CliApp.cs:745-750` | Evaluation teams and mirrored pairings. |
| `Cli/CliApp.cs:753-771` | Canonical seed suites, tick budget, `PairedStudy.Analyze`. |
| `Cli/CliApp.cs:1029-1031` | `simulate --agent` usage text. |
| `Generator/MapGenerator.cs:13-25` | Zone and resource bounds are caller-configured, no ceiling. |
| `Trajectories/TrajectoryModel.cs:6-31` | `TrajectorySchema.CurrentVersion = 3`, state-hash version. |
| `Trajectories/TrajectoryModel.cs:33-45` | The header: seed and map captured so replay needs no generator. |
| `Trajectories/TrajectoryModel.cs:46-53` | `TrajectoryHeader` — no external-agent field. |
| `Trajectories/TrajectoryModel.cs:68-72` | `TrajectoryStep` — actions plus result plus state hash. |
| `Trajectories/TrajectoryModel.cs:79-85` | `TrajectoryFinal` — no external-agent field. |
| `Trajectories/TrajectoryReader.cs:77-80` | Newer schema versions are rejected, not guessed. |
| `docs/SUPPORT_AND_REPRODUCIBILITY.md:212-220` | Structural replay invariants; action validity at record and verify. |
| `docs/SUPPORT_AND_REPRODUCIBILITY.md:222-239` | Backward/forward compatibility and the migration invariant. |
| `docs/CLI.md:34` | `simulate --agent` as documented. |
