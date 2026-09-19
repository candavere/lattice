#!/usr/bin/env python3
"""Unit tests for the benchmark decision comparator.

`compare_benchmarks.py` makes the pass/fail decisions behind the CI regression
gate (and the structural smoke classification), so its logic is exercised here
directly: fingerprint arming, CV-derived tolerances, per-workload overrides,
strict vs informational exit codes, :annotation: output, and the structural
smoke classifier.

Run with:  python3 -m unittest discover -s .github/workflows -p 'test_*.py'
"""

import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

import compare_benchmarks as cb


CANONICAL_NAMES = [
    "micro_raw_2agent",
    "facility_static_4agent",
    "dynamic_contention_4agent",
    "stress_topology_4agent",
    "policy_lookahead_mcts_32",
]

# (name, median throughput, stddev throughput, median latency µs, alloc/step)
BASELINE_ROWS = [
    ("micro_raw_2agent", 650_000, 80_000, 1.33, 3_213),
    ("facility_static_4agent", 350_000, 20_000, 2.50, 4_630),
    ("dynamic_contention_4agent", 230_000, 15_000, 4.00, 7_107),
    ("stress_topology_4agent", 9_000, 800, 100.12, 106_309),
    ("policy_lookahead_mcts_32", 390, 27, 4_903, 11_465_776),
]


def make_workloads(rows):
    output = []
    for name, median, stddev, latency, alloc in rows:
        output.append({
            "Name": name,
            "MedianThroughputPerSecond": median,
            "StdDevThroughputPerSecond": stddev,
            "MedianStepLatencyMicros": latency,
            "AllocationsPerStepBytes": alloc,
        })
    return output


def make_artifact(rows=None, os_="macOS 27.0.0", arch="Arm64",
                  runtime=".NET 10.0.10", ram=8_589_934_592, cores=8):
    return {
        "Metadata": {
            "Commit": "deadbeef",
            "Timestamp": "2026-09-18T20:02:47",
            "Runtime": runtime,
            "Configuration": "Release",
            "Os": os_,
            "Architecture": arch,
            "Cores": cores,
            "RamBytes": ram,
        },
        "Workloads": make_workloads(rows if rows is not None else BASELINE_ROWS),
    }


def below_threshold_artifact(os_="macOS 27.0.0", arch="Arm64",
                             runtime=".NET 10.0.10", factor=0.5):
    rows = [(name, max(median * factor, 1.0), stddev, latency, alloc)
            for name, median, stddev, latency, alloc in BASELINE_ROWS]
    return make_artifact(rows=rows, os_=os_, arch=arch, runtime=runtime)


