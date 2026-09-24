#!/usr/bin/env bash
# One-command setup for the Lattice repo (macOS/Linux, bash 3.2+).
#
# Verifies the SDK requirement from Cli/Lattice.Cli.csproj, restores, builds,
# runs the full test suite, then reproduces one committed claim so the run
# exits with evidence rather than a bare build. Idempotent, no sudo, never
# installs system software.
set -euo pipefail

CDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$CDIR"

CSPROJ="Cli/Lattice.Cli.csproj"
if [[ ! -f "$CSPROJ" ]]; then
  echo "error: $CSPROJ not found; run from the repository root" >&2
  exit 1
fi

# The SDK requirement is read from the project file, never hardcoded here.
TARGET_FRAMEWORK="$(sed -n 's:.*<TargetFramework>\([^<]*\)</TargetFramework>.*:\1:p' "$CSPROJ" | head -n1)"
REQUIRED_MAJOR="$(printf '%s' "$TARGET_FRAMEWORK" | sed 's/^net//; s/\..*//')"
ROLL_FORWARD="$(sed -n 's:.*<RollForward>\([^<]*\)</RollForward>.*:\1:p' "$CSPROJ" | head -n1)"

if [[ -z "$TARGET_FRAMEWORK" || -z "$REQUIRED_MAJOR" ]]; then
  echo "error: could not read TargetFramework from $CSPROJ" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: .NET SDK not found on PATH." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

SDK_VERSION="$(dotnet --version 2>/dev/null || true)"
if [[ -z "$SDK_VERSION" ]]; then
  echo "error: no .NET SDK installed (a runtime alone is not enough)." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

SDK_MAJOR="${SDK_VERSION%%.*}"
if (( SDK_MAJOR < REQUIRED_MAJOR )); then
  echo "error: this repository targets $TARGET_FRAMEWORK (RollForward=$ROLL_FORWARD) but the installed SDK is $SDK_VERSION." >&2
  echo "Install the .NET ${REQUIRED_MAJOR} SDK or newer: https://dotnet.microsoft.com/download/dotnet/${REQUIRED_MAJOR}" >&2
  exit 1
fi

echo "Using .NET SDK $SDK_VERSION (repo requires $TARGET_FRAMEWORK, RollForward=$ROLL_FORWARD)."

echo "Restoring dependencies..."
dotnet restore Lattice.sln

echo "Building in Release..."
dotnet build Lattice.sln -c Release --no-restore

echo "Running the full test suite..."
dotnet test Lattice.sln -c Release --no-build

echo "Reproducing one committed claim: golden trajectory replay..."
GOLDEN="Tests/fixtures/golden_trajectory.jsonl"
if dotnet run -c Release --project Cli -- replay "$GOLDEN" --verify; then
  echo "Reproduced: golden trajectory replay --verify, per-step results match the committed $GOLDEN"
else
  echo "Mismatch: replay --verify did not reproduce $GOLDEN; see the error above." >&2
  exit 1
fi

echo "setup.sh complete."