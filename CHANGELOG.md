# Changelog

All notable changes to Lattice are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Post-`v3.0.0` work on `main`. This section describes unversioned development
state; it creates no release and no tag. The `v3.0.0` narrative below and
[`CITATION.cff`](CITATION.cff) stay pinned to the released 3.0.0 identity.

### Changed

- **The solution now targets .NET 10 (`net10.0`).** All ten versioned projects,
  including the external-agent stub, move from `net8.0` to `net10.0`, and the
  CI, release, and benchmark workflows install the .NET 10 SDK alongside them.
  The ubuntu x64 structural smoke in `benchmarks.yml` moves to the .NET 10
  baseline runtime with it; it stays classified `--smoke`, so it still never
  adjudicates a throughput ratio, because what separates it from the committed
  record is the host class and the shortened budget, not the runtime. No
  benchmark baseline, recorded artifact, or threshold was changed, and no
  `global.json` pin was added: `10.0.x` already tracks the SDK the workflows
  install. `docs/reproduction_packet.md` is deliberately left alone, because its
  `.NET 8` statements describe the immutable `v2.3.2` and `v3.0.0` release
  assets rather than the current tree.

- **The architecture diagram's legend names Lattice's own roles.** The generic
  `Frontend` / `Backend` / `Database` / `Security` / `External` categories are
  replaced with *CLI command*, *artifact on disk*, *site viewer*,
  *verification / CI gate* and *external agent process*, and the column gap is
  narrowed so edge labels render at about 7.7px at README width instead of
  about 7.1px. `docs/architecture.svg` and `docs/architecture.png` are
  re-exported together from the same Archify run.

- **Present-tense .NET 8 wording is corrected; historical .NET 8 is labelled as
  historical.** Seven current-state sites now say .NET 10. Two of them needed
  more than a version swap, and say so: `Cli/JsonArtifact.cs` explains that
  `JsonWriterOptions.NewLine` became available in .NET 9 and the original blocker
  is gone, and `Protocol/ProtocolViolation.cs` records that
  `InvalidDataException` is *still* sealed on .NET 10. ADR-0001 and ADR-0003 keep
  their original `.NET 8` decision text and each gain a dated addendum pointing
  at it as history. `docs/reproduction_packet.md` is untouched: its `.NET 8`
  statements describe the immutable `v2.3.2` and `v3.0.0` release assets.

- **The architecture diagram is redrawn from the current tree with Archify.**
  `docs/architecture.svg` and the new `docs/architecture.png` are exports of an
  Archify-authored candidate validated against this revision: every box cites the
  source that implements it, and the recording box now says **schema 5**, which
  the previous hand-drawn diagram still called schema 4. The README embed, alt
  text, data-flow block and per-box citation table were corrected with it,
  including two citations that had drifted (`TrajectoryWriter.cs:71` and
  `CliApp.cs:1060`).

- **The CRLF example test now asserts the framing contract specifically, plus an
  inverse test proving CR emission is caught.**

