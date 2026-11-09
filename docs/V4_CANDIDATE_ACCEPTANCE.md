# v4.0.0 candidate acceptance summary (source-safe aggregate)

Version 4.0.0 is a **release candidate, not a released version**. This file is
the committed, public-safe aggregate of the acceptance evidence behind the
candidate. It contains no raw keys, no private paths, no model response text,
no hosted outputs and no device identifiers. The raw S10 artifacts and the
private S11 plan live **outside** this repository in the maintainer's local
working tree / private storage and are **not** part of the public checkout:
a reader of a public clone cannot open them, and nothing here claims they
can. Each number below was read from those local artifacts during candidate
preparation; where a number rests only on the maintainer's session report
rather than on an artifact that was re-read, it is labelled as such.

Candidate source SHA (the tree the closeout-session verification below ran on):
`42a85be70bb418074780d35914860279efd959d6` (branch `main`, on this Mac).
This is the last commit this session verified, before the docs commit that
contains this file; a committed file cannot name the commit that contains it,
so the final docs-commit SHA is reported in the session closeout, not here.
The inherited results table below was produced at the earlier candidate SHA
`497bca08b90767f1285524e6f39c8913a49258bd` and is retained verbatim as history.
Candidate version declarations: 4.0.0 (nine project `<Version>` elements,
`CliApp.Version`, `CITATION.cff`); `--version` prints `4.0.0+<source-commit>`
by the build-identity contract.

## Inherited acceptance results (produced at the SHA above, not rerun for the candidate)

Acceptance command: `scripts/acceptance_v4.sh <OUT_DIR>` with
`LATTICE_ACCEPT_LOCAL_ENDPOINT=http://127.0.0.1:8081/v1/chat/completions` and
`LATTICE_ACCEPT_LOCAL_MODEL=qwen05`; script SHA-256
`088d2340388ad07a03c902e75887669a091df9d1f8f6d72ba6370cc6de7f66f9`;
exit code **0**. The slice table is reproduced in `docs/CLI.md`; the raw
evidence directory was read locally during preparation and is not published.

| Slice | Result | Recorded evidence (locally read unless noted) |
|---|---|---|
| S1–S9, S12 | works | Acceptance report rows/table at the SHA above |
| S10 | works | Local-model match (from `local.json` / `S10.log`): 2 completed matches × 200 steps, VoidRuns 0, AgentFailures none, 1 win / 1 loss, mean paired delta −4, decision "Not graded: 1 seeds is below the 30-seed floor"; both recordings `replay --verify`-passed (200 steps, 200 state hashes each, schema v5, from `S10.replay.log`). Sidecar records endpoint host `127.0.0.1:8081`, model `qwen05`, temperature 0.0, prompt/parser version `acceptance`/`1`. The "400 model calls" count (2×200 completed steps) is arithmetic from the completed-step records, not a server-task log, which is not in this tree. Proves plumbing/integration only — not model quality, not a statistical benchmark |
| S11 | **NOT TESTED** | Hosted-model acceptance has not been approved or executed; no hosted call made |
| S13 | **CLOSED** | Maintainer classification: the old credential-shaped test literal (added in `c3467b7`) is a dummy fixture; the current fixture is the obvious `TEST-ONLY` placeholder. The script still records `not-tested` by design (classification is a human decision) |

Local-host scope for S10: macOS arm64, loopback-only server on
`127.0.0.1:8081`. The model-file size (491400032 bytes) and SHA-256
`74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`
were recorded in the maintainer's session report; no matching artifact
survives in this tree, so they carry session-report provenance, not
independent artifact verification.
No claim extends beyond that host, that model, or those seeds.

## Candidate checks (macOS arm64; provenance per row)

The build, full-suite, case-153, Python and golden rows below are prior-session
results whose logs (private, outside tracked source) were re-read this
session; they were not rerun here. The pack-parity row was newly executed
this session. The instrumented row is the prior session's single diagnostic
full-suite run, likewise not rerun.

