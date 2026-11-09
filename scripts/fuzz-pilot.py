#!/usr/bin/env python3
"""Bounded seeded mutation pilot over Lattice's three fuzz surfaces.

Surfaces:
  - recording parser: Tests/fixtures/golden_trajectory.jsonl via `replay --verify`
  - protocol framing: Protocol/ProtocolFraming + CliArgumentVectors sources
    (payload-level mutation of the recording header/step lines)
  - scenario loader: scenarios/dungeon-infiltration.json via `validate-scenario`

Oracle: a mutant is ACCEPTED only when the CLI itself accepts it (exit 0).
Any other outcome is a declared error (exit 1 or 2) unless the process
crashes unhandled (nonzero without one of the three documented exit codes)
or times out. Findings are written as JSON: {surface, cases, accepted,
declared_errors, crashes, timeouts}.

This is random mutation, NOT coverage-guided fuzzing. The committed
in-process harness (Tests/Fuzz) remains the CI gate; this pilot exercises
the shipped CLI entry points as black-box oracles.

Usage:
  python3 scripts/fuzz-pilot.py --lattice-bin <path> --out <dir> [--cases N]
"""

import argparse, hashlib, json, os, random, subprocess, sys, time
from pathlib import Path


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def run(bin_path, args, timeout=20):
    try:
        r = subprocess.run(
            [bin_path, *args], capture_output=True, text=True, timeout=timeout
        )
        return r.returncode, r.stdout, r.stderr
    except subprocess.TimeoutExpired:
        return None, "", "timeout"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lattice-bin", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cases", type=int, default=50)
    args = ap.parse_args()
    repo = Path(__file__).resolve().parent.parent
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(20261110)
    findings = {"seed": 20261110, "surfaces": {}}

    # Self-check: corrupt StateHash must be verify-caught.
    golden = repo / "Tests/fixtures/golden_trajectory.jsonl"
    lines = golden.read_text(encoding="utf-8").splitlines()
    import re

    corrupted = []
    for ln in lines:
        if '"StateHash"' in ln:
            ln = re.sub(r'"StateHash":\s*"[^"]*"', '"StateHash": "deadbeef"', ln)
            corrupted.append(ln)
        else:
            corrupted.append(ln)
    Path(out, "selfcheck.jsonl").write_text(
        "\n".join(corrupted) + "\n", encoding="utf-8"
    )
    code, _, _ = run(
        args.lattice_bin, ["replay", str(out / "selfcheck.jsonl"), "--verify"]
    )
    selfcheck = "caught" if code == 1 else f"FAILED code={code}"
    findings["selfcheck_statehash"] = selfcheck
    if selfcheck != "caught":
        print(f"self-check failed: {selfcheck}", file=sys.stderr)
        return 1

    # Surface 1: recording parser.
    corpus = out / "corpus-recording"
    corpus.mkdir(exist_ok=True)
    counts = {"accepted": 0, "declared": 0, "crashes": 0, "timeouts": 0}
    for i in range(args.cases):
        data = bytearray(golden.read_bytes())
        for _ in range(rng.randint(1, 8)):
            data[rng.randrange(len(data))] = rng.randrange(256)
        p = corpus / f"mut-{i:04d}.jsonl"
        p.write_bytes(bytes(data))
        code, _, err = run(args.lattice_bin, ["replay", str(p), "--verify"])
        if code == 0:
            counts["accepted"] += 1
        elif code in (1, 2):
            counts["declared"] += 1
        elif code is None:
            counts["timeouts"] += 1
        else:
            counts["crashes"] += 1
    findings["surfaces"]["recording-parser"] = counts

    # Surface 3 first (scenario loader is text-clean).
    corpus2 = out / "corpus-scenario"
    corpus2.mkdir(exist_ok=True)
    scenario = repo / "scenarios/dungeon-infiltration.json"
    counts2 = {"accepted": 0, "declared": 0, "crashes": 0, "timeouts": 0}
    for i in range(args.cases):
        data = bytearray(scenario.read_bytes())
        for _ in range(rng.randint(1, 8)):
            data[rng.randrange(len(data))] = rng.randrange(256)
        p = corpus2 / f"mut-{i:04d}.json"
        p.write_bytes(bytes(data))
        code, _, _ = run(args.lattice_bin, ["validate-scenario", str(p)])
        if code == 0:
            counts2["accepted"] += 1
        elif code in (1, 2):
            counts2["declared"] += 1
        elif code is None:
            counts2["timeouts"] += 1
        else:
            counts2["crashes"] += 1
    findings["surfaces"]["scenario-loader"] = counts2

    # Surface 2: protocol framing (mutate first line = handshake envelope).
    corpus3 = out / "corpus-protocol"
    corpus3.mkdir(exist_ok=True)
    counts3 = {"accepted": 0, "declared": 0, "crashes": 0, "timeouts": 0}
    for i in range(args.cases):
        first = bytearray(lines[0].encode("utf-8"))
        for _ in range(rng.randint(1, 6)):
            first[rng.randrange(len(first))] = rng.randrange(256)
        mutated = [first.decode("utf-8", errors="surrogateescape")] + lines[1:]
        p = corpus3 / f"mut-{i:04d}.jsonl"
        p.write_text(
            "\n".join(mutated) + "\n", encoding="utf-8", errors="surrogateescape"
        )
        code, _, _ = run(args.lattice_bin, ["replay", str(p), "--verify"])
        if code == 0:
            counts3["accepted"] += 1
        elif code in (1, 2):
            counts3["declared"] += 1
        elif code is None:
            counts3["timeouts"] += 1
        else:
            counts3["crashes"] += 1
    findings["surfaces"]["protocol-framing"] = counts3

    (out / "findings.json").write_text(
        json.dumps(findings, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(findings, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