- **CI now enforces throughput on GitHub-hosted runners for four of five
  workloads.** The hosted-runner reference
  ([`benchmarks/runner_class_throughput_benchmark.json`](benchmarks/runner_class_throughput_benchmark.json))
  was re-collected as 20 full-protocol sessions on pinned commit `1b9426f5`,
  replacing the earlier 5-session record that was published **demoted**
  (`ArmedWorkloads: []`). **Which workloads are armed, which is not, and why
  the unarmed one is a permanent non-verdict rather than a gap to close, are
  stated once in
  [`FINDING-014`](docs/FINDINGS_LEDGER.md#finding-014--the-mcts-decision-throughput-case-cannot-be-gated-on-this-host-class-and-four-of-five-workloads-can)**
  and are deliberately not restated here, so there is a single canonical
  statement to correct if the record is ever re-derived. The demotion is not
  withdrawn, and no threshold was widened against any record or observed run.
  Engine, agent, CLI, protocol, and test sources are unchanged.

- **Version 3.1.0.** All nine project `<Version>` values and `CliApp.Version`
  move to `3.1.0` together. `CITATION.cff` and the pinned v3.0.0 release record
  are **left alone**: they describe the released 3.0.0 identity, and this work
  is unreleased. No tag, release, or release asset is created here.

- **`FINDING-014` is the single canonical statement of the runner-class arming
  decision.** The MCTS workload's "informational / not armed" status was
  restated in `README.md`, `docs/BENCHMARKING.md`, and this changelog. Those
  restatements are replaced with one-line cross-references to the finding, which
  now states the arming decision, the negative result, and the per-workload
  figures in one place. No meaning changed and no benchmark number moved.

- **The committed `site/infiltration.jsonl` is re-recorded at schema 5.** It
  was regenerated from its committed command at the current tree and reproduces
  its schema-4 predecessor exactly once the new `ScenarioSha256` field and the
  version stamp are removed — the re-recording is a schema migration, not a
  change of episode. The pre-schema-5 file is **not** discarded: it is
  committed as `Tests/fixtures/legacy/infiltration_schema4.jsonl` and is the
  standing proof that a schema-4 recording still reads, replays, and verifies
  under the schema-5 reader.

  **Two committed recordings were deliberately NOT re-recorded, and both
  decisions are recorded rather than papered over.** `site/demo.jsonl` is not
  reproducible from any committed command at the current tree: its 27-tick
  episode has agent 0 idle where a `greedy` collector collects, so no current
  invocation regenerates it, and re-recording it would have *replaced the site's
  demo episode* rather than migrated it — a behavioural change disguised as a
  schema change. `Tests/fixtures/golden_trajectory.jsonl` stays at schema v3
  because that is its documented job: it is the cross-platform replay gate's
  proof that a pre-perception recording is still read and still verified
  without being nagged, and re-recording it would have destroyed the very
  property it exists to hold. Together with the schema-4 legacy fixture, all
  three schema versions are committed and all three verify.

- **Recordings moved to trajectory schema 5, which records the scenario
  descriptor's SHA-256.** The trajectory header gains one optional field,
  `ScenarioSha256`: the SHA-256, in lowercase hex, of the declarative scenario
  descriptor a recording was produced from, taken over that file's exact source
  bytes, read once. `TrajectorySchema.CurrentVersion` is now `5`.

  **Every recording this build produces carries a digest** — a file-loaded run
  carries the digest of the file it was given, and a built-in named invocation
  carries the digest of the committed descriptor that built-in now resolves
  through, so a built-in run names a file a reader can actually open. The
  digest is **not** a per-tick `SimulationStateHash` and the two are never
  compared: the scenario digest names the *declaration*, the state hash names
  the *world* at one tick.

  **Schema 0 through 4 recordings remain readable, replayable, and verifiable
  unchanged.** The digest is a new optional field, omitted when absent, so an
  older recording is not rewritten or rejected by its absence. The committed
  `site/infiltration.jsonl` (schema 4) and `site/demo.jsonl` and the golden
  fixture (schema 3) stay at their own versions **on purpose** — they are the
  standing backward-compatibility evidence, and CI's site-recording gate
  continues to replay them. A schema-5 recording whose digest is absent verifies
  on every tick and reports
  `no scenario digest: the recording does not name the descriptor that produced it`
  as a *notice* — deliberately unlike the state hash, whose absence from a
  schema-3-or-newer recording is a *problem*, because replay recomputes the
  state hash and so can detect its removal, while it cannot recompute a digest
  against a file a self-contained recording never reopens. A malformed digest
  is rejected at read time.

  **Legacy raw recording digests necessarily change, and the change is
  reported honestly rather than papered over.** The new header field and the
  version stamp are additive, so a pre-change recording and its post-change
  counterpart are *not* byte-identical. Equivalence is claimed precisely
  instead: for every built-in across several development and held-out seeds, the
  post-change header equals the pre-change header after removing **only** the
  `ScenarioSha256` and `SchemaVersion` fields, and every per-tick
  `SimulationStateHash` is identical. A file-loaded equivalent of a built-in
  matches the built-in line for line after removing only the digest and the two
  presentation labels each form spells its own way.

- **A scenario descriptor now drives `simulate` and `evaluate`.**
  `simulate --scenario <path>` records an episode whose map, roster, agent
  count, tick budget, and transit speed all come from the descriptor, and
  `evaluate --scenario <path>` runs the paired study on a descriptor's map with
  the study's protocol otherwise unchanged. `--scenario` distinguishes a file
  from a built-in name by a path separator, never by a case-insensitive name
  match, so a file called `infiltration` stays a file. Flag combinations that
  contradict a descriptor — `--steps`, `--agent` — are **refused with a stated
  reason** rather than resolved by a silent precedence, and an `evaluate` study
  refuses a descriptor that does not declare two seats, so a paired delta stays
  commensurable with published studies. A file-loaded study leaves the
  evaluation artifact's field set unchanged, so the published artifact format
  does not move.

- **The external-agent `hello` message's `scenario` field is no longer limited
  to `standard` / `bottleneck`.** The field, its type, and the protocol version
  are unchanged — `protocol` is still exactly `1`, and no field was added,
  removed, or retyped — but the value is no longer a closed two-element set. It
  carries the id of whichever scenario is running, so a study launched from a
  declarative scenario file reports that file's `Id` (for example
  `gated-vault-duel`) rather than one of the two built-in families. An external
  agent that branched on the two-value set must accept any string and must treat
  it as an opaque label identifying the map family. The field is specified in
  [`docs/EXTERNAL_AGENT_PROTOCOL.md`](docs/EXTERNAL_AGENT_PROTOCOL.md) §3
  (`hello`), under the version fixed at `1` by §2.

