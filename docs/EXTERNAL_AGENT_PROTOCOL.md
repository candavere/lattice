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

**The ring capacity is `65536` bytes (64 KiB).** When the ring is full, the
oldest bytes MUST be discarded so that the buffer holds the **last** 64 KiB the
agent wrote. The capacity is a byte count, not a character or line count.

**The last 64 KiB is attached to the failure record** on every process-level
and timing failure: `agent_crashed`, `agent_exited`, `timeout_handshake`,
`timeout_step`, and `timeout_match`. The attached text is what the ring holds
at the moment the failure is recorded. It MUST NOT be parsed, MUST NOT be
interpreted, and MUST NOT be counted as protocol input; it exists so that a
human reading a failed run can see what the agent was doing when it stopped.

An agent that writes to stderr MUST NOT block as a result: Lattice MUST drain
stderr continuously for the life of the process, and MUST terminate the process
if draining it is the only thing keeping it alive. "Continuously" is load-bearing
rather than aspirational: a pipe whose reader stops is a pipe whose writer
deadlocks, so the drain MUST run on its own reader for the whole life of the
process — including while Lattice is blocked waiting on **stdout** — and it MUST
NOT be deferred until the match has failed. An agent that writes more stderr
than the ring holds, or more than the operating system's pipe buffer can hold,
MUST still be able to play to completion. See §14, U-4.

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

### 3.2 Process launch contract

The launch contract is an **argv, never a shell**. This is U-9, settled here;
§9.5's `--agent-cmd` is a CLI-surface concern, and the rule that turns its string
into that argv is **§3.3**.

Lattice MUST receive the agent as a **program plus an argument list** — two
separate values, already split — and MUST start it with an explicit
`ProcessStartInfo` whose arguments are added one at a time through
`ArgumentList`. Lattice MUST NOT:

- build a single command **string** and hand it to a shell, `cmd.exe`, or
  `cmd /c`;
- set `UseShellExecute` to `true`;
- concatenate, quote, escape, or re-parse an argument list into a string and
  back.

The consequences are normative:

- **A space in an argument is a space in an argument.** Splitting into an argv
  is a *caller* responsibility, performed before Lattice sees the value, so no
  quoting rule exists here for Lattice to get wrong. An agent path containing
  spaces is one argv element, not two.
- **There is no shell metacharacter.** `>`, `|`, `&`, `;`, `*`, `~`, `$`, and
  backticks have no meaning to Lattice and MUST NOT be interpreted, expanded,
  or removed. They are ordinary bytes inside whatever single argument contains
  them, and expansion of any kind is the caller's business.
- **Injected input cannot become execution.** Because nothing is re-parsed by a
  shell, a value that reaches Lattice as one argument cannot be split, globbed,
  or chained by Lattice. The injection surface of the launch contract is
  therefore empty, which is the reason for the rule rather than a tidy-up.

**Working directory.** The child MUST be started in **the caller's current
working directory** — the directory Lattice itself was running in. Lattice MUST
NOT start the agent anywhere else, MUST NOT resolve a working directory out of
the agent's path, and MUST NOT walk up from it.

**Environment.** The child MUST **inherit** Lattice's environment. Lattice MUST
NOT clear it, MUST NOT replace it with a minimal one, and MUST NOT remove
anything the caller had set. On top of the inherited set Lattice MUST add
exactly one variable:

| Variable | Value | Meaning |
| :--- | :--- | :--- |
| `LATTICE_PROTOCOL` | `1` | The wire version Lattice is speaking, so an agent can read it without waiting for `hello`. |

`LATTICE_PROTOCOL` is **informational, not authoritative.** An agent MUST NOT
branch its protocol behaviour on it, and MUST NOT treat its absence or a
different value as licence to do anything: negotiation is still the exact-match
handshake of §2, and `LATTICE_PROTOCOL` can never make an incompatible agent
compatible. Its value is the same integer Lattice sends in `hello.protocol`
and in `hello_ack`'s counterpart, `ProtocolLimits.Version`; Lattice MUST NOT
write a value that disagrees with the `protocol` it puts on the wire, and MUST
NOT use this variable as the source of that integer — the constant in the
protocol library is. Lattice MUST add it as the **only** variable it introduces,
so that an agent's environment is a caller environment plus one known key.

**What this section does not settle, and where it is settled instead.** How a
CLI string such as `--agent-cmd "python3 my_agent.py"` becomes a program and an
argument list is **not** a property of the transport: the contract here begins at
the argv, and it holds unchanged whichever splitter produced it. The splitter
itself — the rule, the program resolution, and the exit status for a command that
cannot be run — is fixed in **§3.3**, which is where a CLI author should look.

See §14, U-9.

### 3.1 `hello`

