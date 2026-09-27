#!/usr/bin/env python3
"""Unit tests for the CI test-result summarizer.

`summarize_test_results.py` turns the TRX files that `dotnet test` writes into
the machine-readable summary that each CI matrix leg publishes as an artifact.
The counts that end up in the README proof path are only as trustworthy as this
parser, so the cases that matter are the ones where it must refuse to answer:
absent, malformed, duplicate and self-contradicting input. A summarizer that
emits `total: 0` on a broken run is worse than no summarizer, because it turns
a failure into a green-looking number.

Run with:  python3 -m unittest discover -s .github/workflows -p 'test_*.py'
"""

import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

import summarize_test_results as str_


NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

# Outcome partition, mirroring how the summarizer buckets TRX counters. The
# four numbers the summary publishes come straight from here.
PARTITION = {
    "passed": ["passed"],
    "failed": ["failed", "error", "timeout", "aborted"],
    "skipped": ["notExecuted"],
    "notRun": ["notRunnable", "inconclusive"],
}


def counters(total=4, passed=4, failed=0, error=0, timeout=0, aborted=0,
             notExecuted=0, notRunnable=0, inconclusive=0, executed=None):
    """Build a <Counters> attribute string for a given partition."""
    if executed is None:
        executed = passed + failed + error + timeout + aborted
    return (
        f'total="{total}" executed="{executed}" passed="{passed}" '
        f'failed="{failed}" error="{error}" timeout="{timeout}" '
        f'aborted="{aborted}" inconclusive="{inconclusive}" '
        f'passedButRunAborted="0" notRunnable="{notRunnable}" '
        f'notExecuted="{notExecuted}" disconnected="0" warning="0" '
        f'completed="0" inProgress="0" pending="0"'
    )


def trx(run_id="run-1", results=(), counter_attrs=None, name="ci",
        omit_counters=False, duplicate_summary=False):
    """Build a TRX document with `results` UnitTestResults and one Counters.

    By default the counters are derived from `results`, so a fixture that is
    meant to be well-formed is self-consistent. Tests that need a specific
    inconsistency pass `counter_attrs` explicitly.
    """
    result_xml = "\n".join(
        f'      <UnitTestResult executionId="e{i}" testName="T{i}" '
        f'testId="g{i}" outcome="{outcome}" />'
        for i, outcome in enumerate(results)
    )
    if counter_attrs is None:
        by_outcome = {}
        for outcome in results:
            by_outcome[outcome] = by_outcome.get(outcome, 0) + 1
        counter_attrs = counters(
            total=len(results),
            passed=by_outcome.get("Passed", 0),
            failed=by_outcome.get("Failed", 0),
            error=by_outcome.get("Error", 0),
            timeout=by_outcome.get("Timeout", 0),
            aborted=by_outcome.get("Aborted", 0),
            notExecuted=by_outcome.get("NotExecuted", 0),
            notRunnable=by_outcome.get("NotRunnable", 0),
            inconclusive=by_outcome.get("Inconclusive", 0),
        )
    summary = ""
    if not omit_counters:
        summary = (
            "  <ResultSummary outcome=\"Completed\">\n"
            f"    <Counters {counter_attrs} />\n"
            "  </ResultSummary>\n"
        )
        if duplicate_summary:
            summary += (
                "  <ResultSummary outcome=\"Completed\">\n"
                f"    <Counters {counter_attrs} />\n"
                "  </ResultSummary>\n"
            )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        f'<TestRun id="{run_id}" name="{name}" xmlns="{NS}">\n'
        "  <Results>\n"
        f"{result_xml}\n"
        "  </Results>\n"
        f"{summary}"
        "</TestRun>\n"
    )


