# Release readiness

Short checklist for a future release. Not a manual. `docs/CLI.md` and `README.md`
remain the usage references.

## Tested

- Source commit: `b6475aa51a7c11e9572d5e8113495da0e2edb117` (Stage 1 green, `Improve TUI readability and visual hierarchy`).
- Host actually tested: macOS arm64, .NET 10.0.401 / 10.0.12, `osx-arm64`.
- Clean checkout at that SHA restored, built, and packed with no new dependencies.
- Package: ID `lattice`, version `3.1.0`, SHA-256 `aabd4664f2c1b630a1c436b7566ce82a77c373ee7e0b0f091d87c6d7c2036caf`,
  `.nuspec` declares no dependencies, carries `Lattice.Tui.dll` and
  `Lattice.Protocol.dll` plus the other own assemblies. A `3.1.0` version does
  not mean untagged `main` changes were released.
- Throwaway install (`--tool-path`, local-only NuGet source, `--version 3.1.0`)
  reports `3.1.0` and matches `dotnet run` byte-for-byte on the golden replay
  (`stdout` empty, `stderr` identical, exit `0`) from outside the repo.
- Bare installed `lattice` opens a readable Launchpad in a real PTY; running a
  command, returning, and quitting restores the terminal (alternate screen
  exit, cursor visible). Redirected runs exit `2` with one line and no
  alternate screen; `--help` exits `0`. No new exit codes.
- Replay, live, and Ledger open and leave cleanly in a PTY (resize, ASCII,
  hide/show, nav/edit, Ctrl-C, invalid artifact). Existing lifecycle and
  three-second live-quit tests pass (111/111 in the focused set).
- CI must still pass on Ubuntu, Windows, and macOS; local PTY evidence covers
  only the host above. CI's noninteractive tool-parity smoke does not prove
  native interactive screenshots on all three.

## HEAD-matched CI

For the Stage 1 SHA, `candavere/lattice`:

- CI `37656757241`: Ubuntu/Windows/macOS build-test green, site gate green.
- Benchmarks `37656757303`: macOS gates green, Ubuntu structural smoke green.
- Pages `37656757179`: deploy green.

Read job steps and logs there; badges or other commits are not evidence.

## Result

- PASS: clean restore/build/pack, dependency-free nuspec with Tui+Protocol,
  throwaway install parity, PTY open/quit/restore, redirected refusal, ASCII,
  hide/show, Ctrl-C, lifecycle deadlines.
- FAIL: none found in this preparation. No production code was churned to fill
  the stage.
- NOT TESTED: Windows/macOS/Linux native interactive screenshots beyond the
  macOS host above; bare-metal throughput campaign (AC power is not a gate);
  external registry publication; independent validation.

## Future release pass/fail

A release is a pass only when: the tag, every project `<Version>`, and
`--version` agree (now ten projects including `Protocol` and `Tui`); the full
suite, golden replays, and workflow gates are green on the exact tag SHA;
the native assets for `linux-x64`, `win-x64`, `osx-arm64` smoke-test cleanly;
and the checklist above is re-run from a clean checkout at that SHA. Anything
less is NOT TESTED, not a pass.

## Owner decisions pending

- Intended version for the actual release.
- GitHub Release downloads versus additional .NET tool publication (nuget.org
  is a separate maintainer action; the `lattice` ID may already be taken).
- Package-name availability/ownership if a registry publication is later wanted.
- No new platforms: the native targets stay `linux-x64`, `win-x64`,
  `osx-arm64`.