| Field | Type | Required | Meaning |
| :--- | :--- | :--- | :--- |
| `type` | string | yes | Exactly `"hello"`. |
| `protocol` | integer | yes | Exactly `1`. |
| `scenario` | string | yes | The evaluation scenario family that selected the map. In 3.0.0 the closed set is `"standard"` and `"bottleneck"` (`Cli/CliApp.cs:845-851`). |
| `seed` | integer | yes | The run seed, an unsigned 64-bit value (`Agents/EvaluationHarness.cs:27`). MUST be a JSON number with no fraction, no exponent, no leading `+`, and no leading zeros. |
| `agent_slot` | integer | yes | The agent slot this process plays, in `0..AgentCount-1` (`Environment/Simulation.cs:58-60`). In the `evaluate` path this is exactly `0` or `1`, because evaluation pairings are head-to-head (`Agents/EvaluationHarness.cs:112-117`). |
| `max_ticks` | integer ≥ 1 | yes | The match's tick budget, from `SimulationConfig.MaxTicks` (`Environment/Simulation.cs:28`). It is the **same value** as `EvaluationSimulationConfig.MaxTicks`, which is the per-match `MaxSteps` the harness passes down (`Agents/EvaluationHarness.cs:147-148`). This is the horizon an agent plans against; §7's `match_timeout_ms ≥ step_timeout_ms × MaxTicks` constraint is computed from this number. |
| `agent_count` | integer ≥ 2 | yes | The number of agents in the match, from `SimulationConfig.AgentCount` (`Environment/Simulation.cs:25`), which is bounded to 2..4 (`Environment/Simulation.cs:58-60`). It is always `2` on the `evaluate` path (`Agents/EvaluationHarness.cs:112-117`); the field is carried so an agent can size its model of the episode without inferring the count from `agent_states[]` length. |
| `limits` | object | yes | The two named time limits for this match (§7). |

`limits` has exactly two fields and no others:

| Field | Type | Meaning |
| :--- | :--- | :--- |
| `step_timeout_ms` | integer ≥ 1 | Monotonic milliseconds Lattice will wait for one `action` line after writing an `observation`, and for one `hello_ack` after writing a `hello`. The **default is `5000`**. |
| `match_timeout_ms` | integer ≥ 1 | Monotonic milliseconds Lattice will allow for the whole match, measured from the moment `hello` is written. It is **computed as `step_timeout_ms × max_ticks + 30000`**. |

**The default `step_timeout_ms` is `5000`, and the default `match_timeout_ms` is
computed from it as `step_timeout_ms × max_ticks + 30000`.** `match_timeout_ms`
is not an independent constant: deriving it is what makes §7's
`match_timeout_ms ≥ step_timeout_ms × MaxTicks` constraint **hold by
construction** rather than be a number someone has to remember to keep
consistent. The `+ 30000` slack covers the per-step bookkeeping and the final
exchange, so a match that uses its entire step budget legitimately does not trip
the whole-match limit on the way out.

**The handshake uses `step_timeout_ms`.** `timeout_handshake` is bounded by the
same value as `timeout_step` (§2), so an agent that never starts is not given a
longer grace period than an agent that stalls once, and the two are the same
knob an agent author has to reason about.

**Both values travel in `hello.limits` and MUST be recorded in the run's output
metadata**, so that a reported run states the time limits it was played under
rather than leaving them to be inferred from a version number. The values are
**per-match, not protocol constants**: changing either is not a breaking wire
change (§12.6).

Note that all three timeouts are measured on a **monotonic clock** and never on
the wall clock, so a system time adjustment mid-match cannot manufacture or
suppress a timeout. See §7, §14, U-2.

`scenario` is the closed 3.0.0 set. The `infiltration` scenario is **not**
reachable through the external-agent path in 3.0.0: it is a `simulate`-only
roster and is explicitly separate from the `evaluate` path
(`Cli/CliApp.cs:391-393`, `Cli/CliApp.cs:845-851`).

`hello` carries **no map**. The map arrives with the first `observation` (§5.3),
which is re-sent in full on every step. What `hello` does carry beyond its
identity fields is the two numbers an agent cannot otherwise learn:
`max_ticks`, the horizon it is playing against, and `agent_count`, the size of
the episode. Both are **required** — under the strictness rule in §8.3 a
protocol-1 agent that omits either is refused with `schema_violation`, and one
that sends either under a different name is refused with `unknown_field`. This
closes the usability gap recorded as §14, U-3: an agent that plans a horizon now
knows the horizon from the handshake rather than by running out of steps.

### 3.3 The `--agent-cmd` string, split without a shell

`evaluate --agent-cmd "<command line>"` (§9.5) takes a **string**; §3.2 above
takes a **program plus an argv**. The conversion between them is Lattice's, and
it is fixed here so that it is the same on every operating system and involves no
shell at any point.

