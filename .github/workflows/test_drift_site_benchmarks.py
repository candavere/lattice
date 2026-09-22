#!/usr/bin/env python3
"""Drift test for the site's same-origin benchmark fallback copies.

The replay site ships committed same-origin snapshots of the benchmark result
artifacts under `site/benchmarks/` so the result numbers render even when the
browser cannot reach GitHub (network blocked, offline). Because the page labels
those numbers as "committed values pinned at the pinned revision", the fallback
copies must never silently drift from what is committed in this repository:
if `site/benchmarks/*.json` stops matching the corresponding artifact in
`benchmarks/*.json`, the evidence the page claims to show has changed and we
fail loudly instead of quietly serving stale numbers.

Nothing here touches the network: both sides of the comparison live in this
repository, and this test runs on every CI build.

Run with:  python3 -m unittest discover -s .github/workflows -p 'test_drift*.py'
"""

import json
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

PAIRS = [
    ("benchmarks/mcts_evaluation_results.json",
     "site/benchmarks/mcts_evaluation_results.json"),
    ("benchmarks/bottleneck_evaluation_results.json",
     "site/benchmarks/bottleneck_evaluation_results.json"),
]


def _load(path):
    with (REPO_ROOT / path).open("r", encoding="utf-8") as fh:
        return json.load(fh)


class SiteBenchmarkDriftTest(unittest.TestCase):
    def test_site_fallback_copies_match_committed_benchmarks(self):
        for committed, fallback in PAIRS:
            with self.subTest(committed=committed, fallback=fallback):
                self.assertNotEqual(
                    (REPO_ROOT / committed).stat().st_size,
                    0,
                    f"{committed} looks empty; run the benchmark suite and commit it",
                )
                self.assertEqual(
                    _load(committed),
                    _load(fallback),
                    f"\n\nDRIFT: {fallback} no longer matches the committed artifact {committed}.\n"
                    "The replay page shows these as 'committed values pinned at the pinned revision';\n"
                    "stale numbers would be evidence-lying. Re-copy the committed artifact:\n"
                    "  cp benchmarks/%(base)s site/benchmarks/%(base)s\n"
                    "then re-verify with the CLI replay check (cross-link verify) and commit both." % {
                        "base": committed.split("/")[-1],
                    },
                )


if __name__ == "__main__":
    unittest.main(verbosity=2)
