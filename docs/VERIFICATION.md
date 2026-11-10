# Verification: claims, gates, and test evidence

Every claim in this repository has a command you can run and a committed
artifact it rests on. This page holds the full claims-and-proofs table, the gate
definitions with file-and-line references, the determinism vocabulary, and the
test evidence. The README keeps the headline result and the quickstart; this
page is the audit trail behind them.

Unless a row names a revision, the output fragments below are verbatim from runs
at the commit that introduced the original claims, on a macOS arm64 host. Host
metadata — CPU, cores, OS, and the exact .NET runtime build — is recorded in the
committed artifacts rather than restated here as a standing fact; the
`benchmarks/*.json` files are the authority for it. A local run is evidence that
the command worked on one machine, not that CI is green; the current per-OS CI
counts are the CI artifacts linked under
[Test totals, as CI publishes them](#test-totals-as-ci-publishes-them).

## Claims and proofs

| Claim | Verify with | Rests on |
| :--- | :--- | :--- |
| Same seed, same arguments, same bytes. | `dotnet run -c Release --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out a.jsonl`, then the same again with `--out b.jsonl`, then `shasum -a 256 a.jsonl b.jsonl` | Both runs hash to `d557e80f02fb4783…6a7`, which is the committed [`site/infiltration.jsonl`](../site/infiltration.jsonl) (115,067 bytes, schema 5). Verified by running the command twice outside the repository on a macOS arm64 host at the current tree; both outputs matched the committed file byte for byte. Scoped to a macOS arm64 host on the .NET 10 runtime the artifacts record. |
| Replay is hash-verified, not just re-run. | `dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify` | `replay verified: 12 step(s) serialized-equivalent, 12 state hash(es) matched (seed 2024, schema v3).` Exit 0. Digest computed by [`Trajectories/SimulationStateHash.cs`](../Trajectories/SimulationStateHash.cs), compared in [`Trajectories/TrajectoryReplay.cs`](../Trajectories/TrajectoryReplay.cs). |
| The same check runs on all three operating systems. | Read the `Build & test` matrix in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) | The golden replay-verify step is a matrix step over `ubuntu-24.04`, `windows-latest`, `macos-latest`. Run [36622539217](https://github.com/candavere/lattice/actions/runs/36622539217) on commit `4035de6` shows all three legs green on the pinned labels, 738/738 tests passed on each: `Build & test (ubuntu-24.04)` 738 passed, `Build & test (macos-latest)` 738 passed, `Build & test (windows-latest)` 738 passed. The label is pinned to 24.04 on purpose so GitHub's rollout of `ubuntu-latest` to 26.04 cannot move this gate. A separate `ubuntu-26.04` job builds, golden-replays, and tests as a **non-gating** compatibility probe: on that run it reported `Total tests: 738` and concluded success, but it publishes no artifact, no other job depends on it, and it is not part of the three-OS equivalence contract — read its steps and logs, never infer 26.04 support from a green run. |
| Every committed recording replays, not just the golden one. | `dotnet run -c Release --project Cli -- replay site/demo.jsonl --verify` and the same for `site/infiltration.jsonl` | `27 step(s) … 27 state hash(es) matched` and `20 step(s) … 20 state hash(es) matched`. A separate CI job replays every recording under `site/`. |
| The suite had 711 tests at the v3.0.0 release commit. | `dotnet test Lattice.sln -c Release` | Historical: `Passed! - Failed: 0, Passed: 711, Skipped: 0, Total: 711`, recorded at `a8b2fc6` (the v3.0.0 release commit). It is **not** the current head count — schema-4 decision-time perception and other post-release work added tests after that revision. For today's totals, use the per-OS head-matched TRX summary artifacts, not this number: see [Test totals, as CI publishes them](#test-totals-as-ci-publishes-them). |
| A paired study needs 30 seeds, and the rule is mean > 0 **and** CI lower > 0. | `dotnet run -c Release --project Cli -- evaluate --scenario standard --seed-set dev --seeds 29 --agent-cmd "python3 examples/python/lattice_agent.py"`, then the same with `--seeds 30` | `--seeds 29` → `Not graded: 29 seeds is below the 30-seed floor of the decision rule (the canonical suites run 50).` `--seeds 30` → `Fail: mean paired delta -0.383 and/or the 95% CI lower bound -0.672 did not clear 0 on 30 seeds.` The rule is [`Agents/PairedEvaluation.cs:144-145`](../Agents/PairedEvaluation.cs): `graded = deltas.Length >= 30` and `passed = graded && mean > 0.0 && ciLower > 0.0`, with `ConfidenceLevel = 0.95` at line 74. |
| Every protocol failure is a loss, never a retry. | `dotnet run -c Release --project Cli -- evaluate --scenario standard --seed-set dev --seeds 2 --agent-step-timeout-ms 300 --agent-cmd "python3 crash.py"`, where `crash.py` is `import sys; sys.exit(3)` | `"AgentFailures": {"agent_crashed": 4}`, `VoidRuns: 0`, wins/draws/losses `0/0/4` of 4. The reason set is the closed fourteen-code table at [`EXTERNAL_AGENT_PROTOCOL.md` §8](EXTERNAL_AGENT_PROTOCOL.md) and [`Protocol/ProtocolReasons.cs:17-30`](../Protocol/ProtocolReasons.cs); thirteen are agent-attributable and one, `host_limit`, is host-attributable. |
| A failed match forfeits: external side 0, opponent keeps its score, partials are diagnostic only. | An agent that plays normally, then `sys.exit(3)` at step 10, over 3 seeds | `AgentForfeits` rows read `"PartialScoreAtSlot0": 2, "PartialScoreAtSlot1": 3, "ScoredExternalScore": 0, "ScoredOpponentScore": 3` — the external side's 2 and 3 are recorded but not scored, and the opponent keeps 3 and 2. `AgentFailures: {"agent_crashed": 6}`, all 6 matches losses, mean delta −2.333. |
| A bad `--agent-cmd` is a usage error, not a lost result. | `dotnet run -c Release --project Cli -- evaluate --scenario standard --seed-set dev --seeds 2 --agent-cmd "definitely-not-a-real-binary-xyz"` | Exit **2**, `error: --agent-cmd could not be run: 'definitely-not-a-real-binary-xyz' was not found on PATH.` [`Cli/UsageError.cs:29`](../Cli/UsageError.cs) sets `ExitCode = 2`, mapped at [`Cli/CliApp.cs:244`](../Cli/CliApp.cs). |
| The published negative result is negative. | `python3 -c "import json;print(json.load(open('benchmarks/mcts_evaluation_results.json'))['Studies'][0]['Statistics'])"` | dev: mean −1.12, CI [−1.37, −0.87], 50 seeds, `Passed: false`. held-out: mean −1.25, CI [−1.54, −0.96], `Passed: false`. Committed at source revision `5783ca1`, .NET 10.0.10. |

The four gates the code actually enforces are spelled out with file and line
below.

## The gates, and where the code is authoritative

Where this page describes a gate, the code is authoritative and the reference
below is exact (file and line). Where a committed doc used to disagree with the
code, the doc was corrected rather than the code, and the gate below now matches
what CI enforces.

- **Evaluation verdict gate.** A paired study passes only when both conditions
  hold: the mean paired delta is strictly positive **and** the lower bound of
  the 95% two-sided t-confidence interval is strictly positive, on at least 30
  seeds. [`Agents/PairedEvaluation.cs:144-145`](../Agents/PairedEvaluation.cs)
  enforces `graded = deltas.Length >= 30` and `passed = graded && mean > 0.0 &&
  ciLower > 0.0`; the interval uses `ConfidenceLevel = 0.95`
  ([`Agents/PairedEvaluation.cs:74`](../Agents/PairedEvaluation.cs), computed at
  line 127). The committed artifacts carry the resulting `Passed` and `Decision`
  fields. The protocol doc describes exactly this rule
  ([`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md), section
  5), so doc and code agree here.
- **Replay equivalence gate.** [`.github/workflows/ci.yml:42-43`](../.github/workflows/ci.yml)
  runs `dotnet run -c Release --project Cli -- replay Tests/fixtures/golden_trajectory.jsonl --verify`
  on Ubuntu, macOS, and Windows. [`Trajectories/TrajectoryReplay.cs`](../Trajectories/TrajectoryReplay.cs)
  (`VerifyDetailed`) rebuilds a fresh simulation from the trajectory header,
  re-feeds each recorded turn, compares re-serialized `StepResult`s against the
  recorded ones, **and** recomputes the SHA-256 digest of the complete
  simulation state at every tick
  ([`Trajectories/SimulationStateHash.cs`](../Trajectories/SimulationStateHash.cs))
  and compares it to the digest the step line records, naming the first
  mismatched tick. This is per-step serialized equivalence plus per-tick
  state-digest equality, not raw byte identity, and the digest covers the state
  rather than the header's configuration. A recording made before schema 3
  carries no per-tick digest, verifies on step results alone, and prints
  `no state hash: step-level verification only`; a recording that *claims*
  schema 3 but has no digest is reported as a discrepancy, so stripping the
  hashes is not a way to downgrade the check.
- **Benchmark regression gate.** [`.github/workflows/benchmarks.yml:77-108`](../.github/workflows/benchmarks.yml)
  re-benchmarks the five-case matrix and compares it against the committed
  baseline with `compare_benchmarks.py`, failing when a current workload median
  falls below its threshold times the baseline median. The explicit thresholds
  in the workflow are 0.75 global, 0.6 for `micro_raw_2agent`, and 0.85 for
  `policy_lookahead_mcts_32` (`benchmarks.yml:88-90`); otherwise the threshold is
  derived statistically from the baseline's own dispersion
  ([`.github/workflows/compare_benchmarks.py:104-120`](../.github/workflows/compare_benchmarks.py)),
  with the CLI default floor at 0.8 (`compare_benchmarks.py:640`). The gate
  adjudicates only when the host fingerprint (OS family + architecture + .NET
  runtime major + logical cores + CPU model, trimmed and case-folded; a missing
  or empty host field is also a mismatch) matches
  (`compare_benchmarks.py:479`), and it re-measures once to rule out
  shared-runner jitter (`benchmarks.yml:94-108`). GitHub-hosted runners are a
  different host class than the baseline record, so they get an informational
  cross-host comparison plus the structural checks — the strict throughput
  verdict is reserved for a matching host class. The measured artifact is
  uploaded with `if: always()` (`benchmarks.yml:111-119`) so runner numbers
  survive a failed gate without log access.
  **Current state of the hosted arm64 job: it adjudicates 4 of its 5
  workloads.** A separate `runner-class-gate` job runs on the pinned `macos-26`
  arm64 label against its own hosted record
  ([`benchmarks/runner_class_throughput_benchmark.json`](../benchmarks/runner_class_throughput_benchmark.json)),
  calibrated from 20 full-protocol sessions on pinned commit `1b9426f5` and
  armed by the pre-registered rule (allowed ratios 0.7804–0.8416:
  `micro_raw_2agent`, `facility_static_4agent`, `dynamic_contention_4agent`,
  `stress_topology_4agent`). `policy_lookahead_mcts_32` is **deliberately
  excluded** — 20 sessions span 2.050× max/min on that search-bound workload,
  so a dip there is not separable from runner jitter. It is measured, printed,
  and compared but carries no verdict, and that is a recorded negative result
  ([`FINDING-014`](FINDINGS_LEDGER.md#finding-014--the-mcts-decision-throughput-case-cannot-be-gated-on-this-host-class-and-four-of-five-workloads-can)),
  not a gap to be closed later by widening a tolerance. A green Benchmarks
  workflow is therefore a **narrow, noisy-host claim** — it bounds throughput on
  this runner class only, and says nothing about the bare-metal host or about
  engine performance. The record supersedes an earlier 5-session version that
  was published demoted (`ArmedWorkloads: []`) after its first live run
  ([36471478970](https://github.com/candavere/lattice/actions/runs/36471478970),
  commit `4d05585`) fell below the five-sample minimum on all five workloads
  with no measured code changed; five idle-dispatched samples did not bound the
  host class. That run's slowdown cause remains **unestablished**, the demotion
  is not withdrawn, and no threshold was widened against any record or observed
  run. The bare-metal research reference above is a separate record and is
  unaffected. Details: [`BENCHMARKING.md`](BENCHMARKING.md) and
  [`benchmarks/runner_class_summary.md`](../benchmarks/runner_class_summary.md).
- **Mutation gate.** [`stryker-config.json:22-26`](../stryker-config.json) sets
  Stryker thresholds `high: 80`, `low: 60`, `break: 0`. The only threshold the
  tool enforces is `break`, so the committed gate fails only at a 0% score;
  `high`/`low` are informational. There is no mutation step in CI
  ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs restore, build,
  replay-verify, test, a global-tool pack/install/smoke check, the UI
  regression, a parser unit-test job, and a second job that replays every
  `site/` recording). The 99.61% figure is a historical local calibration
  recorded in
  [`benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json)
  at `053f3eb`, scoped to `Simulation.cs` and `PerceptionFilter.cs`. It was not
  rerun at the current head, it is not whole-repository coverage, and it is not
  a CI gate: nothing in CI runs Stryker.

## Determinism and seeds

Every command is seeded. Identical arguments produce identical per-step
serialized output under the specified .NET 10 BCL runtime contract. The
repository makes five distinct guarantees, documented in
[`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md): engine
transition determinism, per-step serialized `StepResult` replay equivalence,
same-host normalized JSONL byte identity (narrow and explicit), the per-tick
canonical state digest — the fourth covering the state each tick produced, not a
whole-episode hash tree — and the recorded per-agent decision-time
`Perceptions`, which equal what each agent's own `PerceptionFilter` produced
inside its `Decide` call and are independently reprojected by `replay --verify`.
Raw file-byte identity across heterogeneous hosts is not asserted. The first
four are formalized as the formal, implementation-agnostic transition and
perception laws, plus falsifiable challenge questions, in
[`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md); the fifth is
replay-verified rather than an oracle law, because a filter's stale memory is
per-agent internal state no independent party could rebuild from the world
alone, and that document says so in §2.5.

`replay --verify` is the contract that makes those claims checkable rather than
asserted. It rebuilds a fresh simulation from the trajectory header, feeds each
recorded turn of actions back through the engine, compares every re-serialized
`StepResult` against the recorded one, recomputes each step's state digest and
compares that, reprojects each recorded per-agent decision-time perception
through a `PerceptionFilter` built from the header's `AgentVision` and compares
that too (a nonempty recording at schema 4 or later that declares neither
recorded perceptions nor a declared `AgentVision` reports
`no recorded perception: decision-time visibility not verified` rather than
passing in silence; a file below schema 4 is not nagged), and re-computes the
final summary line's aggregates field by field; exit code `0` means every tick,
every recorded perception and the final aggregates reproduced. That is the same
check the CI pipeline runs on the golden trajectory on Ubuntu, macOS, and
Windows, and on every recording under `site/` in one further job.

`infiltration.jsonl` is one JSONL line per tick: the header embeds the seeded
map and simulation config, and each line is that tick's recorded actions and
`StepResult`, plus the SHA-256 digest of the world state that tick produced. The
SVG is a self-contained animated render:

```sh
dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl
dotnet run --project Cli -- render --trajectory infiltration.jsonl --format svg --out infiltration.svg
```

## Test totals, as CI publishes them

`dotnet test` prints a summary line, but a log line is not something a reader can
check. So CI writes VSTest TRX reports and reduces them to one small JSON
document per operating system, uploaded as an Actions artifact:

- **Artifact names.** `dotnet-test-summary-ubuntu`, `dotnet-test-summary-windows`,
  `dotnet-test-summary-macos`, one per matrix leg, retained 30 days. Each
  contains `test-summary.json` plus the raw `.trx` files it was derived from.
- **What is in it.** `total`, `passed`, `failed`, `skipped` and `notRun` counts,
  the head `commitSha` that was tested, the `runnerOs`, `osSlug`, `runnerImage`
  and `dotnetVersion`, and the name of every TRX file the numbers came from.
  Because the raw TRX ships alongside the JSON, the counts can be re-derived
  and audited rather than taken on trust.
- **Read the numbers per OS.** The three legs are three separate documents and
  they are never added together. Summing them would triple-count one suite and
  produce a figure that describes no real run. There is deliberately no
  repository-wide "N tests" shield: a static count goes stale silently, and
  a cross-OS total is not a fact about the suite.
- **The parser refuses rather than guesses.** [`.github/workflows/summarize_test_results.py`](../.github/workflows/summarize_test_results.py)
  exits non-zero and publishes no JSON when the reports are absent, malformed,
  duplicated, internally contradictory, or when a run discovered zero tests. A
  `total: 0` that reads as a pass is the failure this exists to prevent, so a
  run that finds nothing is reported as an error, not as a green empty suite.
  Exit 0 means parsed and nothing failed; exit 1 means parsed with failures,
  and the JSON is still written so a red run's numbers survive.
- **Run the same parser yourself.** `python3 -m unittest discover -s .github/workflows -p 'test_summarize_test_results.py'`
  — 24 tests covering the valid, missing, malformed, duplicate, contradictory
  and nonzero-failure cases, run in CI as the `Test summary parser unit tests`
  job so the refusal logic is itself covered.

Verify an artifact against its run: open the CI run for the commit, download
`test-summary.json`, and check that `commitSha` is the commit you are reading
and that the four counts match the job's log summary for that OS.

### Flaky tests are known and listed

The figures below are **historical**, recorded at `a8b2fc6` (the v3.0.0 release
commit) on one macOS arm64 host. They are not a claim about the current head,
which has since added tests including the schema-4 decision-time perception
suite. Five consecutive full `dotnet test Lattice.sln -c Release` runs at that
revision gave 711/711 twice, 709/711 once, 710/711 once, and 707/711 once.
Every failure was in the external-agent and CLI-argument-fuzz areas, and all 20
of those tests pass when run on their own, so they race on shared temporary
state under the parallel suite rather than being deterministically broken. The
mutation summary at
[`benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json)
records the same class of problem: two mutants attributed to
`CliArgumentFuzzTests` are non-reproducible batch-mode artifacts of the fuzz
harness's shared temp workspace.

Stated rather than smoothed, because the alternative is worse: a README that
says "the suite is green" while a run in three goes red is a claim that cannot
survive a reader running the command. Fixing the shared-state races is real work
in `Tests/**` and the fuzz harness, and not something a README row can paper
over. So this page does not claim a green suite for the current head; it points
at the CI artifact, which reports what actually happened on the run you are
looking at.

### Current-session facts

Current-session facts (2026-11-10, local-only except where linked): full .NET
suite 924/924, Python adapter suite 39/39, golden replay 12/12 on the committed
candidate tree at the time. These counts are local session measurements; read
the CI per-OS artifacts for the published per-commit counts. The S10 acceptance
subset is now published under
[`evidence/acceptance-s10/`](../evidence/acceptance-s10/README.md) with a
SHA-256 manifest covering every published file; the rest of the local
`.evidence/` directory stays untracked and is not part of a public checkout. The
macOS descendant-cleanup probe (issue #12): not reproduced in 40 loaded runs on
this host — a bounded evidence gap, not clearance. Green runs here confirm this
build passed; they do not clear any intermittent failure.

## CI coverage: what CI does and does not run

CI runs the .NET solution on three OS legs, the golden replay-verify, the
pack/tool-path install parity check, `setup.sh` / `setup.ps1`, the Playwright
`ui_tests` regression, the site-recording replay and drift job, and the
test-summary parser unit tests. It also runs .NET tests that exercise the
shipped Python example through `evaluate --agent-cmd`.

CI does **not** run the `examples/python` adapter test suites. There is no step
in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) that invokes
`python3 -m unittest` in `examples/python`, and acceptance slice S4 runs only
`test_lattice_model_agent` (the 25-test file), not
`test_lattice_model_agent_acceptance.py` (the 14-test acceptance file). Both
files pass 39/39 locally at `fe62da8` on 2026-11-10. That is a known
CI-coverage gap, recorded as a follow-up candidate; this page does not imply CI
covers the adapter suite.

## How evidence works

Each committed artifact below is the exact file behind a claim in this
repository:

| Artifact | What it is | Sub-claims it backs |
| :--- | :--- | :--- |
| [`SUPPORT_AND_REPRODUCIBILITY.md`](SUPPORT_AND_REPRODUCIBILITY.md) | The operational contract: what is supported, what is not, the five-equivalence vocabulary, and the viewer's [claim](SUPPORT_AND_REPRODUCIBILITY.md#viewer-claim-semantics) and [presence](SUPPORT_AND_REPRODUCIBILITY.md#viewer-presence-semantics) semantics | Replay equivalence, determinism boundary, support matrix, what the viewer is allowed to report |
| [`INVARIANT_SPECIFICATION.md`](INVARIANT_SPECIFICATION.md) | Formal, implementation-agnostic transition and perception laws plus falsifiable challenge questions | Capacity gates, conflict resolution, perception boundary, replay contract |
| [`benchmarks/mcts_evaluation_results.json`](../benchmarks/mcts_evaluation_results.json) | Standard-map paired study, dev + held-out | Negative baseline, delta, CI, verdict |
| [`benchmarks/bottleneck_evaluation_results.json`](../benchmarks/bottleneck_evaluation_results.json) | Contention-bearing paired study, dev + held-out | Topology-dependent inversion |
| [`benchmarks/throughput_benchmark.json`](../benchmarks/throughput_benchmark.json) | Five-workload timing record on one host | Performance, qualified |
| [`benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json) | Stryker run summary with survivor classification, from a historical local calibration at `053f3eb` scoped to two files. Not a current rerun, not whole-repo coverage, not a CI gate | Mutation score, scope |
| [`reproduction_packet.md`](reproduction_packet.md) | Turnkey guide to verifying the published `v2.3.2` and `v3.0.0` releases asset-for-asset | Release claims, checksums |
| [`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md) | Append-only record of criticisms, edge cases, and resolved issues | Governance, traceability |
| [`CLAIM_CALIBRATION_MATRIX.md`](CLAIM_CALIBRATION_MATRIX.md) | Every public claim mapped to its proving artifact, tested matrix, and boundary | Claim-to-artifact traceability |
| [`EXTERNAL_AGENT_PROTOCOL.md`](EXTERNAL_AGENT_PROTOCOL.md) | The normative external-agent wire contract, including the closed reason set and the forfeit rule | Bring-your-own-agent, failure accounting |

The reproduction packet covers the **v2.3.2** and **v3.0.0** releases. Each has
its own section with its own tag, commit, sizes, and SHA-256 checksums, taken
from the published assets of that release; the v2.3.2 section is deliberately
unchanged, so the two are verified independently rather than one being edited to
match the other.
