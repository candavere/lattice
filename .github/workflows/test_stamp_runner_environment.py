#!/usr/bin/env python3
"""Unit tests for stamp_runner_environment.py.

The stamp only ADDS Metadata.RunnerImage / Metadata.Sdk: measured numbers
must be value-identical after stamping, a conflicting existing value must
refuse (exit 2, file untouched), an undeterminable image must leave the
field absent with a stderr warning, and a malformed artifact must fail
loudly. Run with: python3 -m unittest discover -s .github/workflows -p
'test_*.py'. """

import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from unittest import mock

import stamp_runner_environment as stamp


def sample_artifact():
    return {
        "Metadata": {
            "Commit": "abc123",
            "Runtime": ".NET 10.0.13",
            "Configuration": "Release",
            "Os": "macOS 26.6.2",
            "Architecture": "Arm64",
            "Cores": 3,
            "Cpu": "Apple M1 (Virtual)",
        },
        "Workloads": [
            {
                "Name": "micro_raw_2agent",
                "MedianThroughputPerSecond": 672076.75,
                "StdDevThroughputPerSecond": 110000.0,
            },
        ],
    }


class StampTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.artifact = os.path.join(self._tmp.name, "artifact.json")
        with open(self.artifact, "w", encoding="utf-8") as handle:
            json.dump(sample_artifact(), handle)

    def _read(self):
        with open(self.artifact, encoding="utf-8") as handle:
            return json.load(handle)

    def _imagedata(self, payload):
        path = os.path.join(self._tmp.name, "imagedata.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle)
        return path

    def test_stamps_missing_image_from_env(self):
        env = {"ImageVersion": "20260907.0351", "HOME": self._tmp.name}
        with mock.patch.dict(os.environ, env, clear=False):
            # imagedata path points at nothing: env must win alone
            rc = stamp.main(
                [
                    "--artifact",
                    self.artifact,
                    "--image-family",
                    "macos-26-arm64",
                    "--imagedata-path",
                    os.path.join(self._tmp.name, "absent.json"),
                    "--sdk",
                    "10.0.402",
                ]
            )
        self.assertEqual(rc, 0)
        after = self._read()
        self.assertEqual(
            after["Metadata"]["RunnerImage"], "macos-26-arm64/20260907.0351"
        )
        self.assertEqual(after["Metadata"]["Sdk"], "10.0.402")

    def test_numbers_are_untouched_in_value_terms(self):
        before = sample_artifact()
        env = {"ImageVersion": "20260907.0351", "HOME": self._tmp.name}
        with mock.patch.dict(os.environ, env, clear=False):
            rc = stamp.main(
                [
                    "--artifact",
                    self.artifact,
                    "--image-family",
                    "macos-26-arm64",
                    "--imagedata-path",
                    os.path.join(self._tmp.name, "absent.json"),
                    "--sdk",
                    "10.0.402",
                ]
            )
        self.assertEqual(rc, 0)
        after = self._read()
        self.assertEqual(after["Workloads"], before["Workloads"])
        for key, value in before["Metadata"].items():
            self.assertEqual(after["Metadata"][key], value)

    def test_falls_back_to_imagedata_json(self):
        path = self._imagedata(
            {"imageversion": "20261013.0424", "imagedate": "20261013"}
        )
        env = {"HOME": self._tmp.name}
        with mock.patch.dict(os.environ, env, clear=True):
            rc = stamp.main(
                [
                    "--artifact",
                    self.artifact,
                    "--image-family",
                    "macos-26-arm64",
                    "--imagedata-path",
                    path,
                    "--sdk",
                    "10.0.402",
                ]
            )
        self.assertEqual(rc, 0)
        self.assertEqual(
            self._read()["Metadata"]["RunnerImage"], "macos-26-arm64/20261013.0424"
        )

    def test_refuses_conflicting_overwrite_leaving_file_untouched(self):
        artifact = sample_artifact()
        artifact["Metadata"]["RunnerImage"] = "macos-26-arm64/20260907.0351"
        with open(self.artifact, "w", encoding="utf-8") as handle:
            json.dump(artifact, handle)
        before = open(self.artifact, encoding="utf-8").read()
        env = {"ImageVersion": "20261013.0424", "HOME": self._tmp.name}
        err = io.StringIO()
        with mock.patch.dict(os.environ, env, clear=False):
            with redirect_stdout(io.StringIO()), redirect_stderr(err):
                rc = stamp.main(
                    [
                        "--artifact",
                        self.artifact,
                        "--image-family",
                        "macos-26-arm64",
                        "--sdk",
                        "10.0.402",
                    ]
                )
        self.assertEqual(rc, 2)
        self.assertIn("refusing to overwrite", err.getvalue())
        self.assertEqual(open(self.artifact, encoding="utf-8").read(), before)

    def test_absent_image_when_nothing_determines_it(self):
        env = {"HOME": self._tmp.name}
        err = io.StringIO()
        with mock.patch.dict(os.environ, env, clear=True):
            with redirect_stdout(io.StringIO()), redirect_stderr(err):
                rc = stamp.main(
                    [
                        "--artifact",
                        self.artifact,
                        "--image-family",
                        "macos-26-arm64",
                        "--imagedata-path",
                        os.path.join(self._tmp.name, "absent.json"),
                        "--sdk",
                        "10.0.402",
                    ]
                )
        self.assertEqual(rc, 0)
        self.assertNotIn("RunnerImage", self._read()["Metadata"])
        self.assertIn("warning", err.getvalue())
        self.assertIn("RunnerImage left absent", err.getvalue())

    def test_malformed_artifact_fails_loudly(self):
        with open(self.artifact, "w", encoding="utf-8") as handle:
            handle.write("{not json")
        err = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(err):
            rc = stamp.main(
                [
                    "--artifact",
                    self.artifact,
                    "--image-family",
                    "macos-26-arm64",
                    "--sdk",
                    "10.0.402",
                ]
            )
        self.assertEqual(rc, 2)
        self.assertTrue(err.getvalue().strip())


if __name__ == "__main__":
    unittest.main()
