# ADR-0005 — External agent wire contract: exact-match v1, strict schema, isolated protocol project, `--agent-cmd`

**Status:** accepted

## Context

Stage 2 designed the external-agent protocol core and stopped at 1.1: the
design was delivered as a report, never committed, so there was no contract to
implement against. This ADR records the four decisions from that design that
shaped the committed spec
([`docs/EXTERNAL_AGENT_PROTOCOL.md`](../EXTERNAL_AGENT_PROTOCOL.md)) and are
expensive to reverse once an implementation exists.

The feature in one sentence: let a program that is not in this repository play
Lattice as an agent, over the agent process's stdin/stdout, and score it through
the same paired evaluation and the same floors as an in-process agent. Four
points were genuinely open, and each had at least two defensible answers:

1. **Version negotiation.** How does an agent process and Lattice agree they
   speak the same contract, and what happens when they do not?
2. **Strictness.** When an agent's message carries something the contract does
   not describe — an extra field, a new `type`, a missing required field — does
   Lattice refuse, or does it be liberal in what it accepts?
3. **Where the wire format lives.** The wire shape is derived from
   `Lattice.Environment.Observation` and maps onto
   `Lattice.Environment.AgentAction` (`Environment/StepContracts.cs:31`,
   `Environment/StepContracts.cs:62-67`). Should the protocol project be able
   to see those types, or should the boundary hold?
4. **How an external agent is named on the command line.** `simulate` already
   owns `--agent greedy|random|mcts` (`Cli/CliApp.cs:316-323`). What does the
   external-agent flag on `evaluate` look like?

Each of these interacts with an existing, deliberate property of the codebase.
The determinism and replay claims rest on the step contract being pure data and
`Lattice.Environment` being BCL-only. The trajectory schema already refuses
forward compatibility rather than guessing at it
(`docs/SUPPORT_AND_REPRODUCIBILITY.md:228-231`), and the evaluation statistics
and the 30-seed grading floor are the evidentiary basis for every published
claim (`Agents/PairedEvaluation.cs:144-150`). A protocol that quietly loosened
any of those would be a protocol whose measurements no longer mean what the
docs say they mean.

## Decision

### 1. Protocol version: one integer, exact match, Lattice authoritative

The version is the **integer** `1`, carried as `"protocol": 1` in both
directions. Lattice sends `hello`; the agent replies `hello_ack`; both carry
the same integer. Negotiation is an **exact match**. Any other value — a
different integer, a string, a float, a missing field, no reply — is refused
with reason `protocol_mismatch` and the match is scored as an agent failure.

**Lattice is authoritative and there is no downgrade.** Lattice does not offer
a list of acceptable versions, does not accept a version the agent chose, and
does not fall back to an older protocol.

Alternatives considered:

- **Capability negotiation** (exchange supported-version ranges, intersect,
  pick the highest). This is what a long-lived service would do, and it is
  wrong here. A protocol is a *contract*, and the value of a contract is that
  both sides know exactly which one they hold. Intersecting version ranges
  turns a refusable failure into a silent partial match, and it means the
  version actually in force is a function of the weaker participant — the
  opposite of "Lattice is authoritative".
- **Best-effort with a warning.** Adopting whichever version the agent offers
  and logging a warning. Rejected outright: it produces runs whose meaning
  depends on a log line nobody reads, which is precisely the class of
  untrustworthy measurement this project exists to avoid.
- **A string version like `"1.0.0"`.** Rejected: strings invite
  semver-compatible-range reasoning ("1.0.0" vs "1.0") and make `1` and `"1"`
  two spellings of the same thing, which under the strictness rule below
  cannot both be valid. An integer has exactly one spelling.

### 2. Strictness: refuse, never tolerate

Unknown fields, unknown `type` values, and missing required fields are all
**refused**. There is no silent tolerance anywhere on the wire, and Lattice
MUST NOT ignore an unrecognised field, substitute a default for a missing one,
or log-and-continue. Each maps to a named machine-readable reason:
`unknown_field`, `schema_violation`.

