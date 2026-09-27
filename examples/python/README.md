# A Lattice external agent in Python

`lattice_agent.py` is a complete, conformant
[external agent](../../docs/EXTERNAL_AGENT_PROTOCOL.md) in about 150 lines of
standard-library Python.
It is here for two reasons: so the protocol has a worked example that is not
written by the same people who wrote the parser, and so you have something to run
before you write your own.

It needs **Python 3.8 or newer** and nothing else — no packages, no virtual
environment, no build step. It is launched by Lattice as a child process and
speaks the protocol on stdin/stdout.

## Scoring it

```sh
dotnet run --project Cli -- evaluate --scenario standard --seed-set dev --seeds 30 \
  --agent-cmd "python3 examples/python/lattice_agent.py"
```

That is the whole command. Lattice launches one `python3` process per match, so
60 matches means 60 short-lived interpreters; the run takes a few seconds.

The same agent on the capacity-1 choke family, where the baseline's transit is
the bottleneck:

```sh
dotnet run --project Cli -- evaluate --scenario bottleneck --seed-set dev --seeds 30 \
  --agent-cmd "python3 examples/python/lattice_agent.py"
```

Use the same flags you would for the in-process MCTS study — `--scenario`,
`--seed-set`, `--seeds`, `--rollouts`, `--out`, `--commit` all mean what they
mean there, and `--rollouts` is recorded as provenance only, because an external
agent has no MCTS search to configure. `--agent-step-timeout-ms` (default 5000)
is the per-step budget; the whole-match budget is computed from it and is not
yours to set.

## What the output means

```
evaluation suite=dev seeds=30 matches=60 policy=external:python3 baseline=Scout rollouts=32 max_steps=200
  paired delta   mean=-0.383  median=0  sd=0.773  iqr=0.375
  95% CI         [-0.672, -0.095]  -> FAIL
  outcomes       win=38.3%  draw=23.3%  loss=38.3%  timeout=0.0%  contention=0.0%
  external        valid_seeds=30  void_runs=0  agent_failures=none
  decision        Fail: mean paired delta -0.383 and/or the 95% CI lower bound -0.672 did not clear 0 on 30 seeds.
```

- **`paired delta`** — the mean of, per seed, the score difference from both
  mirrored seatings. Taking both seats cancels positional advantage, so the
  number is about the policy rather than about where it spawned.
- **`95% CI`** — the confidence interval on that mean. A **pass** needs the mean
  above zero *and* the lower bound above zero, on at least 30 valid seeds.
  Anything under 30 seeds is reported as **Not graded**, because a wide interval
  from a handful of seeds is not evidence.
- **`outcomes`** — win/draw/loss/timeout rates over all 60 matches, from the
  agent's own point of view. **Every protocol failure counts as a loss**: an
  agent that crashes cannot improve its score by crashing, and there is no
  retry.
- **`valid_seeds`** — seeds that produced both mirrored matches, which is what
  the grading floor counts. A run Lattice refused on its own limits (a `void`)
  produces no match at all and so is not a seed here.
- **`void_runs`** — matches Lattice refused because its own outbound
  observation would have broken a limit. Never your agent's fault, never a loss,
  excluded from every statistic above.
- **`agent_failures`** — how the agent's plumbing broke, by reason code
  (`timeout_handshake`, `timeout_step`, `agent_crashed`, …). `none` means the
  agent played every match to a normal ending. This count is report-only: it
  changes no rate, no delta, and no verdict.
- **`decision`** — the verdict, in the same words the in-process MCTS study
  records under `Decision` in its artifact, because it is the same analyzer and
  the same rule.

With `--out` you also get a JSON artifact carrying the per-seed rows, the
statistics, `AgentFailures`, `VoidRuns`, the argv Lattice split out of your
command line, and the two time limits the matches were played under.

## The measured result

Both runs below are real output from this agent, not an illustration. Same
command, same agent, only `--scenario` differs.

