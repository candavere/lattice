#!/usr/bin/env python3
"""Compare a freshly measured benchmark artifact against the committed baseline.

Two classifications of a run are supported:

Strict comparison (default):
  Fail if ANY workload's median throughput fell below --threshold x the
  baseline median (default 0.8, i.e. a 20% regression) or a workload
  disappeared from the matrix. Strict mode ONLY arms when the current host
  fingerprint matches the baseline's - the OS family, the architecture, the
  .NET runtime major version, the logical core count, AND the CPU model
  string (trimmed, case-folded). The runtime constraint is deliberate: the
  committed baseline is recorded under a specific runtime, and cross-runtime
  throughput deltas (e.g. .NET 8 vs the .NET 10 baseline) are measurement
  artifacts, not regressions - so a mismatched runtime degrades to the
  informational cross-host mode instead of failing the gate. The
  core-count/CPU-model constraint is deliberate too: the committed baseline
  is a bare-metal machine, while GitHub-hosted runners virtualize the CPU
  with a different core count - an unlike host class whose throughput
  shortfall is a measurement artifact, not a regression. A missing or empty
  host field on either side is a mismatch as well (cross-host,
  informational), never a silent strict pass.

  Per-workload thresholds are derived statistically from the committed
  baseline's own dispersion, not hand-picked. For every workload whose baseline
  entry carries both MedianThroughputPerSecond and StdDevThroughputPerSecond, the
  allowed ratio is calibrated as:

      allowed = clamp(1 - --cv-multiplier * (StdDev/Median), --cv-floor,
                      --cv-cap)

  i.e. a workload may drop until it is ~3 (--cv-multiplier) baseline standard
  deviations below the baseline median. A tight sub-microsecond loop
  (e.g. micro_raw_2agent) has a high committed CV (frequency-scaling +
  shared-runner jitter), so 1-3*CV grants it a proportionally relaxed tolerance;
  a stable matrix workload (facility_static_4agent, stress_topology_4agent,
  policy_lookahead_mcts_32) has a low CV, so its gate stays near 1-3*CV of a
  small dispersion. This removes the need for any manual per-workload ratio.
  Workloads whose baseline lacks dispersion fall back to the global
  --threshold. An explicit --per-workload-threshold NAME=RATIO still wins over
  the derivation when one is supplied.

  Cross-host mode: never fails, but prints the side-by-side table and flags any
  workload below the threshold, so runs on an unlike host class - a different
  OS family, architecture, runtime major, core count, CPU model, or
  incomplete host metadata - still surface drift.

Smoke classification (--smoke):
  Structural only. A smoke artifact (short --steps/--runs, possibly a
  different runtime/host) has no throughput comparable to the full-protocol
  baseline, so no throughput ratio is ever adjudicated. The pass verifies the
  workload matrix (every baseline workload present, an added workload is a
  note not a failure) and that every reported workload median - and the host
  metadata - is sane, then exits 0. A broken or degenerate artifact exits 1
  with one ::error:: per problem so the GitHub annotation names the culprit.

The macOS regression gate (.github/workflows/benchmarks.yml) wraps strict
comparisons in a retry: its first pass runs with --no-annotations so a transient
shared-runner dip is re-measured and cleared before it can fail the job, and
::error:: workflow commands are only emitted once a dip persists across the
retry pass.
"""

import argparse
import json
import sys
from dataclasses import dataclass


def os_family(descriptor: str) -> str:
    return descriptor.split()[0] if descriptor else ""


def runtime_major(runtime: str) -> str:
    return runtime.split()[1].split(".")[0] if len(runtime.split()) > 1 else ""