This adopts, verbatim, the forward-compatibility policy the trajectory reader
already follows — "forward compatibility is refused, not guessed" — and holds
the wire to the same standard as the on-disk format
(`docs/SUPPORT_AND_REPRODUCIBILITY.md:228-231`,
`docs/SUPPORT_AND_REPRODUCIBILITY.md:232-239`).

Alternatives considered:

- **Ignore unknown fields, refuse unknown `type` values.** The most common
  real-world JSON stance, and the one that makes forward compatibility easy.
  Rejected because it makes two agents with the same nominal version behave
  differently depending on what each chose to send, and because a field Lattice
  silently dropped is a field whose absence Lattice then reasoned about wrongly.
  The failure would surface as a mysterious score, not as a protocol error.
- **Log unknown fields and continue, report the count in the run summary.** A
  middle path that keeps a run alive while leaving a trace. Rejected: it makes
  a *result* depend on a diagnostic, so a run that "worked" is
  indistinguishable from a run that quietly ran under a different contract.
- **Refuse unknown fields but tolerate missing optional ones.** Already the
  design, and it is the only tolerance allowed: a nullable field that is absent
  means the documented default (`-1` for an unused action target), and its
  absence is in the schema rather than a violation. That is contract, not
  tolerance, because the value is fixed by `AgentAction`'s definition
  (`Environment/StepContracts.cs:31`).
- **Defer strictness to a `--lenient` flag for experimentation.** Rejected:
  the scoring path is the evidence path (§4). A flag that changes what the
  numbers mean, silently, is a claim-integrity hazard, and there is no
  experiment this project wants to run that cannot be run with a conforming
  agent.

### 3. Architecture: an isolated protocol project, mapping in the runner

The protocol project holds **only** plain wire types, a parser, and a writer,
and has **no `ProjectReference`** to `Lattice.Environment` or
`Lattice.Agents`. The mapping from wire to `Observation` / `AgentAction` lives
in the stage-3 runner, which already references both sides.
`Agents/ObservationView.cs` stays `internal` and unchanged.

The wire shape is therefore a *mechanical* projection of the step contract —
PascalCase to snake_case, nested records to nested objects, null strings
omitted — specified field by field with `file:line` provenance
(`docs/EXTERNAL_AGENT_PROTOCOL.md` §5), so that the contract is auditable
against the source rather than described in prose.

Alternatives considered:

- **Put the wire types in `Lattice.Environment` next to the step contract.**
  Rejected: `Lattice.Environment` is the audited BCL-only core that the golden
  fixtures and the per-step state hashes attest to. A protocol project in this
  assembly is trivially fuzzable *only if* it does not drag the simulation in
  with it, and it cannot both live here and be independent of here. It also
  would not actually have been independent — the dependency would be invisible.
- **Let the protocol project reference `Lattice.Environment` and map
  in-project.** Rejected: it is the shortest path to a working parser and it
  destroys the property that makes the protocol testable as pure bytes. A
  fuzzer for "does this line parse" would need a simulation to exist first, and
  a mapping bug would be indistinguishable from a contract bug.
- **Share types directly — put `Observation` on the wire.** Rejected outright,
  and this is the important one. It would make the wire format a function of
  the internal record layout, so renaming a C# property would silently break
  every external agent in the world, and `JsonPropertyName` attributes would
  leak wire concerns into the audited core. The 1:1 mapping is a *specified
  contract*, not a by-product of the type layout.
- **Make `ObservationView` public so external agents can reuse the
  derivations.** Rejected: it would widen the surface of `Lattice.Agents` for
  a consumer that cannot reference the assembly anyway. The two derivations it
  performs — my zone, and unclaimed resources in ascending id order — are
  documented in the spec instead, and an external agent implements them in
  whatever language it is written in. The ordering matters and is specified;
  the type does not need to travel.
- **Put the protocol in `Lattice.Agents`.** Rejected: `Lattice.Agents`
  already references `Lattice.Environment` (`Agents/Lattice.Agents.csproj`), so
  a protocol project there would be transitively coupled to the simulation.
  Isolation has to be structural, not conventional.

### 4. Scoring: the existing paired evaluation, and the flag is `--agent-cmd`

