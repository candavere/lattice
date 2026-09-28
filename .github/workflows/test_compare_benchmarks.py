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


def make_workloads(rows, protocol=None):
    output = []
    for name, median, stddev, latency, alloc in rows:
        entry = {
            "Name": name,
            "MedianThroughputPerSecond": median,
            "StdDevThroughputPerSecond": stddev,
            "MedianStepLatencyMicros": latency,
            "AllocationsPerStepBytes": alloc,
        }
        # protocol=None models a legacy artifact that records no budget at all,
        # which is what the bare-metal-style fixtures above carry.
        if protocol is not None:
            entry["Iterations"] = 10
            entry["StepsPerIteration"] = (protocol.get(name)
                                          if isinstance(protocol, dict)
                                          else protocol)
        output.append(entry)
    return output


def make_artifact(rows=None, os_="macOS 27.0.0", arch="Arm64",
                  runtime=".NET 10.0.10", ram=8_589_934_592, cores=8,
                  cpu="Apple M1", protocol=None, image=None):
    metadata = {
        "Commit": "deadbeef",
        "Timestamp": "2026-09-18T20:02:47",
        "Runtime": runtime,
        "Configuration": "Release",
        "Os": os_,
        "Architecture": arch,
        "Cores": cores,
        "RamBytes": ram,
    }
    if cpu is not None:  # cpu=None models an artifact missing the Cpu field
        metadata["Cpu"] = cpu
    if image is not None:
        metadata["RunnerImage"] = image
    return {
        "Metadata": metadata,
        "Workloads": make_workloads(
            rows if rows is not None else BASELINE_ROWS, protocol),
    }


# The runner class this repository's hosted gate actually measures: a pinned
# arm64 GitHub-hosted image, a virtualized 3-core M1, and the full protocol.
RUNNER_IMAGE = "macos-26-arm64/20260907.0351"
RUNNER_PROTOCOL = {
    "micro_raw_2agent": 100000,
    "facility_static_4agent": 100000,
    "dynamic_contention_4agent": 100000,
    "stress_topology_4agent": 100000,
    "policy_lookahead_mcts_32": 100,  # decisions metric: 100 ticks by design
}


