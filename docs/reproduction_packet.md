# Lattice v2.3.2 — Immutable-Release Reproduction Challenge Packet

This packet is a self-contained, turnkey guide for an independent external
reviewer who wants to verify Lattice's empirical claims **using published
release assets alone**. No source checkout, build, or network access beyond
downloading the release is required for Experiments 1, 3, and 4. Experiment 2
offers both an asset-only path and a source-pinned path against the canonical
golden trajectory. The governing replication protocol — provenance capture,
pass/fail criteria, and result ingestion — is defined in
[`VALIDATION_PLAN.md`](VALIDATION_PLAN.md); this packet is its execution
procedures.

Everything below anchors to the immutable release **v2.3.2** — the remediated
release that supersedes `v2.3.1` with the parser hardening, deterministic
property and fuzz suites, and calibrated claims. The release tag and its
assets are permanent; if a defect is ever found, the project corrects
it by publishing a higher version, never by rewriting this one. `v2.3.1`
remains published and immutable alongside it; `v2.3.0` is historical and
untouched.

---

## 1. Target Release Integrity & Provenance

| Field | Value |
| --- | --- |
| Release tag | `v2.3.2` |
| Source commit | `4f7816fa5594f7097d6b2978c6c626553d075326` |
| Release URL | `https://github.com/candavere/lattice/releases/tag/v2.3.2` |
| Runtime contract | Pure .NET 8 (`net8.0`) base class library; self-contained single-file binaries |

### 1.1 Published assets and SHA-256 checksums

The checksums below are the exact published values: they are the digests
embedded in `SHA256SUMS.txt` attached to the release, and they independently
match the SHA-256 download digests that the GitHub release API reports for each
asset. The release API reports `immutable: true` for `v2.3.2`.

| Asset | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `lattice-linux-x64` | 67,130,095 | `a9457f805c7a2a68472828994b9c8291e3bd37c76073213d9e7b8fc0edd71876` |
| `lattice-osx-arm64` | 74,075,672 | `55fbc448fa50949b1e97a3d2e1956a95c56d440f963587464bf5a64361918f07` |
| `lattice-win-x64.exe` | 67,853,590 | `879539e0fb6749a7795b2b2d6e07a7f1f8a7a809ea1c147f0e7148948c4f3afd` |
| `SHA256SUMS.txt` | 254 | `9bf4955bd14385e275617c2cd46125a3c4ed8b538778ede1049163a749d9939c` |
| `sbom.json` | 18,304 | `b0446e15817479f3b134ef3584f6a8964a7d4ab05d14ce4616c8e41ba4e83a30` |

Direct download URLs (one per asset):

```
https://github.com/candavere/lattice/releases/download/v2.3.2/lattice-linux-x64
https://github.com/candavere/lattice/releases/download/v2.3.2/lattice-osx-arm64
https://github.com/candavere/lattice/releases/download/v2.3.2/lattice-win-x64.exe
https://github.com/candavere/lattice/releases/download/v2.3.2/SHA256SUMS.txt
https://github.com/candavere/lattice/releases/download/v2.3.2/sbom.json
```

### 1.2 Pre-execution integrity verification

Download all five assets into one directory, then verify against the manifest
before executing any binary.

Linux (and any host with GNU coreutils):

```sh
sha256sum -c SHA256SUMS.txt
```

macOS (ships `shasum`; GNU `sha256sum` is available via coreutils):

```sh
shasum -a 256 -c SHA256SUMS.txt
```

Windows PowerShell:

```powershell
Get-FileHash lattice-linux-x64, lattice-osx-arm64, lattice-win-x64.exe -Algorithm SHA256 | Format-Table
```

Pass criterion: `lattice-linux-x64: OK`, `lattice-osx-arm64: OK`, and
`lattice-win-x64.exe: OK` (or, on Windows, three matching lines that equal the
table above). Any `FAILED`/mismatch means the downloaded bytes are not the
published assets — **do not execute them**. Independently, `sbom.json` may be
checked against its digest above; it enumerates the .NET dependency graph and
must contain only BCL/runtime components.

### 1.3 The source commit is part of the record

`v2.3.2` was built from commit `4f7816fa5594f7097d6b2978c6c626553d075326`. A
reviewer who also wants source provenance can confirm the pinned commit in the
repository:

```sh
git ls-remote --tags https://github.com/candavere/lattice.git 'v2.3.2'
# expect: <object-id-of-tag>  refs/tags/v2.3.2
git show-ref --verify refs/tags/v2.3.2   # after cloning
git rev-parse 'v2.3.2^{commit}'
# expect: 4f7816fa5594f7097d6b2978c6c626553d075326
```

---

## 2. Step-by-Step Reproduction Experiments

