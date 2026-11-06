#!/usr/bin/env python3
"""Tests for lattice_model_agent.py: the thin model adapter.

Standard library only, Python 3.8+, driven as a subprocess against a stub
OpenAI-compatible chat-completions endpoint over real HTTP on 127.0.0.1, so the
wire protocol, the HTTP call, and the failure paths are all the real ones.
"""

import json
import os
import subprocess
import sys
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ADAPTER = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "lattice_model_agent.py"
)

PROTOCOL_HELLO = {
    "type": "hello",
    "protocol": 1,
    "scenario": "standard",
    "seed": 1001,
    "agent_slot": 0,
    "max_ticks": 200,
    "agent_count": 2,
    "limits": {"step_timeout_ms": 5000, "match_timeout_ms": 1030000},
}

OBSERVATION = {
    "type": "observation",
    "step": 7,
    "agent_id": 0,
    "map": {
        "zones": [
            {"id": 0, "position": {"x": 0, "y": 0}, "max_occupancy": 2147483647},
            {"id": 1, "position": {"x": 3, "y": 0}, "max_occupancy": 2147483647},
        ],
        "resources": [
            {"id": 0, "zone_id": 1, "position": {"x": 3, "y": 0}},
        ],
        "choke_points": [
            {"id": 0, "from_zone_id": 0, "to_zone_id": 1, "max_occupancy": 1},
        ],
    },
    "agent_states": [
        {"agent_id": 0, "zone_id": 0, "score": 0},
        {"agent_id": 1, "zone_id": 1, "score": 0},
    ],
    "claims": [],
    "step_number": 7,
}

TRANSIT_OBSERVATION = {
    "type": "observation",
    "step": 3,
    "agent_id": 0,
    "map": OBSERVATION["map"],
    "agent_states": [
        {
            "agent_id": 0,
            "zone_id": 0,
            "score": 0,
            "transit": {"from_zone_id": 0, "to_zone_id": 1, "remaining_ticks": 1},
        },
        {"agent_id": 1, "zone_id": 1, "score": 0},
    ],
    "claims": [],
    "step_number": 3,
}

SECRET = "TEST-ONLY-NOT-A-SECRET-PLACEHOLDER"


def hello_line(step_timeout_ms=5000):
    hello = dict(PROTOCOL_HELLO)
    hello["limits"] = {
        "step_timeout_ms": step_timeout_ms,
        "match_timeout_ms": step_timeout_ms * 200 + 30000,
    }
    return json.dumps(hello, separators=(",", ":"))


def observation_line(observation):
    return json.dumps(observation, separators=(",", ":"))


class StubEndpoint:
    """A stub OpenAI-compatible chat completions endpoint on 127.0.0.1.

    `behavior` is a callable taking (body_bytes, headers) and returning
    (status, content_bytes). Every request is recorded.
    """

    def __init__(self, behavior):
        self.behavior = behavior
        self.requests = []

        adapter = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                body = self.rfile.read(length) if length else b""
                adapter.requests.append((self.path, dict(self.headers), body))
                status, payload = adapter.behavior(body, dict(self.headers))
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.url = "http://127.0.0.1:%d/v1/chat/completions" % self.port
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


def chat_payload(content):
    return json.dumps(
        {"choices": [{"message": {"role": "assistant", "content": content}}]}
    ).encode("utf-8")


def legal_wait(body, headers):
    return 200, chat_payload('{"action":"wait"}')


def legal_move(body, headers):
    return 200, chat_payload('{"action":"move","zone_id":1}')


def legal_collect(body, headers):
    return 200, chat_payload('{"action":"collect","resource_id":0}')


def http_500(body, headers):
    return 500, b'{"error":"boom"}'


def not_json_content(body, headers):
    return 200, chat_payload("this is not json")


def wrong_shape_content(body, headers):
    return 200, chat_payload('{"action":"fly","speed":3}')


