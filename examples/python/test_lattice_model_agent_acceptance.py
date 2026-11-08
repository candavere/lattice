#!/usr/bin/env python3
"""Offline acceptance-mode tests for lattice_model_agent.py.

Loopback stub server + fresh adapter subprocesses only. No provider calls,
no spend, no 400 real model calls (cap boundary uses small caps and one
prepopulated-ledger boundary test).
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest

from test_lattice_model_agent import (
    OBSERVATION,
    SECRET,
    StubEndpoint,
    hello_line,
    observation_line,
    run_adapter,
)


def acceptance_args(stub_url, ledger, cap=None, extra=()):
    args = [
        "--endpoint",
        stub_url,
        "--model",
        "stub-model",
        "--prompt-version",
        "accept",
        "--acceptance-ledger",
        ledger,
    ]
    if cap is not None:
        args += ["--acceptance-max-attempts", str(cap)]
    return args + list(extra)


def read_ledger(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def with_model_envelope(content, model=None):
    import json as j

    env = {"choices": [{"message": {"role": "assistant", "content": content}}]}
    if model is not None:
        env["model"] = model
    return j.dumps(env).encode("utf-8")


class AcceptanceLedgerTest(unittest.TestCase):
    def setUp(self):
        self.stub = None
        self.td = tempfile.TemporaryDirectory()
        self.ledger = os.path.join(self.td.name, "ledger.json")

    def tearDown(self):
        if self.stub is not None:
            self.stub.close()
        self.td.cleanup()

    def _stub(self, behavior):
        self.stub = StubEndpoint(behavior)
        return self.stub

    def test_retry_consumes_two_reservations(self):
        import time

        def slow(body, headers):
            time.sleep(0.6)
            return 200, with_model_envelope('{"action":"wait"}', model="stub-model")

        stub = self._stub(slow)
        proc = run_adapter(
            acceptance_args(
                stub.url, self.ledger, extra=["--request-timeout-ms", "150"]
            ),
            hello_line(5000) + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(2, len(stub.requests))
        data = read_ledger(self.ledger)
        self.assertEqual(2, len(data["attempts"]))
        self.assertEqual(
            ["recorded", "recorded"], [a["status"] for a in data["attempts"]]
        )
        self.assertEqual(
            ["timeout", "timeout"], [a["error_class"] for a in data["attempts"]]
        )

    def test_cap_shared_across_processes_rejects_after_exhaustion(self):
        stub = self._stub(
            lambda body, headers: (
                200,
                with_model_envelope('{"action":"wait"}', model="stub-model"),
            )
        )
        ok_in = hello_line() + "\n" + observation_line(OBSERVATION) + "\n"
        for _ in range(2):
            proc = run_adapter(
                acceptance_args(stub.url, self.ledger, cap=2), ok_in, timeout=15
            )
            self.assertEqual(0, proc.returncode, proc.stderr)
        before = len(stub.requests)
        proc = run_adapter(
            acceptance_args(stub.url, self.ledger, cap=2), ok_in, timeout=15
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(
            before, len(stub.requests), "cap-exhausted process must not send"
        )
        data = read_ledger(self.ledger)
        self.assertEqual(2, len(data["attempts"]))

    def test_boundary_cap_via_prepopulated_ledger(self):
        # 399 recorded + 1 reserved = 400 consumed; next attempt refused.
        data = {
            "version": 1,
            "cap": 400,
            "stopped": None,
            "attempts": [
                {
                    "n": i + 1,
                    "status": ("reserved" if i == 399 else "recorded"),
                    "http_status": (None if i == 399 else 200),
                    "error_class": None,
                    "requested_model": "stub-model",
                    "server_model": (None if i == 399 else "stub-model"),
                }
                for i in range(400)
            ],
        }
        with open(self.ledger, "w", encoding="utf-8") as handle:
            json.dump(data, handle)
        stub = self._stub(
            lambda body, headers: (
                200,
                with_model_envelope('{"action":"wait"}', model="stub-model"),
            )
        )
        proc = run_adapter(
            acceptance_args(stub.url, self.ledger),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(0, len(stub.requests))

    def test_fatal_401_403_429_prevents_subsequent_requests_either_process(self):
        for status in (401, 403, 429):
            with self.subTest(status=status):
                with tempfile.TemporaryDirectory() as td:
                    ledger = os.path.join(td, "ledger.json")
                    stub = StubEndpoint(lambda b, h, s=status: (s, b'{"error":"nope"}'))
                    try:
                        in_text = (
                            hello_line() + "\n" + observation_line(OBSERVATION) + "\n"
                        )
                        p1 = run_adapter(
                            acceptance_args(stub.url, ledger), in_text, timeout=15
                        )
                        self.assertNotEqual(0, p1.returncode)
                        self.assertEqual(1, len(stub.requests))
                        p2 = run_adapter(
                            acceptance_args(stub.url, ledger), in_text, timeout=15
                        )
                        self.assertNotEqual(0, p2.returncode)
                        self.assertEqual(
                            1, len(stub.requests), "second process must not send"
                        )
                        data = read_ledger(ledger)
                        self.assertIsNotNone(data["stopped"])
                        self.assertEqual(status, data["stopped"]["status"])
                    finally:
                        stub.close()

    def test_default_mode_unchanged_and_nonfatal_not_reclassified(self):
        stub = self._stub(lambda b, h: (500, b'{"error":"boom"}'))
        proc = run_adapter(
            [
                "--endpoint",
                stub.url,
                "--model",
                "stub-model",
                "--prompt-version",
                "accept",
            ],
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(1, len(stub.requests))
        # acceptance mode: 500 is recorded but does not stop the run
        stub2 = StubEndpoint(lambda b, h: (500, b'{"error":"boom"}'))
        try:
            proc2 = run_adapter(
                acceptance_args(stub2.url, self.ledger),
                hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
                timeout=15,
            )
            self.assertNotEqual(0, proc2.returncode)
            data = read_ledger(self.ledger)
            self.assertIsNone(data["stopped"])
            self.assertEqual("endpoint", data["attempts"][0]["error_class"])
            self.assertEqual(500, data["attempts"][0]["http_status"])
        finally:
            stub2.close()

    def test_server_model_matching_missing_different_retained(self):
        for server_model, expect in (
            ("stub-model", "stub-model"),
            (None, None),
            ("other-model", "other-model"),
        ):
            with self.subTest(server_model=server_model):
                with tempfile.TemporaryDirectory() as td:
                    ledger = os.path.join(td, "ledger.json")
                    stub = StubEndpoint(
                        lambda b, h, m=server_model: (
                            200,
                            with_model_envelope('{"action":"wait"}', model=m),
                        )
                    )
                    try:
                        proc = run_adapter(
                            acceptance_args(stub.url, ledger),
                            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
                            timeout=15,
                        )
                        self.assertEqual(0, proc.returncode, proc.stderr)
                        entry = read_ledger(ledger)["attempts"][0]
                        self.assertEqual("stub-model", entry["requested_model"])
                        self.assertEqual(expect, entry["server_model"])
                    finally:
                        stub.close()

    def test_corrupt_ledger_fails_closed_no_request(self):
        with open(self.ledger, "w", encoding="utf-8") as handle:
            handle.write("{not json")
        stub = self._stub(
            lambda body, headers: (
                200,
                with_model_envelope('{"action":"wait"}', model="stub-model"),
            )
        )
        proc = run_adapter(
            acceptance_args(stub.url, self.ledger),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(0, len(stub.requests))

    def test_cap_mismatch_fails_closed(self):
        data = {"version": 1, "cap": 10, "stopped": None, "attempts": []}
        with open(self.ledger, "w", encoding="utf-8") as handle:
            json.dump(data, handle)
        stub = self._stub(
            lambda body, headers: (
                200,
                with_model_envelope('{"action":"wait"}', model="stub-model"),
            )
        )
        proc = run_adapter(
            acceptance_args(stub.url, self.ledger, cap=20),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(0, len(stub.requests))

    def test_credentials_never_in_ledger_sidecar_stdout_stderr(self):
        stub = self._stub(
            lambda body, headers: (
                200,
                with_model_envelope('{"action":"wait"}', model="stub-model"),
            )
        )
        sidecar = os.path.join(self.td.name, "sidecar.json")
        env = {"LATTICE_TEST_KEY": SECRET}
        proc = run_adapter(
            acceptance_args(
                stub.url,
                self.ledger,
                extra=[
                    "--api-key-env",
                    "LATTICE_TEST_KEY",
                    "--sidecar",
                    sidecar,
                ],
            ),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            env_extra=env,
            timeout=15,
        )
        self.assertEqual(0, proc.returncode, proc.stderr)
        for blob in (
            open(self.ledger, encoding="utf-8").read(),
            open(sidecar, encoding="utf-8").read(),
            proc.stdout,
            proc.stderr,
        ):
            self.assertNotIn(SECRET, blob)
        lines = proc.stdout.splitlines()
        self.assertEqual(2, len(lines))
        for line in lines:
            json.loads(line)  # stdout strictly valid protocol JSON


    def test_opt_in_request_fields_absent_by_default(self):
        stub = self._stub(lambda b, h: (200, with_model_envelope('{"action":"wait"}', model="stub-model")))
        proc = run_adapter(
            acceptance_args(stub.url, self.ledger),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertEqual(1, len(stub.requests))
        import json as j

        body = j.loads(stub.requests[0][2].decode("utf-8"))
        self.assertEqual({"model", "messages", "temperature"}, set(body.keys()))

    def test_opt_in_request_fields_present_when_set(self):
        stub = self._stub(lambda b, h: (200, with_model_envelope('{"action":"wait"}', model="stub-model")))
        proc = run_adapter(
            acceptance_args(
                stub.url,
                self.ledger,
                extra=["--reasoning-effort", "none", "--max-completion-tokens", "2048"],
            ),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertEqual(0, proc.returncode, proc.stderr)
        import json as j

        body = j.loads(stub.requests[0][2].decode("utf-8"))
        self.assertEqual("none", body["reasoning_effort"])
        self.assertEqual(2048, body["max_completion_tokens"])

    def test_max_request_bytes_guard_fails_closed_no_send(self):
        stub = self._stub(lambda b, h: (200, with_model_envelope('{"action":"wait"}', model="stub-model")))
        proc = run_adapter(
            acceptance_args(
                stub.url, self.ledger, extra=["--max-request-bytes", "10"]
            ),
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(0, len(stub.requests), "guard must trip before any HTTP send")
        data = read_ledger(self.ledger)
        self.assertEqual(1, len(data["attempts"]))
        self.assertEqual("request-too-large", data["attempts"][0]["error_class"])

    def test_lock_sidecar_is_stable_and_excludes_while_held(self):
        # The lock lives on "<ledger>.lock", a file that is never replaced:
        # flocking the ledger itself would race its atomic-replace inode swap.
        import fcntl
        import threading
        import time

        import importlib.util

        spec = importlib.util.spec_from_file_location(
            "lattice_model_agent", os.path.join(os.path.dirname(__file__), "lattice_model_agent.py")
        )
        agent = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(agent)

        ledger = os.path.join(self.td.name, "ledger.json")
        lock_path = ledger + ".lock"
        held_fd = os.open(lock_path, os.O_RDWR | os.O_CREAT, 0o600)
        fcntl.flock(held_fd, fcntl.LOCK_EX)
        try:
            done = []

            def try_reserve():
                led = agent.AcceptanceLedger(ledger, 10, "stub-model")
                try:
                    led.reserve()
                    done.append(True)
                except agent.AcceptanceRefused:
                    done.append(False)

            worker = threading.Thread(target=try_reserve)
            worker.start()
            worker.join(2.0)
            self.assertTrue(worker.is_alive(), "reserve must block while the sidecar is held")
            self.assertEqual([], done)
            fcntl.flock(held_fd, fcntl.LOCK_UN)
            worker.join(10.0)
            self.assertFalse(worker.is_alive())
            self.assertEqual([True], done)
            data = read_ledger(ledger)
            self.assertEqual(1, len(data["attempts"]))
        finally:
            os.close(held_fd)

    def test_concurrent_reservations_no_lost_updates(self):
        # Parallel reservers on one ledger: every successful reservation must
        # survive with a unique n, and successes must equal ledger entries.
        import threading

        import importlib.util

        spec = importlib.util.spec_from_file_location(
            "lattice_model_agent", os.path.join(os.path.dirname(__file__), "lattice_model_agent.py")
        )
        agent = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(agent)

        ledger = os.path.join(self.td.name, "ledger.json")
        cap = 120
        successes = []
        lock = threading.Lock()

        def hammer():
            led = agent.AcceptanceLedger(ledger, cap, "stub-model")
            n = 0
            try:
                while True:
                    led.reserve()
                    n += 1
            except (agent.AcceptanceRefused, agent.AcceptanceStopped):
                pass
            with lock:
                successes.append(n)

        threads = [threading.Thread(target=hammer) for _ in range(8)]
        for t in threads:
            t.start()
        for t in threads:
            t.join(60.0)
        self.assertFalse(any(t.is_alive() for t in threads))
        data = read_ledger(ledger)
        ns = [a["n"] for a in data["attempts"]]
        self.assertEqual(cap, sum(successes))
        self.assertEqual(cap, len(ns))
        self.assertEqual(len(ns), len(set(ns)), "duplicate attempt numbers: lost update")
        self.assertEqual(list(range(1, cap + 1)), ns)


if __name__ == "__main__":
    unittest.main()