All commands assume the verified binary for the reviewer's platform is named
`./lattice` (rename or symlink as needed; on Windows use
`.\lattice-win-x64.exe`). Exit codes: `0` = success, `1` = any bad argument or
runtime error.

### Experiment 1 — CLI & parity check

Commands:

```sh
./lattice --version
./lattice --help
```

Expected output:

1. `--version` prints exactly `2.3.2` on stdout and exits `0`.
2. `--help` prints the usage surface to stdout and exits `0`. The surface must
   list the seven commands `generate`, `simulate`, `render`, `analyze`,
   `replay`, `benchmark`, `evaluate`, plus `-h, --help` and `-v, --version`.

Pass if:

| Check | Criterion |
| --- | --- |
| Version parity | stdout is exactly `2.3.2` (no extra prefix/suffix) |
| Exit code | `0` for both commands |
| Command surface | All seven commands appear in `--help` |

Fail if: the version string differs from `2.3.2`, either command exits
non-zero, `--help` omits a documented command, or the binary is not the exact
hash-verified asset from Section 1.2.

### Experiment 2 — Per-step serialized `StepResult` replay equivalence

**What this proves.** Replaying a recorded trajectory reconstructs each tick
through the same pure step contract and produces the recorded tick's
serialized `StepResult` exactly — tick-by-tick serialized equivalence between
the recorded episode and the replayed engine (`TrajectoryReplay.Verify`).

**What this does NOT prove.** This is not canonical simulation-state hash tree
equivalence. No canonical state hash currently exists: replay verifies
serialized `StepResult` equality, never a single state digest. It also does not
assert raw file-byte identity — the reviewer's machine never uses a digest to
summarize the episode.

Asset-only path (a trajectory recorded and verified by the released binary
itself):

```sh
./lattice simulate --seed 42 --steps 40 --out reviewer_fixture.jsonl --quiet
./lattice replay reviewer_fixture.jsonl --verify
```

Expected: the first command exits `0` and writes `reviewer_fixture.jsonl`;
`--verify` prints to stderr

```
replay verified: 40 step(s) serialized-equivalent (seed 42, schema v2).
```

and exits `0`.

Source-pinned path (canonical golden trajectory, requires the pinned commit):

```sh
git clone https://github.com/candavere/lattice.git lattice
git -C lattice checkout v2.3.2
cd lattice
./lattice replay Tests/fixtures/golden_trajectory.jsonl --verify
```

Expected: `replay verified: N step(s) serialized-equivalent (seed 2024, schema
v2).` for `N` = the recorded step count, exit `0`.

Pass if: the verify line appears with matching seed/schema and exit code `0`.
Fail if: any step reports a serialized divergence (printed to stderr with a
non-zero exit), a hand-corrupted or truncated recording is silently accepted,
or `--verify` exits `0` without the `replay verified` line.

### Experiment 3 — Context-dependent evidence: "one policy, two conclusions"

The same MCTS evaluation subject (32 rollouts per action, depth 12) is graded
against the deterministic `ScoutCollectorAgent` baseline under two different
topology distributions, with mirrored seats per seed so spawn bias cancels out
of the paired delta. The two experiments below are **complementary**, not
contradictory: they demonstrate that policy quality is a property of the
policy–environment interaction, not a universal ranking.

Supported command:

```sh
./lattice evaluate --scenario standard --seeds 10 --rollouts 32
./lattice evaluate --scenario bottleneck --seeds 10 --rollouts 32
```

(Standard topology is the default; `--scenario standard` is written
explicitly for clarity.)

**Standard topology — MCTS underperforms Scout.** With 32 rollouts the MCTS
policy loses the paired comparison on open generated facility layouts. The
committed reference records mean paired deltas of −1.12 (dev suite) and −1.25
(held-out suite) with 95% CIs entirely below 0.

**Procedural bottleneck topology — MCTS outperforms Scout.** Under capacity-1
choke contention the same budget wins the paired comparison: committed mean
paired deltas of +2.03 (dev) and +2.60 (held-out) with 95% CIs entirely above
0 and non-zero mean contention saturation (about 16%).

Interpretation and grading:

- The default seed suite is held-out; `--seeds 10` runs the first 10 seeds.
  With fewer than 30 seeds the built-in decision rule reports the study as
  **not graded** — the reviewer judges the *direction* of the mean paired
  delta against the committed reference above.
- Expect the standard run to report a **negative** mean paired Δ and the
  bottleneck run to report a **positive** mean paired Δ, each with bounded,
  reproducible per-seed dispersion.
- Expect non-zero mean contention saturation on the bottleneck run and `0` on
  the standard run.
- A single-run reversal of direction is a finding worth reporting (Section 3),
  not immediately a defect: these results express policy–environment
  interaction under contention, and should be understood as
  topology-conditional, never as a universal policy ranking.

### Experiment 4 — Structural performance smoke