def sleep_then_wait(seconds):
    def behavior(body, headers):
        time.sleep(seconds)
        return 200, chat_payload('{"action":"wait"}')

    return behavior


def run_adapter(args, stdin_text, env_extra=None, timeout=15):
    env = os.environ.copy()
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    if env_extra:
        env.update(env_extra)
    return subprocess.run(
        [sys.executable, ADAPTER] + args,
        input=stdin_text,
        capture_output=True,
        text=True,
        timeout=timeout,
        env=env,
    )


def stdout_json_lines(proc):
    return [json.loads(line) for line in proc.stdout.splitlines() if line.strip()]


class ModelAgentTest(unittest.TestCase):
    def setUp(self):
        self.stub = None

    def tearDown(self):
        if self.stub is not None:
            self.stub.close()

    def _stub(self, behavior):
        self.stub = StubEndpoint(behavior)
        return self.stub

    def _happy_run(
        self,
        behavior,
        reply_check,
        extra_args=(),
        observation=None,
        step_timeout_ms=5000,
    ):
        stub = self._stub(behavior)
        args = [
            "--endpoint",
            stub.url,
            "--model",
            "stub",
            "--prompt-version",
            "v1",
        ] + list(extra_args)
        proc = run_adapter(
            args,
            hello_line(step_timeout_ms)
            + "\n"
            + observation_line(observation or OBSERVATION)
            + "\n",
        )
        reply_check(stub, proc)
        return stub, proc

    # --- happy paths: each action kind reaches the exact wire envelope

    def test_wait_reply_produces_exact_wire_envelope(self):
        def check(stub, proc):
            self.assertEqual(0, proc.returncode, proc.stderr)
            lines = stdout_json_lines(proc)
            self.assertEqual(["hello_ack", "action"], [m["type"] for m in lines])
            self.assertEqual({"type": "action", "step": 7, "kind": "Wait"}, lines[1])

        self._happy_run(legal_wait, check)

    def test_move_reply_produces_exact_wire_envelope(self):
        def check(stub, proc):
            self.assertEqual(0, proc.returncode, proc.stderr)
            lines = stdout_json_lines(proc)
            self.assertEqual(
                {"type": "action", "step": 7, "kind": "Move", "zone_id": 1}, lines[1]
            )

        self._happy_run(legal_move, check)

    def test_collect_reply_produces_exact_wire_envelope(self):
        def check(stub, proc):
            self.assertEqual(0, proc.returncode, proc.stderr)
            lines = stdout_json_lines(proc)
            self.assertEqual(
                {"type": "action", "step": 7, "kind": "Collect", "resource_id": 0},
                lines[1],
            )

        self._happy_run(legal_collect, check)

    def test_wire_step_echoes_the_answered_observation_at_zero(self):
        observation = dict(OBSERVATION, step=0, step_number=0)

        def check(stub, proc):
            self.assertEqual(0, proc.returncode, proc.stderr)
            lines = stdout_json_lines(proc)
            self.assertEqual(0, lines[1]["step"])

        self._happy_run(legal_wait, check, observation=observation)

    def test_transit_observation_yields_wait_without_a_model_call(self):
        def check(stub, proc):
            self.assertEqual(0, proc.returncode, proc.stderr)
            lines = stdout_json_lines(proc)
            self.assertEqual({"type": "action", "step": 3, "kind": "Wait"}, lines[1])
            self.assertEqual(
                [], stub.requests, "a mid-crossing observation must not reach the model"
            )

        self._happy_run(legal_wait, check, observation=TRANSIT_OBSERVATION)

    # --- model-reply shape enforcement

    def test_reply_with_an_extra_step_key_is_rejected(self):
        behavior = lambda body, headers: (
            200,
            chat_payload('{"action":"wait","step":7}'),
        )

        def check(stub, proc):
            self.assertNotEqual(0, proc.returncode)
            self.assertNotIn('"kind"', proc.stdout)

        self._happy_run(behavior, check)

    def test_reply_with_an_unknown_action_is_rejected(self):
        self._happy_run(
            wrong_shape_content,
            lambda stub, proc: self.assertNotEqual(0, proc.returncode),
        )

    def test_unparseable_reply_exits_nonzero(self):
        self._happy_run(
            not_json_content, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    def test_move_reply_with_id_out_of_range_is_rejected(self):
        def behavior(body, headers):
            return 200, chat_payload('{"action":"move","zone_id":99}')

        self._happy_run(
            behavior, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    def test_collect_reply_with_id_out_of_range_is_rejected(self):
        def behavior(body, headers):
            return 200, chat_payload('{"action":"collect","resource_id":99}')

        self._happy_run(
            behavior, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    def test_move_reply_with_negative_id_is_rejected(self):
        def behavior(body, headers):
            return 200, chat_payload('{"action":"move","zone_id":-1}')

        self._happy_run(
            behavior, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    def test_move_reply_with_boolean_id_is_rejected(self):
        def behavior(body, headers):
            return 200, chat_payload('{"action":"move","zone_id":true}')

        self._happy_run(
            behavior, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    # --- malformed protocol input

    def test_observation_with_missing_step_is_malformed(self):
        broken = dict(OBSERVATION)
        del broken["step"]
        self._happy_run(
            legal_wait,
            lambda stub, proc: self.assertNotEqual(0, proc.returncode),
            observation=broken,
        )

    def test_observation_with_non_integer_step_is_malformed(self):
        self._happy_run(
            legal_wait,
            lambda stub, proc: self.assertNotEqual(0, proc.returncode),
            observation=dict(OBSERVATION, step=7.5),
        )

    def test_observation_with_boolean_step_is_malformed(self):
        self._happy_run(
            legal_wait,
            lambda stub, proc: self.assertNotEqual(0, proc.returncode),
            observation=dict(OBSERVATION, step=True),
        )

    def test_line_with_wrong_type_is_malformed(self):
        self._happy_run(
            legal_wait,
            lambda stub, proc: self.assertNotEqual(0, proc.returncode),
            observation=dict(OBSERVATION, **{"type": "action"}),
        )

    def test_hello_with_wrong_protocol_is_a_bad_handshake(self):
        hello = dict(PROTOCOL_HELLO, protocol=2)
        stub = self._stub(legal_wait)
        proc = run_adapter(
            ["--endpoint", stub.url, "--model", "stub", "--prompt-version", "v1"],
            json.dumps(hello, separators=(",", ":")) + "\n",
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual("", proc.stdout)

    # --- endpoint failures

    def test_http_500_exits_nonzero(self):
        self._happy_run(
            http_500, lambda stub, proc: self.assertNotEqual(0, proc.returncode)
        )

    def test_timeout_without_retry_budget_exits_nonzero_with_one_request(self):
        # request-timeout-ms (1500) cannot fit inside the offered step budget
        # (1200 ms), so no retry may happen: exactly one request, then exit.
        stub = self._stub(sleep_then_wait(3))
        args = [
            "--endpoint",
            stub.url,
            "--model",
            "stub",
            "--prompt-version",
            "v1",
            "--request-timeout-ms",
            "1500",
        ]
        proc = run_adapter(
            args,
            hello_line(1200) + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(1, len(stub.requests))

    def test_timeout_with_retry_budget_retries_exactly_once_then_exits_nonzero(self):
        stub = self._stub(sleep_then_wait(2))
        args = [
            "--endpoint",
            stub.url,
            "--model",
            "stub",
            "--prompt-version",
            "v1",
            "--request-timeout-ms",
            "300",
        ]
        proc = run_adapter(
            args,
            hello_line(10000) + "\n" + observation_line(OBSERVATION) + "\n",
            timeout=15,
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(2, len(stub.requests))

    def test_missing_limits_means_no_retry(self):
        # A hello that does not make the budget known cannot authorise a retry.
        hello = dict(PROTOCOL_HELLO)
        del hello["limits"]

        def boom(body, headers):
            return 500, b"{}"

        stub = self._stub(boom)
        proc = run_adapter(
            ["--endpoint", stub.url, "--model", "stub", "--prompt-version", "v1"],
            json.dumps(hello, separators=(",", ":"))
            + "\n"
            + observation_line(OBSERVATION)
            + "\n",
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(1, len(stub.requests))

    # --- credentials and sidecar

    def test_sidecar_records_the_comparability_tuple_and_carries_no_key(self):
        import tempfile

        with tempfile.TemporaryDirectory() as td:
            sidecar = os.path.join(td, "sidecar.json")
            stub = self._stub(legal_wait)
            env = {"LATTICE_TEST_KEY": SECRET}
            args = [
                "--endpoint",
                stub.url,
                "--model",
                "stub-model",
                "--prompt-version",
                "v7",
                "--api-key-env",
                "LATTICE_TEST_KEY",
                "--temperature",
                "0.25",
                "--sidecar",
                sidecar,
            ]
            proc = run_adapter(
                args,
                hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
                env_extra=env,
            )
            self.assertEqual(0, proc.returncode, proc.stderr)
            with open(sidecar, "r", encoding="utf-8") as handle:
                content = handle.read()
            record = json.loads(content)
            self.assertEqual("127.0.0.1:%d" % stub.port, record["endpoint_host"])
            self.assertEqual("stub-model", record["model"])
            self.assertEqual(0.25, record["temperature"])
            self.assertEqual("v7", record["prompt_version"])
            self.assertIn("parser_version", record)
            self.assertNotIn(SECRET, content)

    def test_sidecar_write_failure_exits_before_the_handshake(self):
        stub = self._stub(legal_wait)
        sidecar = os.path.join("/definitely/not/a/real/dir", "sidecar.json")
        proc = run_adapter(
            [
                "--endpoint",
                stub.url,
                "--model",
                "stub",
                "--prompt-version",
                "v1",
                "--sidecar",
                sidecar,
            ],
            hello_line() + "\n",
        )
        self.assertNotEqual(0, proc.returncode)
        self.assertEqual(
            "", proc.stdout, "no hello_ack may be written when the sidecar write fails"
        )

    def test_api_key_travels_only_in_the_authorization_header(self):
        stub = self._stub(legal_wait)
        env = {"LATTICE_TEST_KEY": SECRET}
        args = [
            "--endpoint",
            stub.url,
            "--model",
            "stub",
            "--prompt-version",
            "v1",
            "--api-key-env",
            "LATTICE_TEST_KEY",
        ]
        proc = run_adapter(
            args,
            hello_line() + "\n" + observation_line(OBSERVATION) + "\n",
            env_extra=env,
        )
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertNotIn(SECRET, proc.stdout)
        self.assertNotIn(SECRET, proc.stderr)
        ((path, headers, body),) = stub.requests
        self.assertEqual("Bearer " + SECRET, headers.get("Authorization"))
        self.assertNotIn(SECRET, path)
        self.assertNotIn(SECRET, proc.stdout)

    def test_api_key_env_naming_an_unset_variable_exits_nonzero(self):
        stub = self._stub(legal_wait)
        args = [
            "--endpoint",
            stub.url,
            "--model",
            "stub",
            "--prompt-version",
            "v1",
            "--api-key-env",
            "LATTICE_TEST_KEY_DEFINITELY_UNSET_9f3a",
        ]
        env = {
            k: v
            for k, v in os.environ.items()
            if k != "LATTICE_TEST_KEY_DEFINITELY_UNSET_9f3a"
        }
        env["PYTHONDONTWRITEBYTECODE"] = "1"
        proc = subprocess.run(
            [sys.executable, ADAPTER] + args,
            input=hello_line() + "\n",
            capture_output=True,
            text=True,
            timeout=15,
            env=env,
        )
        self.assertNotEqual(0, proc.returncode)


if __name__ == "__main__":
    unittest.main()
