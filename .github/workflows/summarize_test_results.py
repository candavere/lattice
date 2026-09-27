#!/usr/bin/env python3
"""Summarize the TRX files that `dotnet test` writes into one JSON document.

Each CI matrix leg runs the solution's tests, writes VSTest TRX reports, and
uploads them as an artifact. This turns those reports into a small, stable JSON
document carrying the run's identity (commit SHA, OS, runner image) next to the
counts, so a reader can check a number against the run that produced it instead
of trusting a shield image.

The parser is deliberately strict. It refuses to emit a summary when the input
is absent, malformed, duplicated or self-contradicting, because the failure mode
that matters here is a confident-looking `{"total": 0, "passed": 0}` published
as proof of a green suite. Silence is recoverable; a wrong count is not.

Scope note: the counts here are per operating system. A solution can hold
several test projects and those are summed, but the three matrix legs are three
separate documents and are never added together -- a "711 tests" figure that
quietly tripled itself across runners is not a fact about the suite.

Usage:
    summarize_test_results.py RESULTS_DIR --out summary.json \
        --commit-sha SHA --os-slug ubuntu --runner-os Linux

Exit codes:
    0  parsed cleanly and nothing failed
    1  parsed cleanly and at least one test did not pass
    2  the results could not be trusted; no summary is written
"""

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET

SCHEMA = "lattice.ci.test-summary/1"
VSTEST_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

# How the raw VSTest counters are bucketed into the four numbers published in
# the summary. Everything not a pass is reported as a failure rather than being
# quietly dropped, so `total` always equals the sum of the buckets.
OUTCOME_BUCKETS = {
    "passed": ("passed",),
    "failed": ("failed", "error", "timeout", "aborted"),
    "skipped": ("notExecuted",),
    "notRun": ("notRunnable", "inconclusive"),
}

# Attributes a TRX <Counters> element must carry for a count to be quotable.
REQUIRED_COUNTERS = (
    "total", "executed", "passed", "failed", "error", "timeout", "aborted",
    "notExecuted", "notRunnable", "inconclusive",
)

# A run that discovered no tests is reported as a parse failure, not as a
# passing run of zero. A suite that silently stopped discovering is exactly the
# state a reader must not mistake for green.
MIN_TOTAL = 1


class SummaryError(Exception):
    """The TRX input could not be trusted; no summary should be published."""


def _localname(tag):
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def _parse_counter(counters_el, where):
    """Read and validate the <Counters> element of one TRX report."""
    missing = [a for a in REQUIRED_COUNTERS if a not in counters_el.attrib]
    if missing:
        raise SummaryError(
            "malformed {0}: <Counters> is missing required counter "
            "attribute(s): {1}".format(where, ", ".join(missing)))

    values = {}
    for attr in REQUIRED_COUNTERS:
        raw = counters_el.attrib[attr].strip()
        try:
            value = int(raw)
        except ValueError:
            raise SummaryError(
                "malformed {0}: counter '{1}' is not an integer: {2!r}".format(
                    where, attr, raw))
        if value < 0:
            raise SummaryError(
                "malformed {0}: counter '{1}' is negative: {2}".format(
                    where, attr, value))
        values[attr] = value
    return values


def parse_trx(path, display_name):
    """Parse one TRX report into a per-run count record.

    Raises SummaryError for anything that would make the counts
    unquotable: bad XML, a foreign root element, a missing or duplicated
    <ResultSummary>, or counters that disagree with each other or with the
    results actually present in the file.
    """
    try:
        tree = ET.parse(path)
    except ET.ParseError as exc:
        raise SummaryError("malformed {0}: {1}".format(display_name, exc))
    except OSError as exc:
        raise SummaryError("unreadable {0}: {1}".format(display_name, exc))

    root = tree.getroot()
    if _localname(root.tag) != "TestRun" or (
            "}" in root.tag and root.tag.split("}", 1)[0][1:] != VSTEST_NS):
        raise SummaryError(
            "malformed {0}: root element is {1!r}, expected a VSTest "
            "<TestRun>".format(display_name, _localname(root.tag)))

    run_id = root.attrib.get("id", "")

    summaries = [el for el in root if _localname(el.tag) == "ResultSummary"]
    if not summaries:
        raise SummaryError(
            "{0}: no <ResultSummary> element, so the run reported no <Counters> "
            "totals".format(display_name))
    if len(summaries) > 1:
        raise SummaryError(
            "{0}: {1} <ResultSummary> elements conflict; the <Counters> totals "
            "are ambiguous, expected exactly one".format(
                display_name, len(summaries)))

    counter_els = [el for el in summaries[0] if _localname(el.tag) == "Counters"]
    if not counter_els:
        raise SummaryError(
            "{0}: <ResultSummary> carries no <Counters>, so the totals are "
            "absent".format(display_name))
    if len(counter_els) > 1:
        raise SummaryError(
            "{0}: {1} <Counters> elements conflict; expected exactly one".format(
                display_name, len(counter_els)))

    counts = _parse_counter(counter_els[0], display_name)

    buckets = {name: sum(counts[a] for a in attrs)
               for name, attrs in OUTCOME_BUCKETS.items()}
    partition = sum(buckets.values())

    # Cross-checks. Each of these catches a report that is internally
    # inconsistent, which is what a truncated, merged or hand-edited file
    # looks like from the outside.
    if partition != counts["total"]:
        raise SummaryError(
            "{0}: conflicting <Counters>: {1} ({2}) do not add up to total={3}".format(
                display_name, partition,
                " + ".join("{0}={1}".format(k, buckets[k])
                           for k in sorted(buckets)),
                counts["total"]))

    terminal = (counts["passed"] + counts["failed"] + counts["error"]
                + counts["timeout"] + counts["aborted"])
    if counts["executed"] != terminal:
        raise SummaryError(
            "{0}: conflicting <Counters>: executed={1} but the non-skipped outcomes "
            "sum to {2}".format(display_name, counts["executed"], terminal))

    if counts["total"] < MIN_TOTAL:
        raise SummaryError(
            "{0}: total is zero, so this run discovered no tests; refusing to "
            "report it as a passing run".format(display_name))

    result_count = 0
    for results_el in root:
        if _localname(results_el.tag) != "Results":
            continue
        result_count += sum(1 for child in results_el
                            if _localname(child.tag) == "UnitTestResult")
    if result_count != counts["total"]:
        raise SummaryError(
            "{0}: conflicting <Counters>: total={1} but the file lists {2} test "
            "result(s)".format(display_name, counts["total"], result_count))

    return {
        "runId": run_id,
        "total": counts["total"],
        "executed": counts["executed"],
        "buckets": buckets,
        "trxName": display_name,
    }


