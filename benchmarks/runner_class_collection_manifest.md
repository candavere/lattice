# Runner-class re-collection: pre-registered manifest and progress log

Stage: unversioned evidence stage.
Written *before* any new throughput value was inspected.

**Collection status: COMPLETE.** 20 sessions dispatched, 20 sessions included,
0 excluded, 0 cohort split, 0 re-dispatches. The rules below were applied to all
20 and every row's verdict is final. The derived calibration, the armed set, and
the MCTS negative result are in
`benchmarks/runner_class_throughput_benchmark.json`,
`benchmarks/runner_class_summary.md`, and `docs/FINDINGS_LEDGER.md`
(`FINDING-014`). All 20 raw artifacts are committed under
`benchmarks/runner_class_raw/`, each re-verified byte-for-byte against the
SHA-256 recorded here before any value was computed.

Owner session note: this file remains the resume point and the pre-registration
record. A fresh session reads it, reconciles run IDs against
`benchmarks/runner_class_raw/`, and must not re-dispatch: the cohort is closed
and complete.

## Fixed target

| Field | Value |
| --- | --- |
| Fixed SHA (all new sessions) | `1b9426f5247e485343296e0d8863795084552535` |
| Tree | `1b9426f5...` (verified as `origin/main` at session start; branch ref `runner-class-calibration-2026-09-29` created pointing at exactly this commit) |
| Runner label | `macos-26` (arm64) |
| Runner image expected | `macos-26-arm64/20260907.0351` (from the historical runs' own `Image Release` log lines) |
| Protocol | `--runs 10 --steps 100000 --warmup 50000` (dispatch inputs `runs=10 steps=100000 warmup=50000`) |
| Job measured | `runner-class-gate` -> `Runner-class gate vs hosted-runner reference (macos-26 arm64)` |
| Artifact | `runner-class-macos-arm64`, `runner-class.json` |
| Sample target | **20 new full sessions** (task's 15-20 band, upper end) - **achieved: 20/20 included** |
| Spacing | **25 minutes** between dispatches, at differing minutes-of-hour |

Rationale for the SHA: measured code is identical to the historical sampled
tree. `git diff --stat 633ecf70...  1b9426f5... -- Engine Agents Analytics Cli Tests Protocol Generator Trajectories`
is empty: between the historical five-sample tree and this SHA nothing under a
measured path changed. The changes are confined to `.github/`, `benchmarks/`,
and `docs/`.

## Ref policy

`workflow_dispatch --ref main` is unusable here: `main` can move while the
viewer session works, and a moving ref is not a pin. A clearly named temporary
**branch** ref is pushed at exactly the fixed SHA and every dispatch targets
it:

```
git push origin 1b9426f5247e485343296e0d8863795084552535:refs/heads/runner-class-calibration-2026-09-29
gh workflow run benchmarks.yml --ref runner-class-calibration-2026-09-29 \
  -f runs=10 -f steps=100000 -f warmup=50000
```

No tag is created. `main` is not altered for collection. Measured code is not
changed. The branch is deleted only after the evidence is downloaded and
reconciled. (Reconciliation is done; the 20 artifacts are committed under
`benchmarks/runner_class_raw/` and SHA-256 verified. The branch is retained
until this evidence stage is committed.) Workflow presence on the ref is verified before relying on it.

## Inclusion rules (fixed in advance)

A session is **included** in the primary cohort only if all of:

1. `headSha` of the run is exactly the fixed SHA.
2. The `runner-class-gate` job completed (not cancelled; its exit code is
   recorded either way).
3. The real `runner-class-macos-arm64` artifact downloaded and parsed as JSON.
4. That JSON's `Metadata.Commit` equals the fixed SHA.
5. All five workloads present, each `Iterations` = 10, `StepsPerIteration` =
   100000 (100 for `policy_lookahead_mcts_32`, by design, as in every prior
   record), each `MedianThroughputPerSecond` > 0.
6. Fingerprint fields match the historical class: `Architecture` = `Arm64`,
   `Cores` = 3, `Runtime` = `.NET 10.0.12`, `Configuration` = `Release`,
   `GcMode` = `Workstation`, `Cpu` contains `M1`.
7. `Image Release` from **that run's own** job log is the expected image. A
   different image build is a **cohort split**, not an exclusion to be hidden:
   it is retained and reported as a separate stratum.

A session is **excluded** (logged with reason) if: wrong `headSha`, wrong
`Metadata.Commit`, missing/corrupt artifact, cancelled job, missing workload,
zero/negative median, shortened protocol, or a duplicate of a run already
counted (retry pass, re-download, or re-dispatch of the same run ID).

## Image and fingerprint policy

- The image build is read from each run's own `Image Release` log line, never
  inferred from the `macos-26` label and never assumed stable.
- A fingerprint mismatch is recorded, not silently accepted.
- If the image build changes mid-collection, collection **pauses** and the
  cohort split is reported rather than pooling two images into one envelope.

## Rule (unchanged, pre-existing)

Applied independently per workload to the accepted cohort:

```
min_ratio_w = lowest accepted session median_w / cross-run median_w
allowed_w   = min(0.95, 0.95 * min_ratio_w)
arm_w       = (0.95 * min_ratio_w >= 0.75)
```

Quantiles reported: p5/p95 by linear interpolation between order statistics
(numpy-style, `q*(n-1)` index). CV: population standard deviation divided by
the mean (ddof = 0); the sample (ddof = 1) value is also reported.

## Treatment of prior evidence

| Evidence | Treatment |
| --- | --- |
| The five originals (36469355765, 36469885628, 36469902164, 36469918606, 36469934258) on `633ecf70` | **Historical.** Same image build, same measured code, same protocol as the new cohort, but a *different commit*. Not pooled into the primary analysis. Eligible for a clearly-labelled same-image / measured-code-equivalent sensitivity check only. |
| Failing run 36471478970 on `4d05585` | **Historical failure evidence**, retained. Its uploaded artifact is the **second pass only**; its first-pass figures stay as the comparator-table observations they are, never as a downloaded JSON or an independent session. |
| Any push-triggered / load-triggered run | Included only if it independently meets every rule above; otherwise retained as separately labelled context. |

## Expected / tolerated noise

`regression-gate` compares hosted runners against the **bare-metal** record at
0.75x. It is expected to go red on some dispatches. That is recorded per run
and is **not** a reason to stop, retry, or re-dispatch; the runner-class
artifact is uploaded with `if: always()` and is unaffected.

Stop conditions: the runner-class job or its artifact failing **3 times in a
row**; the image build changing; or the branch ref no longer dispatching.

## Progress log

Columns: dispatch time (UTC), run ID, URL, event, head SHA, state/conclusion,
artifact path + SHA-256, image release, verdict (INCLUDED / EXCLUDED + reason).

| 2026-09-28T21:21:09Z | 36485512323 | https://github.com/candavere/lattice/actions/runs/36485512323 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | `benchmarks/runner_class_raw/36485512323.json` sha256=88813b7a19074a2bff1200dd7e5712b9e57e2831dfbae8301b7b45b9eb2028f6 | macos-26-arm64/20260907.0351 | INCLUDED (pilot: proved branch-ref dispatch resolves to the fixed SHA and the artifact downloads with matching `Metadata.Commit`) |
| 2026-09-28T21:24:44Z | 36485904887 | https://github.com/candavere/lattice/actions/runs/36485904887 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36485904887/runner-class.json sha256=ccf841428a3961cb7ddda6489189c466b8ccf1b277d53b31a5c21bcd8a887df4 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-28T21:53:27Z | 36489039586 | https://github.com/candavere/lattice/actions/runs/36489039586 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36489039586/runner-class.json sha256=80633957d010669df8e199476f7c63ba7100803c5914a7d7888c5ea15b0d48b9 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-28T22:21:49Z | 36491926155 | https://github.com/candavere/lattice/actions/runs/36491926155 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36491926155/runner-class.json sha256=e17b0907e4fc3eda1fb237b0c94665f1fca60d6a8be47659c6ae24e4bcee78ed | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T06:40:54Z | 36532339243 | https://github.com/candavere/lattice/actions/runs/36532339243 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36532339243/runner-class.json sha256=6a7c70deace42e326e04b49c449ac4918790e71da7e3a5f5e781180fcddbd55c | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T07:09:38Z | 36535002867 | https://github.com/candavere/lattice/actions/runs/36535002867 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36535002867/runner-class.json sha256=dd30f28cfb7b0d03815910f1e47963cf0da21e090aa2bade7737924fa5c551a7 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T07:38:24Z | 36537836695 | https://github.com/candavere/lattice/actions/runs/36537836695 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36537836695/runner-class.json sha256=fb71f05bd640504035fb230faf4c3121290233372c2423505093773f32ce9329 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T08:06:38Z | 36540673750 | https://github.com/candavere/lattice/actions/runs/36540673750 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36540673750/runner-class.json sha256=b22102c08c5101e4bfea5b18a771cbefd59df534bc3ff24e8835d89e5e6623ad | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T08:34:53Z | 36543622754 | https://github.com/candavere/lattice/actions/runs/36543622754 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36543622754/runner-class.json sha256=3877acd005e005adf76d37b5c5490a6567725e3e7861898aca2de55c87ab3367 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T09:03:38Z | 36546690090 | https://github.com/candavere/lattice/actions/runs/36546690090 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36546690090/runner-class.json sha256=fa44f2bf15ffe5b737263dca03b5eb0c76fef1c88e48ca5920000584eebd93b8 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T09:31:53Z | 36549765862 | https://github.com/candavere/lattice/actions/runs/36549765862 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36549765862/runner-class.json sha256=aa43af24ff65133c4d30a08ba18b12f15d8f337d291cd737aa7eba09a1cb63d9 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T10:01:11Z | 36552908013 | https://github.com/candavere/lattice/actions/runs/36552908013 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36552908013/runner-class.json sha256=9b2809ef24f02b18ae1305dd7f137815af29a5322a9e1c975a3db595d545f948 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T10:29:23Z | 36555874290 | https://github.com/candavere/lattice/actions/runs/36555874290 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36555874290/runner-class.json sha256=d101e060c5c515e14993475dc87cd1b98d851f281381613921714036ad38e021 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T10:57:34Z | 36558772911 | https://github.com/candavere/lattice/actions/runs/36558772911 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36558772911/runner-class.json sha256=d095a438289c0878ab0bd4522714687dc3bb89b6ef1d1e517d5af579cbadf3f8 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T11:26:18Z | 36561735364 | https://github.com/candavere/lattice/actions/runs/36561735364 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36561735364/runner-class.json sha256=28fa3adb77f2cf383d1c4b384621228146a267168e8e0d4305f6477f15d6fac0 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T11:55:02Z | 36564719815 | https://github.com/candavere/lattice/actions/runs/36564719815 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36564719815/runner-class.json sha256=23e6abfd57c4c076b0815e3cc839e65ff0a6868a82598781591cc361b9e6eefc | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T12:23:46Z | 36567818281 | https://github.com/candavere/lattice/actions/runs/36567818281 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36567818281/runner-class.json sha256=77327a3406b4cd6cd729beb01b0d91da8514bf96ef6220856ba2f034e417eeea | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T12:52:01Z | 36571007131 | https://github.com/candavere/lattice/actions/runs/36571007131 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36571007131/runner-class.json sha256=ae520eeb78a2a1dd1c09e94896e592778fff6fe1bdddd053453a6bbd8cf61568 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T13:20:15Z | 36574344509 | https://github.com/candavere/lattice/actions/runs/36574344509 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36574344509/runner-class.json sha256=98fbc0dced15f131e2e35b72a3377010a22f22109a526f51d31be92293e239e0 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
| 2026-09-29T13:48:30Z | 36577844146 | https://github.com/candavere/lattice/actions/runs/36577844146 | workflow_dispatch | 1b9426f5247e485343296e0d8863795084552535 | run=success / runner-class=success / regression-gate=success | /private/var/folders/tw/h_hdmnl577ncjfb65x3p46xm0000gn/T/opencode/runner_class_raw/run_36577844146/runner-class.json sha256=96f115ea9e904badfe11e229ee76c3e0b3755f26477472bd12c05f17a74512b1 | macos-26-arm64/20260907.0351 | INCLUDED (rules 1-7 verified: headSha = fixed SHA; runner-class job completed successfully; artifact downloaded and parsed as JSON; Metadata.Commit = fixed SHA; all five workloads present at Iterations 10 and StepsPerIteration 100000 (100 for policy_lookahead_mcts_32) with positive medians; fingerprint Arm64 / 3 cores / .NET 10.0.12 / Release / Workstation / Cpu contains M1; Image Release from this run's own log = macos-26-arm64/20260907.0351, so no cohort split) |
