# Lattice Reproduction Challenge Packets

> **Two packets, one document.** The first, below, verifies the immutable
> release **v2.3.2**. The second, further down, verifies the immutable release
> **v3.0.0**. They are independent: each has its own tag, commit, asset sizes,
> SHA-256 checksums, expected outputs, and step counts, and each is graded
> against the release it names. No expected value in one section was edited to
> match the other. The scope section of each packet (§4 and §B4) states
> honestly what that release's experiments do and do not prove; the two
> statements differ, because the releases differ.

This packet is a self-contained, turnkey guide for an independent external
reviewer who wants to verify Lattice's empirical claims **using published
release assets alone**. No source checkout, build, or network access beyond
downloading the release is required for Experiments 1, 3, and 4. Experiment 2
offers both an asset-only path and a source-pinned path against the canonical
golden trajectory. The governing replication protocol — provenance capture,
pass/fail criteria, and result ingestion — is defined in
[`VALIDATION_PLAN.md`](VALIDATION_PLAN.md); this packet is its execution
procedures.

Everything in the first packet anchors to the immutable release **v2.3.2** — the remediated
release that supersedes `v2.3.1` with the parser hardening, deterministic
property and fuzz suites, and calibrated claims. The release tag and its
assets are permanent; if a defect is ever found, the project corrects
it by publishing a higher version, never by rewriting this one. `v2.3.1`
remains published and immutable alongside it; `v2.3.0` is historical and
untouched.

> **Scope of the first packet below: the v2.3.2 release only.** Every tag,
> commit, size, and SHA-256 in that section is a property of the published
> **v2.3.2** assets and is deliberately left unchanged. The repository is now at
> source version **3.0.0**, so `git describe`-style expectations and the
> `v2.3.2` checksums do **not** describe the current source tree.
>
> **This document now also carries a v3.0.0 packet**, further down, anchored to
> the published **v3.0.0** assets. The two sections are independent: read the
> one that matches the release you are verifying. No `v3.0.0` label in this
> repository attaches to the `v2.3.2` checksums, and none of the `v2.3.2`
> expected values were edited to match `v3.0.0`.

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

A verified download is still not runnable: the POSIX assets arrive without their
executable bit. Once the checksums above pass, set it on the checksum-verified
files in this directory:

```sh
chmod +x lattice-linux-x64
chmod +x lattice-osx-arm64
```

`lattice-win-x64.exe` needs no `chmod` on Windows. Section 2 refers to the
verified binary as `./lattice`; renaming or symlinking the platform's asset
under that name (`cp` or `ln -s`) carries the executable bit across, so the
`chmod` is not repeated there.

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
replay verified: 10 step(s) serialized-equivalent (seed 42, schema v2).
```

and exits `0`. Note the step count is `10`, not the requested `--steps 40`:
this seed's episode terminates early at `resources-exhausted` (reported by
`simulate` as `recorded 10 steps (resources-exhausted, winner: agent 0)`), and
the recording stops there. A lower count than `--steps` is correct behaviour,
not a divergence; the run is deterministic, so a repeat yields a byte-identical
file and the same `10`.

Source-pinned path (canonical golden trajectory, requires the pinned commit):

```sh
git clone https://github.com/candavere/lattice.git lattice-src
git -C lattice-src checkout v2.3.2
cd lattice-src
# The source tree ships no `lattice` executable — the only published binaries
# are the release assets of section 1.2. Supply the checksum-verified asset
# under that name before running the replay.
cp ../lattice-osx-arm64 ./lattice   # macOS; use your platform's asset
./lattice replay Tests/fixtures/golden_trajectory.jsonl --verify
```

The clone target above is deliberately `lattice-src`, not `lattice`: section 2
names the verified binary `./lattice`, and cloning into a directory that already
holds that file fails with `fatal: destination path 'lattice' already exists and
is not an empty directory.` Keeping the two names distinct lets the source
checkout and the verified binary sit side by side, and the `cp` above lands
inside the freshly cloned `lattice-src` directory.

Expected: `replay verified: 11 step(s) serialized-equivalent (seed 2024, schema
v2).` for `N` = the recorded step count (`11` at this commit), exit `0`.

Do not run this path against the current `main` branch: the golden trajectory
on `main` is schema v3, and the `v2.3.2` binary rejects it with
`error: Trajectory header schema version 3 is newer than the supported version
2.` and a non-zero exit. The `checkout v2.3.2` above is what keeps the two in
step.

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

---

## Packet B — Lattice v3.0.0

This is the second packet in this document, and it is **additive**: the `v2.3.2`
packet above is unchanged, including its own heading. Everything below is the
`v3.0.0` packet, with the same structure, the same four experiments, and the
same pass/fail discipline. Run the section that matches the release you are
verifying; the two do not share checksums, expected output, or a step count.

`v3.0.0` supersedes `v2.3.2`. It is the release that adds **per-tick state
authentication** to every recording: schema 3 makes a `StateHash` mandatory on
every step, and `replay --verify` recomputes and compares that digest as well as
the re-serialized `StepResult`. That is the substantive difference between the
two packets, and Section B4 states precisely what it does and does not prove.
`v2.3.2` remains published and immutable alongside it; nothing below modifies,
retags, or rewrites any earlier release.

> **Scope of this section: the v3.0.0 release only.** Every tag, commit, size,
> and SHA-256 below is a property of the published **v3.0.0** assets. The
> `v2.3.2` section above is deliberately left exactly as it was, and no
> `v3.0.0` label in this repository attaches to the `v2.3.2` checksums.

## B1. Target Release Integrity & Provenance

| Field | Value |
| --- | --- |
| Release tag | `v3.0.0` |
| Source commit | `c39d79b746e0f3aebce536dbe1cde387bd4e7991` |
| Release URL | `https://github.com/candavere/lattice/releases/tag/v3.0.0` |
| Runtime contract | Pure .NET 8 (`net8.0`) base class library; self-contained single-file binaries |
| Trajectory schema | `3` — a `StateHash` on every step line is mandatory |

