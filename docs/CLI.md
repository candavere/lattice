# Lattice.Cli reference

`Lattice.Cli` exposes seven commands. Run any of them with
`dotnet run --project Cli -- <command> ...`; the binary name is `lattice`.
Every command is seeded, and exit status is `0` on success, non-zero on a bad
argument or runtime error. This page is the full flag-by-flag reference; the
landing page keeps a [compact table](../README.md#reference).

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
| `--scenario <infiltration>` | Fixed Dungeon Infiltration & Sentry Patrol scenario; `--agent` is forbidden |
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

## evaluate — mirrored-seat MCTS evidence

| Flag | Description |
| :--- | :--- |
| `--seed-set <dev\|heldout>` | Canonical suites: `dev` = 1001..1050, `heldout` = 2001..2050 |
| `--rollouts <n>` | MCTS rollouts per action; default 32 |
| `--seeds <n>` | Cap on seeds per suite (default 50; the decision rule needs ≥ 30) |
| `--scenario <standard\|bottleneck>` | `standard` = generated maps; `bottleneck` = capacity-1 choke contention family |
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
Argument errors in the other commands keep their existing exit status of 1.

| Artifact field | Meaning |
| :--- | :--- |
| `AgentFailures` | Object keyed by protocol reason code (`timeout_step`, `agent_crashed`, …), each mapped to the number of matches that failed that way. Report-only: every failure is already a loss in the statistics above, and this says how many of the losses had a cause |
| `VoidRuns` | Matches Lattice refused on its own limits. Not a loss, and excluded from every statistic and from the grading denominator |
| `AgentCommand` | The argv that was launched, program first |
| `AgentLimits` | The `StepTimeoutMs` and `MatchTimeoutMs` the matches were played under; the match budget is computed from the step budget, never chosen separately |

All four appear only when `--agent-cmd` is used: an in-process artifact carries
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