def runner_artifact(rows=None, armed=None, os_="macOS 26.6.2",
                    runtime=".NET 10.0.12", cores=3,
                    cpu="Apple M1 (Virtual)", image=RUNNER_IMAGE,
                    protocol=None, factor=1.0):
    """A full-protocol artifact from the hosted runner class.

    `armed` writes Provenance.ArmedWorkloads, the record's own statement of
    which workloads the gate may adjudicate. `factor` scales the current
    artifact's medians so a test can sit a known distance from the threshold.
    """
    if rows is None:
        rows = [(name, median * factor, stddev, latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
    artifact = make_artifact(
        rows=rows, os_=os_, arch="Arm64", runtime=runtime, cores=cores,
        cpu=cpu, ram=7_519_192_768, image=image,
        protocol=RUNNER_PROTOCOL if protocol is None else protocol)
    if armed is not None:
        artifact["Provenance"] = {"ArmedWorkloads": list(armed)}
    return artifact


def below_threshold_artifact(os_="macOS 27.0.0", arch="Arm64",
                             runtime=".NET 10.0.10", factor=0.5, cores=8,
                             cpu="Apple M1"):
    rows = [(name, max(median * factor, 1.0), stddev, latency, alloc)
            for name, median, stddev, latency, alloc in BASELINE_ROWS]
    return make_artifact(rows=rows, os_=os_, arch=arch, runtime=runtime,
                         cores=cores, cpu=cpu)


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

    # Host-class gate tests: the strict fingerprint must include the logical
    # core count and the CPU model string, so a GitHub-hosted runner (same OS
    # family + architecture as the bare-metal M1 baseline, but a different
    # host class) gets an informational comparison instead of a strict
    # verdict, while a truly matching host still gets the strict gate.

    def test_different_core_count_is_cross_host_informational(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(cores=3),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("logical cores differ", out)

    def test_different_cpu_model_is_cross_host_informational(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(cpu="Apple M2"),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("CPU model differs", out)

    def test_fully_matching_host_strict_gate_still_fails(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("workload regression on a matching host", out)

    def test_fully_matching_host_above_floor_passes(self):
        rc, out, err = self.run_comparator(
            make_artifact(), make_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("strict comparison passed", out)
        self.assertNotIn("::error::", out)

    def test_missing_cpu_on_baseline_treated_as_mismatch(self):
        rc, out, err = self.run_comparator(
            make_artifact(cpu=None), below_threshold_artifact(),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("baseline.Cpu", out)
        self.assertNotIn("strict comparison passed", out)

    def test_missing_cpu_on_current_treated_as_mismatch(self):
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(cpu=None),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("current.Cpu", out)
        self.assertNotIn("strict comparison passed", out)

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


class RunnerClassGateTests(ComparatorTestCase):
    """The runner-class reference: a hosted-runner baseline whose own record
    declares the fingerprint, the protocol, and which workloads the gate may
    adjudicate.

    The bare-metal reference record stays a cross-host comparison for a hosted
    runner, so this class covers the separate runner-class baseline and the
    rules that keep a strict verdict from being manufactured across unlike
    hosts, unlike images, unlike runtimes, or unlike measurement budgets.
    """

    # -- arming on a full fingerprint + protocol match --------------------

    def test_matching_fingerprint_above_threshold_passes_strict(self):
        rc, out, err = self.run_comparator(
            runner_artifact(), runner_artifact(factor=2.0), "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("fingerprint match: True", out)
        self.assertIn("strict comparison passed", out)
        self.assertNotIn("::error::", out)

    def test_matching_fingerprint_below_threshold_fails_strict(self):
        rc, out, err = self.run_comparator(
            runner_artifact(), runner_artifact(factor=0.5), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("workload regression on a matching host", out)
        self.assertIn("::error::", out)

    def test_all_five_workloads_are_reported_when_present(self):
        rc, out, err = self.run_comparator(
            runner_artifact(), runner_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 0)
        for name in CANONICAL_NAMES:
            self.assertIn(name, out)

    def test_a_missing_workload_still_fails_the_runner_class_gate(self):
        rows = BASELINE_ROWS[:-1]
        rc, out, err = self.run_comparator(
            runner_artifact(),
            runner_artifact(rows=[(n, m, s, la, a) for n, m, s, la, a in rows]),
            "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("missing workloads", out)
        self.assertIn("policy_lookahead_mcts_32", out)

    # -- unlike host class stays informational ---------------------------

    def test_missing_cores_is_cross_host_informational(self):
        artifact = runner_artifact()
        artifact["Metadata"].pop("Cores")
        rc, out, err = self.run_comparator(
            runner_artifact(), artifact, "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("current.Cores", out)

    def test_missing_cpu_is_cross_host_informational(self):
        rc, out, err = self.run_comparator(
            runner_artifact(), runner_artifact(cpu=None), "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)
        self.assertIn("current.Cpu", out)

    def test_bare_metal_reference_against_a_runner_is_informational(self):
        """The pre-existing reference record must stay a cross-host
        comparison for a hosted runner - a runner-class baseline is a
        separate file, not a rewrite of that one."""
        rc, out, err = self.run_comparator(
            make_artifact(), runner_artifact(factor=0.5), "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("cross-host comparison (informational)", out)

    # -- unlike runner image --------------------------------------------

    def test_different_runner_image_is_cross_host_informational(self):
        rc, out, err = self.run_comparator(
            runner_artifact(),
            runner_artifact(image="macos-26-arm64/20261231.9999", factor=0.5),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("runner image differs", out)
        self.assertIn("cross-host comparison (informational)", out)

    def test_a_missing_runner_image_carries_no_constraint_either_way(self):
        """Absence is not a mismatch: a record that predates the field must
        neither arm nor disarm a gate it cannot speak to."""
        for baseline_image, current_image in ((None, RUNNER_IMAGE),
                                              (RUNNER_IMAGE, None),
                                              (None, None)):
            with self.subTest(baseline=baseline_image, current=current_image):
                rc, out, err = self.run_comparator(
                    runner_artifact(image=baseline_image),
                    runner_artifact(image=current_image, factor=2.0),
                    "--strict-if-matching")
                self.assertEqual(rc, 0)
                self.assertIn("fingerprint match: True", out)

    # -- unlike runtime and unlike protocol ------------------------------

    def test_different_runtime_is_cross_host_informational(self):
        rc, out, err = self.run_comparator(
            runner_artifact(),
            runner_artifact(runtime=".NET 8.0.414", factor=0.5),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("runtime major differs", out)
        self.assertIn("cross-host comparison (informational)", out)

    def test_a_shortened_budget_is_cross_host_informational(self):
        """A smoke pass must never be adjudicated against a full-protocol
        baseline: a shorter budget is not a speed regression."""
        rc, out, err = self.run_comparator(
            runner_artifact(),
            runner_artifact(protocol=10000, factor=0.5),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("recorded protocol does not match", out)
        self.assertIn("cross-host comparison (informational)", out)

    def test_an_incomplete_measurement_is_cross_host_informational(self):
        artifact = runner_artifact(factor=0.5)
        for workload in artifact["Workloads"]:
            workload.pop("StepsPerIteration")
        rc, out, err = self.run_comparator(
            runner_artifact(), artifact, "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("omits StepsPerIteration", out)
        self.assertIn("cross-host comparison (informational)", out)

    # -- the record's own armed set --------------------------------------

    def test_an_unarmed_workload_is_reported_but_never_fails(self):
        """The whole point of the armed set: a workload whose measured spread
        is too wide to gate on is still reported when it dips, but its dip
        does not fail the build."""
        armed = [n for n in CANONICAL_NAMES if n != "policy_lookahead_mcts_32"]
        rows = [(name,
                 median * (0.5 if name == "policy_lookahead_mcts_32" else 1.0),
                 stddev, latency, alloc)
                for name, median, stddev, latency, alloc in BASELINE_ROWS]
        rc, out, err = self.run_comparator(
            runner_artifact(armed=armed), runner_artifact(rows=rows),
            "--strict-if-matching")
        self.assertEqual(rc, 0)
        self.assertIn("strict comparison passed", out)
        self.assertIn("informational workloads", out)
        self.assertIn("policy_lookahead_mcts_32", out)
        self.assertIn("does not fail this gate", out)
        self.assertNotIn("::error::", out)

    def test_an_armed_workload_still_fails_while_others_are_unarmed(self):
        armed = [n for n in CANONICAL_NAMES if n != "policy_lookahead_mcts_32"]
        rc, out, err = self.run_comparator(
            runner_artifact(armed=armed), runner_artifact(factor=0.5),
            "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("workload regression on a matching host", out)
        self.assertIn("micro_raw_2agent", out)
        # the unarmed workload dips too, but it is not adjudicated, so it is
        # never named in the failure list
        self.assertNotIn("policy_lookahead_mcts_32: ", out)

    def test_a_baseline_without_provenance_arms_every_workload(self):
        """Back-compatibility: the bare-metal record declares no armed set, so
        every workload in its matrix is still adjudicated."""
        rc, out, err = self.run_comparator(
            make_artifact(), below_threshold_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("workload regression on a matching host", out)
        self.assertIn("policy_lookahead_mcts_32", out)

    def test_an_armed_set_naming_an_unknown_workload_is_an_error(self):
        rc, out, err = self.run_comparator(
            runner_artifact(armed=CANONICAL_NAMES + ["not_a_workload"]),
            runner_artifact(), "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("not_a_workload", out)
        self.assertIn("::error::", out)

    # -- malformed artifacts ---------------------------------------------

    def test_a_malformed_artifact_is_rejected_not_scored(self):
        baseline_path = self._write("baseline.json", runner_artifact())
        broken_path = os.path.join(self._tmp.name, "broken.json")
        with open(broken_path, "w", encoding="utf-8") as handle:
            handle.write("{not json at all")
        with self.assertRaises(json.JSONDecodeError):
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                cb.main([baseline_path, broken_path, "--strict-if-matching"])

    def test_an_artifact_missing_its_workload_matrix_is_rejected(self):
        artifact = runner_artifact()
        artifact["Workloads"] = []
        rc, out, err = self.run_comparator(
            runner_artifact(), artifact, "--strict-if-matching")
        self.assertEqual(rc, 1)
        self.assertIn("missing workloads", out)


if __name__ == "__main__":
    unittest.main()