**The splitter.** Lattice MUST split the string by exactly this rule, and by no
other:

- **Unquoted whitespace separates.** Spaces and tabs are separators; runs of them
  collapse, and leading and trailing whitespace is not an argument.
- **A double quote groups.** `"` opens a group and the next unescaped `"`
  closes it. The quotes themselves are removed, and everything between them —
  whitespace included — is one argument. `""` is therefore a legal empty
  argument, and `""` as a whole command is not (§ below).
- **Inside a group, backslash escapes only a double quote or a backslash.** `\"`
  is a double quote in the argument and `\\` is a backslash. A backslash before
  any other character is that character.
- **Everywhere else a backslash is an ordinary character.** Outside a group there
  is no escape sequence at all, so `C:\agents\python.exe` is one argument,
  unquoted and unmangled. This is the rule that makes an unquoted Windows path
  work, and it is why a path survives the trip in one piece.
- **A group extends the argument it opens inside.** `"` is about whitespace, not
  about starting a new argument, so `a\"b c"` is the single argument `a\b c` and
  not two.
- **Only the space and the tab separate.** A line feed, a carriage return, or any
  other whitespace is a byte inside an argument, so the rule cannot be changed
  underneath a caller by a platform's idea of what whitespace is.
- **There is no globbing, no variable expansion, and no single quote.** `*`, `?`,
  `$`, `` ` ``, `~`, `|`, `&`, and `;` are bytes inside whatever argument
  contains them, and `'` has no quoting meaning at all — it is a character like
  any other. A caller who wants a shell has one; this is deliberately not it, for
  the injection reason §3.2 gives.
- **Two conditions are usage errors**, and both are reported before anything
  runs: an **unterminated quote** (a group that reaches the end of the string),
  and an **empty command** (nothing but whitespace, or a program that is empty
  once split — which `""` produces).

**Resolution of the program.** The first element is resolved **once, before any
match is played**:

- if it contains a directory separator — and **either** `/` or `\` counts, on
  every platform — it is used as a **path**, relative to the caller's current
  directory (§3.2). A Windows-shaped value is a path even on a host that has no
  such path, where the honest answer is "does not exist" rather than a search for
  a file whose name happens to contain backslashes;
- otherwise it is a **bare name**, and Lattice MUST search `PATH` for it, trying
  each `PATHEXT` extension on Windows.

A program that resolves to nothing, or that is found but cannot be started, is
**not a match result and not a §8 reason code**: no agent ever spoke, so there is
no agent behaviour to attribute anything to. Lattice MUST report it as a
**usage error naming the program**, write it to stderr, and exit **2** — before
any match runs, so that no seed is consumed, no statistic is computed, and **no
artifact is written**. The resolution is deliberately up front rather than left
to the first `Process.Start`: a launch failure discovered mid-suite would have to
be either scored as a loss, which blames the agent for Lattice's own `PATH`, or
retried, which §9.1 forbids. Refusing to start is the third option, and it is the
only one that invents no score.

See §14, U-9.

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

The `limits` numbers in the `hello` example are the **normative defaults** and
MUST satisfy §7's constraint; here they are the defaults for the
`max_ticks` the example shows, so `match_timeout_ms` is
`5000 × 500 + 30000 = 2530000`. `max_ticks` and `agent_count` remain
per-match values shown with a plausible but not normative pair. Every other
value in these examples is normative and matches the field tables that follow.

**`hello`**

```json
{"type":"hello","protocol":1,"scenario":"standard","seed":1001,"agent_slot":0,"max_ticks":500,"agent_count":2,"limits":{"step_timeout_ms":5000,"match_timeout_ms":2530000}}
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
(`Environment/MapData.cs:31-33`).

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
(`Environment/StepContracts.cs:29-31`):

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
`hello.limits`** (§3.1), whose defaults are fixed here and whose
`match_timeout_ms` is computed from the other.

| Limit | Value | Applies to | Enforced by | Reason on breach |
| :--- | :--- | :--- | :--- | :--- |
| `max_line_bytes` | `1048576` (1 MiB), **excluding** the terminating LF | every line, in both directions | Lattice | `line_too_long` (agent's line) / `host_limit` (Lattice's own line) |
| `max_json_depth` | `32` | every JSON value Lattice parses | Lattice | `depth_exceeded` |
| `step_timeout_ms` | **`5000`** by default, carried in `hello.limits` | the wait for one `action` after one `observation`, and the wait for one `hello_ack` after one `hello` | Lattice | `timeout_step` (after an `observation`) / `timeout_handshake` (after `hello`) |
| `match_timeout_ms` | **`step_timeout_ms × max_ticks + 30000`**, carried in `hello.limits` | the whole match, from `hello` written to termination | Lattice | `timeout_match` |

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
  agent would be scored a loss for a limit Lattice itself mis-set. **The default
  satisfies this by construction** rather than by assertion: `match_timeout_ms` is
  *defined* as `step_timeout_ms × max_ticks + 30000`, so the constraint cannot be
  violated by choosing a `step_timeout_ms` and then forgetting to scale the
  match budget with it. A caller that overrides `match_timeout_ms` with a value
  below `step_timeout_ms × MaxTicks` has mis-set it and MUST be refused before
  the match starts, exactly as Lattice refusing its own over-long outbound line
  (§7, `host_limit`).