- **The site viewer shows the scenario digest.** The provenance panel reports
  `ScenarioSha256` when a recording carries one and says plainly that a
  pre-schema-5 recording names no descriptor, rather than inventing a value.

### Added

- **Declarative scenario files, and a `validate-scenario` command.** An
  experiment setup can now be written as a declarative, closed-schema JSON
  document describing the environment, the roster, the tick budget, and which
  existing win and scoring rules apply. Scenario files are data only: no
  scripts, no expressions, no reflection, no dynamic type loading, no external
  commands, and no environment-variable-dependent behaviour, and a file can only
  select mechanics the engine already implements.

  A map is declared one of two ways, and both are first class. A scenario can
  name one of the seeded generator families (`standard` or `bottleneck`) **as a
  skeleton** and narrow it with ordered, explicit overrides and additions —
  topology, obstacles, resources, roster, step limit, and victory/scoring are
  all under the file's control, while the family's own seed variation is
  preserved untouched. Or a scenario can be **hand-authored outright** with no
  generator at all.

  `lattice validate-scenario <file>` checks a descriptor and reports its id,
  SHA-256, map source, roster, and victory/scoring. It runs **no episode and
  writes no artifact**, so it is safe in a pre-commit or CI check. Unknown
  fields and unrecognised enum strings are rejected rather than ignored, and
  every fault is reported with the offending field's JSON path — all in one
  pass. Obstacles use the engine's own capacity semantics (a zone or choke with
  `MaxOccupancy: 0` is impassable) rather than a wall mechanic invented for the
  format, so "inaccessible resource" is a real unreachable claim under the step
  contract. Bounds (1 MiB per file, checked before parsing; 256 zones, 4096
  resources, 8192 choke points, 100000 ticks) prevent a descriptor from
  requesting a pathological allocation.

  The digest is the SHA-256 of the descriptor's **exact source bytes, read
  once**, in lowercase hex — never over a re-serialization, the path, or an
  mtime. It identifies the **file**, not the meaning, so two descriptors that
  differ only in whitespace or line endings hash differently on purpose;
  semantic equivalence and digest equality are deliberately separate properties.
  It is also distinct from a recording's per-tick `SimulationStateHash`, which
  names the simulation *state* at a tick; the two are computed over different
  things and are never compared to each other.

  `Cli/ScenarioDescriptor.cs`, `scenarios/*.json`,
  `Tests/fixtures/scenarios/invalid/`, `docs/SCENARIOS.md`, and the
  `docs/CLI.md` / `README.md` references land together with the behaviour.

- **Ubuntu 26.04 compatibility probe (informational).** CI gains a distinctly
  named `ubuntu-26.04` job that restores, builds in Release, runs the golden
  replay verification, and runs the test suite under the same .NET 10 SDK as the
  gated matrix. It is `continue-on-error` and non-gating: it publishes no
  artifact, nothing depends on it, and it is not part of the three-OS
  cross-platform equivalence contract, so its step results and logs must be read
  directly rather than inferred from a green workflow.