Command (note: the measured-iteration flag is `--runs`; `--iterations` is not
part of the Lattice CLI):

```sh
./lattice benchmark --steps 100000 --runs 5
```

The benchmark runs the five-case workload matrix (raw stepping, facility,
dynamic topology, stress topology, and MCTS lookahead). Expected: one table row
per case with positive median throughput (steps/sec for the raw cases,
decisions/sec for the MCTS case), per-step latency percentiles, allocation
counters, GC deltas — and, critically, every measured iteration must reproduce
the warm-up iteration's internal FNV-1a step digest anchor, or the harness
fails loudly instead of reporting timings.

Host-variance expectations — read this before judging:

- Shared and virtualized runners exhibit clock jitter, CPU frequency scaling,
  and scheduler noise. Median throughput almost certainly differs from any
  committed reference record, which was host-scoped (CPU model, cores, RAM,
  OS, runtime, GC mode all stamped into the artifact metadata).
- This experiment is a **structural smoke pass**, not an authoritative
  throughput gate. Pass/fail is about structure and integrity, not absolute
  numbers.

Pass if: all five cases complete, each reports positive median throughput, and
no iteration reports a step-digest mismatch (which would mean an episode went
off-script). Fail if: a case errors out, a digest mismatch is reported, or a
median is non-positive.

---

## 3. External Environment Capture & Reporting Template

Reviewers are asked to paste this template — filled in — with their report to
make every pass/fail judgment independently checkable.

```markdown
## Reproduction report — Lattice v2.3.2

### Host environment
- CPU model: `<model string>`
- Architecture: `<x86_64 | arm64>`
- Core count: `<n>`
- RAM: `<n GiB>`
- OS name/version: `<name, version>`
- Binary platform: `<linux-x64 | osx-arm64 | win-x64>`
- .NET runtime patch (from `./lattice --version` … n/a; the binary is
  self-contained. If running the dotted deployment under a local SDK, record
  `dotnet --info` runtime): `<version>`

### Integrity verification
- `sha256sum -c SHA256SUMS.txt` (or platform equivalent) result:
  `<lattice-linux-x64: OK / FAILED>`, `<lattice-osx-arm64: OK / FAILED>`,
  `<lattice-win-x64.exe: OK / FAILED>`
- `sbom.json` digest check: `<OK / FAILED>`

### Experiment 1 — CLI & parity
- `./lattice --version` output and exit code:
  <exact output> / <exit code>
- `./lattice --help` command surface confirms all seven commands: `<yes/no>`

### Experiment 2 — Replay equivalence
- Verify line: `<replay verified: N step(s) serialized-equivalent (seed …, schema v2).>`
- Exit code: `0`

### Experiment 3 — Paired evaluation
- `standard --seeds 10 --rollouts 32`: mean Δ = <value>, CI = <value>,
  contention = <value>, per-seed rows = <count>
- `bottleneck --seeds 10 --rollouts 32`: mean Δ = <value>, CI = <value>,
  contention = <value>, per-seed rows = <count>
- Direction vs committed reference: standard `<negative | positive | reversed>`,
  bottleneck `<positive | negative | reversed>`

### Experiment 4 — Structural smoke
- All five cases completed: `<yes/no>`
- Medians (steps/sec or decisions/sec): <per-case list>
- Execution duration: `<seconds>`
- Step-digest anchors reproduced: `<yes/no>`

### Discrepancies / mismatches
- <Describe any pass/fail criterion that did not hold, with exact output and
  commands.>

### Reporting
- File a public issue at:
  https://github.com/candavere/lattice/issues/new
  Title it `Reproduction report — Lattice v2.3.2` and paste the full template.
```

Notes for honest reporting:

- Do not normalize or scale observed timings "to compare fairly" — report them
  verbatim and let the host metadata explain differences.
- A tolerated failure (e.g. a mismatched checksum you cannot resolve, or a
  reversed delta direction) is more valuable than a silent pass; it goes
  straight to the issue above.
- The reference artifacts for comparison live in the repository at
  `benchmarks/mcts_evaluation_results.json` (standard) and
  `benchmarks/bottleneck_evaluation_results.json` (bottleneck), with the
  narrative reading in `benchmarks/throughput_summary.md`.

## 4. Honest scope of this packet

- **Equivalence claims are serialized `StepResult`-scoped.** Replay verification
  never claims more than per-step serialized equivalence. There is no canonical
  simulation-state hash tree, and this packet does not provide one.
- **Performance claims are host-scoped.** The committed benchmark record
  describes one host; this packet's structural smoke pass cannot be used as an
  authoritative throughput adjudicator across arbitrary machines.
- **Release immutability is honored.** This packet reads only the published
  `v2.3.2` tag and its attached assets; it does not modify, retag, or rewrite
  `v2.3.2`, `v2.3.1`, or any earlier release.