class WriteDir:
    """Context manager writing named files into a throwaway results dir."""

    def __init__(self, files):
        self.files = files

    def __enter__(self):
        self.tmp = tempfile.TemporaryDirectory()
        for rel, text in self.files.items():
            path = os.path.join(self.tmp.name, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(text)
        return self.tmp.name

    def __exit__(self, *exc):
        self.tmp.cleanup()
        return False


class SummarizeValidRuns(unittest.TestCase):
    def test_all_passing_run(self):
        with WriteDir({"a.trx": trx(results=["Passed"] * 4)}) as d:
            s = str_.summarize(d)
        self.assertEqual(
            (s["total"], s["passed"], s["failed"], s["skipped"]), (4, 4, 0, 0))

    def test_mixed_run_partitions_total(self):
        # 3 passed, 1 failed, 1 skipped, 1 not runnable -> total 6.
        with WriteDir({"a.trx": trx(
            results=["Passed", "Passed", "Passed", "Failed", "NotExecuted",
                     "NotRunnable"],
        )}) as d:
            s = str_.summarize(d)
        self.assertEqual(
            (s["total"], s["passed"], s["failed"], s["skipped"]), (6, 3, 1, 1))

    def test_error_timeout_and_aborted_count_as_failed(self):
        with WriteDir({"a.trx": trx(
            results=["Failed", "Error", "Timeout", "Aborted"],
            counter_attrs=counters(total=4, passed=0, failed=1, error=1,
                                   timeout=1, aborted=1),
        )}) as d:
            s = str_.summarize(d)
        self.assertEqual(s["failed"], 4)
        self.assertEqual(s["passed"], 0)

    def test_multiple_trx_files_sum_within_one_os(self):
        # Two test projects in one solution run under one OS: summing them is a
        # per-OS total, which is a different claim from summing across OSes.
        with WriteDir({
            "one.trx": trx(run_id="r1", results=["Passed"] * 4),
            "nested/two.trx": trx(run_id="r2", results=["Passed"] * 3),
        }) as d:
            s = str_.summarize(d)
        self.assertEqual(s["total"], 7)
        self.assertEqual(s["trxFiles"], 2)
        self.assertEqual(sorted(s["trxNames"]), ["nested/two.trx", "one.trx"])

    def test_provenance_is_recorded(self):
        with WriteDir({"a.trx": trx(results=["Passed"] * 2)}) as d:
            s = str_.summarize(d, commit_sha="deadbeef" * 5,
                               runner_os="Linux", os_slug="ubuntu")
        self.assertEqual(s["schema"], str_.SCHEMA)
        self.assertEqual(s["commitSha"], "deadbeef" * 5)
        self.assertEqual(s["runnerOs"], "Linux")
        self.assertEqual(s["osSlug"], "ubuntu")


class SummarizeRejectsBadInput(unittest.TestCase):
    def assertRejected(self, files, needle):
        with WriteDir(files) as d:
            with self.assertRaises(str_.SummaryError) as ctx:
                str_.summarize(d)
        self.assertIn(needle, str(ctx.exception).lower())

    def test_absent_results_directory(self):
        with self.assertRaises(str_.SummaryError) as ctx:
            str_.summarize("/nonexistent/results/dir")
        self.assertIn("no such", str(ctx.exception).lower())

    def test_absent_trx_file(self):
        self.assertRejected({}, "no trx")

    def test_malformed_xml(self):
        self.assertRejected({"a.trx": "<TestRun><oops>"}, "malformed")

    def test_wrong_root_element(self):
        self.assertRejected({"a.trx": "<NotATestRun />"}, "malformed")

    def test_missing_counters_is_absent_not_zero(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4, omit_counters=True)},
            "counters")

    def test_missing_total_attribute(self):
        attrs = counters().replace('total="4" ', "")
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4, counter_attrs=attrs)},
            "total")

    def test_non_integer_counter(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4,
                          counter_attrs=counters().replace('total="4"',
                                                           'total="lots"'))},
            "integer")

    def test_negative_counter(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4,
                          counter_attrs=counters().replace('passed="4"',
                                                           'passed="-1"'))},
            "negative")

    def test_zero_total_is_not_reported_as_success(self):
        # The exact failure this parser exists to prevent: a green run of zero
        # tests rendering as "0 passed" and looking like a pass.
        self.assertRejected(
            {"a.trx": trx(results=[], counter_attrs=counters(total=0,
                                                              executed=0,
                                                              passed=0))},
            "zero")

    def test_duplicate_test_run_id(self):
        self.assertRejected({
            "a.trx": trx(run_id="same", results=["Passed"] * 4),
            "b.trx": trx(run_id="same", results=["Passed"] * 4),
        }, "duplicate")

    def test_duplicate_result_summaries_conflict(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4, duplicate_summary=True)},
            "counters")

    def test_conflicting_total_versus_result_list(self):
        # Counters claim 4 but only 3 results are present: a truncated or
        # hand-edited report must not produce a confident number.
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 3,
                          counter_attrs=counters(total=4, passed=4))},
            "conflict")

    def test_conflicting_executed_decomposition(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4,
                          counter_attrs=counters(total=4, passed=4,
                                                 executed=99))},
            "conflict")

    def test_conflicting_outcome_partition(self):
        self.assertRejected(
            {"a.trx": trx(results=["Passed"] * 4,
                          counter_attrs=counters(total=4, passed=2, failed=1))},
            "conflict")