- **All three timeouts are measured on a monotonic clock**, never on the wall
  clock. A system clock adjustment — an NTP correction, a manual change, a
  daylight-saving boundary — MUST NOT be able to manufacture a timeout or
  suppress one mid-match. The whole-match limit is likewise measured from the
  moment `hello` is written, and MUST NOT be measured from process start or from
  the first `observation`.
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
gone.

**`host_limit` is the stronger case, and it is not a matter of permission.**
Lattice MUST NOT write an `error` line for `host_limit`. The rule covers both
places the code can fire: a refusal **before the agent process is started**, where
there is no peer and no stream to write to, and a refusal at a later step, where
the exchange has been going on and the line would be a second, contradictory
report of a match Lattice has already decided not to finish. In both cases the
code appears in the run record and the reported void count (§9.3) rather than on
the wire.

§8.4's precedence rests on the first of those two orderings specifically: the
step-0 gate runs before a process exists and therefore before the handshake, so a
match refused there has no agent behaviour behind it for a violation to outrank.
A mid-match refusal is already inside the exchange, and it is still the last thing
reported — Lattice detected its own limit, not an agent fault.

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

Every agent-attributable code in that table additionally **forfeits**: the row
carries the external agent's score as `0` and the opponent's as it stood at the
moment of failure. The forfeit is a property of the *score* on the row and never
of its classification, so the "Match classified as" column above is unchanged by
it. See "A failed match's scores" below for the rule, the incentive it removes, and
where the partial scores are recorded. `host_limit` is not a forfeit: it is a void
run, and produces no row at all.

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
are never merged. The breakdown is **by reason code**, so a run in which every
failure is `timeout_handshake` is visibly distinguishable from one in which every
failure is `agent_crashed`. For the process-level and timing codes the record also
carries **the last 64 KiB of the agent's stderr** (§1.1): diagnostic text
attached to the failure, never parsed, and never entering any statistic, rate, or
decision rule. See §14, U-6.

**What the study artifact carries.** The counts above live in the run's output,
and which of them the `evaluate` artifact records depends on which path produced
it:

- An **in-process** candidate's artifact is **unchanged**: the same fields, in
  the same order, byte for byte, as before this protocol existed. The external
  reporting below is additive and MUST NOT alter it.
- An **external** candidate's artifact carries, in addition: **`AgentFailures`**,
  an object keyed by §8 reason code whose value is the number of matches that
  failed with that code — a code that never fired is absent, and an empty object
  means a clean run; **`VoidRuns`**, the void count above; **`AgentCommand`**, the
  argv the §3.3 splitter produced, with the program as its first element, so the
  exact command that was scored is part of the result rather than part of the
  reader's shell history; **`AgentLimits`**, the two effective §7 limits the
  matches were actually played under — `step_timeout_ms` and the
  `match_timeout_ms` computed from it — as §3.1 requires a reported run to state;
  and **`AgentForfeits`**, one entry per forfeited match, carrying the partial
  scores the forfeit was computed from. That last field is present only when
  something was forfeited, and is described under "A failed match's scores" below.

  **The trade-off in that last field.** `AgentForfeits` is **omitted** from a run
  in which nothing was forfeited rather than written as an empty array. That is a
  deliberate choice, and it is the opposite of `AgentFailures`, which *is* written
  as an empty object on a clean run. An always-present `AgentForfeits: []` would
  have cost every clean run a fixed number of bytes and a fixed token in the
  artifact forever, to carry no information; omitting it means the field costs a
  clean run **zero** bytes and its **presence** stays a reliable "something was
  forfeited" signal that needs no separate flag to read. A consumer can therefore
  test for the field's existence instead of testing it for emptiness, and a clean
  run's artifact is byte for byte what it would have been had the field not been
  defined. The cost of that choice is that the field has no zero value: a reader
  that wants "how many forfeits" must treat absence as `0` rather than expecting
  the count to be there.