### B1.1 Published assets and SHA-256 checksums

The checksums below are the exact published values: they are the digests
embedded in `SHA256SUMS.txt` attached to the release, they independently match
the SHA-256 download digests the GitHub release API reports for each asset, and
they were independently recomputed from the downloaded bytes. The release API
reports `immutable: true` for `v3.0.0`.

| Asset | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `lattice-linux-x64` | 67,258,364 | `f64d3eed40782dde8591b09aa971e0761ffc440dd002d09f9d54570a645e698d` |
| `lattice-osx-arm64` | 74,208,168 | `b14d30e286ea1237885c10d913d92630d14e4de5241245758f2c7ff3237e53ff` |
| `lattice-win-x64.exe` | 67,989,169 | `3a794d2233d2d1e2b8340a50fb8cb054719db536fe9fe25678af82f1d40b476f` |
| `SHA256SUMS.txt` | 254 | `a304fa9d370fa39a397d613ddb3fc45f428b4ad79e06c5e67277db3a79f7ab5f` |
| `sbom.json` | 18,304 | `f06f09b91a45708eb7a6593f7796e83a126d8089220912bf129937eccb4aa8c0` |

`v3.0.0` additionally attaches one `<asset>.sha256` per binary, captured by the
release job from the exact bytes it smoke-tested before uploading them. They are
a convenience cross-check, not the manifest of record:

| Asset | Size (bytes) | SHA-256 |
| --- | ---: | --- |
| `lattice-linux-x64.sha256` | 84 | `79a93538ca3fb150b0f675998e7413ab756aafaed1900d58240d536bb090d777` |
| `lattice-osx-arm64.sha256` | 84 | `404b51a13fa16d964cd16ebfd840beddb4df097c52bd73c211bc0f250806b2e8` |
| `lattice-win-x64.exe.sha256` | 86 | `a470f139f526b357a20af97e3243bc0a22f44b211b87585dedd1b1a27dd1dd4c` |

Direct download URLs (one per asset):

```
https://github.com/candavere/lattice/releases/download/v3.0.0/lattice-linux-x64
https://github.com/candavere/lattice/releases/download/v3.0.0/lattice-osx-arm64
https://github.com/candavere/lattice/releases/download/v3.0.0/lattice-win-x64.exe
https://github.com/candavere/lattice/releases/download/v3.0.0/SHA256SUMS.txt
https://github.com/candavere/lattice/releases/download/v3.0.0/sbom.json
```

### B1.2 Pre-execution integrity verification