- **Machine-readable per-OS test totals.** CI now writes VSTest TRX reports and
  reduces them to one JSON summary per operating system, uploaded as
  `dotnet-test-summary-ubuntu`, `-windows`, and `-macos` with the raw TRX files
  alongside. Each summary carries `total`/`passed`/`failed`/`skipped`/`notRun`,
  the head `commitSha` tested, `runnerOs`, `runnerImage`, and `dotnetVersion`.
  The parser refuses rather than guesses: it exits non-zero and publishes no
  JSON when reports are absent, malformed, duplicated, contradictory, or when
  a run discovered zero tests. The three legs are never summed — a cross-OS
  total would triple-count one suite and describe no real run. The parser's own
  tests run in CI as the `Test summary parser unit tests` job.
- **Decision-time perception recording (trajectory schema 4).** Step lines may
  carry a `Perceptions` array with one `PartialObservation` per agent slot —
  the masked, vision-bounded view that agent's own `PerceptionFilter` produced
  inside its `Decide` call — and the header carries `AgentVision`, the vision
  radius in graph hops each filter was built with. `replay --verify` reprojects
  and checks these alongside the existing per-step `StepResult` equivalence and
  per-tick state digest. Schema 3's state-hash contract is unchanged.

### Changed

- **Pinned the Ubuntu CI runner to 24.04.** Every active `ubuntu-latest` runner
  choice in `ci.yml`, `pages.yml`, `release.yml`, and `benchmarks.yml` is now
  `ubuntu-24.04`, so GitHub's staged migration of the `ubuntu-latest` label
  from 24.04 to 26.04 (2026-10-19 to 2026-11-19) cannot move a gate or a
  measurement under a merge. Windows and macOS labels, the `macos-26`
  measurement jobs, action pins, artifacts, benchmark thresholds and baselines,
  and release safeguards are unchanged. The gated three-OS matrix still covers
  24.04, not 26.04.
- **The site viewer is honest about what a recording proves.** It distinguishes
  world, recorded, and derived claims (`claimed in world` vs `claims seen` vs
  `claims in derived view`), reports no count at all for frames with no
  recorded perception rather than inventing one, paints loot above room cards,
  scales the title census with font size, fixes room/label/caption layout
  collisions, and makes zone-table presence follow the painted ego view. The
  terminal ego frame is labelled as the last decision-time view, not presented
  as a fresh one. The detailed truth and provenance table, and its pixel and
  geometry tests, are in
  [`docs/SUPPORT_AND_REPRODUCIBILITY.md`](docs/SUPPORT_AND_REPRODUCIBILITY.md).
