# Committed acceptance evidence: S10 + reference (committed subset)

This directory holds the **committed subset** chosen for issue #11: the exact
bytes behind the S10 local-model and reference slices of
`docs/V4_CANDIDATE_ACCEPTANCE.md`, so a reviewer can check the acceptance
claims byte-for-byte without access to the maintainer's machine.

## Contents

| File | What it is |
|---|---|
| `local.json` | S10 evaluation JSON (1 dev seed, 2 matches, mean delta -4, `Not graded`, 1 win / 1 loss) |
| `local-sidecar.json` | S10 comparability tuple (loopback host `127.0.0.1:8081`, model `qwen05`, temperature 0.0, prompt/parser `acceptance`/`1`) |
| `ref.json` | Reference evaluation JSON (stub agent) for the same seed |
| `local-episodes/` | 2 S10 recordings (200 steps each), replay-verified at publication |
| `ref-episodes/` | 2 reference recordings (66 steps each), replay-verified at publication |
| `S10.log` | S10 CLI output |
| `S10.replay.log` | `replay --verify` output (200 steps, 200 state hashes each, schema v5) |
| `rows.tsv` | Slice table (S1–S13) |
| `ACCEPTANCE.md` | Acceptance report |
| `SHA256SUMS.txt` | SHA-256 manifest covering every file above (relative paths) |

Verify: `shasum -a 256 -c SHA256SUMS.txt` from this directory (11 files OK).
Replay: `dotnet run -c Release --project Cli -- replay <file> --verify` for
each episode (expect 200 steps for `local-episodes/`, 66 for `ref-episodes/`).

## Selection and exclusions

Selected: S10 + reference artifacts needed to check the acceptance claims.
Excluded from publication:

- Build/test logs (`S2.log`/`S3.log`) containing local absolute build paths.
- Slice logs `S1`–`S9`/`S12` (not needed for the S10 claim; absolute paths in some).
- The self-hosted Laya run bundle (`LAYA_VERIFICATION_REPORT.md`): not present in
  this checkout; recorded only as a reported result in the acceptance doc, not
  published here.
- Local-only remainder: the rest of `.evidence/` stays **untracked and unmoved**
  in the maintainer's working tree. Originals are read-only sources; this
  directory contains copies, byte-identical for selected files (hashes match the
  local baseline).

## Privacy audit

No keys, tokens, or passwords were found. The `--api-key-env` mentions name an
unset test variable only, never a value. The only endpoint metadata is the
loopback `127.0.0.1:8081` recorded in the sidecar and evaluation JSONs.
No private user paths are in the selected files (build logs with `/Users/...`
paths were excluded).

## Committed-subset policy

`.evidence/` remains the acceptance script's default **local** scratch
directory and stays untracked. Publication happens only by copying selected
files into `evidence/` (this directory) via a normal PR with a SHA-256 manifest.
Anything not under `evidence/` is local-only and not part of the public
checkout. Future acceptance runs must not leave ambiguous state: either an
`evidence/` commit with a fresh manifest, or local-only by default.