Download all five assets into one directory, then verify against the manifest
before executing any binary. The commands are identical to the `v2.3.2`
section; the expected values are the table above, **not** the one above that.

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
table in B1.1). Any `FAILED`/mismatch means the downloaded bytes are not the
published assets — **do not execute them**. Independently, `sbom.json` may be
checked against its digest above; it is CycloneDX JSON declaring
`name: Lattice`, `version: 3.0.0`, and enumerates the .NET 8 dependency graph.

A verified download is still not runnable: the POSIX assets arrive without their
executable bit. Once the checksums pass, set it on the checksum-verified files:

```sh
chmod +x lattice-linux-x64
chmod +x lattice-osx-arm64
```

`lattice-win-x64.exe` needs no `chmod` on Windows. Section B2 refers to the
verified binary as `./lattice`; renaming or symlinking the platform's asset
under that name carries the executable bit across.

### B1.3 The source commit is part of the record

`v3.0.0` was built from commit `c39d79b746e0f3aebce536dbe1cde387bd4e7991`. A
reviewer who also wants source provenance can confirm the pinned commit in the
repository:

```sh
git ls-remote --tags https://github.com/candavere/lattice.git 'v3.0.0'
# expect: <object-id-of-tag>  refs/tags/v3.0.0
git show-ref --verify refs/tags/v3.0.0   # after cloning
git rev-parse 'v3.0.0^{commit}'
# expect: c39d79b746e0f3aebce536dbe1cde387bd4e7991
```

The tag is annotated, so `git ls-remote` reports the **tag object** id, not the
commit id. Peel it with `v3.0.0^{commit}` (as above) or `git cat-file -p`; the
`object` line of the tag is the commit you are verifying.

---

## B2. Step-by-Step Reproduction Experiments

All commands assume the verified binary for the reviewer's platform is named
`./lattice` (rename or symlink as needed; on Windows use
`.\lattice-win-x64.exe`). Exit codes: `0` = success, `1` = any bad argument or
runtime error.

### Experiment B1 — CLI & parity check

Commands:

```sh
./lattice --version
./lattice --help
```

Expected output:

1. `--version` prints exactly `3.0.0` on stdout and exits `0`.
2. `--help` prints the usage surface to stdout and exits `0`. The surface must
   list the seven commands `generate`, `simulate`, `render`, `analyze`,
   `replay`, `benchmark`, `evaluate`, plus `-h, --help` and `-v, --version`.
   `simulate` and `evaluate` each document two usage lines in `v3.0.0` (the
   base form and, for `evaluate`, the external-agent `--agent-cmd` form); the
   seven command names are what this check grades.

Pass if:

| Check | Criterion |
| --- | --- |
| Version parity | stdout is exactly `3.0.0` (no extra prefix/suffix) |
| Exit code | `0` for both commands |
| Command surface | All seven commands appear in `--help` |

Fail if: the version string differs from `3.0.0`, either command exits
non-zero, `--help` omits a documented command, or the binary is not the exact
hash-verified asset from Section B1.2.

### Experiment B2 — Replay equivalence, including the per-tick state digest

**What this proves.** Replaying a recorded trajectory reconstructs each tick
through the same pure step contract and produces, for every tick, both the
recorded tick's serialized `StepResult` **and** the recorded tick's end-of-tick
simulation-state digest. At schema 3 both are mandatory, and a recording that
carries a state hash on some steps but not others fails verification rather
than verifying with a notice.

**What this does NOT prove.** It is not raw file-byte identity, and it is not a
single digest over the whole episode: the digest is per tick, and the reviewer
never needs a summary hash to compare two runs.

Asset-only path (a trajectory recorded and verified by the released binary
itself):

```sh
./lattice simulate --seed 42 --steps 40 --out reviewer_fixture.jsonl --quiet
./lattice replay reviewer_fixture.jsonl --verify
```

Expected: the first command exits `0`, reports
`recorded 10 steps (resources-exhausted, winner: agent 0)`, and writes
`reviewer_fixture.jsonl`; `--verify` prints to stderr

```
replay verified: 10 step(s) serialized-equivalent, 10 state hash(es) matched (seed 42, schema v3).
```

and exits `0`. Note the step count is `10`, not the requested `--steps 40`:
this seed's episode terminates early at `resources-exhausted`, and the recording
stops there. A lower count than `--steps` is correct behaviour, not a
divergence; the run is deterministic, so a repeat yields a byte-identical file
and the same `10`. The trailing `10 state hash(es) matched` clause is the
`v3.0.0` addition — a `v2.3.2` binary cannot produce it.