- **The hosted runner-class throughput record is demoted, not re-thresholded.**
  The `runner-class-gate` job on the pinned `macos-26` arm64 label measures the
  full protocol and prints the full fingerprint and a per-workload table, but
  its record's `Provenance.ArmedWorkloads` is `[]`, so it adjudicates no
  workload and exits 0. Its first live run
  ([36471478970](https://github.com/candavere/lattice/actions/runs/36471478970))
  fell below the five-sample minimum on all five workloads with no measured
  code changed since the sampled tree; **the cause of that slowdown is
  unestablished**, and the record was demoted rather than re-thresholded. The
  bare-metal research record is a separate, untouched artifact. Consequence to
  read honestly: **CI currently enforces no throughput on GitHub-hosted
  runners.** See [`docs/BENCHMARKING.md`](docs/BENCHMARKING.md) and
  [`benchmarks/runner_class_summary.md`](benchmarks/runner_class_summary.md).

### Fixed

- **README reproducibility proof row.** The landing page's proof row quoted the
  pre-schema-4 recording digest for the currently committed
  `site/infiltration.jsonl`, giving a false expected result. It now quotes the
  committed digest, shows both output paths, and scopes the claim to the
  runtime and host the artifacts record. An unverifiable seed-43 digest claim
  was removed rather than restated.
- **Platform-scope wording.** `CONTRIBUTING.md` described the determinism
  guarantee as tested on "x64 and ARM64" across Linux, macOS and Windows; the
  CI matrix is three rolling OS labels and does not vary architecture per leg.
  A stale throughput figure and a personal device specification in
  `CONTRIBUTING.md` prose were replaced with a pointer to the dated, host-scoped
  artifact.
- **Historical test counts.** The 711-test figure and the five-run flaky
  distribution are now labelled historical at `a8b2fc6` (the v3.0.0 release
  commit) rather than presented as the current head, and current totals are
  referred to the per-OS head-matched TRX summary artifacts.
- **Claim-matrix navigation.** `docs/CLAIM_CALIBRATION_MATRIX.md` referenced
  README section names that no longer exist ("Key Mechanics", "Design
  principles", "MCTS Empirical Evaluation"); references now resolve to current
  headings and anchors, and host hardware specs were removed from matrix prose
  in favour of the benchmark artifacts.

### Not changed

`docs/VALIDATION_PLAN.md` remains intentionally scoped to a v2.3.2-judged
validation and is not restated here as a v3 independent-validation claim. No
version is bumped and no tag is created by this section.

## [3.0.0] - 2026-09-27

Source version 3.0.0. No release tag is created by this commit; the owner
publishes the release after audit.

### Added

- **External-agent protocol.** A normative, language-neutral wire contract for
  playing Lattice as an agent from an external process over stdin/stdout, in
  [`docs/EXTERNAL_AGENT_PROTOCOL.md`](docs/EXTERNAL_AGENT_PROTOCOL.md), with the
  governing decisions in
  [`docs/adr/0005-external-agent-wire-contract.md`](docs/adr/0005-external-agent-wire-contract.md).
  Protocol version `1` is independent of the product version. The wire types
  live in a new leaf project, `Protocol/`, which references nothing else in the
  repository.
- **`evaluate --agent-cmd` and `evaluate --agent-step-timeout-ms`.** An external
  agent process is scored in the candidate seat, under the same seeds, mirror,
  budget, statistics, and grading floor as the in-process study, so its rows are
  commensurable with published results. `--agent-step-timeout-ms` (default 5000)
  sets the per-step budget; the whole-match budget is computed from it as
  `step_timeout_ms × max_ticks + 30000` and is not caller-settable.
- **A conformant example agent.**
  [`examples/python/lattice_agent.py`](examples/python/lattice_agent.py) is a
  complete external agent in about 150 lines of standard-library Python, scored
  in [`examples/python/README.md`](examples/python/README.md). At commit
  `c8417f0` it **fails** the standard suite (mean paired delta −0.383, 95% CI
  [−0.672, −0.095], 30 seeds) and **passes** the bottleneck suite (+1.733, CI
  [+1.424, +2.042]), reproducing the same topology-conditional inversion the
  in-process MCTS study shows.
- **Failure accounting.** A closed, machine-readable set of **fourteen** reason
  codes partitioned by fault ([`docs/EXTERNAL_AGENT_PROTOCOL.md` §8](docs/EXTERNAL_AGENT_PROTOCOL.md),
  enumerated in [`Protocol/ProtocolReasons.cs:17-30`](Protocol/ProtocolReasons.cs)):
  thirteen are agent-attributable and one, `host_limit`, is host-attributable.
  The `evaluate` artifact gained `AgentFailures` (a count per reason code),
  `VoidRuns`, and `AgentForfeits`. Every agent-attributable failure is scored as
  a loss for the external agent; there is no retry.
- **Exit code 2 for `--agent-cmd` usage errors**, including a program that cannot
  be resolved or started, set in [`Cli/UsageError.cs:29`](Cli/UsageError.cs) and
  mapped at [`Cli/CliApp.cs:1326`](Cli/CliApp.cs). A usage error is reported
  before any match runs and writes no artifact.
- **`CHANGELOG.md` and `CITATION.cff`.**

### Changed

- **The forfeit rule.** A failed match forfeits: the row carries the external
  agent's score as `0` and the opponent's as it stood at the moment of failure.
  The forfeit is a property of the score on the row, never of its
  classification, so the match is still classified a win for the baseline side
  and the external agent's own outcome is still a loss. The external agent's
  partial scores are preserved in `AgentForfeits` for diagnosis and are not
  scored.
- **A failed match still occupies both mirrored seatings** for its seed, so the
  paired analyzer's mirror is never broken. A `host_limit` refusal is the one
  exception: it produces no match row, is counted as a void run rather than a
  loss, and reduces the number of valid seeds the grading floor counts.
- **Per-tick state authentication in `replay --verify`.** The verifier now
  recomputes a canonical SHA-256 digest of the complete simulation state at
  every tick and compares it to the digest recorded on the step line, on top of
  the existing per-step serialized `StepResult` comparison and final-summary
  re-computation. A schema-3 recording carries these digests; an older one
  verifies on step results alone and says so.
- **CI actions moved to Node 24.** `actions/checkout` v4→v5,
  `actions/setup-dotnet` v4→v5, `actions/upload-artifact` v4→v6,
  `actions/configure-pages` v5→v6, `actions/deploy-pages` v4→v5, and
  `actions/upload-pages-artifact` v3→v5. Runner images, jobs, steps, .NET
  versions, and test commands are unchanged. The Node 20 deprecation annotation
  is gone from the CI, Benchmarks, and Pages run summaries.
- **Stability work across the evidence base.** Site recordings were re-recorded
  on the current engine and gated in CI by a job that replays every recording
  under `site/`; the committed benchmark baselines were re-anchored on a single
  reference host after the choke-crossing fix (`28c89d3`, `41530ef`); the strict
  throughput gate is now armed only on a matching host class, with other hosts
  receiving an informational cross-host comparison; CLI JSON artifacts are
  LF-only and golden comparison is newline-agnostic; two Windows-only test
  failures and two flaky external-agent spawn tests were fixed; and drifted
  file:line citations were corrected.
- **Version 2.3.2 → 3.0.0** across the nine project `<Version>` elements and the
  CLI's reported version. The release workflow's tag-parity gate asserts that
  tag, project, and `CLI --version` agree.
- **Documentation.** The README is reorganised around a claim-and-proof table in
  which every claim carries a command a reader can run and the artifact it
  rests on, plus a bring-your-own-agent section and an explicit known-limitations
  section. `docs/reproduction_packet.md` and `docs/VALIDATION_PLAN.md` are
  labelled as scoped to the v2.3.2 release assets and checksums, which are
  deliberately unchanged.

### Known limitations

- A seed with one completed match and one void refuses the study: there is no
  drop-and-count yet. `Agents/PairedEvaluation.cs:100-105` throws when a seed is
  missing one side of the mirrored pair, and a void run produces no match row
  at all.
- Replay verifies the recorded environment actions; it cannot reproduce a
  nondeterministic external agent's decisions. Such an agent yields a different
  trajectory from the same seed on every run, and `replay --verify` still
  succeeds on each one.
- Exit code 2 is reserved for `--agent-cmd` usage errors. An unknown flag on
  `evaluate` still exits 1, like every other command and every other usage
  error, so the two are not the same channel.
- Timing bounds are calibrated on an Apple M1 and are far from binding: the
  default step budget is 5000 ms and the whole-match budget is
  `5000 × max_ticks + 30000` ms. They were not measured on slower runners.
- The `TempFile` test helper swallows an `IOException` on delete and leaves the
  file, so a refused delete is not a test failure. Real handle leaks are caught
  directly, by asserting the exclusive-open property, in
  `ExternalAgentFileHandleTests`.
- `benchmarks/throughput_summary.md` describes the regression gate as failing on
  a `>20%` drop while the README describes the enforced ratios of 0.75, 0.6, and
  0.85. The conflict is recorded, not reconciled; the workflow and comparator
  are authoritative.

## Prior versions

Release notes for `v1.0.0`, `v2.0.0`, `v2.1.0`, `v2.2.0`, `v2.3.0`, `v2.3.1`, and
`v2.3.2` are the published GitHub releases, and this project did not keep
in-repository release notes before 3.0.0. History is there and is not
paraphrased here:
<https://github.com/candavere/lattice/releases>.

The reproduction and independent-replication procedures that cover those
releases are [`docs/reproduction_packet.md`](docs/reproduction_packet.md) and
[`docs/VALIDATION_PLAN.md`](docs/VALIDATION_PLAN.md), both scoped to the
`v2.3.2` release assets and checksums.

[3.0.0]: https://github.com/candavere/lattice/compare/v2.3.2...v3.0.0
[Unreleased]: https://github.com/candavere/lattice/compare/v3.0.0...HEAD