def parse_per_workload(items, error=ValueError):
    """Turn --per-workload-threshold NAME=RATIO items into a dict.

    Malformed items raise ``error(message)`` (ValueError by default so the
    parsing rule is unit-testable; main() swaps in the argparse parser.error).
    """
    parsed = {}
    for item in items:
        if "=" not in item:
            raise error(
                f"--per-workload-threshold expects NAME=RATIO, got {item!r}")
        name, _, ratio = item.partition("=")
        try:
            parsed[name] = float(ratio)
        except ValueError:
            raise error(
                f"--per-workload-threshold ratio must be numeric, got {ratio!r}"
                f" for {name!r}")
    return parsed


def load_benchmark(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def derive_allowed(base_work, cv_multiplier, cv_floor, cv_cap):
    """Per-workload allowed ratio from the baseline's own dispersion.

    allowed_w = 1 - k*CV, clamped to [cv_floor, cv_cap]. A busy shared runner
    inflates a tight sub-microsecond workload's committed StdDev (frequency
    scaling + virtualization jitter), so 1 - k*CV grants that workload a
    proportionally relaxed tolerance, while a stable matrix workload keeps a
    tight gate.
    """
    derived = {}
    for name, workload in base_work.items():
        median = workload.get("MedianThroughputPerSecond") or 0.0
        stddev = workload.get("StdDevThroughputPerSecond") or 0.0
        cv = stddev / median if median > 0 else 0.0
        derived[name] = min(cv_cap,
                            max(cv_floor, 1.0 - cv_multiplier * cv))
    return derived


FINGERPRINT_FIELDS = ("Os", "Architecture", "Runtime", "Cores", "Cpu")


def describe_fingerprint(meta) -> str:
    """One-line printable form of the strict fingerprint fields.

    Prints every field the strict gate compares - OS descriptor, architecture,
    runtime, logical core count, CPU model - with '?' for a missing value, so
    a mismatched host class is visible in the log on every run.
    """
    cores = meta.get("Cores")
    cpu = _string_component(meta, "Cpu")
    return (f"{meta.get('Os') or '?'} / {meta.get('Architecture') or '?'} / "
            f"{meta.get('Runtime') or '?'} / "
            f"{cores if cores is not None else '?'} cores / {cpu or '?'}")


def _string_component(meta, key):
    """The trimmed descriptor for `key`, or None when missing or empty."""
    value = meta.get(key)
    if value is None:
        return None
    text = str(value).strip()
    return text or None


@dataclass(frozen=True)
class HostFingerprint:
    os_matched: bool
    arch_matched: bool
    runtime_matched: bool
    cores_matched: bool
    cpu_matched: bool
    missing_fields: tuple = ()

    @property
    def os_arch_matched(self) -> bool:
        return self.os_matched and self.arch_matched

    @property
    def matched(self) -> bool:
        return (self.os_arch_matched and self.runtime_matched
                and self.cores_matched and self.cpu_matched)


def fingerprint(base_meta, curr_meta) -> HostFingerprint:
    """Strict host fingerprint: OS family + architecture + .NET runtime major
    + logical cores + CPU model string (trimmed, case-folded).

    Every component must be present on BOTH sides and equal: a missing or
    empty host field is a mismatch (cross-host classification), never a
    silent strict pass. The bare-metal baseline and a GitHub-hosted runner
    can share OS family + architecture, so the cores and CPU model components
    are what keep the strict gate from arming on an unlike host class.
    """
    missing = []
    for meta, side in ((base_meta, "baseline"), (curr_meta, "current")):
        for key in FINGERPRINT_FIELDS:
            if key == "Cores":
                if meta.get("Cores") is None:
                    missing.append(f"{side}.{key}")
            elif _string_component(meta, key) is None:
                missing.append(f"{side}.{key}")

    base_os = _string_component(base_meta, "Os")
    curr_os = _string_component(curr_meta, "Os")
    os_matched = bool(base_os and curr_os) and \
        os_family(base_os).casefold() == os_family(curr_os).casefold()

    base_arch = _string_component(base_meta, "Architecture")
    curr_arch = _string_component(curr_meta, "Architecture")
    arch_matched = bool(base_arch and curr_arch) and \
        base_arch.casefold() == curr_arch.casefold()

    base_rt = runtime_major(base_meta.get("Runtime") or "")
    curr_rt = runtime_major(curr_meta.get("Runtime") or "")
    runtime_matched = bool(base_rt and curr_rt) and base_rt == curr_rt

    base_cores = base_meta.get("Cores")
    curr_cores = curr_meta.get("Cores")
    cores_matched = (base_cores is not None and curr_cores is not None
                     and base_cores == curr_cores)

    base_cpu = _string_component(base_meta, "Cpu")
    curr_cpu = _string_component(curr_meta, "Cpu")
    cpu_matched = bool(base_cpu and curr_cpu) and \
        base_cpu.casefold() == curr_cpu.casefold()

    return HostFingerprint(os_matched, arch_matched, runtime_matched,
                           cores_matched, cpu_matched, tuple(missing))


@dataclass(frozen=True)
class WorkloadCompare:
    name: str
    base_value: float
    curr_value: float
    ratio: float
    threshold: float
    failed: bool


@dataclass(frozen=True)
class ThroughputComparison:
    rows: list
    missing: set
    extra: set


def compare_throughput(base_work, curr_work, per_workload, derived,
                       global_threshold) -> ThroughputComparison:
    """Adjudicate throughput ratios for every baseline workload.

    Threshold resolution order is preserved verbatim from the CLI: an explicit
    --per-workload-threshold wins; otherwise the statistically derived
    tolerance applies; otherwise the global --threshold is the floor.
    """
    missing = set(base_work) - set(curr_work)
    extra = set(curr_work) - set(base_work)
    rows = []
    for name in sorted(base_work):
        if name not in curr_work:
            continue
        base_value = base_work[name]["MedianThroughputPerSecond"]
        curr_value = curr_work[name]["MedianThroughputPerSecond"]
        ratio = curr_value / base_value if base_value else 0.0
        threshold = per_workload.get(name, derived.get(name, global_threshold))
        rows.append(WorkloadCompare(
            name, base_value, curr_value, ratio, threshold,
            curr_value < threshold * base_value))
    return ThroughputComparison(rows, missing, extra)


@dataclass(frozen=True)
class SmokeCheck:
    workloads: list
    errors: list
    notes: list


def smoke_check(baseline, current) -> SmokeCheck:
    """Structural classification of a smoke artifact.

    No throughput ratio is adjudicated: a smoke pass may run tiny budgets on a
    different host/runtime than the full-protocol baseline, so its throughput is
    not comparable. Only the matrix shape and sane, positive reported values are
    checked, so a degenerate artifact can never be mistaken for a clean gate.
    """
    base_meta, curr_meta = baseline["Metadata"], current["Metadata"]
    base_work = {w["Name"]: w for w in baseline["Workloads"]}
    curr_work = {w["Name"]: w for w in current["Workloads"]}

    errors = []
    notes = []
    missing = set(base_work) - set(curr_work)
    extra = set(curr_work) - set(base_work)
    if missing:
        errors.append(f"current artifact is missing workloads: {sorted(missing)}")
    if extra:
        notes.append(f"current artifact added workloads not in the baseline: "
                     f"{sorted(extra)}")

    rows = []
    for name in sorted(curr_work):
        workload = curr_work[name]
        median = workload.get("MedianThroughputPerSecond") or 0.0
        latency = workload.get("MedianStepLatencyMicros") or 0.0
        rows.append((name, median, latency, workload.get("AllocationsPerStepBytes")))
        if median <= 0:
            errors.append(
                f"{name}: MedianThroughputPerSecond must be positive, got {median:g}.")
        if latency <= 0:
            errors.append(
                f"{name}: MedianStepLatencyMicros must be positive, got {latency:g}.")

    if (curr_meta.get("RamBytes") or 0) <= 0:
        errors.append("Metadata.RamBytes must be positive.")
    if (curr_meta.get("Cores") or 0) <= 0:
        errors.append("Metadata.Cores must be positive.")
    for key in ("Os", "Architecture", "Runtime"):
        if not str(curr_meta.get(key) or "").strip():
            errors.append(f"Metadata.{key} is empty.")

    return SmokeCheck(rows, errors, notes)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", help="committed reference artifact (JSON)")
    parser.add_argument("current", help="freshly measured artifact (JSON)")
    parser.add_argument("--threshold", type=float, default=0.8,
                        help="min ratio current/reference median before a "
                             "regression is flagged (default 0.8 = 20%% drop). "
                             "Serves as the floor/fallback for workloads whose "
                             "committed baseline carries no dispersion data.")
    parser.add_argument("--cv-multiplier", type=float, default=3.0,
                        help="how many committed-baseline standard deviations "
                             "(coefficient of variation) of headroom each "
                             "workload gets before it is flagged (default 3). "
                             "The per-workload allowed ratio is derived as "
                             "1 - multiplier * StdDev/Median from the "
                             "baseline artifact, clamped to --cv-floor.."
                             "--cv-cap.")
    parser.add_argument("--cv-floor", type=float, default=0.55,
                        help="lower clamp for the derived per-workload allowed "
                             "ratio (default 0.55); no derived tolerance may "
                             "go below this even for an extremely jittery "
                             "workload.")
    parser.add_argument("--cv-cap", type=float, default=0.95,
                        help="upper clamp for the derived per-workload allowed "
                             "ratio (default 0.95); no derived tolerance may "
                             "demand tighter than this purely from baseline "
                             "sample noise.")
    parser.add_argument("--strict-if-matching", action="store_true",
                        help="fail on a regression only when the host "
                             "fingerprint (OS family + architecture + runtime "
                             "major + logical cores + CPU model) matches the "
                             "baseline")
    parser.add_argument("--per-workload-threshold", action="append",
                        default=[], metavar="NAME=RATIO",
                        help="override the threshold for one workload as "
                             "NAME=RATIO; repeatable (e.g. "
                             "micro_raw_2agent=0.7). Unlisted workloads keep "
                             "the global --threshold.")
    parser.add_argument("--no-annotations", action="store_true",
                        help="print failures as plain lines instead of "
                             "::error:: workflow commands. The macOS gate runs "
                             "its first comparison pass with this flag so a "
                             "transient dip that clears on the retry pass does "
                             "not leave misleading error annotations.")
    parser.add_argument("--smoke", action="store_true",
                        help="classify the current artifact structurally "
                             "(workload matrix + positive medians + sane host "
                             "metadata) instead of adjudicating throughput "
                             "ratios. For a bounded smoke pass whose host or "
                             "budget differs from the full-protocol baseline.")
    return parser


def main(argv=None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    per_workload = parse_per_workload(
        args.per_workload_threshold, error=parser.error)

    baseline = load_benchmark(args.baseline)
    current = load_benchmark(args.current)

    def emit_error(line: str) -> None:
        print(line if args.no_annotations else f"::error::{line}")

    base_meta, curr_meta = baseline["Metadata"], current["Metadata"]
    base_work = {w["Name"]: w for w in baseline["Workloads"]}
    curr_work = {w["Name"]: w for w in current["Workloads"]}

    if args.smoke:
        fp = fingerprint(base_meta, curr_meta)
        check = smoke_check(baseline, current)
        print(f"smoke classification: structural only - this pass carries no "
              f"throughput ratio against the full-protocol baseline")
        print(f"baseline fingerprint: {describe_fingerprint(base_meta)}")
        print(f"current fingerprint : {describe_fingerprint(curr_meta)}")
        print(f"fingerprint match: {fp.matched}")
        print()

        header = (f"{'workload':<28}{'median throughput':>18}"
                  f"{'median latency μs':>18}{'alloc/step':>14}")
        print(header)
        for name, median, latency, alloc in check.workloads:
            alloc_str = "—" if alloc is None else f"{alloc:,.0f}"
            print(f"{name:<28}{median:>16,.0f}{latency:>15,.3f}{alloc_str:>14}")
        for note in check.notes:
            print(f"note: {note}")

        if check.errors:
            print()
            emit_error("smoke classification failed:")
            for err in check.errors:
                emit_error(err)
            return 1
        print()
        print("smoke classification passed: full workload matrix present, "
              "medians positive, host metadata sane.")
        return 0

    missing = set(base_work) - set(curr_work)
    extra = set(curr_work) - set(base_work)
    if missing:
        emit_error(
            f"current artifact is missing workloads: {sorted(missing)}")
        return 1
    if extra:
        print(f"note: current artifact added workloads not in the baseline: "
              f"{sorted(extra)}")

    derived = derive_allowed(base_work, args.cv_multiplier, args.cv_floor,
                             args.cv_cap)
    comparison = compare_throughput(
        base_work, curr_work, per_workload, derived, args.threshold)

    fp = fingerprint(base_meta, curr_meta)
    matched = fp.matched
    strict = matched and args.strict_if_matching

    print(f"baseline fingerprint: {describe_fingerprint(base_meta)}")
    print(f"current fingerprint : {describe_fingerprint(curr_meta)}")
    if fp.missing_fields:
        print("fingerprint: host metadata is incomplete on one side "
              f"(missing or empty: {', '.join(fp.missing_fields)}) - "
              "classified cross-host (informational), never a silent "
              "strict pass")
    elif not fp.os_arch_matched:
        print("fingerprint: OS family + architecture do not match the "
              "baseline (strict gate not armed)")
    elif not fp.runtime_matched:
        print("fingerprint: runtime major differs from the baseline "
              f"({runtime_major(base_meta.get('Runtime') or '')} vs "
              f"{runtime_major(curr_meta.get('Runtime') or '')}) - strict gate "
              "not armed to avoid measuring a runtime delta as a regression")
    elif not fp.cores_matched:
        print("fingerprint: logical cores differ from the baseline "
              f"({base_meta.get('Cores')} vs {curr_meta.get('Cores')}) - "
              "classified cross-host (informational): an unlike host class "
              "is not a throughput regression")
    elif not fp.cpu_matched:
        print("fingerprint: CPU model differs from the baseline "
              f"({base_meta.get('Cpu')!r} vs {curr_meta.get('Cpu')!r}) - "
              "classified cross-host (informational)")
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
    for row in comparison.rows:
        print(f"{row.name:<28}{row.base_value:>16,.0f}"
              f"{row.curr_value:>16,.0f}{row.ratio:>9.3f}")
        if row.failed:
            failed.append((row.name, row.base_value, row.curr_value,
                           row.ratio, row.threshold))

    if comparison.extra:
        for name in sorted(comparison.extra):
            print(f"{name:<28}{'-':>16}"
                  f"{curr_work[name]['MedianThroughputPerSecond']:>16,.0f}"
                  f"{'(new)':>9}")

    if strict:
        if failed:
            print()
            emit_error("workload regression on a matching host "
                       "(threshold factor; the per-workload allowed ratio "
                       "follows):")
            for name, base_value, curr_value, ratio, threshold in failed:
                emit_error(f"{name}: {curr_value:,.0f} vs baseline "
                           f"{base_value:,.0f} ({ratio:.1%}, allowed "
                           f"{threshold:g}x)")
            return 1
        print()
        print("strict comparison passed: no workload regressed beyond the "
              "threshold.")
    else:
        print()
        print("cross-host comparison (informational): the strict gate needs "
              "a matching OS family + architecture + .NET runtime major + "
              "logical cores + CPU model.")
    return 0


if __name__ == "__main__":
    sys.exit(main())