| | `--scenario standard` | `--scenario bottleneck` |
| :--- | :--- | :--- |
| valid seeds | 30 | 30 |
| void runs | 0 | 0 |
| agent failures | none | none |
| mean paired delta | −0.383 | +1.733 |
| 95% CI | [−0.672, −0.095] | [+1.424, +2.042] |
| win / draw / loss | 38.3% / 23.3% / 38.3% | 63.3% / 0.0% / 36.7% |
| contention | 0.0% | 30.1% |
| verdict | **Fail** | **Pass** |

- **Command (standard):** `dotnet run --project Cli -- evaluate --scenario
  standard --seed-set dev --seeds 30 --agent-cmd "python3 examples/python/lattice_agent.py"`
- **Command (bottleneck):** the same with `--scenario bottleneck`
- **Code state:** commit `c8417f0` (the `evaluate --agent-cmd` implementation),
  with this agent as written in the commit that adds it
- **Date:** 2026-09-27
- **Host:** macOS 27.0.0 on arm64, .NET 10.0.12, 8 cores, CPython 3.9.6

Read those two verdicts together, because the difference between them is the
interesting part. On generated maps the agent is roughly even with the Scout
baseline and loses on the margin — it walks to the nearest resource and takes it,
with no notion of denying anything to anybody. On the bottleneck family it wins
clearly, and the reason is visible in the last row: transit through a
capacity-1 choke is where a shortest-path-collector loses time it does not have,
and beating an opponent there is a matter of turning up in the right place. The
same policy, the same 30 seeds, one map family apart. Neither number is a claim
about the agent's quality; they are what the harness reports, and the harness is
the thing you should be checking.

## What the agent actually does

Per observation, in order:

1. If the agent is mid-crossing (`transit` present), **wait** — it is on an edge,
   not at a node.
2. If an unclaimed resource is in the current zone, **collect** it (lowest id
   first).
3. Otherwise **move** one hop along the shortest path to the nearest unclaimed
   resource, measured over the choke graph. Ties go to whichever target the
   traversal reaches first over a sorted adjacency, which is fixed — so the choice
   is deterministic.
4. Otherwise **wait**.

Every step is deterministic, so a run is reproducible from its seed and the
recorded action stream replays exactly.

## Writing your own agent in any language

The contract is language-neutral and normative:
[`docs/EXTERNAL_AGENT_PROTOCOL.md`](../../docs/EXTERNAL_AGENT_PROTOCOL.md). Read
these, in this order:

| Section | What it fixes |
| :--- | :--- |
| §1 Transport | One JSON value per line, LF, UTF-8, on stdin/stdout. stderr is diagnostic and is never parsed |
| §2 Protocol version | `hello` carries `protocol: 1`; reply `hello_ack` with `protocol: 1`. Exact match, no downgrade |
| §3 Handshake | `hello` → `hello_ack` → one `observation`/`action` pair per step. Lattice drives; you answer |
| §4 Message catalogue | Exactly five message types, and nothing else |
| §5 Observation | Every field you get, including the full map on every step |
| §6 Action | `Wait`, `Move` + `zone_id`, `Collect` + `resource_id`; the step must be echoed; a field that does not apply must be **absent** |
| §7 Limits | Line length, JSON depth, and the two time budgets — which you must not exceed |
| §8 Error codes | The fourteen reasons a match can fail, and who each one is faulted to |
| §9 Failure | Every agent failure is scored as a loss. There is no retry, ever |

Then check your agent against the two rules people get wrong most often:

- **Flush after every line.** Lattice reads with a step timeout, so a buffered
  line is indistinguishable from a hung agent, and a hung agent is a loss.
- **Do not send fields that do not apply.** `zone_id` on a `Wait` is a
  `schema_violation`, not a harmless extra. Lattice refuses what it does not
  expect rather than ignoring it.

Launch it with the same flag this example uses:

```sh
dotnet run --project Cli -- evaluate --scenario standard --seed-set dev --seeds 30 \
  --agent-cmd "your-interpreter your/agent.py"
```

The command line is split **without a shell**: unquoted whitespace separates, a
double quote groups, a backslash escapes only `"` and `\` inside quotes and is
an ordinary character everywhere else. Quote a path that contains a space; an
unquoted Windows path needs no escaping. If the program cannot be found or
started, the CLI says so and exits 2 without writing anything — that is a
problem with your command line, not a result you lost.