Source-pinned path (canonical golden trajectory, requires the pinned commit):

```sh
git clone https://github.com/candavere/lattice.git lattice-src
git -C lattice-src checkout v3.0.0
cd lattice-src
# The source tree ships no `lattice` executable — the only published binaries
# are the release assets of section B1.2. Supply the checksum-verified asset
# under that name before running the replay.
cp ../lattice-osx-arm64 ./lattice   # macOS; use your platform's asset
./lattice replay Tests/fixtures/golden_trajectory.jsonl --verify
```

Expected: `replay verified: 12 step(s) serialized-equivalent, 12 state hash(es)
matched (seed 2024, schema v3).` for `N` = the recorded step count (`12` at this
commit), exit `0`.

The `v2.3.2` section above warns against running its binary against `main`,
because `main` carries a schema 3 golden trajectory that a schema 2 binary
rejects. At `v3.0.0` the two are in step and that hazard is gone: the `v3.0.0`
binary verifies the committed golden trajectory, and the recorded step count is
`12` (not the `11` the `v2.3.2` section reports — the fixture was re-recorded
when the state hash was added, and the reviewer should expect the number to
differ between the two sections rather than treat either as wrong).

Pass if: the verify line appears with matching seed, step count, and
`schema v3`, and the exit code is `0`.
Fail if: any step reports a serialized divergence or a state-hash divergence
(printed to stderr with a non-zero exit), a hand-corrupted or truncated
recording is silently accepted, or `--verify` exits `0` without the
`replay verified` line.

### Experiment B3 — Context-dependent evidence: "one policy, two conclusions"

The same MCTS evaluation subject (32 rollouts per action, depth 12) is graded
against the deterministic `ScoutCollectorAgent` baseline under two different
topology distributions, with mirrored seats per seed so spawn bias cancels out
of the paired delta. The two runs below are **complementary**, not
contradictory: they demonstrate that policy quality is a property of the
policy–environment interaction, not a universal ranking.

Supported command:

```sh
./lattice evaluate --scenario standard --seeds 10 --rollouts 32
./lattice evaluate --scenario bottleneck --seeds 10 --rollouts 32
```

(Standard topology is the default; `--scenario standard` is written explicitly
for clarity.)

Reference values below were produced by the published `lattice-osx-arm64` asset
at `--seeds 10 --rollouts 32`. They are orientation, not a byte-exact target:
per-seed deltas are deterministic, but the ordering of the statistics block and
the formatting are presentation details.

**Standard topology — MCTS underperforms Scout.** Observed on the `v3.0.0`
asset: mean paired delta **−0.85**, 95% CI **[−1.481, −0.219]** entirely below
0, 5 wins / 4 draws / 11 losses, mean contention saturation **0**. This matches
the direction of the committed 50-seed reference (mean deltas of −1.12 dev and
−1.25 held-out, CIs entirely below 0).

**Procedural bottleneck topology — MCTS outperforms Scout.** Observed on the
`v3.0.0` asset: mean paired delta **+2.60**, 95% CI **[1.910, 3.290]** entirely
above 0, 10 wins / 4 draws / 6 losses, mean contention saturation **≈0.136**
(about 14%). This matches the direction of the committed reference (mean deltas
of +2.03 dev and +2.60 held-out, CIs entirely above 0, contention about 16%).

Interpretation and grading:

- The default seed suite is held-out; `--seeds 10` runs the first 10 seeds.
  With fewer than 30 seeds the built-in decision rule reports the study as
  **not graded** — the reviewer judges the *direction* of the mean paired delta
  against the reference values above. Both runs at 10 seeds are expected to
  print `Passed: false` together with a `Not graded: 10 seeds is below the
  30-seed floor…` decision. That is the decision rule working, not a failure.
- Expect the standard run to report a **negative** mean paired Δ with the CI
  below 0, and the bottleneck run a **positive** mean paired Δ with the CI
  above 0.
- Expect non-zero mean contention saturation on the bottleneck run and `0` on
  the standard run.
- A single-run reversal of direction is a finding worth reporting (Section B3),
  not immediately a defect: these results express policy–environment
  interaction under contention, and should be understood as
  topology-conditional, never as a universal policy ranking.

