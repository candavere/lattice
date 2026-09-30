#!/usr/bin/env bash
# Verify (or re-record) the artifacts the GitHub Pages viewer plays.
#
# The site viewer is evidence, so every byte it draws has to be accounted for by
# a command a reader can run. The two recordings are NOT in the same category,
# and pretending otherwise is how a gate starts lying:
#
#   site/infiltration.jsonl  Regenerable. Recorded by the command below, so
#                            re-deriving it must reproduce the committed bytes
#                            exactly. That is the drift check.
#
#   site/demo.jsonl          Deliberately pinned at trajectory schema v3. It is
#                            the committed proof that a pre-perception recording
#                            is read, replayed and verified *without* the reader
#                            nagging it about fields that did not exist yet
#                            (TrajectoryReplay.NoPerceptionNotice, gated on
#                            DecisionTimePerceptionVersion = 4). Re-recording it
#                            at the current schema would make it a current-schema
#                            recording that records no perception, which the
#                            reader is REQUIRED to flag. It is therefore checked
#                            for integrity and rendered for drift, never
#                            regenerated and never rewritten.
#
#   ./scripts/regenerate-site-demos.sh            # re-record the regenerable pair
#   ./scripts/regenerate-site-demos.sh --check    # verify only; never writes
#
# --check re-derives the regenerable artifacts into a scratch directory and
# byte-compares them. It exits non-zero and prints the first differing path on
# drift. It deliberately does NOT rewrite anything: a drifting committed artifact
# is a finding for a human to look at, not something a gate should quietly paper
# over by re-recording.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# The documented settings for the regenerable recording.
INFILTRATION_SEED=42
INFILTRATION_SCENARIO=infiltration
INFILTRATION_STEPS=100

CONFIG="Release"
LATTICE=(dotnet run -c "$CONFIG" --project Cli --no-build)

CHECK=0
case "${1:-}" in
  --check) CHECK=1 ;;
  "") ;;
  *) echo "usage: $0 [--check]" >&2; exit 2 ;;
esac

if [ "$CHECK" -eq 1 ]; then
  OUT="$(mktemp -d)"
  trap 'rm -rf "$OUT"' EXIT
else
  OUT="site"
fi

# ---------------------------------------------------------------- regenerable
echo "recording infiltration (seed $INFILTRATION_SEED, $INFILTRATION_SCENARIO, $INFILTRATION_STEPS ticks)"
"${LATTICE[@]}" simulate --seed "$INFILTRATION_SEED" \
  --scenario "$INFILTRATION_SCENARIO" --steps "$INFILTRATION_STEPS" \
  --out "$OUT/infiltration.jsonl" >/dev/null
"${LATTICE[@]}" render --trajectory "$OUT/infiltration.jsonl" \
  --format svg --out "$OUT/infiltration.svg" >/dev/null

# ---------------------------------------------------------- deliberately pinned
# Rendered from the COMMITTED demo recording, never from a fresh simulation.
echo "rendering demo.svg from the committed demo.jsonl (schema v3, pinned)"
"${LATTICE[@]}" render --trajectory site/demo.jsonl \
  --format svg --out "$OUT/demo.svg" >/dev/null

if [ "$CHECK" -eq 0 ]; then
  echo
  echo "wrote site/infiltration.jsonl and site/infiltration.svg"
  echo "site/demo.jsonl was NOT touched: it is deliberately pinned at schema v3."
  exit 0
fi

status=0

# The pinned recording must still replay and still verify with no notices: that is
# the property it exists to hold, and it is invisible to a byte comparison.
echo "verifying the pinned demo.jsonl replays with no perception notice"
if "${LATTICE[@]}" replay site/demo.jsonl --verify 2>&1 | grep -q "no recorded perception"; then
  echo "NOTICE   site/demo.jsonl replayed but reported a missing-perception notice" >&2
  status=1
else
  echo "OK       site/demo.jsonl replays clean, no notices"
fi

for f in infiltration.jsonl infiltration.svg demo.svg; do
  if [ ! -f "site/$f" ]; then
    echo "MISSING  committed site/$f" >&2
    status=1
  elif cmp -s "$OUT/$f" "site/$f"; then
    echo "OK       site/$f"
  else
    echo "DRIFT    site/$f differs from a fresh regeneration" >&2
    echo "         committed $(wc -c < "site/$f") bytes, regenerated $(wc -c < "$OUT/$f") bytes" >&2
    status=1
  fi
done

if [ "$status" -ne 0 ]; then
  cat >&2 <<'MSG'

The committed site artifacts are stale. Nothing was rewritten: re-record
deliberately, review the diff, and confirm no recording changed behaviourally,
then commit the data and its documentation together.

  ./scripts/regenerate-site-demos.sh

site/demo.jsonl is deliberately NOT part of that rewrite; see the header of this
script for why it stays at schema v3.
MSG
fi
exit "$status"
