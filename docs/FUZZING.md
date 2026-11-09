# Fuzzing harness and pilot (issue #13)

Two layers, honestly distinguished:

## Committed in-process harness (CI gate)

`Tests/Fuzz/` runs seeded mutation inside the test suite on every CI OS leg:

- Recording parser: `Tests/Fuzz/TrajectoryReaderFuzzTests.cs` over trajectory fixtures.
- Protocol framing / CLI surface: `Tests/Fuzz/CliArgumentFuzzTests.cs` + `CliArgumentVectors.cs`.
- Scenario / rules loader: `Tests/Fuzz/DynamicMapRuleFuzzTests.cs`.

Each target uses `FuzzHarness.Run` (per-case budget 10 s, base+case seeds always
reported, declared errors classified via `ExceptionContract`). Run:

```sh
dotnet test Tests/Lattice.Tests.csproj -c Release --filter FullyQualifiedName~Fuzz
```

Toolchain: .NET 10 SDK only. No coverage instrumentation: this is **seeded
random mutation**, not coverage-guided fuzzing.

## Black-box pilot harness (`scripts/fuzz-pilot.py`)

`scripts/fuzz-pilot.py` exercises the **shipped CLI entry points** as oracles
(`replay --verify` for recording + protocol framing, `validate-scenario` for the
scenario loader). It is also random mutation (fixed seed `20261110`), **not**
coverage-guided feedback; nothing here uses libFuzzer/AFL-style coverage.

- Seeds/corpus: fixture `Tests/fixtures/golden_trajectory.jsonl` and
  `scenarios/dungeon-infiltration.json`, mutated with a fixed RNG seed, so the
  corpus regenerates byte-identically (outputs are written outside the repo).
- Oracle: exit 0 = accepted; exit 1/2 = declared error; any other exit or a
  host timeout = crash/timeout finding.
- Self-check: a `StateHash` corruption must be verify-caught (exit 1) before
  any mutant is trusted; the pilot aborts otherwise.
- Bounded run: `--cases N` per surface (default 50), 20 s per-case host timeout.
- Findings format: JSON `{seed, surfaces:{...}, selfcheck_statehash}`.

Run:

```sh
dotnet build Cli/Lattice.Cli.csproj -c Release
python3 scripts/fuzz-pilot.py --lattice-bin Cli/bin/Release/net10.0/lattice --out "$TMPDIR/fuzz-out" --cases 50
```

## Pilot result (2026-11-09 session, local)

50 cases per surface = 150 mutants. Self-check: `caught`. Each surface:
1 accepted, 49 declared errors, 0 crashes, 0 timeouts. Accepted mutants are
benign byte edits the parsers still validly accept (e.g., whitespace/comments
outside validated fields); they are recorded, not treated as defects. Full
output is in the PR's local evidence; rerun the script to reproduce.

The old 240-mutant scratch pilot is **not** coverage-guided and is not claimed
as such. A true coverage-guided campaign (libFuzzer/AFL route) and independent
security review remain deferred pending a funding/scope decision; vulnerability
reports flow through `SECURITY.md`.