`AgentFailures` is **report-only** here exactly as it is above: it enters no
rate, no delta, no confidence interval, and no decision rule, and it MUST NOT be
added to `Wins`, `Draws`, `Losses`, or `Timeouts`. Every failure is already a
loss in those four, by §9.1; this field says how many of the losses had a cause.

**A failed match's scores: the forfeit.** A recorded row for a match that ended in
an agent failure MUST carry the external agent's score as **`0`**, whatever the
scoreboard said when the plumbing broke, and MUST carry the **opponent's** score as
it stood at the moment of failure. The outcome on that row is still a win for the
baseline side — a failure cannot be won, whatever the numbers say — so the forfeit
moves a score and never an outcome. Both scores are indexed by **slot**, as every
row this protocol writes, so which side forfeits is decided by the seat the agent
actually played and never by the position of a field.

*Handshake failures* are `0-0` already: no step ever began, so there is no partial
to forfeit from, and the opponent's score at that moment is `0` as well. The rule
is a no-op there and states it rather than inventing a number in either direction.

*Void runs are unaffected.* `host_limit` is not an agent failure, so it is not a
forfeit: it still produces no row, no delta, and no denominator, exactly as above.

**Why.** The paired delta of §9.4 is computed from **scores**, so the score a row
carries is the score the delta is built from. While a failed match reported the
scoreboard it had reached, an agent that led `5-1` and then stopped answering banked
a `+4` contribution to the mean paired delta **and** took the loss in the outcome
rates — leading and then stalling was strictly better than never leading, because
the crash was free. The delta measures how well a policy played; a policy that
stopped playing did not play well, so crediting it for the lead it abandoned
measured the stall rather than the play. The forfeit removes that incentive. It
does so **without** making the delta a different statistic from the in-process one,
because it is applied to the external row before the row is built and the same
`PairedStudy.Analyze` still computes everything else identically — which §9.4
requires and which an analyzer change would have broken.

**Why the opponent keeps its score.** Only the side that broke the contract
forfeits. The baseline played every step it was asked to play; zeroing its score as
well would make a stall a **draw**, handing the external agent a point for its own
failure and reintroducing the same incentive one level down. The kept score is also
what keeps the result honest: with the external side at `0` and the opponent's at a
non-negative number, each mirrored delta is `-opponent`, so **a stall can never
produce a positive delta for the external side** — a structural property of the
rule rather than a property any particular case happens to have.

**Where the partial scores live.** The raw scores at the moment of failure MUST be
preserved, because a row of zeros alone would make a match that ran two hundred
steps and one that never started indistinguishable, and telling those apart is the
whole of what a reader diagnosing a flaky agent has to go on. They are recorded
**outside the row that reaches the analyzer**, in the fields
`PartialScoreAtSlot0` and `PartialScoreAtSlot1` of the artifact's `AgentForfeits`
array, alongside `Seed`, `ExternalSeat`, `Reason`, and the two scores the study was
actually scored from (`ScoredExternalScore`, `ScoredOpponentScore`). Each entry
names the seat, so the slot-indexed partials are never misread as team-indexed ones.

Those partials are **report-only** in the strongest available sense: they enter no
rate, no delta, no confidence interval, no per-seed row, and no decision rule. The
guarantee is structural rather than a promise — the statistics are computed by the
analyzer from the `MatchResult` rows *before* `AgentForfeits` is assembled, and the
analyzer has no field from which to read a partial. `AgentForfeits` is **absent**
from a run in which nothing was forfeited, so a clean run's artifact is unchanged
byte for byte.

**Voids and the grading floor.** Void runs are excluded from the denominator
above, so they reduce the number of **valid seeds** — the seeds for which *both*
mirrored seatings produced a match, which is what §9.4's analyzer requires and
counts. A run whose voids leave fewer than 30 valid seeds is reported as
**"Not graded"**, and the report MUST say so **explicitly**, naming the void
count. This is the only respect in which a void can change a verdict, and it
changes it by removing evidence rather than by adding any: a study that quietly
fell below the floor would be indistinguishable from one that was never large
enough to grade.

### 9.4 Scoring is through the existing paired evaluation

An external agent is scored with **identical** statistics and identical floors
to an in-process agent. There is no external-agent-specific statistic, no
adjusted floor, and no separate report. Specifically, an external agent run
through `evaluate` MUST use the same:

- mirrored-seat pairings `(0, 1)` and `(1, 0)` (`Cli/CliApp.cs:900`);
- canonical seed suites, dev `1001..1050` and held-out `2001..2050`
  (`Cli/CliApp.cs:65-66`);