class CliSurface(unittest.TestCase):
    def _run(self, files, extra=()):
        with WriteDir(files) as d:
            out = os.path.join(d, "summary.json")
            argv = [d, "--out", out,
                    "--commit-sha", "abc123", "--os-slug", "ubuntu",
                    "--runner-os", "Linux", *extra]
            stdout, stderr = io.StringIO(), io.StringIO()
            rc = 0
            with redirect_stdout(stdout), redirect_stderr(stderr):
                rc = str_.main(argv)
            payload = None
            if os.path.exists(out):
                with open(out, encoding="utf-8") as fh:
                    payload = json.load(fh)
        return rc, stdout.getvalue(), stderr.getvalue(), payload

    def test_writes_json_and_exits_zero_when_green(self):
        code, _, _, payload = self._run({"a.trx": trx(results=["Passed"] * 4)})
        self.assertEqual(code, 0)
        self.assertEqual(payload["passed"], 4)
        self.assertEqual(payload["failed"], 0)
        self.assertEqual(payload["commitSha"], "abc123")

    def test_exit_zero_only_when_nothing_failed(self):
        # A green suite is exit 0; a nonzero failure count is exit 1 even though
        # the JSON is still written, so the artifact survives for inspection.
        code, _, _, payload = self._run({"a.trx": trx(
            results=["Passed", "Failed"],
            counter_attrs=counters(total=2, passed=1, failed=1))})
        self.assertEqual(code, 1)
        self.assertEqual(payload["failed"], 1)
        self.assertEqual(payload["total"], 2)

    def test_exit_two_and_no_payload_on_unparseable_input(self):
        code, _, stderr, payload = self._run({"a.trx": "<TestRun><oops>"})
        self.assertEqual(code, 2)
        self.assertIsNone(payload)
        self.assertIn("summary", stderr.lower())

    def test_nonzero_failure_does_not_mask_as_success(self):
        # The headline guard: whatever the counts say, a run with failures never
        # reports exit 0, and never reports a green summary.
        code, _, _, payload = self._run({"a.trx": trx(
            results=["Failed"] * 3,
            counter_attrs=counters(total=3, passed=0, failed=3))})
        self.assertNotEqual(code, 0)
        self.assertGreater(payload["failed"], 0)

    def test_writes_nothing_on_conflicting_totals(self):
        code, _, _, payload = self._run({"a.trx": trx(
            results=["Passed"] * 3,
            counter_attrs=counters(total=4, passed=4))})
        self.assertEqual(code, 2)
        self.assertIsNone(payload)


if __name__ == "__main__":
    unittest.main()
