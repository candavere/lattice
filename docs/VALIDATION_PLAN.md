# Lattice Independent Validation Plan

This plan defines the formal protocol under which outside researchers
replicate the empirical claims of the `candavere/lattice` project and register
their results with the project. It is the governing process for the execution
procedures in
[`reproduction_packet.md`](reproduction_packet.md) — the turnkey challenge guide
anchored to the immutable release **v2.3.2** (commit `4f7816f`) — and the
ingestion contract for the permanent record in
[`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md).

Its purpose is to make independent replication a first-class, auditable
activity: a reviewer who follows it produces, in one artifact directory, the
environment capture, raw transcripts, pass/fail judgments for the four
experiments, and any discrepancies — all of which a second reviewer can
re-check without trusting the first.

## 1. Scope and status of validation evidence

Project claims are accepted into the repository only when they map to committed
code, deterministic tests, CI workflows, or benchmark artifacts (README,
"Development and Verification Method"). This plan distinguishes three kinds of
verification, and each holds a different evidentiary weight:

| Kind | Who runs it | What it proves | Status today |
| --- | --- | --- | --- |
| Internal tool-based verification | Maintainers, on committed code | The test and mutation gates defined in `stryker-config.json` and the CI matrix pass against the scoped transition surfaces | Closed for the scoped surfaces (see §7 and `benchmarks/mutation_stryker_summary.json`) |
| Independent human replication | External reviewers, following this plan and `reproduction_packet.md` | The published release assets reproduce the four experiments on a reviewer's own host | **Open** — no external replication report has yet been filed |
| Independent third-party review | External experts auditing claims, code, or methodology | The claims withstand adversarial scrutiny beyond executing the scripted experiments | **Open** |

Per the project's falsification discipline, tool-based verification and
independent human replication are different things. **No claim in this document
asserts that independent replication has occurred.** The project states that
reviewer replication is invited, structured, and formally ingestible — not that
it has been completed.

## 2. Preconditions for a valid replication

A replication is scored only if every precondition holds; a report that omits
one is returned to the reporter as incomplete rather than ingested as a
finding.

1. **Released asset integrity.** The reviewer verifies the published assets
   against the committed SHA-256 manifest exactly as specified in
   `reproduction_packet.md` §1.2 before executing any binary. A checksum
   failure aborts the run: no experiment may proceed on unverified bytes.
2. **Environment capture.** The reviewer records, for the host on which the
   experiments ran, at minimum the fields in §4 below. This makes results
   attributable to a host class, not a mythical "average machine".
3. **Raw transcripts.** Every command in the four experiments is run with its
   stdout and stderr captured verbatim into a text file, plus the exit code.
   No transcript may be edited, summarized, or normalized; host-variance
   explanations are written in prose beside the verbatim output, never into it.
4. **Single judged target.** Judgments are made against the published `v2.3.2`
   release (`--version` reports `2.3.2`) unless the report explicitly labels
   itself a source-checkout reproducibility study of a different commit.

## 3. The four replication experiments

The experiments are executed exactly as specified in `reproduction_packet.md`
§2. Each is a pass/fail judgment with an explicit failure criterion so a
reviewer never has to improvise what "worked" means.

### Experiment 1 — Version & CLI contract verification

Command:

```sh
./lattice --version
./lattice --help
```

Criterion: `--version` prints exactly `2.3.2` on stdout and exits `0`;
`--help` lists the seven commands `generate`, `simulate`, `render`, `analyze`,
`replay`, `benchmark`, `evaluate` plus `-h, --help` and `-v, --version`, and
exits `0`.

### Experiment 2 — Serialized `StepResult` replay equivalence

Experiment 2 verifies **per-step serialized `StepResult` equivalence**, against
the canonical golden trajectory at `Tests/fixtures/golden_trajectory.jsonl`
when the source-pinned path is used. It is explicitly **not** canonical
simulation-state hash-tree equivalence: no canonical state digest exists, and
the experiment never uses one to summarize an episode.

Source-pinned path:

```sh
git clone https://github.com/candavere/lattice.git lattice
git -C lattice checkout v2.3.2
cd lattice
./lattice replay Tests/fixtures/golden_trajectory.jsonl --verify
```

Criterion: `--verify` reports `replay verified: N step(s) serialized-equivalent
(seed 2024, schema v2).` with the recorded step count, on stderr, and exits
`0`. Any reported stage of serialized divergence is a fail. Asset-only
round-trip verification of a freshly simulated episode may be run as a
supplement but does not substitute for the golden-trajectory check.

### Experiment 3 — Context-dependent policy interaction

```sh
./lattice evaluate --scenario standard --seeds 10 --rollouts 32
./lattice evaluate --scenario bottleneck --seeds 10 --rollouts 32
```

This experiment demonstrates that policy outcome is a property of the
policy–environment interaction: the 32-rollout MCTS subject loses the paired
comparison on standard generated layouts (**Scout > MCTS**; committed mean
paired deltas −1.12 dev / −1.25 held-out, 95% CIs below 0) and wins under
capacity-1 choke contention (**MCTS > Scout**; committed mean paired deltas
+1.42 dev / +1.38 held-out, 95% CIs above 0, 22–27% mean contention).

Criterion: the standard run reports a **negative** mean paired Δ and the
bottleneck run a **positive** mean paired Δ, each with bounded per-seed
dispersion; contention is non-zero on the bottleneck run and zero on the
standard run. A reversal of direction on one host is a **finding worth
reporting**, scored per §5, not an automatic claim failure: these results are
topology-conditional research observations (`FINDING-006`), never a universal
policy ranking. The reviewer must not treat the two directions as
contradictory — they are the point of the experiment.

### Experiment 4 — Structural performance smoke

```sh
./lattice benchmark --steps 100000 --runs 5
```

Criterion: all five cases of the workload matrix complete; each reports a
positive median throughput; no iteration reports a step-digest mismatch. This
is a **structural smoke pass**, not an authoritative throughput adjudicator:
host variance is expected and the committed reference record is stamped with
its own host metadata. Absolute numbers are not compared against any committed
reference; structure and integrity are.

## 4. Provenance capture requirements

A replication report must include, in a section titled `Host environment`:

- CPU model string; architecture (`x86_64` / `arm64`); core count; RAM.
- OS name and version; the binary platform used
  (`linux-x64` / `osx-arm64` / `win-x64`).
- If the dotted .NET deployment is run under a local SDK instead of the
  self-contained binary, the reviewer's `dotnet --info` runtime line; for the
  self-contained binary, note that it embeds its runtime and no host SDK is used.
- The SHA-256 verification result for each asset (from `reproduction_packet.md`
  §1.2) and the `sbom.json` digest check.
- For experiment 2's source path: the pinned commit
  `4f7816fa5594f7097d6b2978c6c626553d075326` and the git version used.
- Date/time the run started; the git revision of this repository (for the
  source-pinned path) and of `reproduction_packet.md` / this file, so the
  reviewer's procedure version is auditable.

The complete report is assembled per the reporting template in
`reproduction_packet.md` §3, extended with the fields above.

## 5. Logging ingestion and the findings ledger

Every replication result — success or failure — is ingested into
[`FINDINGS_LEDGER.md`](FINDINGS_LEDGER.md) with provenance
`EXTERNAL_INDEPENDENT`. The ledger is append-only by policy and git history;
no entry is rewritten, and later findings supersede by reference.

| Result class | Definition | Ingestion |
| --- | --- | --- |
| Positive replication | All four experiments pass; transcripts and provenance complete | Logged as evidence supporting the claimed claims; linked report at the reproduction URL |
| Negative finding | A pass/fail criterion did not hold on a verified host (e.g. reversed delta direction, version mismatch, divergence reported) | New ledger entry, severity per impact, disposition `OPEN` until reproduced or falsified |
| Anomaly | Output that neither passes nor cleanly fails a criterion (e.g. per-seed dispersion outliers, unexplained exit paths) | New ledger entry, severity `Low` by default, disposition `OPEN`, with raw transcript attached |
| Non-reproducible run | Incomplete provenance, failed checksum, or interrupted execution; the report lacks the preconditions of §2 | Not ingested as a finding; returned to the reporter with the missing precondition named |

Submission channel: a public issue at
`https://github.com/candavere/lattice/issues/new`, titled
`Reproduction report — Lattice v2.3.2`, containing the filled template.
Maintainer handling of an ingested finding follows the closure standards in the
ledger (`FIXED`, `ACCEPTED_LIMITATION`, `REJECTED_WITH_EVIDENCE`, `OPEN`).

A tolerated failure (e.g. a reversed delta direction, or a checksum failure the
reviewer cannot resolve) is reported honestly and enters the ledger as described
above; a silent pass on unverifiable evidence is worse than a loud failed
criterion.

## 6. Conflict resolution between reviewer and committed evidence

A disagreement between a reviewer's result and a committed artifact is the
**start** of an investigation, not a verdict either way. The steps:

1. Confirm the reviewer's host and transcript satisfy §2; a transcript that
   cannot be re-checked is not evidence.
2. Reproduce the reviewer's run on the committed `v2.3.2` assets before
   publishing any adjudication.
3. Log the outcome as a ledger entry with provenance `EXTERNAL_INDEPENDENT`
   (when filed externally) and record the disposition under the closure
   standards.

The preserved negative result `FINDING-006` is the standing precedent: a
seemingly contradictory observation became committed evidence precisely because
it was reproduced, captured, and ingested rather than dismissed.

## 7. Relationship to internal verification

Maintainer-run Stryker mutation analysis (see
[`stryker-config.json`](../stryker-config.json) and
[`benchmarks/mutation_stryker_summary.json`](../benchmarks/mutation_stryker_summary.json))
and the CI matrix are **internal tool-based verification**: they exercise the
committed suite against the scoped transition surfaces on maintainer-owned
hosts and runners. They do not count as independent human replication and do
not close the two **open** lines in §1. Independent replication and third-party
review remain open until an external report is filed and ingested. This
document and the reproduction packet make no claim otherwise.

## 8. Versioning of this plan

This plan is versioned with the repository. It is anchored to the `v2.3.2`
immutable release; a future release supersedes it by publishing a revised plan
at a higher version, never by rewriting the historical `v2.3.2`-scoped
document. The plan reads only published `v2.3.2` assets and does not modify,
retag, or rewrite `v2.3.2`, `v2.3.1`, or any earlier release.