- per-match tick budget `EvaluationSimulationConfig.MaxTicks`
  (`Cli/CliApp.cs:74-75`);
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
selector** (`Cli/CliApp.cs:316-323`, `Cli/CliApp.cs:1434`,
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

**What `--agent-cmd` is mutually exclusive with.** The external agent *is* the
candidate side: it takes the seat the in-process candidate takes, against the
same baseline, on the same maps, under the same mirrored pairings, and its rows
go through the same statistics (§9.4). It therefore **MUST NOT be combined with
any in-process candidate selector** — two candidate selectors on one run is
ambiguous, and which one won would be a property of flag order rather than of
the study. Such a combination is a **usage error** and exits **2**, like every
other `--agent-cmd` usage error (§3.3), before any match runs. In 3.0.0
`evaluate` exposes no in-process candidate selector at all — the target policy is
fixed — so the rule holds by construction and is recorded here so that adding a
selector later cannot quietly make the two combinable. The other `evaluate` flags
(`--scenario`, `--seed-set`, `--seeds`, `--rollouts`, `--out`, `--commit`) are
**not** candidate selectors and mean exactly what they mean for an in-process
run.

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

## 13. Non-goals for 3.0.0

The following are explicitly **out of scope** and MUST NOT be added under
protocol `1`:

- **Custom scenario files.** Scenario selection in 3.0.0 is the existing
  `evaluate --scenario standard|bottleneck` switch
  (`Cli/CliApp.cs:845-851`). A user-authored scenario file is a later
  protocol.
- **In-process plugins.** An `IAgent` loaded into the harness
  (`Agents/IAgent.cs:14-27`) is the existing path and is unchanged. The wire
  protocol is an addition, not a replacement.
- **External agents through `simulate`.** `simulate` MUST NOT accept an
  external agent in 3.0.0. It runs one episode, prints one result, and has no
  paired-study machinery, no mirrored seatings, and no seed suite — the
  apparatus §9.4 scores external agents with. Running an external process
  through `simulate` would produce a number that looks like a measurement and is
  not one: a single episode has no mirror, no confidence interval, and no
  grading floor, so a result from it could not be compared with anything
  published here. `simulate --agent greedy|random|mcts` keeps its exact
  meaning — an in-process policy enum (`Cli/CliApp.cs:316-323`) — and MUST NOT
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
  threat model, and is not in 3.0.0.

---

## 14. Settled points

Points that stage 2 settled are recorded here with their resolution, so the
reasoning survives the fact that the answer is now in the normative text above;
points stage 3 settled are recorded the same way. A point listed here is a
decision, not a gap, and this document does **not** invent answers for anything
it has not decided: where a row says a value or a contract is fixed, that text
lives in the sections the row cites, and if the two ever disagree the row is not
what governs.

| # | Settled point | Why it mattered | Resolution |
| :--- | :--- | :--- | :--- |
| U-1 | The protocol project's assembly name and directory. | §11 requires a project with a hard no-`ProjectReference` rule; the name is a stage-3 mechanical choice. | **Resolved.** The project is `Lattice.Protocol`, in `Protocol/`, a leaf that references nothing in this repository. Tests are in `Tests/Protocol/`; fixtures are in `Tests/fixtures/protocol/`. §11.1 states the name, the directory, the leaf property, and the one-way test reference. |
| U-2 | The values of `step_timeout_ms` and `match_timeout_ms`. | Decision 5 defers these to stage 3 explicitly. | **Resolved.** The default `step_timeout_ms` is **`5000`**, and `match_timeout_ms` is **computed as `step_timeout_ms × max_ticks + 30000`**. The handshake uses `step_timeout_ms`, so `timeout_handshake` and `timeout_step` are the same knob. Both values are carried in `hello.limits` and MUST be recorded in the run's output metadata. Because `match_timeout_ms` is *defined* in terms of `step_timeout_ms` and `max_ticks`, §7's `match_timeout_ms ≥ step_timeout_ms × MaxTicks` constraint now **holds by construction** instead of being a relationship a reader has to verify; the §4.1 `hello` example has been corrected to `5000 × 500 + 30000 = 2530000` to satisfy it, and the test that existed only to excuse the previous divergence is gone. All three timeouts are measured on a **monotonic** clock, never the wall clock. The values are per-match, not protocol constants, so fixing them is not a version change (§12.6). Normative in §3.1, §4.1, and §7. |
| U-3 | **`hello` carried no tick budget or agent count.** | A conforming v1 agent could learn its slot, its seed, and its map, but **not** `MaxTicks` and **not** the agent count up front. An agent that plans a horizon had no way to know the horizon except by running out of steps, and `MaxTicks` is the input to any time-aware plan. This was the largest usability gap in the contract. | **Resolved.** `hello` gains two **required** integer fields: `max_ticks` (from `SimulationConfig.MaxTicks`, `Environment/Simulation.cs:28`, the same value the harness passes as `MaxSteps`, `Agents/EvaluationHarness.cs:147-148`) and `agent_count` (from `SimulationConfig.AgentCount`, `Environment/Simulation.cs:25`, bounded 2..4 at `Environment/Simulation.cs:58-60`). Both are additions to a `hello` that no released agent has ever seen, so no version bump is due (§12.8). The `hello` field list, the §4.1 example line, the §12 versioning rules, and the §7 `match_timeout_ms` constraint (which is now computed from the number Lattice actually sends) are all updated to match. |
| U-4 | The stderr ring-buffer capacity. | §1.1 requires a bounded ring but no bound is given. | **Resolved.** The capacity is **`65536` bytes (64 KiB)** per match, discarding oldest-first so the buffer always holds the **last** 64 KiB written. The drain is **continuous, on its own reader, for the whole life of the process** — including while Lattice is blocked reading stdout — so a chatty agent can never fill the pipe and deadlock; the drain MUST NOT be deferred until a failure has been detected. The last 64 KiB is **attached to the failure record** on `agent_crashed`, `agent_exited`, `timeout_handshake`, `timeout_step`, and `timeout_match`, and is **never parsed**, never interpreted as protocol, and never allowed to influence the action stream or any statistic. Normative in §1.1, cross-referenced from §9.3. |
| U-5 | **There was no handshake-timeout reason code.** | The closed set had `timeout_step` and `timeout_match` but nothing for "the agent never sent `hello_ack`". Assigning it to `timeout_step` treated the handshake as step 0's exchange, which was defensible but an interpretation, not a decision. An *invalid* `hello_ack` was not affected — a wrong `protocol` value is `protocol_mismatch` (§2). | **Resolved.** A thirteenth agent code, `timeout_handshake`, is added: no valid `hello_ack` within `step_timeout_ms` of `hello`. It is scored as an **agent failure** like every other agent code (§9.1, §9.3). It is distinct from `protocol_mismatch` (a wrong `protocol` value, which arrived) and from `schema_violation` (a malformed `hello_ack`, which also arrived). It sits in the Timing group in §8.2 and second among the agent codes in the §8.4 precedence, immediately after `protocol_mismatch`. §2, §7, §8, §8.1, §8.2, §8.4, and §9.3 are updated. |
| U-6 | **A protocol failure had no reported count of its own** — it was a loss and nothing more. | §9.3 scores failures as losses per decision 6, which is right for the statistics but means a study cannot report "how often did the external agent's plumbing break" separately from "how badly did it play". | **Resolved.** The reported statistics carry an **`AgentFailures`** count, incremented once per match whose `TerminationReason` is one of the thirteen agent-attributable codes, kept **separate** from `MatchOutcome.Timeout` (`Timeout` remains "budget burned without a terminal tick") and separate from `VoidRuns`. It is **report-only**: it MUST NOT change scoring, the paired delta, the confidence interval, or the decision rule, all of which stay exactly as §9.4 specifies. Agent failures still count as losses, and the forfeit to `0` that §9.3 mandates is the scoring rule those losses are already scored under, not something `AgentFailures` introduces or alters. §9.3 states the boundary. |
| U-7 | Whether an over-long **outbound** line is a loss for the external agent. | §7 requires Lattice to refuse a match whose `observation` would exceed 1 MiB. No reason code covered that, and under decision 6's literal wording ("every failure ... is scored as a loss for the external agent") the agent was blamed for a limit Lattice's own map generation caused — a map-size choice the agent could neither see nor influence. | **Resolved.** A fourteenth code, `host_limit`, records the refusal. Lattice checks its own outbound `observation` against **both** `max_line_bytes` and `max_json_depth` **before writing any byte**, refuses the match if it would breach either, records the run as **void / invalid**, and does **NOT** score it as a loss. Void runs are **reported as a count** (`VoidRuns`) and **excluded from the paired statistics** — from the delta, the per-outcome rates, the confidence interval, the decision rule, and the 30-seed grading denominator — because neither agent played. The §8 and §9.3 tables carry an explicit **`Fault`** column (`agent` vs `host`) so the partition is normative rather than inferred. §8.4 puts `host_limit` first, since it is detected before any wire exchange exists. An *inbound* over-long line is unaffected and remains `line_too_long`, an agent loss. |
| U-8 | Process lifetime scope: one process per match, or one long-lived process across a whole evaluation suite. | A per-match process is simpler and matches the in-process factory contract, which builds a fresh agent per (pairing, seed) so RNG streams cannot leak between matches (`Agents/IAgentFactory.cs:12-23`, `Agents/EvaluationHarness.cs:175`). A long-lived process would need a reset message that does not exist in the five-type catalogue. | **Resolved: one agent process per match.** Stated normatively in §3 as the first handshake requirement — Lattice MUST launch a fresh process per match, send `hello` exactly once, and MUST NOT reuse a process across matches, seed pairings, or the mirrored seatings of one seed. This buys unconditional isolation (one agent's crash, hang, or memory growth cannot affect another match) and makes state carryover between seeds impossible by construction. A suite-scoped process would need a reset message, and the closed five-type catalogue (§4) has none; that remains a protocol-2 change. **Semantics only here** — process launch, the stdin/stdout pumps, and the timeout enforcement are stage 3, not stage 2. |
| U-9 | Process launch details: argv, environment, working directory, and how `--agent-cmd` is split into program and arguments. | §9.5 names the flag and its value shape; the launch contract is not written anywhere. | **Resolved: an argv, never a shell.** Normative in the new **§3.2**, alongside §3's existing one-process-per-match lifetime rule. Lattice receives a **program plus an argument list**, already split, and starts it with `ProcessStartInfo.ArgumentList` — one argument at a time — with `UseShellExecute = false`. Lattice MUST NOT build a command string, MUST NOT involve a shell or `cmd /c`, and MUST NOT concatenate, quote, escape, or re-parse an argv into a string and back, so a space in an argument stays one argument and a shell metacharacter has no meaning. The **working directory is the caller's current directory**, with no resolution from the agent's own path. The **environment is inherited**, and Lattice adds exactly one variable, **`LATTICE_PROTOCOL=1`**, which is informational only: it can never make an incompatible agent compatible, because negotiation remains the exact-match handshake of §2. **How a CLI string such as `--agent-cmd "python3 my_agent.py"` becomes a program and an argv was explicitly stage 4's concern, not this document's** — the contract here begins at the argv, and holds unchanged whichever splitter produces it. **That follow-up is now settled as well, in §3.3**: unquoted whitespace separates, a double quote groups, a backslash escapes only `"` and `\` inside a group and is an ordinary character everywhere else (so `C:\agents\python.exe` survives unquoted), there is no globbing, no variable expansion, and no single-quote semantics, and an unterminated quote or an empty command is a usage error exiting **2** before any match runs. The program is resolved **once, up front** — a value containing a directory separator (`/` or `\`, on any platform) is a path, otherwise `PATH` (plus `PATHEXT` on Windows) is searched — and a program that does not resolve is a **usage error naming it**: not a match result, not a §8 code, and no artifact. Resolving up front is the same discipline as §9.1's no-retry rule: a launch failure found mid-suite could only be scored as a loss, which blames the agent for Lattice's own `PATH`, or retried; refusing to start invents no score. |
| U-10 | Whether the external agent may also be run through `simulate`. | Decision 9 scopes external agents to `evaluate` and says `simulate --agent greedy|random|mcts` is unchanged. Whether a *separate* `simulate` flag for external agents is wanted was unaddressed. | **Resolved: no.** `simulate` MUST NOT accept an external agent in 3.0.0; it is listed as an explicit **non-goal** in §13. The reason is commensurability, not convenience: `simulate` runs one episode and has no mirror, no confidence interval, and no grading floor, so a number produced there could not be compared with any published result. `simulate --agent` keeps its exact in-process-enum meaning; the external path is `evaluate --agent-cmd` only (§9.5). |

**All ten points are now resolved.** U-1, U-3, U-5, U-6, U-7, U-8 and U-10 were
settled in stage 2; U-2, U-4 and U-9 are settled here, in the body of this
document. The one half of U-9 that stage 3 deliberately left to the CLI surface —
how a command-line **string** becomes a program and an argv — is settled in
**§3.3**, so the launch contract is now specified end to end rather than from
the argv onwards. Nothing in the table above is provisional: each row is
normative in the sections it cites. Because no conforming agent has ever existed,
none of this required a version bump — the discipline in §12.8 binds from the
first release onward, and the reason set is frozen at fourteen codes.

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
| `Cli/CliApp.cs:316-323` | `simulate --agent greedy\|random\|mcts` — the existing selector. |
| `Cli/CliApp.cs:391-393` | `infiltration` forbids `--agent`. |
| `Cli/CliApp.cs:804` | The `evaluate` command. |
| `Cli/CliApp.cs:845-851` | `evaluate --scenario standard\|bottleneck`. |
| `Cli/CliApp.cs:854-858`, `Cli/CliApp.cs:900`, `Cli/CliApp.cs:1000` | Evaluation teams, and the mirrored seatings on both the in-process (`(0, 1)` and `(1, 0)`) and the external (`externalSlot` 0 then 1) path. |
| `Cli/CliApp.cs:913-919` | Canonical seed suites, tick budget, `PairedStudy.Analyze`. |
| `Cli/CliApp.cs:1434` | `simulate --agent` usage text. |
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