### Experiment B4 — Structural performance smoke

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
the warm-up iteration's internal FNV-1a step digest anchor, or the harness fails
loudly instead of reporting timings.

The five case names are stable and are what a structural pass checks for:

| Case | Metric | Steps per iteration |
| --- | --- | ---: |
| `micro_raw_2agent` | steps | 100,000 |
| `facility_static_4agent` | steps | 100,000 |
| `dynamic_contention_4agent` | steps | 100,000 |
| `stress_topology_4agent` | steps | 100,000 |
| `policy_lookahead_mcts_32` | decisions | 100 |

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

## B3. External Environment Capture & Reporting Template

Reviewers are asked to paste this template — filled in — with their report to
make every pass/fail judgment independently checkable.

```markdown
## Reproduction report — Lattice v3.0.0

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
- `sbom.json` component name/version: `<Lattice / 3.0.0>`

### Experiment B1 — CLI & parity
- `./lattice --version` output and exit code:
  <exact output> / <exit code>
- `./lattice --help` command surface confirms all seven commands: `<yes/no>`

### Experiment B2 — Replay equivalence
- Verify line:
  `<replay verified: N step(s) serialized-equivalent, N state hash(es) matched (seed …, schema v3).>`
- Exit code: `0`
- Both clauses present (serialized-equivalent AND state hash(es) matched): `<yes/no>`

### Experiment B3 — Paired evaluation
- `standard --seeds 10 --rollouts 32`: mean Δ = <value>, CI = <value>,
  contention = <value>, per-seed rows = <count>
- `bottleneck --seeds 10 --rollouts 32`: mean Δ = <value>, CI = <value>,
  contention = <value>, per-seed rows = <count>
- Direction vs the B1.1/Experiment B3 reference: standard `<negative | positive | reversed>`,
  bottleneck `<positive | negative | reversed>`
- `Not graded` decision at 10 seeds observed: `<yes/no>`

### Experiment B4 — Structural smoke
- All five cases completed: `<yes/no>`
- Case names observed: `<per-case list>`
- Medians (steps/sec or decisions/sec): <per-case list>
- Execution duration: `<seconds>`
- Step-digest anchors reproduced: `<yes/no>`

### Discrepancies / mismatches
- <Describe any pass/fail criterion that did not hold, with exact output and
  commands.>

### Reporting
- File a public issue at:
  https://github.com/candavere/lattice/issues/new
  Title it `Reproduction report — Lattice v3.0.0` and paste the full template.
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

## B4. Honest scope of this packet

- **Equivalence claims are serialized `StepResult` plus per-tick state digest.**
  At `v3.0.0`, replay verification asserts two things per tick: the
  re-serialized `StepResult` and the recomputed end-of-tick state digest
  (`Trajectories/SimulationStateHash.cs:83`, mandatory from schema 3 per
  `Trajectories/TrajectoryModel.cs:30`, compared at
  `Trajectories/TrajectoryReplay.cs:176-183`). This is a real widening of the
  `v2.3.2` claim, and it is still **not** a single canonical digest over a whole
  episode, and still not raw file-byte identity.
- **The digest is a tamper detector, not an anti-tampering guarantee.** It
  authenticates that a replayed run reproduces the recorded state; anyone who
  can rewrite a `.jsonl` can recompute the digests. It evidences determinism and
  detects accidental or naive corruption. It is not a signature and not a
  provenance chain.
- **Performance claims are host-scoped.** The committed benchmark record
  describes one host; this packet's structural smoke pass cannot be used as an
  authoritative throughput adjudicator across arbitrary machines.
- **The step count in Experiment 2 differs from the `v2.3.2` section on
  purpose.** The golden fixture is schema 3 here and schema 2 above, so `12`
  and `11` are each correct for their own release. Do not "reconcile" them.
- **Release immutability is honored.** This section reads only the published
  `v3.0.0` tag and its attached assets; it does not modify, retag, or rewrite
  `v3.0.0`, `v2.3.2`, `v2.3.1`, or any earlier release. The `v2.3.2` section
  above is byte-for-byte unchanged.
- **The experiments do not cover the external-agent path.** `evaluate
  --agent-cmd` and the protocol 1 wire contract are exercised by the repository
  test suite, not by these four experiments; a reviewer reproducing this packet
  is verifying replay equivalence, evaluation, and throughput structure, not
  protocol conformance.