def find_trx_files(results_dir):
    """Return (display_name, absolute_path) for every TRX under results_dir."""
    if not os.path.isdir(results_dir):
        raise SummaryError(
            "no such results directory: {0}".format(results_dir))
    found = []
    for dirpath, _dirnames, filenames in os.walk(results_dir):
        for filename in filenames:
            if filename.lower().endswith(".trx"):
                absolute = os.path.join(dirpath, filename)
                display = os.path.relpath(absolute, results_dir)
                found.append((display.replace(os.sep, "/"), absolute))
    return sorted(found)


def summarize(results_dir, commit_sha=None, runner_os=None, os_slug=None,
              runner_image=None, dotnet_version=None, event=None, ref=None):
    """Build the summary document for every TRX under results_dir."""
    trx_files = find_trx_files(results_dir)
    if not trx_files:
        raise SummaryError(
            "no TRX results under {0}; `dotnet test` wrote no report to "
            "summarize".format(results_dir))

    records = []
    by_run_id = {}
    for display, absolute in trx_files:
        record = parse_trx(absolute, display)
        run_id = record["runId"]
        if run_id and run_id in by_run_id:
            raise SummaryError(
                "duplicate test run {0} appears in both {1} and {2}; the two "
                "reports cannot both be counted".format(
                    run_id, by_run_id[run_id], display))
        if run_id:
            by_run_id[run_id] = display
        records.append(record)

    totals = {name: sum(r["buckets"][name] for r in records)
              for name in OUTCOME_BUCKETS}
    total = sum(totals.values())
    if total < MIN_TOTAL:
        raise SummaryError(
            "total is zero across {0} TRX report(s); refusing to report an "
            "empty run as passing".format(len(records)))

    return {
        "schema": SCHEMA,
        "commitSha": commit_sha,
        "event": event,
        "ref": ref,
        "runnerOs": runner_os,
        "osSlug": os_slug,
        "runnerImage": runner_image,
        "dotnetVersion": dotnet_version,
        "trxFiles": len(records),
        "trxNames": [r["trxName"] for r in records],
        "total": total,
        "passed": totals["passed"],
        "failed": totals["failed"],
        "skipped": totals["skipped"],
        "notRun": totals["notRun"],
        "perFile": [
            {"trxName": r["trxName"], "runId": r["runId"], "total": r["total"]}
            for r in records
        ],
    }


def _build_parser():
    parser = argparse.ArgumentParser(
        description="Summarize dotnet test TRX reports into one JSON document.")
    parser.add_argument("results_dir",
                        help="directory `dotnet test --results-directory` wrote to")
    parser.add_argument("--out", required=True,
                        help="path of the JSON summary to write")
    parser.add_argument("--commit-sha", default=None,
                        help="head commit the suite was run against")
    parser.add_argument("--runner-os", default=None, help="Runner.os, e.g. Linux")
    parser.add_argument("--os-slug", default=None,
                        help="stable CI matrix label, e.g. ubuntu")
    parser.add_argument("--runner-image", default=None,
                        help="runner image name and version")
    parser.add_argument("--dotnet-version", default=None,
                        help="SDK version that ran the suite")
    parser.add_argument("--event", default=None, help="GitHub event name")
    parser.add_argument("--ref", default=None, help="git ref being built")
    return parser


def main(argv=None) -> int:
    """Entry point. `argv` follows the usual convention: no program name.

    Prints a one-line human summary on stdout and any reason the counts are
    untrustworthy on stderr, then returns the exit code documented above.
    """
    args = _build_parser().parse_args(argv)
    try:
        summary = summarize(
            args.results_dir,
            commit_sha=args.commit_sha,
            runner_os=args.runner_os,
            os_slug=args.os_slug,
            runner_image=args.runner_image,
            dotnet_version=args.dotnet_version,
            event=args.event,
            ref=args.ref,
        )
    except SummaryError as exc:
        sys.stderr.write(
            "error: could not publish a test summary: {0}\n".format(exc))
        return 2

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(summary, handle, indent=2, sort_keys=True)
        handle.write("\n")

    sys.stdout.write(
        "{0}: total {1}, passed {2}, failed {3}, skipped {4} (not-run {5}) "
        "from {6} TRX report(s) at {7}\n".format(
            args.os_slug or args.results_dir, summary["total"],
            summary["passed"], summary["failed"], summary["skipped"],
            summary["notRun"], summary["trxFiles"], summary["commitSha"]))

    # A nonzero failure count is reported honestly and the JSON is still
    # written, so the artifact survives for inspection. Only a clean run with
    # nothing failed exits 0.
    if summary["failed"]:
        sys.stderr.write(
            "error: {0} test(s) did not pass; the summary is written but this "
            "run is not green\n".format(summary["failed"]))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