External agents are scored through the existing `evaluate` path with the
existing mirrored seatings, the existing seed suites, the existing statistics,
and the existing 30-seed grading floor
(`Cli/CliApp.cs:900`, `Cli/CliApp.cs:65-66`,
`Agents/PairedEvaluation.cs:144-150`). Every protocol
failure is recorded as a result and scored as a **loss** for the external agent;
failures are never retried, and a failed match still occupies both mirrored
seatings so the pair stays whole (`Agents/PairedEvaluation.cs:100-105`).

`simulate --agent greedy|random|mcts` is **unchanged**. The external-agent flag
on `evaluate` is **`--agent-cmd`**, and it takes a **command line**, not an
enum.

Alternatives considered:

- **Reuse `--agent` on `evaluate`, accepting any string as a command.**
  Rejected. `--agent` in this CLI means a three-value policy enum
  (`Cli/CliApp.cs:316-323`, `docs/CLI.md:34`). Overloading it with an arbitrary
  command line makes `--agent mcts` and `--agent "python3 my_agent.py"`
  syntactically identical and semantically unrelated, and forces a reader to
  know which subcommand they are under to know what the value means. The
  existing flag also has a hard rule attached to it — `--agent` is *forbidden*
  with `--scenario infiltration` (`Cli/CliApp.cs:391-393`) — and a flag cannot
  carry a three-way meaning.
- **A separate subcommand, e.g. `lattice evaluate-external`.** Rejected: it
  duplicates the entire `evaluate` flag surface, and the two commands would
  drift. The moment a seed suite, a floor, or a statistic changed, one of them
  would be stale — and the stale one is the one publishing numbers.
- **A new policy name registered in the existing enum, e.g. `--agent
  external`.** Rejected for the same reason as above in a different place: it
  forces the external agent *and its command line* through a value slot that is
  currently a closed set of in-process policy names, and it puts user-supplied
  text into a position the CLI validates against a fixed list.
- **Score external agents in a separate report with its own statistics.**
  Rejected: the entire value of this feature is comparability. An external
  agent that cannot be dropped into the same table as MCTS and the Scout
  baseline, under the same mirror, the same suites, and the same floor, has not
  been integrated — it has been given a parallel scoring system whose numbers
  are not commensurable with the project's published ones.
- **Retry a failed process, or substitute a default action.** Rejected: a
  retry rule makes the reported numbers a function of Lattice's internal
  recovery policy rather than the agent's behaviour, and a substituted default
  action would let an agent improve its *score* by crashing.

The failure-as-loss rule is deliberately harsher than it first looks, and the
harshness is the feature: a crash must never be a scoring strategy. One
consequence is flagged and unresolved in the spec — an over-long *outbound*
`observation` line, which Lattice refuses to emit, currently falls under the
same "every failure is a loss for the external agent" wording even though the
agent did not cause it. That gap is recorded as an open point rather than
patched here, because the reason-code set is closed and adding a code is a
version change.

## Addendum — open points settled before the first release (2026-09-27)

