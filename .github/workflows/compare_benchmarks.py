#!/usr/bin/env python3
"""Compare a freshly measured benchmark artifact against the committed baseline.

Strict mode (--strict-if-matching): fail if ANY workload's median throughput
fell below --threshold x the baseline median (default 0.8, i.e. a 20%
regression) or a workload disappeared from the matrix. Strict mode ONLY arms
when the current host fingerprint matches the baseline's - the OS family, the
architecture, AND the .NET runtime major version. The runtime constraint is
deliberate: the committed baseline is recorded under a specific runtime, and
cross-runtime throughput deltas (e.g. .NET 8 vs the .NET 10 baseline) are
measurement artifacts, not regressions - so a mismatched runtime degrades to
the informational cross-host mode instead of failing the gate.

Per-workload thresholds: a flat threshold is wrong for heterogeneous runner
dispersion. Tight sub-microsecond loops (e.g. micro_raw_2agent) are dominated
by CPU frequency scaling and shared-runner virtualization jitter, so they
legitimately wander well below a stable-workload threshold. Pass
--per-workload-threshold NAME=RATIO (repeatable) to grant those workloads a
relaxed tolerance while keeping the strict 0.8x gate for the stable matrix
(e.g. facility_static_4agent, stress_topology_4agent,
policy_lookahead_mcts_32). Any workload without an explicit override uses the
global --threshold.

Cross-host mode: never fails, but prints the side-by-side table and flags any
workload below the threshold, so OS/arch/runtime-mismatched runs still surface
drift.
"""

import argparse
import json
import sys


def os_family(descriptor: str) -> str:
    return descriptor.split()[0] if descriptor else ""


def runtime_major(runtime: str) -> str:
    return runtime.split()[1].split(".")[0] if len(runtime.split()) > 1 else ""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", help="committed reference artifact (JSON)")
    parser.add_argument("current", help="freshly measured artifact (JSON)")
    parser.add_argument("--threshold", type=float, default=0.8,
                        help="min ratio current/reference median before a "
                             "regression is flagged (default 0.8 = 20%% drop)")
    parser.add_argument("--strict-if-matching", action="store_true",
                        help="fail on a regression only when the host "
                             "fingerprint (OS family + architecture + runtime "
                             "major) matches the baseline")
    parser.add_argument("--per-workload-threshold", action="append",
                        default=[], metavar="NAME=RATIO",
                        help="override the threshold for one workload as "
                             "NAME=RATIO; repeatable (e.g. "
                             "micro_raw_2agent=0.7). Unlisted workloads keep "
                             "the global --threshold.")
    args = parser.parse_args()

    per_workload: dict[str, float] = {}
    for item in args.per_workload_threshold:
        if "=" not in item:
            parser.error(
                f"--per-workload-threshold expects NAME=RATIO, got {item!r}")
        name, _, ratio = item.partition("=")
        try:
            per_workload[name] = float(ratio)
        except ValueError:
            parser.error(
                f"--per-workload-threshold ratio must be numeric, got {ratio!r}"
                f" for {name!r}")

    with open(args.baseline, encoding="utf-8") as handle:
        baseline = json.load(handle)
    with open(args.current, encoding="utf-8") as handle:
        current = json.load(handle)

    base_meta, curr_meta = baseline["Metadata"], current["Metadata"]
    base_work = {w["Name"]: w for w in baseline["Workloads"]}
    curr_work = {w["Name"]: w for w in current["Workloads"]}

    missing = set(base_work) - set(curr_work)
    if missing:
        print(f"::error::current artifact is missing workloads: "
              f"{sorted(missing)}")
        return 1
    extra = set(curr_work) - set(base_work)
    if extra:
        print(f"note: current artifact added workloads not in the baseline: "
              f"{sorted(extra)}")

    os_arch_matched = \
        os_family(base_meta["Os"]) == os_family(curr_meta["Os"]) and \
        base_meta["Architecture"] == curr_meta["Architecture"]
    runtime_matched = runtime_major(base_meta["Runtime"]) == \
        runtime_major(curr_meta["Runtime"])
    matched = os_arch_matched and runtime_matched
    strict = matched and args.strict_if_matching

    print(f"baseline host: {base_meta['Os']} / {base_meta['Architecture']} "
          f"/ {base_meta['Runtime']}")
    print(f"current host : {curr_meta['Os']} / {curr_meta['Architecture']} "
          f"/ {curr_meta['Runtime']}")
    if not os_arch_matched:
        print("fingerprint: OS family + architecture do not match the "
              "baseline (strict gate not armed)")
    elif not runtime_matched:
        print("fingerprint: runtime major differs from the baseline "
              f"({runtime_major(base_meta['Runtime'])} vs "
              f"{runtime_major(curr_meta['Runtime'])}) - strict gate not armed "
              "to avoid measuring a runtime delta as a regression")
    else:
        print(f"fingerprint match: {matched}  (strict gate armed: {strict})")
    if per_workload:
        print(f"per-workload thresholds: "
              f"{', '.join(f'{n}={v:g}' for n, v in sorted(per_workload.items()))}")
    print()

    header = (f"{'workload':<28}{'baseline median':>16}"
              f"{'current median':>16}{'ratio':>9}")
    print(header)
    failed = []
    for name in sorted(base_work):
        base_value = base_work[name]["MedianThroughputPerSecond"]
        curr_value = curr_work[name]["MedianThroughputPerSecond"]
        ratio = curr_value / base_value if base_value else 0.0
        print(f"{name:<28}{base_value:>16,.0f}{curr_value:>16,.0f}{ratio:>9.3f}")
        threshold = per_workload.get(name, args.threshold)
        if curr_value < threshold * base_value:
            failed.append((name, base_value, curr_value, ratio, threshold))

    if extra:
        for name in sorted(extra):
            print(f"{name:<28}{'-':>16}"
                  f"{curr_work[name]['MedianThroughputPerSecond']:>16,.0f}"
                  f"{'(new)':>9}")

    if strict:
        if failed:
            print()
            print("::error::workload regression on a matching host "
                  "(threshold factor; default 0.8 = 20% drop):")
            for name, base_value, curr_value, ratio, threshold in failed:
                print(f"  {name}: {curr_value:,.0f} vs baseline "
                      f"{base_value:,.0f} ({ratio:.1%}, allowed {threshold:g}x)")
            return 1
        print()
        print("strict comparison passed: no workload regressed beyond the "
              "threshold.")
    else:
        print()
        print("cross-host comparison (informational): the strict gate needs "
              "a matching OS family + architecture + .NET runtime major.")
    return 0


if __name__ == "__main__":
    sys.exit(main())