class ComparatorTestCase(unittest.TestCase):
    """Baseline: invokes cb.main() against real temp artifacts and captures
    stdout + exit code."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)

    def _write(self, filename, artifact):
        path = os.path.join(self._tmp.name, filename)
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(artifact, handle)
        return path

    def run_comparator(self, baseline, current, *flags):
        base_path = self._write("baseline.json", baseline)
        curr_path = self._write("current.json", current)
        argv = [base_path, curr_path] + list(flags)
        stdout, stderr = io.StringIO(), io.StringIO()
        rc = 0
        with redirect_stdout(stdout), redirect_stderr(stderr):
            try:
                rc = cb.main(argv)
            except SystemExit as exc:  # argparse parse-error paths
                rc = exc.code if exc.code is not None else 0
        return rc, stdout.getvalue(), stderr.getvalue()


class ParsingTests(unittest.TestCase):
    def test_os_family_extracts_first_token(self):
        self.assertEqual(cb.os_family("macOS 27.0.0"), "macOS")
        self.assertEqual(cb.os_family("Ubuntu 24.04.1 LTS"), "Ubuntu")
        self.assertEqual(cb.os_family("Windows Server 2022"), "Windows")

    def test_os_family_empty(self):
        self.assertEqual(cb.os_family(""), "")

    def test_runtime_major_extracts_dotnet_major(self):
        self.assertEqual(cb.runtime_major(".NET 10.0.10"), "10")
        self.assertEqual(cb.runtime_major(".NET 8.0.414"), "8")

    def test_runtime_major_degenerate(self):
        self.assertEqual(cb.runtime_major(""), "")
        self.assertEqual(cb.runtime_major("net8.0"), "")

    def test_parse_per_workload_ok(self):
        parsed = cb.parse_per_workload(["micro_raw_2agent=0.6", "stress=0.5"])
        self.assertEqual(parsed, {"micro_raw_2agent": 0.6, "stress": 0.5})

    def test_parse_per_workload_missing_equals(self):
        with self.assertRaises(ValueError):
            cb.parse_per_workload(["micro_raw_2agent0.6"])

    def test_parse_per_workload_non_numeric(self):
        with self.assertRaises(ValueError):
            cb.parse_per_workload(["micro_raw_2agent=abc"])


class FingerprintTests(unittest.TestCase):
    def test_matching_fingerprint_is_strict_eligible(self):
        base = make_artifact()
        curr = make_artifact()
        fp = cb.fingerprint(base["Metadata"], curr["Metadata"])
        self.assertTrue(fp.os_arch_matched)
        self.assertTrue(fp.runtime_matched)
        self.assertTrue(fp.matched)

    def test_os_family_mismatch_disarms(self):
        base = make_artifact()
        curr = make_artifact(os_="Ubuntu 24.04.1 LTS", runtime=".NET 10.0.10")
        fp = cb.fingerprint(base["Metadata"], curr["Metadata"])
        self.assertFalse(fp.os_arch_matched)
        self.assertFalse(fp.matched)

    def test_architecture_mismatch_disarms(self):
        base = make_artifact()
        curr = make_artifact(arch="x64")
        fp = cb.fingerprint(base["Metadata"], curr["Metadata"])
        self.assertFalse(fp.os_arch_matched)
        self.assertFalse(fp.matched)

    def test_runtime_major_mismatch_disarms(self):
        base = make_artifact()
        curr = make_artifact(runtime=".NET 8.0.414")
        fp = cb.fingerprint(base["Metadata"], curr["Metadata"])
        self.assertTrue(fp.os_arch_matched)
        self.assertFalse(fp.runtime_matched)
        self.assertFalse(fp.matched)


class DerivedThresholdTests(unittest.TestCase):
    def test_derived_is_one_minus_k_times_cv(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 10_000}}
        derived = cb.derive_allowed(base_work, cv_multiplier=3.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.7)  # 1 - 3 * 0.1

    def test_derived_floors_highly_jittery_workloads(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 40_000}}
        derived = cb.derive_allowed(base_work, cv_multiplier=3.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.55)  # 1 - 3*0.4 = -0.2 → floor

    def test_derived_caps_rock_stable_workloads(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 500}}
        derived = cb.derive_allowed(base_work, cv_multiplier=3.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.95)  # 1 - 3*0.005 → cap

    def test_derived_respects_custom_multiplier(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 10_000}}
        derived = cb.derive_allowed(base_work, cv_multiplier=2.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.8)  # 1 - 2 * 0.1

    def test_derived_without_dispersion_clamps_to_cap(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000}}
        derived = cb.derive_allowed(base_work, cv_multiplier=3.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.95)

    def test_derived_with_zero_median_clamps_to_cap(self):
        base_work = {"w": {"MedianThroughputPerSecond": 0,
                           "StdDevThroughputPerSecond": 0}}
        derived = cb.derive_allowed(base_work, cv_multiplier=3.0,
                                    cv_floor=0.55, cv_cap=0.95)
        self.assertAlmostEqual(derived["w"], 0.95)


class ThroughputComparisonTests(unittest.TestCase):
    def test_identical_artifacts_do_not_fail(self):
        base_work = {w["Name"]: w for w in make_workloads(BASELINE_ROWS)}
        curr_work = {w["Name"]: w for w in make_workloads(BASELINE_ROWS)}
        comparison = cb.compare_throughput(base_work, curr_work, {}, {}, 0.8)
        self.assertEqual(comparison.missing, set())
        self.assertEqual(comparison.extra, set())
        self.assertTrue(all(row.ratio > 0.9 for row in comparison.rows))
        self.assertFalse(any(row.failed for row in comparison.rows))

    def test_regression_below_derived_threshold_fails(self):
        base_work = {w["Name"]: w for w in make_workloads(BASELINE_ROWS)}
        rows = [(name, median * 0.5, stddev, latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
        curr_work = {w["Name"]: w for w in make_workloads(rows)}
        derived = cb.derive_allowed(base_work, 3.0, 0.55, 0.95)
        comparison = cb.compare_throughput(base_work, curr_work, {}, derived, 0.8)
        self.assertGreater(len([r for r in comparison.rows if r.failed]), 0)

    def test_per_workload_override_beats_derived(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 10_000}}
        curr_work = {"w": {"MedianThroughputPerSecond": 71_000}}
        derived = cb.derive_allowed(base_work, 3.0, 0.55, 0.95)
        # derived = 0.7 → 71k is a pass; an override of 0.75 makes 71k fail.
        comparison = cb.compare_throughput(base_work, curr_work,
                                           {"w": 0.75}, derived, 0.8)
        self.assertTrue(comparison.rows[0].failed)
        self.assertAlmostEqual(comparison.rows[0].threshold, 0.75)

    def test_per_workload_override_can_rescue_a_flag(self):
        base_work = {"w": {"MedianThroughputPerSecond": 100_000,
                           "StdDevThroughputPerSecond": 10_000}}
        curr_work = {"w": {"MedianThroughputPerSecond": 71_000}}
        derived = cb.derive_allowed(base_work, 3.0, 0.55, 0.95)
        comparison = cb.compare_throughput(base_work, curr_work,
                                           {"w": 0.5}, derived, 0.8)
        self.assertFalse(comparison.rows[0].failed)


class StrictGateEndToEndTests(ComparatorTestCase):
    def test_strict_gate_fails_on_matching_host_regression(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("workload regression on a matching host", out)
        self.assertIn("micro_raw_2agent", out)

    def test_strict_gate_passes_on_healthy_matching_host(self):
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("strict comparison passed", out)
        self.assertNotIn("::error::", out)

    def test_missing_workload_fails_before_anything_else(self):
        rows = BASELINE_ROWS[:-1]
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=rows), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("missing workloads", out)
        self.assertIn("policy_lookahead_mcts_32", out)

    def test_added_workload_is_a_note_not_a_failure(self):
        rows = BASELINE_ROWS + [("brand_new_workload", 10_000, 1, 1, 64)]
        rc, out, err = self.run_comparator(
            make_artifact(),
            make_artifact(rows=rows, os_="Ubuntu 24.04.1 LTS",
                          runtime=".NET 8.0.414"),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("added workloads", out)
        self.assertIn("brand_new_workload", out)

    def test_cross_host_regression_is_informational_only(self):
        rc, out, err = self.run_comparator(
            make_artifact(),
            below_threshold_artifact(os_="Ubuntu 24.04.1 LTS",
                                     runtime=".NET 8.0.414"),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)

    def test_runtime_major_mismatch_disarms_strict_gate(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(runtime=".NET 8.0.414"),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("runtime major differs from the baseline", out)

    def test_architecture_mismatch_disarms_strict_gate(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(arch="x64"),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("OS family + architecture do not match", out)

    def test_failures_emit_annotations_by_default(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("::error::", out)
        self.assertIn("micro_raw_2agent", out)

    def test_no_annotations_emits_plain_lines(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(),
            "--strict-if-matching", "--no-annotations")
        self.assertEqual(rc, 1)
        self.assertNotIn("::error::", out)
        self.assertIn("vs baseline", out)

    def test_per_workload_override_applied_through_cli(self):
        rc, out, _ = self.run_comparator(
            make_artifact(), below_threshold_artifact(factor=0.9),
            "--strict-if-matching",
            "--per-workload-threshold", "micro_raw_2agent=0.95")
        self.assertEqual(rc, 1)
        self.assertIn("per-workload thresholds:", out)
        self.assertIn("micro_raw_2agent=0.95", out)
        self.assertIn("micro_raw_2agent", out)

    def test_malformed_per_workload_flag_exits_nonzero(self):
        rc, _, err = self.run_comparator(
            make_artifact(), make_artifact(),  # no '=' in the override
            "--per-workload-threshold", "micro_raw_2agent")
        self.assertEqual(rc, 2)
        self.assertIn("expects NAME=RATIO", err)


class SmokeClassificationTests(ComparatorTestCase):
    def test_healthy_smoke_artifact_passes(self):
        rc, out, err = self.run_comparator(
            make_artifact(),
            make_artifact(os_="Ubuntu 24.04.1 LTS", arch="x64",
                          runtime=".NET 8.0.414",
                          cores=4, ram=7_200_000_000),
            "--smoke")
        self.assertEqual(rc, 0)
        self.assertIn("smoke classification passed", out)
        self.assertNotIn("::error::", out)
        self.assertIn("smoke classification: structural only", out)

    def test_smoke_never_adjudicates_throughput_ratios(self):
        rows = [(name, max(median * 0.05, 1.0), stddev, latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=rows, os_="Ubuntu 24.04.1 LTS"),
            "--smoke")
        self.assertEqual(rc, 0)
        self.assertNotIn("allowed", out)
        self.assertNotIn("regression", out)

    def test_missing_workload_fails_smoke(self):
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=BASELINE_ROWS[:-1]), "--smoke")
        self.assertEqual(rc, 1)
        self.assertIn("smoke classification failed", out)
        self.assertIn("missing workloads", out)

    def test_added_workload_is_a_smoke_note(self):
        rows = BASELINE_ROWS + [("brand_new_workload", 10_000, 1, 1, 64)]
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=rows), "--smoke")
        self.assertEqual(rc, 0)
        self.assertIn("added workloads", out)

    def test_zero_median_fails_smoke(self):
        rows = [(name, 0 if name == "micro_raw_2agent" else median,
                 stddev, latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=rows), "--smoke")
        self.assertEqual(rc, 1)
        self.assertIn("micro_raw_2agent: MedianThroughputPerSecond", out)

    def test_non_positive_latency_fails_smoke(self):
        rows = [(name, median, stddev,
                 0 if name == "stress_topology_4agent" else latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(rows=rows), "--smoke")
        self.assertEqual(rc, 1)
        self.assertIn("stress_topology_4agent: MedianStepLatencyMicros", out)

    def test_degenerate_metadata_fails_smoke(self):
        rc, out, err = self.run_comparator(
            make_artifact(),
            make_artifact(ram=0, cores=0, os_="", runtime=""),
            "--smoke")
        self.assertEqual(rc, 1)
        self.assertIn("Metadata.RamBytes", out)
        self.assertIn("Metadata.Cores", out)
        self.assertIn("Metadata.Os", out)
        self.assertIn("Metadata.Runtime", out)


if __name__ == "__main__":
    unittest.main()