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
is the full flag-by-flag reference; the landing page keeps a
[compact table](../README.md#reference).

Every command in this reference can also be invoked as `lattice <command>` if
you have installed the CLI as a .NET global tool (`dotnet pack Cli -c Release`
then `dotnet tool install -g --add-source ./Cli/bin/Release lattice`; see
[the README](../README.md#or-install-the-cli-once-as-a-net-global-tool)). That
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

## tui

`tui` is outside the eight commands above: it opens full-screen viewers that
read keys and draw on the terminal, so it refuses redirected streams where the
eight keep working. With no arguments at all, `lattice` opens the Launchpad
when standard input and standard output are both a terminal; on a redirected
stream, or with any argument, it behaves exactly as the `--help` text says.
The eight commands still run directly for scripts, with the stdout bytes and
exit codes this page documents.

```sh
dotnet run --project Cli -- tui replay site/demo.jsonl
dotnet run --project Cli -- tui simulate --seed 42 --steps 100
dotnet run --project Cli -- tui ledger benchmarks/mcts_evaluation_results.json
```

Display: the charcoal/olive panels, lime/sage/pink identity and logo colours are
unchanged. Titles and pane titles are Bold; the Launchpad command preview reads
`$ lattice ...` in Bold; instructions, table headers, key hints, defaults and
required values use the readable secondary `#a8b0a3` (at least 4.5:1 on the
panel fill, standard sRGB) and errors use the readable `#e88a80`; the sampled
dim `#696f65` is reserved for decoration and disabled content. Selection is
lime on the raised surface with Bold, focus keeps its `>`/caret/Reverse cue,
and PASS/FAIL/not-graded, LIVE/paused/playing and choke marks are words and
glyphs as well as colour. Panels are painted only under TrueColor; on
256/16-colour and no-colour the terminal's own background is left alone and
hierarchy comes from Bold, markers, spacing and words.

### tui replay

`replay <trajectory.jsonl> [--ascii] [--hide-panels]` plays a recorded
trajectory in a read-only cockpit and runs no simulation. The screen shows the
world, the scoreboard, the event log and the timeline; the hint row reads

```
space pause  n/p step  < > speed  [ ] scrub  home/end jump  q quit
```

`space` pauses, `n`/`p` step one frame forward/back, `left`/`right` and
`pgup`/`pgdn` do the same, `up`/`down` and `<`/`>` (also `+`/`-`) change speed,
`[`/`]` scrub a tenth of the recording, `home`/`end` jump to the first/last
frame, `h` hides the side panes so the world pane gets the room and `h` again
brings them back, and `q` (or Ctrl-C) quits. Hiding is display only: it never
moves the replay position, the speed or the pause state. While hidden, the hint
row names the way back first:

```
h show panels  space pause  n/p step  < > speed  [ ] scrub  q quit
```

Below 100x30 the cockpit is replaced by the world alone with a line naming
the size the cockpit needs; the flag changes nothing there.

### tui simulate

`simulate --seed <n> [--steps <n>] [--agent <a>] [--scenario <infiltration|file>] [--rules <f>] [--quiet] [--ascii] [--hide-panels]`
runs a live episode in the same cockpit. The roster, map, rules, seed and tick
budget are the ones `lattice simulate` would use for the same arguments. The
hint row reads

```
LIVE  space pause  n tick  p back  < > speed  [ ] speed  home/end jump  r restart  q quit
```

`space` pauses, `n` asks for one tick at the frontier, `p` steps back over
produced frames, `left`/`right` and `pgup`/`pgdn` step one produced frame
back or forward, `up`/`down` and the brackets, angle brackets, `+` and `-`
change speed, `home` goes to the start, `end` goes as far as the frames
produced so far, `r` restarts the episode from the same seed, `h` hides and
shows the side panes, and `q` (or Ctrl-C) quits. While hidden, the hint row
names the way back first:

```
LIVE  h show panels  space pause  n tick  p back  < > [ ] speed  r restart  q quit
```

A live run records nothing: `--out` is refused, `--quiet` is
accepted and does nothing, and a `--rules` file named beside a scenario file
prints a notice that it has no effect and the run continues. A simulation that
fails inside the viewer exits `1` with one line on stderr; a clean quit exits
`0`.

### tui ledger

`ledger <artifact.json> [<artifact.json> ...] [--ascii]` opens a read-only
screen over one or more `evaluate --out` artifacts: where and on what each was
produced, one row per suite, and the per-seed rows each paired delta is built
from, with two artifacts put side by side and a protocol line per suite. The
hint row reads

```
tab artifact  up/down suite  pgup/pgdn/home/end seed  c compare  q quit
```

`tab`/`right` move to the next artifact, `backtab`/`left` to the previous,
`down`/`j` and `up`/`k` move between suites, `pgdn`/`pgup` move through the
seed rows by a page, `home`/`end` jump to the first/last seed row, `c` swaps
the detail band between the seed rows and the comparison, and `esc`, `q` (or
Ctrl-C) leaves. The Ledger has no hide mode: `--hide-panels` is refused as an
unknown flag.

### bare lattice: the Launchpad

With no command, and standard input and standard output both a terminal,
`lattice` opens the Launchpad: every command and every `tui` screen with its
real flags as a form, the equivalent `lattice ...` command line shown live
underneath, and inline validation from the CLI parser itself. Two key modes:

```
j/k or arrows command  tab form  enter run  q quit
tab next field  enter commit  esc leave field  ctrl-c quit
```

Navigation: `j`/`k` or the arrows move, `tab` enters the form, `enter` runs,
`q` or `esc` leaves. Editing: every printable character types into the field,
including `q`, `j` and `k`; `backspace`, `delete`, the arrows, `home` and
`end` edit; `enter` or `tab` commits; `esc` leaves the field and keeps the
text. A command run from the Launchpad leaves the alternate screen, restores
the terminal, and runs through the same `CliApp.Run` path with the process's
own stdout and stderr.

### Refusals and exit codes

A viewer whose input or output is redirected is not a viewer: entering the
alternate screen would put control sequences into whatever the redirect was
for. The run prints one line of reason on stderr and exits `2`. Nothing is
built first: no key source, and no console input property is read or set — the
refusal is decided from the detected capabilities alone. (The Ledger
additionally refuses before opening any artifact file.) The cockpit says
`lattice tui: standard input is redirected; ...`, `lattice tui: standard
output is redirected; ...`, or names both; the Ledger says the same with `the
ledger` for the noun, and the Launchpad with `the launchpad` under the
`lattice:` prefix. `--ascii` forces ASCII glyphs on all three screens whatever
the locale says. Quitting any screen exits `0`.