Protocol `1` had not shipped when this ADR was accepted, so the points the
spec left open in its §14 could be settled by amending the spec rather than by
cutting version `2`: `U-1` fixes the protocol project as `Lattice.Protocol` in
`Protocol/`, a leaf with no `ProjectReference` to any project in this repository,
with tests in `Tests/Protocol/` and fixtures in `Tests/fixtures/protocol/`,
which is the same name ADR decision 3 left to chance; `U-3` adds the required
integer fields `max_ticks` (from `SimulationConfig.MaxTicks`,
`Environment/Simulation.cs:28`) and `agent_count` (from
`SimulationConfig.AgentCount`, `Environment/Simulation.cs:25`) to `hello`,
closing the largest usability gap in the contract and superseding the paragraph
in §3.1 that read "`hello` carries no tick budget, agent count, or map" — the
map is still absent, and still arrives with the first observation; `U-5` adds
the thirteenth agent reason code `timeout_handshake`, bounded by
`step_timeout_ms` from the same `hello`, and **supersedes decision 1's clause
"a missing field, no reply — is refused with reason `protocol_mismatch`"**: a
`hello_ack` that never arrives is `timeout_handshake`, while one that arrives
and carries a wrong `protocol` value remains `protocol_mismatch`, and the new
code is scored as an agent failure like any other; `U-6` adds a report-only
`AgentFailures` count, incremented per agent-attributable failure and kept
separate from `MatchOutcome.Timeout` and from `VoidRuns`, which changes no
outcome, no delta, no confidence interval, and no decision rule, so the
"separate report" this ADR rejected is still rejected — the count is a column
in the existing report, not a parallel one; `U-7` adds the fourteenth code
`host_limit` and **settles the fairness problem this ADR explicitly left
unpatched** at the end of decision 4: when Lattice's own outbound `observation`
would breach `max_line_bytes` or `max_json_depth`, Lattice refuses the match
before sending, records it as a void run, reports the count, excludes it from
the paired statistics, and does not score it as a loss — so decision 4's
"every protocol failure is a loss" now holds for every fault the agent controls
and stops at the first fault only Lattice controls, which is a narrowing chosen
for fairness rather than a loosening of the anti-crash rule the ADR exists to
defend; `U-8` settles process lifetime as **one agent process per match**,
normative in §3 and matching the in-process fresh-agent-per-(pairing, seed)
contract, with the launch mechanics left to stage 3 as U-9; and `U-10` settles
that `simulate` does **not** accept external agents in v3.0, on commensurability
grounds — a single episode has no mirror, no confidence interval, and no
grading floor, so it could not be compared with anything published here — which
leaves `simulate --agent greedy|random|mcts` exactly as it is and the external
path on `evaluate --agent-cmd` only. `U-2`, `U-4`, and `U-9` remain open for
stage 3 — **since settled; see the addendum below.** Because no conforming agent exists yet, none of this required a
version bump; the discipline in spec §12.8 now binds, and the reason set is
frozen at fourteen codes from the first release onward.

- `docs/EXTERNAL_AGENT_PROTOCOL.md` is a committed, normative contract: a
  closed five-type message catalogue, a field-by-field projection of
  `Observation` and `AgentAction` with `file:line` provenance at `5fa9526`, a
  fourteen-code error table partitioned by fault, and an explicit list of points
  that are *not* settled. The last is deliberate — the spec says where it stops
  rather than inventing an answer.
- No existing behaviour changes. `Lattice.Environment` and `Lattice.Generator`
  stay BCL-only. `ObservationView` stays `internal` and unchanged. The
  trajectory schema stays at version 3 with no new header or step field
  (`Trajectories/TrajectoryModel.cs:21`,
  `Trajectories/TrajectoryModel.cs:46-85`), so no golden fixture moves.
- External actions are recorded exactly like in-process actions, so
  `replay --verify` and the per-step state hashes work with no special case —
  and replay never re-runs the external process, so a trajectory stays
  replayable with the agent binary absent. An agent that is itself
  nondeterministic produces different trajectories from the same seed; that is
  reported, not hidden, and must never be claimed as replay-equivalent.
- Because a failure is a loss and not a retry, an external agent cannot improve
  its measured score by crashing, and the reported statistics always account for
  every match played.
- The strictness rule is a real cost for agent authors: adding any field to the
  wire is a protocol-2 change, and a protocol-1 agent that guesses wrong fails
  loudly instead of half-working. That is the intended trade, and it is the same
  trade the trajectory reader already makes.
- A protocol failure is recorded as a loss and not in the `Timeout` bucket, so
  a study can report "how badly did it play" but not separately "how often did
  its plumbing break". Whether that reporting gap should be closed is an open
  question in the spec, not a decision taken here. **Closed by the addendum
  above (U-6): an `AgentFailures` count, report-only, alongside the existing
  statistics and not instead of them.**
## Addendum — stage 3 closed the last three open points (2026-09-27)

The first addendum left `U-2`, `U-4` and `U-9` open for stage 3, having
deliberately declined to invent answers to them. Stage 3 closed all three by
amending the spec rather than cutting version `2`, which the spec's own §12.8
permits precisely because no conforming agent has ever existed. **With these
three, every point in spec §14 is settled and none remains open.**