| Check | Command | Result |
|---|---|---|
| Build | `dotnet build Lattice.sln -c Release --no-restore` | exit 0, 0 errors |
| Full .NET suite | `dotnet test Lattice.sln -c Release --no-build` | **exit 1 — BROKEN: fuzz case exceeded wall-clock budget; cause unconfirmed.** 912/913 passed; the failure is `CliArgumentFuzzTests` base seed 987654, case index 153, case seed `-1417550597`, exceeding the harness's 10 s per-case budget |
| Fuzz case 153 (regenerated) | Repo's own `CliArgumentVectors.Generate` + `Rng` for that seed | Vector: `render --trajectory <fixture> --format ascii --out <path>` (7 tokens, 221 chars; trajectory fixture 44,060 chars, newly written). Isolated `CliApp.Run` wall time 1,471 ms, exit 0; generation 17 ms. The render path streams recorded steps with no step-count loop |
| Instrumented diagnostic run | One full-suite run with temporary test-only timing instrumentation (prior session; surviving log read this session, not a rerun) | Log shows 5 failures, 908/913 passed: both fuzz seeds failed their 10 s budget at different cases than the original (base 987654 case #134, caseSeed 450637920; base 1010 case #52, caseSeed -1748514957), plus two external-agent handshake-timeout failures and one Python-example test timeout. The surviving log contains no per-case generation/run split, CPU, allocation/GC, or concurrent-process capture, so it does not distinguish vector generation from `CliApp.Run` and rules out no cause — **cause remains UNKNOWN**; the isolated case-153 pass (1,471 ms) is a supporting observation only, not a waiver and not a flake verdict. Test file restored byte-identical to its pre-instrumentation copy (SHA-256 verified this session) |
| Python adapter suite | `python3 -m unittest -v test_lattice_model_agent` (in `examples/python`) | exit 0, 25/25 |
| Golden replay | `replay Tests/fixtures/golden_trajectory.jsonl --verify` | exit 0, 12 steps / 12 state hashes (schema v3) |
| Pack + throwaway install parity | `dotnet pack Cli -c Release --no-build --output /tmp/lattice-pack-now/pkg-source`, then `dotnet tool install --tool-path /tmp/lattice-pack-now/tool-path --version 4.0.0 --add-source /tmp/lattice-pack-now/pkg-source lattice` (newly executed this session, throwaway directory outside the repo) | exit 0; installed `--version` (`4.0.0+497bca08b90767f1285524e6f39c8913a49258bd`) byte-identical to `dotnet run`; installed golden replay exit 0. Global installation untouched |

## Diagnostic capture and acceptance-guard patch (this session)

**Fuzz diagnostic (one run, approved plan):** `Tests/Fuzz/CliArgumentFuzzTests.cs`
temporarily instrumented to record, per case, generation/fixture time
separately from `CliApp.Run` wall time, fixture written-vs-skipped state,
and **process-wide** (not per-case attributable under parallel execution)
CPU/allocation/GC deltas plus observed concurrent `dotnet` process counts;
restored byte-identical afterwards (SHA-256 `f5728441…1136ca7` verified
this session). Command: `dotnet test Lattice.sln -c Release --no-build`
(normal schedule). Result: **913/913 passed (2 m 8 s) — the fuzz budget
failure was not reproduced in this run.** That is not a repair and not a
flake verdict. Per-case timings (1,600 cases): generation max 150.8 ms
(mean 0.7 ms), so generation is ruled out; `CliApp.Run` p95 30.5 ms,
mean 21.7 ms, max 5,077.9 ms (case 153) and 4,507.3 ms (case 134) —
9,000+ ms of main-case wall time sits in `CliApp.Run`/scheduling, not
generation. The 16 slowest cases are all `render` on the 44,060-char
fixture. **Cause for the original >10 s tail remains UNKNOWN**; a green
validation run after a red run is not clearance. A permanent timing or
fixture repair is not proposed on this evidence and needs your approval.

**Acceptance guard (opt-in, off by default; `examples/python/lattice_model_agent.py`):**
new `--acceptance-ledger <path>` + `--acceptance-max-attempts <n>`
(default 400, max 400). Reserves each HTTP attempt in a durable JSON
ledger **before** sending (retries included; reserved slots never
reclaimed); shared path across both seats enforces the global cap;
HTTP 401/403/429 marks `stopped` durably before any further send;
requested vs. server-reported `response.model` recorded per attempt
(null when missing/malformed); POSIX `fcntl.flock` with bounded 30 s
wait; corrupt/ambiguous or cap-mismatched state fails closed; no
credentials, headers or raw responses in ledger/sidecar/stdout/stderr.
Default behavior is unchanged when the option is absent (no ledger
writes, no timing change; `fcntl` never imported in default mode).

**Newly executed this session:** Python adapter suite **34/34** (25
existing + 9 acceptance: retry=2 reservations, shared cap, 400-boundary
via prepopulated ledger, first-401/403/429 stop across processes,
default parity, server-model matching/missing/different, corrupt and
cap-mismatch fail-closed, no credential leakage + valid protocol framing);
`dotnet build` 0 errors; golden replay 12/12 exit 0; targeted
`PythonExampleAgentTests`+`EvaluateAgentCmdTests` **21/21**. These are
offline loopback-stub results on the dirty candidate tree; S10 was not
rerun and no live model call was made. The earlier full-suite red
(912/913, case 153) remains the gate; this diagnostic pass does not
clear it.

Suite and diagnostic logs were retained outside tracked source during
preparation and are not part of this public aggregate.

## Closeout session 2026-11-09 (this Mac, rerun now)

Session worked toward this file's evidence-base commit, with per-phase
temporary instrumentation restored byte-identical afterwards (manifest-verified).
Nothing below claims remote CI for local commits (no push was made); CI status
cited is the earlier push of `65e6babcb1600511fdbc0399d1054fd081eac8f2`.

| Gate | Before (inherited) | This session | Evidence provenance |
|---|---|---|---|
| Full suite (baseline) | 913/913 on the repaired tree (later session) | **913/913 locally** (macOS, `Release`, 1 m 31 s), exit 0 | Rerun now; log in session scratch `$WORK/baseline-test.log` |
| Fuzz budget tail | mechanism measured and reduced; residual risk not excluded - not cleared | **No case ≥ 5 s in 6 instrumented runs** (unloaded full ×1 + loaded full ×3 + targeted under load ×2). Max `CliApp.Run` wall 2,590.7 ms (generate+min-fairness, loaded; solo ~1.2 s adversarial incl. startup). Gen max 264 ms. Slowest class in every run was `generate`, not `render` (render max ~717 ms). **Gate NOT cleared; original >10 s never reproduced on demand.** | Rerun now with temporary per-case instrumentation (generation vs run split, written=214/232, skipped=0, process-wide CPU/alloc/GC labeled non-attributable); file restored byte-identical (SHA-256 `f5728441…1136ca7`); analysis + adversarial sweep in session scratch `fuzz-tail.md` |
| macOS descendant-cleanup intermittent | One CI failure (42528584218); 15/15 green locally; instrumentation on main | Loaded loop 20/20 green, `clean=True` 20/20, liveness empty 20/20. Sibling `StepTimeout…` produced one load-red of a different shape (first-exchange `timeout_step`) in a full-suite loaded run. `unreaped` non-empty in 10/20 green iterations with the worker already dead. Spawn-delay adversarial (300 ms, restored) still green and analysis shows a late spawn makes the parent die first (no worker), so it cannot make `clean=False`. **Gate stays OPEN.** | Rerun now, bounded loaded loop + stub-side injection (restored byte-identical, SHA-256 `d53a2f62…e5d4e`); DIAG analysis in session scratch `mac-status.md` |
| S11 hosted-model acceptance | NOT TESTED | **BLOCKED - owner-owned:** no `GROQ_API_KEY` variable on this machine (checked without printing); no inference run, nothing spent. Costs/terms/budget unverified - gate stops at key check. | Checked now; record in session scratch `s11-status.md` |
| CI (65e6bab, prior push) | Green per task brief at session start | **Full CI green after one failed-job rerun** (ubuntu-24.04 UI regression failed once on Microsoft-package-repo 403 `install-deps`; rerun of the single failed job green; macOS setup.sh leg green both times). | Prior push shown via `gh` (external run; logs not in tree) |
| Version parity | 4.0.0 everywhere (nine projects + `CliApp.Version` + `CITATION.cff`) | **Verified current:** 9/9 project `<Version>` = 4.0.0, `CliApp.cs:60` = `4.0.0`, `CITATION.cff` = 4.0.0; CHANGELOG `4.0.0 - UNRELEASED (release candidate)`; README/LICENSE/SECURITY/CODE_OF_CONDUCT/CONTRIBUTING present | Rerun now; no change needed |
| Release workflow pre-tag readiness | Tag-only triggers | **Added `workflow_dispatch` + `release_version` input** (required, no default) so validate/publish can be exercised pre-tag; input used only when ref is not a tag; publish matrix and `if: startsWith(github.ref, 'refs/tags/v')` publish guard byte-identical. | Edited + committed this session; YAML parse + full diff review (actionlint absent, nothing installed) |

Per-phase content-hash manifests enforced that no other tracked or untracked file
changed; see the session's scratch manifest snapshots for the cumulative allowed set.
Raw per-phase logs were kept in the session's scratch `$WORK` directory, not in this tree.
The closeout's fresh-clone verification (`$WORK/fresh-clone.md`) was run at the final
HEAD after this doc commit; the final SHA is not named here by construction.
Green runs here are measurements only: they do not clear the intermittent defect.

## Post-candidate render repair and re-verification (later session)

Scope: output-preserving repairs approved by the maintainer ("If it doesnt
stray form the actual prod and improves the efficiency then yes"). No hosted
call, no tag, no push; all results below are offline on the repaired tree.

- **Ledger guard fix (`examples/python/lattice_model_agent.py`):** the
  acceptance ledger lock is now taken on a stable `<path>.lock` sidecar; the
  previous flock-on-ledger-file excluded nothing across the ledger's atomic
  replace (inode swap). Deterministic cross-process reproduction before the
  fix (lost update, duplicate reserved attempt numbers); serialized after.
  Two regression tests added (`test_lattice_model_agent_acceptance.py`);
  Python adapter suite 36/36.
- **Render repair (`Visualization/AsciiRenderer.cs`,
  `Visualization/TrajectoryPlayback.cs`):** flat-grid rasterization and
  single-pass frame assembly replace per-cell dictionary lookups and
  per-frame string copies. Rendered output byte-identical on the golden
  vector and the three previously slow fuzz vectors (case seeds
  -1417550597, 450637920, -1748514957). Same-host measured effect:
  `CliApp.Run` render 166.2 -> 47.0 ms (~3.5x) and 149.0 -> 128.5 MB (-14%)
  on the 44,060-char fixture; per-frame allocation unchanged (5.2 MB).
  Degenerate-span behavior preserved (OutOfMemoryException, same type).
- **Adapter opt-in controls (`examples/python/lattice_model_agent.py`,
  maintainer-approved):** three optional flags, off by default and absent
  from the request body when unset, so default/S10 behavior is unchanged:
  `--reasoning-effort`, `--max-completion-tokens`, `--max-request-bytes`
  (fail-closed before any HTTP send; recorded in the ledger as
  `request-too-large`). Added for hosted-model compatibility and an
  enforceable cost bound for a future S11 run. Python adapter suite 39/39
  (3 new contract tests: fields absent by default, present when set, guard
  trips before send).
- **Re-verification on the repaired tree:** `dotnet build` 0 errors;
  full suite **913/913**; Python adapter suite **36/36**; golden replay
  12/12 exit 0; pack + throwaway-install parity exit 0 with `--version`
  parity and installed golden replay 12/12; self-contained linux-x64 publish
  smoke (`--version`, replay verify) green **on Linux only** - no
  Windows/macOS runtime claim is made here.
- **Instrumented fuzz re-run (temporary timing instrumentation, restored
  byte-identical after):** 9,200 cases, max case wall **85.4 ms**
  (p99 15.0 ms), vs the pre-repair Mac run's 5,077.9 ms max. These are
  different hosts and are **not** presented as a like-for-like reduction;
  the controlled same-host figure is the ~3.5x render time reduction above.
- **Red-gate status:** the fuzz-budget failure's mechanism (render wall time
  under parallel load) was measured before and after the repair and is
  materially reduced; the original >10 s event was never reproduced on
  demand, so the gate is recorded as **mechanism measured and reduced,
  residual flake risk not excluded** - not cleared. S11 remains NOT TESTED
  pending its own approval.

## Limitations

- The candidate is **red**: the fuzz budget original >10 s event was never
  reproduced on demand and is not cleared by this session's green measurements
  (see the closeout table above); the macOS descendant-cleanup intermittent
  gate also stays open, and S11 remains blocked owner-owned. Local checkpoint
  commits exist (work was committed per-phase as allowed), but no tag, push,
  or release has been made for them in this session.
- No cross-platform results are claimed from this macOS-only session. Remote
  CI on push `65e6babcb1600511fdbc0399d1054fd081eac8f2` (an earlier push)
  showed full green after a single-failed-job rerun; this session's local
  commits have no remote CI status and claim none. Native release assets
  remain unpublished (no tag).
- S10's raw artifacts are published as a **committed subset** under
  [`evidence/acceptance-s10/`](../evidence/acceptance-s10/README.md) (evaluation
  JSONs, sidecar, replay-verified episodes, slice table, and a SHA-256 manifest
  covering every published file). The remainder of the local `.evidence/`
  directory stays untracked/local-only, and the Laya bundle is still reported
  separately (not in this checkout). The counts above are checkable against the
  manifest; no claim beyond integration/plumbing is made.
- Hosted-model acceptance (S11) is NOT TESTED and, per the 2026-11-09
  closeout session, BLOCKED owner-owned: no `GROQ_API_KEY` variable exists
  on this machine, so the key check stopped the gate before any cost-terms
  verification, inference, or spend. It needs its own explicit approval,
  entitlement check and bounded run plan before any call.

## Laya real-model verification (external run, reported separately)

Following the user's "Use laya" selection (hosted-provider run declined), this
candidate was exercised end to end against the real Laya typed-decision engine
in a Linux x64 test environment. The harness is NOT part of the shipped product: it needs
external packages (`laya` 0.4.29, torch, safetensors) and the 842 MB
`convaiinnovations/laya` checkpoint (Apache-2.0, public Hugging Face
download; no account, key or payment). The exact harness source
(sha256-listed), checkpoint identity, raw run artifacts and replay evidence
are preserved in a separate verification bundle
(`LAYA_VERIFICATION_REPORT.md`), not in this repository.

Measured result: `evaluate --scenario standard --seed-set dev --seeds 1
--agent-step-timeout-ms 30000` exited 0; the attempt ledger recorded **400
attempts, 0 errors, no stop**; **2/2 matches recorded** (both seats) and
**both recordings replay-verified** (200 steps, 200 state hashes, schema
v5); valid_seeds=1, void_runs=0, agent_failures=none; decision **"Not
graded: 1 seeds is below the 30-seed floor"**. Scoreline: Wins=1, Losses=1,
MeanDelta=-3.5 vs Scout - recorded as run output only; **no model-quality or
gameplay-strength claim is made**.

Scope and caveats:

- This verifies run integrity (protocol, ledger, replay) with a real model
  in the loop on ONE dev seed. It is not a graded acceptance verdict, and it
  does not cover the hosted-provider chat-adapter path (S11), which remains
  NOT TESTED - declined by the user.
- A first attempt recorded agent_crashed=1 with the seat-1 episode skipped.
  Memory contention from overlapping evaluation processes (~2 GB RAM in the
  test environment) is the probable cause, but causality is not proven and
  a product contribution is not excluded. The clean solo rerun above is
  green end to end; no product code was changed in response.
- The shipped files add no third-party runtime libraries: the .NET product
  assemblies reference no external packages and the Python examples use the
  standard library only. Building and running the suite still requires the
  .NET 10 SDK, and the chat adapter calls an external hosted provider over
  HTTP whenever it is actually used.

## Issue-solving session 2026-11-09 (issues #7-#13, local commits only)

Session facts below are measured this session on this Mac (macOS arm64). No push, tag, provider call, or spend. A green run confirms this build passed; it is not a clearance claim for any intermittent.

- Committed fixes: external-agent descendant capture/bookkeeping corrections (force-capture at dispose, null-start recheck, `UnreapedDescendants` reporting, `ProcessTree` fail-closed gone-checks) and the barrier-held exited-process identity test (`hold-until-eof` stub mode, EOF release). Validation before commit: build 0 errors; targeted `ProcessTreeTests`+`ExternalAgentDescendantCleanupTests` 17/17 four times including three loaded runs; full .NET suite 924/924; Python adapter suite 39/39; golden replay 12/12. (924 = 913 prior + 11 new tests in the two classes.)
- Issue #7 (descendant capture): covered by the committed corrections above; validation as recorded.
- Issue #8 render leg: the output-preserving repair (flat-grid rasterization + single-pass assembly, `Visualization/AsciiRenderer.cs`) is present (flat-grid comment line 162, `Array.Fill` line 178). Re-measured this session via process wall on the built dll: golden render wall 0.183 s, exit 0, output 10,180,743 bytes (fixture `Tests/fixtures/golden_trajectory.jsonl`, 44,060 bytes). This process-wall figure is not comparable to the recorded 47 ms in-process `CliApp.Run` figure (includes runtime startup). No further output-preserving correction was evidenced or applied; the byte-identity scope remains the golden vector and three slow fuzz vectors as previously recorded. A hypothesis that post-repair cost is dominated by O(output) emission stays a hypothesis, not a diagnosis.
- Issue #8 generate leg: `--min-fairness NaN` (seed 1) rejected at parse, wall 0.067 s, exit 2. Worst-case `--min-fairness 0.0` (process wall, built dll, this Mac): seeds 1 accept 0.251 s, 42 exhaust 0.271 s, 2024 exhaust 0.346 s, 987654 accept 0.308 s, 1010 exhaust 0.294 s, 31337 accept 0.152 s, all exit 1 on exhaustion as expected; loaded (two `yes` hogs): 42 0.288 s, 2024 0.365 s, 1010 0.311 s. No single-seed generate or golden render exceeded 10 s unloaded. No wall-budget change adopted.
- Fuzz red-gate honesty: the original >10 s event was never reproduced on demand in this session; cause unknown; residual flake risk not excluded (not cleared).
- Issue #9 (NaN/generate): `--min-fairness NaN` parse rejection verified above.
- Issue #12 macOS worker-state probe (bounded, this host): 40 loaded runs of `ExternalAgentDescendantCleanupTests` with a 0.2 s `ps` sampler — not reproduced in 40 loaded runs on this host. That is a bounded evidence gap, not clearance. The stopped-pending-kill hypothesis (SIGSTOP-then-SIGKILL on macOS) stays open and unconfirmed on this host; no correction applied; architecture-level changes remain proposals only.
- Issue #10 study status: full 30-seed paired-study package prepared (scratch `STUDY_PLAN.md`, external-agent config recovered from `.evidence/acceptance-local/local-sidecar.json`: `127.0.0.1:8081`, model `qwen05`, temperature 0.0, prompt `acceptance`, parser 1). No local endpoint present this session (`curl http://127.0.0.1:8081/v1/models` exit 7), so no smoke run and no per-match wall estimate; study prepared, execution pending. The perception-recording caveat (external recordings carry no perceptions; replay covers step/state-hash only) is recorded as a decision block in the session report, not fixed here.
- Issue #11 manifest: `.evidence` manifested (88 files, 2.8 MB) to a scratch manifest; publication vehicle pending owner decision.
- Issue #13: bounded scratch structured-mutation pilot over replay and scenario surfaces (240 mutants, declared-error oracle with self-check: golden verified, deliberate StateHash corruption verify-caught). Counts: replay 102 parse-rejected / 4 verified / 14 verify-caught; scenario 12 accepted / 108 parse-rejected; zero findings. Not a coverage-guided campaign.
- S11 remains NOT TESTED.
