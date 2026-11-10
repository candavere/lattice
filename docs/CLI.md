# Lattice.Cli reference

`Lattice.Cli` exposes eight commands. Run any of them with
`dotnet run --project Cli -- <command> ...`; the binary name is `lattice`.
Every command is seeded, and the exit status is one of three values: `0` on
success, `2` when the command line is not runnable, and `1` when the command
was runnable and the work it named failed. The line between the two failures is
*when the fault became knowable* — everything decidable from `args` alone (no
command, an unknown command, an unknown/duplicated/valueless flag, a missing
required flag, a malformed value, an unexpected argument) is `2` and nothing is
run; everything discovered by touching the filesystem or running the engine (a
file that is missing or unparseable, a descriptor that fails validation or
declares something this build cannot run, a replay divergence) is `1`. This page
is the full flag-by-flag reference; the landing page points here from
[Where to go next](../README.md#where-to-go-next).

## Command summary

| Command | Purpose | Key flags |
| :--- | :--- | :--- |
| `generate` | Write a valid seeded map | `--seed`, `--min-fairness`, `--out` |
| `simulate` | Record an episode as JSONL | `--seed`, `--steps`, `--agent`, `--scenario`, `--rules`, `--out` |
| `render` | Replay a recording as ASCII or SVG | `--trajectory`, `--format`, `--out` |
| `analyze` | Report on a recording (contention, pathing, heatmaps) | `--trajectory`, `--out` |
| `replay` | Replay and optionally verify per-step serialized equivalence and the final summary line | `<file>`, `--verify`, `--out` |
| `benchmark` | Run the five-case workload matrix | `--runs`, `--warmup`, `--commit`, `--cpu`, `--out` |
| `evaluate` | Mirror-seated paired MCTS study, or an external agent process | `--seed-set`, `--rollouts`, `--seeds`, `--scenario`, `--out`, `--agent-cmd`, `--agent-step-timeout-ms` |
| `validate-scenario` | Check a declarative scenario descriptor and print its SHA-256; runs no episode and writes no artifact | `<file>` or `--out <file>` |

`evaluate --agent-cmd "<command line>"` scores an external agent process in place
of the in-process MCTS candidate, under the same seeds, budget, and statistics;
a complete conformant agent is in
[`examples/python/`](../examples/python/README.md). The command line is split
**without a shell**: unquoted whitespace separates, a double quote groups, a
backslash escapes only `"` and `\` inside quotes and is an ordinary character
everywhere else.

Every command in this reference can also be invoked as `lattice <command>` if
you have installed the CLI into a throwaway local tool path. Version 4.0.0
is a release candidate, not a released version and not a registry package:
package it from this source tree only (`dotnet pack Cli -c Release`, then
install from that local source into a throwaway `--tool-path` with
`--version 4.0.0` — never a global `-g` install); published users use the
`v3.1.0` native binaries from its GitHub Release. See
[INSTALL.md](INSTALL.md) for the full install, packaging, launcher, and
`DOTNET_ROOT` paths. That
is the same compiled binary under a launcher name, so the flags, output and exit
codes below are identical either way; every example on this page uses the
zero-install `dotnet run --project Cli --` form and stays valid as written.

## generate — write a valid map

| Flag | Description |
| :--- | :--- |
| `--seed <ulong>` | Required RNG seed; same seed → same map |
| `--min-fairness <0..1>` | Reject candidates whose measured `SpawnBiasIndex` exceeds the threshold; retry, never patch |
| `--out <file>` | Write JSON to a file instead of stdout |

```sh
dotnet run --project Cli -- generate --seed 123
dotnet run --project Cli -- generate --seed 123 --out map.json
dotnet run --project Cli -- generate --seed 123 --min-fairness 0.3
```

Output is compact PascalCase JSON, the same shape the trajectory header
embeds. With `--min-fairness`, candidates run through the mirrored fairness
arena (greedy policy, two agents, 200 ticks, transit speed 8); a seed whose
retry budget yields no fair map exits non-zero.

## simulate — record an episode as JSONL

| Flag | Description |
| :--- | :--- |
| `--seed <ulong>` | Required RNG seed for map generation and agents |
| `--steps <n>` | Tick budget; default 100. Episode ends on budget or when all resources are claimed |
| `--agent <greedy\|random\|mcts>` | Policy for player 0 (default `greedy`); `mcts` selects the evaluation subject |
| `--scenario <infiltration>` | Fixed Dungeon Infiltration & Sentry Patrol scenario; `--agent` is forbidden. A **path** to a scenario file is also accepted; see [above](#simulate--record-an-episode-from-a-scenario-file) |
| `--rules <file>` | Load a JSON `DynamicMapRuleSet` into the episode |
| `--out <file>` | Write trajectory to a file instead of stdout |

```sh
dotnet run --project Cli -- simulate --seed 42
dotnet run --project Cli -- simulate --seed 42 --steps 40
dotnet run --project Cli -- simulate --seed 42 --steps 40 --agent mcts
dotnet run --project Cli -- simulate --seed 42 --rules rules.json --steps 60 --out dynamic.jsonl
dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl
```

Without `--scenario`, runs `GreedyCollectorAgent` vs `RandomAgent` on a
procedurally generated map. A summary line — steps recorded, termination
reason, outcome — goes to stderr.

## render — replay a recorded trajectory

| Flag | Description |
| :--- | :--- |
| `--trajectory <file>` | Required JSONL trajectory |
| `--format <ascii\|svg>` | `ascii` (default) or `svg` |
| `--out <file>` | Write output to a file instead of stdout |

```sh
dotnet run --project Cli -- render --trajectory out/trajectory.jsonl
dotnet run --project Cli -- render --trajectory out/trajectory.jsonl --format svg --out frame.svg
```

`ascii` streams terminal frames; `svg` emits one self-contained,
CSS-animated, dependency-free SVG.

## analyze — report on a recorded trajectory

| Flag | Description |
| :--- | :--- |
| `--trajectory <file>` | Required JSONL trajectory |
| `--out <file>` | Write the full Markdown report to a file instead of the terminal view |

```sh
dotnet run --project Cli -- analyze --trajectory out/trajectory.jsonl
dotnet run --project Cli -- analyze --trajectory out/trajectory.jsonl --out report.md
```

The report covers contention events, turning points, per-agent pathing
efficiency, resource timelines, zone/edge heatmaps, and a per-agent steps
timeline. Analysis is a pure function of the trajectory.

## replay

```bash
# Replay a trajectory interactively or headless
dotnet run -c Release --project Cli -- replay <path-to-trajectory.jsonl>

# Replay with strict per-step serialized StepResult and state-digest verification
dotnet run -c Release --project Cli -- replay <path-to-trajectory.jsonl> --verify

# Write the re-serialized recording to a file instead of stdout (refused with --verify)
dotnet run -c Release --project Cli -- replay <path-to-trajectory.jsonl> --out <path>
```

`--verify` rebuilds the simulation state and dynamic topology rules from the
header, steps the engine identically, asserts tick-by-tick serialized
`StepResult` equality, and — for schema-3 recordings — recomputes the SHA-256
digest of the simulation state at every tick and compares it to the digest
recorded on each step line, naming the first mismatched tick. It is not raw
file-byte identity. A recording made before schema 3 carries no per-tick digest,
still verifies, and prints `no state hash: step-level verification only`; a
recording that declares schema 3 or later but carries no digest is a
discrepancy, not a notice. The exit code is `0` only when every recorded tick
reproduces its serialized `StepResult` **and** every recorded digest matches.
The same check runs against the canonical golden trajectory on the Ubuntu,
macOS, and Windows CI matrix.

## benchmark — measure the work-load matrix

| Flag | Description |
| :--- | :--- |
| `--runs <n>` | Measured iterations per workload; default 10 |
| `--warmup <n>` | Warm-up budget in ticks (realized as 1–2 full iterations); default 50000 |
| `--steps <n>` | Per-iteration ticks for raw cases (smoke passes); MCTS case keeps its own catalog budget |
| `--commit <sha>` | Source revision recorded in the artifact (provenance) |
| `--cpu <model>` | CPU model string recorded in the artifact (provenance) |
| `--out <file>` | Write the JSON artifact to a file instead of stdout |

```sh
dotnet run --project Cli -- benchmark --runs 10 --warmup 50000 --out benchmarks/throughput_benchmark.json
```

Five reproducible workloads, one harness protocol: JIT-settling warm-up that
anchors an FNV-1a step digest, measured iterations with a forced GC sweep
before each, per-step latency into one histogram, managed allocation via
`GC.GetAllocatedBytesForCurrentThread`, and Gen0/1/2 collection-count deltas.
Every measured iteration must reproduce the warm-up anchor's step digest —
off-script runs fail loudly instead of reporting timings.

## simulate — record an episode from a scenario file

`--scenario` takes **either** a built-in name **or** a path to a descriptor
file. The two are told apart by a path separator, never by a case-insensitive
name match, so a file that happens to be called `infiltration` stays a file and
a mistyped built-in name is reported as a name.

```sh
dotnet run --project Cli -- simulate --seed 42 --scenario scenarios/gated-vault-duel.json
```

```
scenario gated-vault-duel (sha256 a483b1a5143c51e122449594d6aabbec48e507700afafaf5a53fcf6516cc2523)
recorded 12 steps (resources-exhausted, winner: agent 0)
```

The descriptor is the authority: it supplies the map, the roster, the agent
count, the tick budget, and the transit speed, and the recording's header
carries the descriptor's SHA-256. A roster whose policies carry perception
filters (a sentry/infiltrator pair) is recorded with decision-time
perceptions; a roster that does not is recorded without them, because the
recording is all-or-nothing across the roster.

### Compatibility rules, stated rather than resolved

Some flags state the same thing the descriptor does. When both are present the
command **refuses**, because a silent precedence would make the recording
disagree with what the caller asked for without saying so:

| Combination | Behaviour |
| :--- | :--- |
| `--steps` with a scenario file | Refused. The descriptor's `Simulation.StepLimit` governs; passing both is a contradiction, and the error names the declared budget |
| `--agent` with a scenario file | Refused. The roster is the descriptor's `Slots`; overriding one seat would make the run something the file does not describe |
| `--rules` with a scenario file | Refused. Dynamic topology is not part of the descriptor schema in this release |

## evaluate — a paired study on a scenario file's map

`evaluate --scenario <path>` runs the mirrored-seat paired study on the map a
descriptor supplies, with the study's protocol otherwise unchanged.

```sh
dotnet run --project Cli -- evaluate --scenario scenarios/bottleneck-contention.json \
  --seed-set dev,heldout --seeds 30 --rollouts 32
```

What the descriptor controls and what it does not is deliberate:

- It **supplies the map** for every seed, and its SHA-256 is printed so the run
  names the file it came from.
- It does **not** supply the roster — the study remains MCTS vs Scout, or an
  external candidate in the MCTS seat under `--agent-cmd`.
- It does **not** supply the simulation config. A paired delta is only
  commensurable with other paired deltas under the same protocol, so adopting a
  descriptor's agent count or tick budget would produce a number shaped like a
  published study and not comparable to one. A descriptor declaring other than
  **two seats** is refused, with the reason.

The **artifact field set is unchanged** by a file-loaded study. It is pinned to
the in-process shape by a golden fixture, and the scenario digest goes to stderr
rather than into the JSON, so the published artifact format does not move under a
study that is otherwise identical to one already run. A file-loaded bottleneck
study produces the same per-seed rows and statistics as `--scenario bottleneck`.

## validate-scenario — check a declarative scenario descriptor

| Argument | Description |
| :--- | :--- |
| `<file>` (positional) or `--out <file>` | Required path to a scenario descriptor; both spellings are accepted |

```sh
dotnet run --project Cli -- validate-scenario scenarios/collection-skirmish.json
dotnet run --project Cli -- validate-scenario --out scenarios/gated-vault-duel.json
```

The command reads the descriptor **once**, validates it, and reports what it
declares on stderr:

```
scenario collection-skirmish is valid (schema v1)
  sha256         a63e5e05fd0dbd11a83d1cbca36d89ea1f55bc78afee3acd6be9a56e49ab4fbb
  map            generated family 'standard'
  simulation     2 agent(s), step limit 100, transit speed 0
  slot 0         greedy (Collector)
  slot 1         random (Opponent)
  victory        first-of-either   scoring resources-claimed
```

It has **no simulation or write side effect**: it runs no episode, emits no
trajectory on stdout, and creates no file even on the success path. That makes
it safe to run in a pre-commit or CI check.

A rejected descriptor prints one `scenario error: <field-path>: <reason>` line
per fault and exits non-zero, naming the offending field by its JSON path:

```
$ dotnet run --project Cli -- validate-scenario bad.json
scenario error: Map.ChokePoints[].ToZoneId: choke 0 ends at zone 9, which is not declared.
scenario error: Slots[0].RivalSlot: is required by policy 'sentry', which decides against a named opponent slot.
```

Every fault in the document is reported in one pass rather than only the first,
so an author fixing a hand-written descriptor sees the whole list at once.

### The descriptor format

A scenario descriptor is a **declarative, closed-schema JSON document**. It
contains data only: there are no scripts, no expressions, no reflection, no
dynamic type loading, no external commands, and no environment-variable-driven
behaviour. A descriptor can only select from mechanics the engine already
implements — it cannot introduce a new one.

The top-level fields are:

| Field | Required | Meaning |
| :--- | :--- | :--- |
| `SchemaVersion` | yes | Must be `1`, the only version this build accepts. A different value is rejected rather than interpreted |
| `Id` | yes | Lower-case kebab-case name (`a-z`, `0-9`, single dashes). This is the scenario's name in reports and provenance |
| `Name` | no | Human-readable display name |
| `Description` | no | Prose describing the scenario's intent |
| `Map` | yes | Where the topology comes from — see below |
| `Simulation` | yes | `AgentCount`, `StepLimit`, `TransitSpeed` |
| `Slots` | yes | The ordered agent roster, one entry per slot |
| `Victory` | yes | `Condition`, from the closed win-condition menu |
| `Scoring` | yes | `Scheme`, from the closed scoring menu |

**Unknown fields are rejected, not ignored.** A descriptor carrying a field the
schema does not define is an error naming both the field and the fields that
are known there, so a typo like `"StepLimit "` cannot silently fall back to a
default.

#### `Map` — a generated skeleton, a hand-authored graph, or both

`Map.Source` is `generated` or `static`, and the two forms are mutually
exclusive. Each is described in [Scenario files](SCENARIOS.md), which is the
full contract: the field-by-field schema, the closed menus, every validation
rule, and worked examples of both forms.

In short: `static` declares the whole topology in the file; `generated` names
one of the seeded families the engine already ships (`standard` or
`bottleneck`) and may narrow it with ordered overrides and additions, so a
descriptor can be a skeleton with full control over specific parts of it
without giving up the family's seed variation.

#### `Simulation`

| Field | Range | Meaning |
| :--- | :--- | :--- |
| `AgentCount` | 2–4 | Number of agent seats; the range `SimulationConfig` itself accepts |
| `StepLimit` | 1–100000 | Tick budget. The episode ends on budget or when all resources are claimed |
| `TransitSpeed` | 0 or ≥ 1 | `0` is instantaneous transit (the default); a positive value is distance-units per tick |

#### `Slots` — the ordered roster

`Slots` is an **ordered array**, and a slot's `Slot` must equal its position in
the array. Roster order is therefore explicit and total rather than implied by
a map or a set, so the seat each policy plays is never a guess.

| Field | Required | Meaning |
| :--- | :--- | :--- |
| `Slot` | yes | Must equal the entry's index in the array, `0..AgentCount-1` |
| `Policy` | yes | One of the closed menu: `greedy`, `random`, `mcts`, `scout`, `sentry`, `infiltrator` |
| `Role` | no | Display label for the seat (e.g. `"Sentry"`) |
| `RivalSlot` | policy-dependent | The opponent slot. **Required** by `sentry` and `infiltrator`, which decide against a named opponent; **rejected** on every other policy, which decides from the full observation |
| `Vision` | policy-dependent | The decision-time perception cone in graph hops, or `-1` for unbounded. **Accepted** by `scout`, `sentry`, `infiltrator`; **rejected** on every other policy |

Rejecting a field the policy does not use is deliberate: a `Vision` on
`greedy` would be a field the engine never reads, and accepting it would let a
descriptor assert a fog the run does not have.

#### `Victory` and `Scoring` — closed menus

These are **closed, documented menus of mechanics that already exist**. There is
no way to name a rule the engine does not implement, because an unrecognised
string is rejected with the menu quoted.

- `Victory.Condition` ∈ `resources-exhausted`, `step-limit`, `first-of-either`.
  Each names one of the two terminal reasons `Simulation.Step` actually raises,
  or the engine's real behaviour (whichever arrives first). Declaring a
  condition states which terminal reason the scenario is about; a recorded run
  always reports the reason that actually fired.
- `Scoring.Scheme` ∈ `resources-claimed`. This is the only scoring the engine
  has: `Simulation.Step` adds exactly 1 per successfully claimed resource, and
  the winner is the highest score with ties going to the lowest slot.

#### The SHA-256 digest

`validate-scenario` prints the descriptor's **SHA-256 digest, in lowercase hex**,
and the same digest is what a recording made from that file carries in its
header. The contract is exact:

- The digest is computed over the **exact raw source bytes of the file, read
  once** — never over a re-serialization, a canonicalized form, the file path,
  or an mtime.
- It is **lowercase hex**, 64 characters.
- **Whitespace and line endings are significant to the digest.** Two
  descriptors that are *semantically* identical but differ by a single byte of
  indentation, or by CRLF versus LF, hash differently. The digest identifies
  the **file**, not the meaning — which is the point: a reader holding a
  recording can name the exact bytes that produced it. Semantic equivalence and
  digest equality are deliberately separate properties, and this command
  reports the second while validating the first.

## evaluate — mirrored-seat MCTS evidence

| Flag | Description |
| :--- | :--- |
| `--seed-set <dev\|heldout>` | Canonical suites: `dev` = 1001..1050, `heldout` = 2001..2050 |
| `--rollouts <n>` | MCTS rollouts per action; default 32 |
| `--seeds <n>` | Cap on seeds per suite (default 50; the decision rule needs ≥ 30) |
| `--scenario <standard\|bottleneck>` | `standard` = generated maps; `bottleneck` = capacity-1 choke contention family. A **path** to a scenario file is also accepted and supplies the study's map; see [above](#evaluate--a-paired-study-on-a-scenario-files-map) |
| `--commit <sha>` | Source revision recorded in the artifact |
| `--out <file>` | Write the JSON artifact to a file instead of stdout |
| `--agent-cmd "<command line>"` | Score an external agent process as the candidate (see below) |
| `--agent-step-timeout-ms <n>` | External agents only: the per-step budget; default 5000. Accepted but unused without `--agent-cmd` |
| `--record-episodes <dir>` | External agents only: write each completed match's episode as trajectory JSONL under `<dir>` (see below). Requires `--agent-cmd` |

```sh
dotnet run --project Cli -- evaluate --seed-set dev,heldout --rollouts 32 --out benchmarks/mcts_evaluation_results.json
dotnet run --project Cli -- evaluate --seed-set dev,heldout --rollouts 32 --seeds 30 --scenario bottleneck --out benchmarks/bottleneck_evaluation_results.json
```

For every seed, two matches with mirrored seats. The per-seed paired delta
Δ = avg((score_MCTS − score_Scout) at seat 0, (score_Scout − score_MCTS) at
seat 1) cancels positional spawn bias. The report covers Δ statistics, a 95%
confidence interval on the mean, win/draw/loss/timeout rates, contention
saturation, and a verdict: **pass** only if mean Δ > 0 and the CI lower bound
> 0.

### Scoring an external agent

`--agent-cmd` names a process to launch instead of an in-process policy. The
external agent takes the seat MCTS would have taken, against the same Scout
baseline, on the same maps, under the same mirrored pairings, and is scored by
the same analyzer — same Δ, same confidence interval, same per-outcome rates,
same 30-seed floor, same verdict. It is not a fourth mode, and it cannot be
combined with an in-process candidate selector. `simulate` does not accept it at
all: a single episode has no mirror and no grading floor, so a number from there
could not be compared with a published study.

```sh
dotnet run --project Cli -- evaluate --scenario standard --seed-set dev --seeds 30 --agent-cmd "python3 examples/python/lattice_agent.py"
```

`--record-episodes <dir>` records what the external agent actually played. For
every match whose episode exists, the CLI writes the match as a trajectory JSONL
via the same `TrajectoryWriter.Record` path `simulate` uses — the map the match
ran, `EvaluationSimulationConfig` (`AgentCount: 2, MaxTicks: 200, TransitSpeed:
4`), the built-in digest of the scenario under evaluation as `ScenarioSha256`
(`collection-skirmish` for `standard`, `bottleneck-contention` for `bottleneck`,
or the descriptor's digest when the scenario came from a file), and
`AgentRoles` naming the external seat (`external:<program>`) and the baseline.
The file is named `<dir>/evaluate-seed<seed>-seat<slot>.jsonl` with `<slot>` the
external agent's seat, one file per completed match, so a `--seeds 1` run on the
dev suite produces two files. A faulted match carries no episode: nothing is
written for it and the skip is named on stderr. The directory is created if
missing; if any target file already exists the run refuses to start — no match
is played, the existing file is named, and the exit status is non-zero — because
a recording is evidence and is never silently overwritten. Each written file
passes `replay --verify`, exactly as any `simulate` recording does. The flag
requires `--agent-cmd`: an in-process evaluation produces no episodes to
record.

The value is a command line, split **without a shell**: unquoted whitespace
separates, a double quote groups, and a backslash escapes only `"` and `\` inside
a group and is an ordinary character everywhere else. There is no globbing, no
variable expansion, and no single-quote meaning. So a path containing a space
has to be quoted (`"C:\Program Files\agent.exe"`), and an unquoted Windows path
needs no escaping at all.

The program is resolved once, before any match is played: a value containing a
directory separator is used as a path, otherwise `PATH` is searched (`PATHEXT`
too, on Windows). A program that cannot be resolved or cannot be started is
**not a match result and not a scored failure** — no agent ever spoke — so the
CLI names it on stderr, exits **2**, and writes no artifact. So do an
unterminated quote, an empty command, and `--agent-step-timeout-ms` below 1.
Those four are a subset of the general usage contract at the top of this page:
**every** command-line fault in **every** command is a usage error and exits
**2**, decided before any work runs, so an author writing a script against this
CLI has one status for "I typed it wrong" rather than one per surface.

| Artifact field | Meaning |
| :--- | :--- |
| `AgentFailures` | Object keyed by protocol reason code (`timeout_step`, `agent_crashed`, …), each mapped to the number of matches that failed that way. Report-only: every failure is already a loss in the statistics above, and this says how many of the losses had a cause |
| `VoidRuns` | Matches Lattice refused on its own limits. Not a loss, and excluded from every statistic and from the grading denominator |
| `AgentCommand` | The argv that was launched, program first |
| `AgentLimits` | The `StepTimeoutMs` and `MatchTimeoutMs` the matches were played under; the match budget is computed from the step budget, never chosen separately |
| `AgentForfeits` | One entry per match that failed on the agent's plumbing: `Seed`, `ExternalSeat`, `Reason`, `PartialScoreAtSlot0`, `PartialScoreAtSlot1`, and the `ScoredExternalScore` / `ScoredOpponentScore` the study was scored from. Absent when nothing was forfeited |

`AgentForfeits` is omitted rather than written as an empty array, **by design**,
for byte stability: a run in which nothing was forfeited emits exactly the bytes
it would have emitted had the field not been defined, so the field costs a clean
run nothing and its presence stays a reliable "something was forfeited" signal —
a reader tests for the field, not for its emptiness. The trade-off is that the
field has no zero value, so a reader wanting a count must read absence as `0`.
This is the opposite of `AgentFailures` above, which *is* written as `{}` on a
clean run because a per-code breakdown with no codes still says "the breakdown
exists, and it is empty".

A match that ends in an agent failure is scored from **0 for the external side**,
with the opponent keeping the score it had at the moment of failure, so an agent
that leads and then stalls cannot bank a positive paired delta for the crash. The
partial scores it had reached are still recorded, under `AgentForfeits`, and enter
no statistic: the delta, the rates, the interval, and the decision are all computed
from the forfeited rows, which carry no field a partial could be read from.

All five appear only when `--agent-cmd` is used: an in-process artifact carries
exactly the seven fields it carried before external agents existed, in the same
order. The golden fixture pins that field set and every evaluation value in it,
though it compares tokens rather than raw bytes and excludes the clock and the
host provenance. The terminal
summary adds one line per suite reporting `valid_seeds` (the seeds that count
toward the grading floor), `void_runs`, and `agent_failures` broken down by code,
followed by a `decision` line carrying the same verdict string the in-process
study records under `Decision` in its artifact — the same analyzer's words, not a
second phrasing of them. If voids leave fewer than 30 valid seeds the run is "Not
graded", and the summary says that the exclusion is why.

## Model adapter (`lattice_model_agent.py`)

`examples/python/lattice_model_agent.py` is the v4 bring-your-own-model
adapter. It speaks protocol 1 on stdin/stdout (one JSON value per line,
nothing else on stdout) and, for each observation, POSTs
`{"model", "messages", "temperature"}` to an OpenAI-compatible
chat-completions endpoint and reads the reply text from the first choice's
message content. It accepts exactly `{"action":"wait"}`,
`{"action":"move","zone_id":N}` and `{"action":"collect","resource_id":N}`
(exact key sets; ids are integers in the observation's ranges) and otherwise
fails explicitly: any endpoint error, request timeout, or reply that fails
parse or validation is one stderr line and a non-zero exit — never a guessed
action. One retry is allowed on a request *timeout* only, and only when it
fits the step budget Lattice offered in `hello.limits`; an endpoint error
never retries.

Exact options (from the adapter's own argument parser):

| Option | Meaning |
| :--- | :--- |
| `--endpoint <url>` | Required. Full chat-completions URL (loopback for the acceptance slice; the adapter itself takes any URL its caller supplies) |
| `--model <id>` | Required. Model id sent in the request body |
| `--api-key-env <VAR>` | Optional. Names an environment variable holding the key; the key is sent only as an `Authorization: Bearer` header, never in the URL, argv, logs or sidecar. If the variable is unset or empty the adapter exits non-zero before the handshake |
| `--temperature <float>` | Sampling temperature sent to the endpoint; default `0.0` |
| `--request-timeout-ms <n>` | Per-request HTTP timeout; default `1500` |
| `--prompt-version <id>` | Required. Prompt-template version id recorded in the sidecar |
| `--sidecar <file>` | Optional. Writes one JSON object at startup (before the handshake): endpoint host, model, temperature, prompt version, parser version — never the key |
| `--acceptance-ledger <file>` | Optional, **opt-in acceptance mode, off by default**. Durable attempt ledger at this path; both seat processes of an acceptance pair must be given the **same** path. Every HTTP attempt (retries included) is reserved before it is sent; a `reserved` slot is never reclaimed after a crash. HTTP 401/403/429 marks the run stopped so no later process sends. Records requested model and server-reported `response.model` (or null) per attempt, HTTP status and error class; never credentials, headers or raw responses. Requires POSIX locking (`fcntl.flock`); fails closed elsewhere. Keep the ledger outside public source |
| `--acceptance-max-attempts <n>` | Optional, acceptance mode only (requires `--acceptance-ledger`); global HTTP attempt cap across processes; default `400`, must not exceed `400`. Counts retries and uncertain (post-crash) reservations too |

Public examples never put a key in `--agent-cmd` or in recorded artifacts;
the only credential channel is `--api-key-env`. The adapter works with any
OpenAI-compatible endpoint the caller has access to. It does not imply native
support for every provider or local runner, says nothing about model playing
quality, and supports no cross-provider benchmark-validity claim.

## Acceptance report for the external-agent path

`scripts/acceptance_v4.sh [OUT_DIR]` runs the checks that back the bring-your-own-model adapter (`examples/python/lattice_model_agent.py`) and writes `ACCEPTANCE.md` into a new evidence directory (default `.evidence/acceptance-v4-<UTC>`; an existing directory is refused, nothing is overwritten). Each slice is recorded as `works`, `broken` or `not-tested` with its command, exit code and evidence file. The script stops at the first `broken` slice and records the rest as `not-tested: not reached`. It does not restore packages (`--no-restore`), so it makes no network use.

| Slice | What it checks |
| :--- | :--- |
| S1 | Tracked tree clean (or only this script and this docs section differ, before they are committed); author and committer of every commit after `eb13fad` are the project identity |
| S2, S3 | `dotnet build --no-restore`; `dotnet test --no-build` (counts recorded, not asserted) |
| S4 | Adapter unit tests (stub endpoint, no network beyond loopback) |
| S5, S6 | Reference-agent match with `--record-episodes`; `replay --verify` on each recording |
| S7, S8, S9 | Expected-negative checks: overwrite refusal (exit 1), `--record-episodes` without `--agent-cmd` (exit 2), unset `--api-key-env` variable fails closed (exit 1, empty stdout, one documented stderr line) |
| S10 | Local model match on a loopback endpoint you started (2 completed matches, no void runs or agent failures, recordings replay-verified) (`LATTICE_ACCEPT_LOCAL_ENDPOINT`, `LATTICE_ACCEPT_LOCAL_MODEL`, optional `LATTICE_ACCEPT_STEP_MS`); `not-tested` otherwise. A non-loopback endpoint is refused. **Completed (inherited evidence):** source SHA `497bca08b90767f1285524e6f39c8913a49258bd`, evidence `.evidence/acceptance-local/` (maintainer-local working tree only; not present in a public checkout) — 2 completed 200-step matches, 400 model calls, VoidRuns 0, no AgentFailures, both recordings `replay --verify`-passed, acceptance exit 0. This proves integration/plumbing, not model quality or a statistical benchmark |
| S11 | Hosted model match: **NOT TESTED** — execution approval is pending and no hosted call has been made. It needs a provider and cost terms chosen and approved by a person; the script itself always records it `not-tested` |
| S12 | Token-shaped secret scan of tracked files and the net `eb13fad..HEAD` diff (per-commit history is not covered); paths and pattern names only |
| S13 | Fixture-literal classification: **CLOSED** — the maintainer classified the old credential-shaped literal in commit `c3467b7` as a dummy test fixture; the current fixture is the obvious `TEST-ONLY` placeholder. The script still records this slice `not-tested` because classification is a human decision it cannot make; it is not reopened by this candidate |

A passing report shows that the checked plumbing works. It does not show that a real model plays well, and no report here states that v4 is usable: 4.0.0 remains a release candidate until the hosted-model S11 slice passes under its own separate approval. The committed, public-safe aggregate for this candidate (commands, source SHA, counts, provenance, inherited vs newly executed status, host scope and limitations) is [`docs/V4_CANDIDATE_ACCEPTANCE.md`](V4_CANDIDATE_ACCEPTANCE.md); raw local evidence is not published.