- **`U-2` — the time-limit values.** The default `step_timeout_ms` is `5000`, and
  `match_timeout_ms` is **computed** as `step_timeout_ms × max_ticks + 30000`.
  The choice worth recording is *why it is computed rather than a second
  constant*: spec §7 requires `match_timeout_ms ≥ step_timeout_ms × MaxTicks`, and
  two independently chosen numbers make that constraint something a future
  editor can silently break by changing only one of them. Deriving the match
  budget from the step budget and the tick budget makes the constraint hold **by
  construction**, which is the same move this ADR has made everywhere else — a
  rule enforced by structure rather than by vigilance. The `+ 30000` slack covers
  per-step bookkeeping and the final exchange so a match that legitimately uses
  its whole step budget does not trip the match limit on the way out. The
  handshake uses `step_timeout_ms`, so "never started" and "stalled once" are the
  same knob, and both values are recorded in the run's output metadata rather
  than inferred from a version number. All three timeouts are measured on a
  **monotonic** clock: a system clock adjustment must not be able to manufacture
  or suppress a timeout mid-match.
- **`U-4` — the stderr ring capacity.** `65536` bytes (64 KiB), per match,
  discarding oldest-first so it always holds the *last* 64 KiB. The load-bearing
  part is not the number but the **continuous drain on its own reader for the
  whole life of the process**. An undrained pipe is a deadlock, not a slow path:
  an agent that writes more than the OS pipe buffer holds blocks forever, and
  Lattice would then record `timeout_step` for a stall the *host* caused. So the
  drain runs while Lattice is blocked reading stdout and MUST NOT be deferred
  until a failure is detected, which is also why a >1 MiB stderr flood is a
  required test rather than a theoretical one. The tail is attached to the
  failure record on `agent_crashed`, `agent_exited` and the three `timeout_*`
  codes, and — consistent with §1.1's standing rule that stderr is diagnostic
  only — is never parsed and never enters a statistic.
- **`U-9` — the launch contract: an argv, never a shell.** Lattice receives a
  **program plus an argument list**, already split, and starts the child with
  `ProcessStartInfo.ArgumentList` one argument at a time with
  `UseShellExecute = false`. No command string, no `cmd /c`, and no
  concatenate-quote-reparse round trip. This continues the ADR's existing
  posture rather than introducing a new one: `--agent-cmd` is arbitrary
  user-supplied text, and the one place a shell would help is the one place an
  argument becomes a command. Deriving the argv means a space in a path stays one
  argument and a shell metacharacter is just bytes, so Lattice's injection
  surface is empty by construction. The working directory is the **caller's**
  current directory and the environment is **inherited**, with exactly one
  variable added: **`LATTICE_PROTOCOL=1`**, which is explicitly *informational* —
  it can never make an incompatible agent compatible, because negotiation remains
  the exact-match handshake of decision 1. Deliberately left to **stage 4** is
  how a CLI *string* becomes a program and an argv: that is a CLI-surface
  question, the contract here begins at the argv, and everything above holds
  unchanged whichever splitter stage 4 picks.

Because the reason set, the field list, and both protocol constants are untouched
by this addendum, and because the two time limits are per-match rather than
protocol constants (§12.6), **no version bump is due**: protocol `1` ships with
fourteen codes, a `hello` carrying `max_ticks` and `agent_count`, and the limits
above. The `LATTICE_PROTOCOL` variable is the one genuinely new observable in
this addendum, and it is additive to the *process environment* rather than to the
wire, so it is not the kind of change §12.4 governs.

- The launch contract, the timeouts, and the stderr drain are now normative in
  spec §3.2, §3.1/§7, and §1.1 respectively, and are implemented by the
  stage-3 runner in `Agents/External/`. §11.2's placement holds: the wire
  vocabulary stayed in the leaf `Lattice.Protocol` and the wire ↔
  `Observation`/`AgentAction` mapping sits in the runner, which is the only
  place that sees both.
- `Protocol/ProtocolLimits.cs` still asserts **no** time value, deliberately. The
  defaults are the spec's and the runner's; the protocol project is a leaf that
  must not grow a policy it would then have to keep in step with §3.1.

## Addendum — stage 4 settled the splitter (2026-09-27)

U-9's second half — how a CLI **string** becomes a program and an argv — was
left open by the addendum above, and is now fixed in spec **§3.3**:
unquoted whitespace separates, a double quote groups, a backslash escapes only
`"` and `\` inside a group and is an ordinary character everywhere else (so
`C:\agents\python.exe` survives unquoted), and there is no globbing, no variable
expansion, and no single-quote semantics. Two conditions are usage errors that
exit **2** before any match runs: an unterminated quote, and an empty command.
The program is resolved **once, up front** — a value containing a directory
separator is a path, otherwise `PATH` (plus `PATHEXT` on Windows) is searched —
and a program that does not resolve is a **usage error naming it**, not a match
result and not a §8 code, because no agent ever spoke.

The reasoning is the ADR's own, applied to the one place it had not reached. A
shell would be the only thing that could turn an argument into a command, and
§3.2 exists to keep that from happening; a fixed splitter keeps the launch
surface identical on all three operating systems instead of inheriting three
different shells' rules. Resolving the program *before* the first match is the
same choice as §9.1's no-retry rule seen from the other side: a launch failure
discovered mid-suite could only be scored as a loss — blaming the agent for
Lattice's own `PATH` — or retried, and refusing to start is the only option that
invents no score and consumes no seed. The two new usage-error conditions are
deliberately narrow: they are the two inputs from which no argv can be derived
honestly, and every other input is bytes in an argument.

The external agent is scored by the **existing** `PairedStudy.Analyze`, with no
change to the delta, the confidence interval, the per-outcome rates, the 30-seed
floor, or the decision rule: an external run is converted into the same
`MatchResult` rows an in-process run produces and handed to the same analyzer,
so "identical statistics" (§9.4) is a property of the code path rather than a
promise. The one scoring decision this ADR had to make is **what a failed match's
row carries**, and it is made on the external row rather than in the analyzer, which
is what keeps the two paths on one implementation.

That decision is the **forfeit**. A match that ends in an agent failure is scored
from `0` for the external side, with the opponent keeping the score it had at the
moment of failure (§9.3). The rejected alternative was to carry the scoreboard the
match had reached, on the reasoning that a real number beats a fabricated zero.
That reasoning was sound about the *report* and wrong about the *statistic*: the
paired delta is computed from scores, so carrying the partials let an agent that
led 5-1 and then stalled bank a `+4` contribution to the mean delta while taking
the loss in the outcome rates. Leading and then stalling was strictly better than
never leading, because the crash was free. The delta is meant to measure how well a
policy played, and a policy that stopped playing did not play well.

Two properties of the chosen form are worth recording, because they are what make
it safe rather than merely stricter. First, the forfeit is applied **to the row
before the row is built**, in `ExternalMatchRunner`, and the analyzer is untouched
— so the statistic stays the same statistic, which an analyzer-level exclusion of
failed matches would not have been. Second, only the side that broke the contract
forfeits: zeroing the opponent too would make a stall a **draw**, handing the agent
a point for its own failure and reintroducing the same incentive one level down.
With the external side at `0` and the opponent's at a non-negative number, each
mirrored delta is `-opponent`, so a stall can never produce a positive delta for
the external side — a structural consequence rather than a hoped-for one.

The information the forfeit discards is not discarded. The raw partials are
recorded on `ExternalMatchResult` and in the artifact's `AgentForfeits` array,
**outside** the `MatchResult` the analyzer reads, so no code path exists by which a
partial reaches the delta, a rate, an interval, or the decision rule. A match that
ran two hundred steps and one that never started therefore stay distinguishable,
and `AgentForfeits` is absent entirely when nothing was forfeited.

The study artifact gains `AgentFailures`, `VoidRuns`, the agent argv, the
effective limits, and `AgentForfeits` **only** on the external path; an in-process
artifact carries exactly the seven fields it carried before this protocol existed,
in the same order, and the golden fixture that pins that field set is the check. It
compares canonicalized tokens rather than raw bytes, and excludes the timestamp
and the host provenance, so "unchanged" here means every field name, every field
order, and every evaluation value — which is the claim worth making.
