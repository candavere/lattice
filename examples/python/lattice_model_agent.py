#!/usr/bin/env python3
"""A model-backed Lattice external agent: standard library only, Python 3.8+.

It speaks protocol 1 (docs/EXTERNAL_AGENT_PROTOCOL.md) on stdin/stdout, one JSON
value per line, LF-terminated, UTF-8, and nothing else on stdout ever. For each
observation it asks the configured chat-completions endpoint for one action,
validates the reply against the model-reply shape documented below, and emits
the wire envelope Lattice expects. It never guesses: any failure — endpoint
error, request timeout, a reply that fails parse or validation, malformed
protocol input — is one stderr line and a non-zero exit.

Model-reply shape (what the adapter asks the model to emit, and what it
accepts; anything else is a parse failure):

    {"action":"wait"}
    {"action":"move","zone_id":<int>}
    {"action":"collect","resource_id":<int>}

The JSON object must carry exactly the keys shown for its action; `zone_id` and
`resource_id` must be integer ids inside the observation's ranges (move targets
a zone id in 0..len(zones)-1, collect targets a resource id in
0..len(resources)-1). A reply carrying any extra key — including a `step`
key — is rejected, translated to nothing, and the process exits non-zero.
The adapter translates a valid reply into the protocol 1 wire envelope:

    {"type":"action","step":N,"kind":"Wait"}
    {"type":"action","step":N,"kind":"Move","zone_id":N}
    {"type":"action","step":N,"kind":"Collect","resource_id":N}

with `step` echoing the observation being answered, `kind` the exact-cased
ActionKind member name, and the conditional fields present exactly when the
kind requires them.

Endpoint contract: the endpoint speaks OpenAI-compatible chat completions. The
adapter POSTs {"model", "messages", "temperature"} to the URL given by
--endpoint (the full chat-completions path), and reads the reply text from the
first choice's message content. Anything else is an endpoint error.

Credentials: --api-key-env names an environment variable holding the key. The
key travels only in the Authorization header of the request — never in the URL
or a query string, never on argv, never in logs or the sidecar file. If the
variable is named and not set to a non-empty value, the adapter exits non-zero
before the handshake.

Sidecar: with --sidecar, one JSON object is written at startup (before the
handshake): endpoint host, model, temperature, prompt version, parser version.
The write happens before hello_ack is sent; if it fails, the process exits
non-zero without handshaking. The sidecar never carries the key.

Retry: one retry is allowed on a request *timeout*, and only when
    elapsed + request-timeout-ms + safety margin <= step_timeout_ms
where step_timeout_ms is read from hello.limits (the budget Lattice actually
offered). A hello that does not make the budget known authorises no retry.
An endpoint error never retries. A timeout after the permitted retry is a
failure like any other: one stderr line, non-zero exit.

Stderr is diagnostic only; stdout is the protocol.

Acceptance mode (opt-in, OFF by default): with --acceptance-ledger PATH
(and optional --acceptance-max-attempts N, default 400, max 400), every
HTTP attempt — retries included — is reserved in a durable JSON ledger
shared by both seat processes at the same PATH, before it is sent. A
reserved slot is never reclaimed after a crash. HTTP 401/403/429 marks
the run stopped so no later process sends. Mutual exclusion is taken on a
stable PATH.lock sidecar (the ledger itself is replaced atomically, which
would swap the inode an flock is held on). The ledger records the
requested model id and the server-reported response.model (or null when
missing/malformed) per attempt, plus HTTP status and error class; it
never records credentials, headers, env values or raw responses, and it
lives wherever the caller points it (keep it outside public source).
Without --acceptance-ledger, default behavior and timing are unchanged.
"""

import argparse
import json
import os
import socket
import sys
import time
import urllib.error
import urllib.request

PROTOCOL = 1  # the only version Lattice speaks; see the spec's section 2
PARSER_VERSION = "1"  # bump when the model-reply shape or its validation changes
CONTENT_MAX_BYTES = 1048576  # refuse an unbounded response body; one line, spec §7
SAFETY_MARGIN_MS = 100  # headroom for the one permitted retry decision

PROMPT_TEMPLATE = """You are playing Lattice, a resource-collection game on a zone graph.
Reply with exactly one JSON object and nothing else. No markdown, no code fence, no commentary.

Allowed replies (exact key sets; no other keys are accepted):
{"action":"wait"}
{"action":"move","zone_id":N}
{"action":"collect","resource_id":N}

Rules:
- N is an integer, not a string, not a boolean, not a float.
- For move, zone_id must be between 0 and %d.
- For collect, resource_id must be between 0 and %d.
- Output only the JSON object, nothing before or after it.

Observation:
%s"""


def log(message):
    """Diagnostics go to stderr and nowhere else: stdout is the protocol."""
    sys.stderr.write("lattice_model_agent: %s\n" % message)
    sys.stderr.flush()


def die(message):
    """The settled failure policy: one stderr line, non-zero exit, no guess."""
    log(message)
    return 1


def send(message):
    """Write one protocol line, then flush it.

    The flush is not optional. Lattice reads with a step timeout, so a buffered
    line is indistinguishable from an agent that stopped answering, and the match
    is scored a loss (spec section 7).
    """
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def is_int(value):
    """A JSON integer: Python's bool is an int subclass, so exclude it."""
    return isinstance(value, int) and not isinstance(value, bool)


def read_json_line():
    """One line from stdin as parsed JSON, or None on clean EOF, or False on a
    framing violation."""
    line = sys.stdin.readline()
    if line == "":
        return None
    if line.endswith("\n"):
        line = line[:-1]
    if line.endswith("\r") or line == "":
        # A blank line, or a CR-terminated one, is a framing violation rather
        # than whitespace to tidy away (spec section 1).
        return False
    try:
        return json.loads(line)
    except ValueError:
        return False


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Model-backed Lattice external agent (protocol 1)."
    )
    parser.add_argument(
        "--endpoint",
        required=True,
        help="full URL of the OpenAI-compatible chat-completions path",
    )
    parser.add_argument(
        "--model", required=True, help="model id to send in the request"
    )
    parser.add_argument(
        "--api-key-env",
        default=None,
        help="name of the environment variable holding the API key",
    )
    parser.add_argument(
        "--temperature",
        type=float,
        default=0.0,
        help="sampling temperature to send (default 0)",
    )
    parser.add_argument(
        "--request-timeout-ms",
        type=int,
        default=1500,
        help="HTTP request timeout in milliseconds (default 1500)",
    )
    parser.add_argument(
        "--prompt-version",
        required=True,
        help="id of the prompt template; bump when the template changes",
    )
    parser.add_argument(
        "--sidecar",
        default=None,
        help="write the comparability tuple (endpoint host, model, temperature, "
        "prompt version, parser version) here at startup",
    )
    parser.add_argument(
        "--reasoning-effort",
        choices=("none", "low", "medium", "high"),
        default=None,
        help="OPT-IN: include reasoning_effort in the request body (for "
        "reasoning models that reject temperature at nonzero effort). "
        "Omitted from the request when unset.",
    )
    parser.add_argument(
        "--max-completion-tokens",
        type=int,
        default=None,
        help="OPT-IN: include max_completion_tokens in the request body to "
        "cap billed output tokens (reasoning tokens included). Omitted when unset.",
    )
    parser.add_argument(
        "--max-request-bytes",
        type=int,
        default=None,
        help="OPT-IN: refuse to send and fail closed when the serialized "
        "request body would exceed this many bytes. Off by default.",
    )
    parser.add_argument(
        "--acceptance-ledger",
        default=None,
        help="OPT-IN acceptance mode: durable attempt ledger at this path. "
        "Both seat processes of an acceptance pair must be given the SAME "
        "path. Off by default; default behavior is unchanged when absent.",
    )
    parser.add_argument(
        "--acceptance-max-attempts",
        type=int,
        default=400,
        help="global HTTP attempt cap in acceptance mode (default 400). "
        "Counts every request, retries included, across processes.",
    )
    args = parser.parse_args(argv)
    if args.request_timeout_ms < 1:
        parser.error("--request-timeout-ms must be a positive integer")
    if args.acceptance_max_attempts < 1:
        parser.error("--acceptance-max-attempts must be a positive integer")
    if args.acceptance_max_attempts > 400:
        parser.error("--acceptance-max-attempts must not exceed 400")
    if args.acceptance_ledger is None and "--acceptance-max-attempts" in argv:
        parser.error("--acceptance-max-attempts requires --acceptance-ledger")
    if args.max_completion_tokens is not None and args.max_completion_tokens < 1:
        parser.error("--max-completion-tokens must be a positive integer")
    if args.max_request_bytes is not None and args.max_request_bytes < 1:
        parser.error("--max-request-bytes must be a positive integer")
    return args


FATAL_HTTP_STATUSES = (401, 403, 429)
ACCEPTANCE_LOCK_WAIT_S = 30.0


class AcceptanceStopped(Exception):
    pass


class AcceptanceRefused(Exception):
    pass


class _LedgerLock:
    """Bounded exclusive cross-process lock via fcntl.flock on a sidecar file.

    The lock is taken on "<ledger path>.lock", never on the ledger itself:
    the ledger is rewritten by atomic replace, which swaps its inode, and an
    flock held on the old inode does not exclude a process that opens the path
    after the replace. The sidecar is created once and never replaced or
    unlinked, so its inode is stable and the exclusion is real.

    Crash behaviour: the lock is advisory on the open file descriptor and is
    released by the OS when the holding process exits, so an abandoned lock
    cannot deadlock a later process. Wait is bounded (no infinite spin).
    fcntl is POSIX-only; acceptance mode is therefore POSIX-only and fails
    closed where flock is unavailable, while default mode never imports it.
    """

    def __init__(self, path):
        self._path = path + ".lock"
        self._fd = None

    def __enter__(self):
        try:
            import fcntl
        except ImportError:
            raise AcceptanceRefused(
                "acceptance mode requires POSIX file locking (fcntl); refusing"
            )
        deadline = time.monotonic() + ACCEPTANCE_LOCK_WAIT_S
        while True:
            self._fd = os.open(self._path, os.O_RDWR | os.O_CREAT, 0o600)
            try:
                fcntl.flock(self._fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                self._fcntl = fcntl
                return self
            except OSError:
                os.close(self._fd)
                self._fd = None
                if time.monotonic() >= deadline:
                    raise AcceptanceRefused(
                        "acceptance ledger lock wait exceeded 30s; refusing to proceed"
                    )
                time.sleep(0.05)

    def __exit__(self, *exc):
        if self._fd is not None:
            try:
                getattr(self, "_fcntl", None)  # locked below if present
                import fcntl as _fcntl

                _fcntl.flock(self._fd, _fcntl.LOCK_UN)
            finally:
                os.close(self._fd)
                self._fd = None
        return False


class AcceptanceLedger:
    """Durable cross-process attempt ledger for opt-in acceptance mode.

    Reserve-before-send: each HTTP attempt is recorded (status "reserved")
    and fsynced before the request is made, so a crash after reservation
    leaves the slot consumed (never reclaimed). Outcomes are recorded in the
    same locked transaction as reservation where applicable; a fatal HTTP
    status (401/403/429) also sets `stopped` before any further attempt is
    allowed. Corrupt/ambiguous state fails closed.
    """

    def __init__(self, path, cap, requested_model):
        self.path = path
        self.cap = cap
        self.requested_model = requested_model

    def _read(self):
        try:
            with open(self.path, "r", encoding="utf-8") as handle:
                raw = handle.read()
                handle.flush()
        except FileNotFoundError:
            return {
                "version": 1,
                "cap": self.cap,
                "stopped": None,
                "attempts": [],
            }
        if raw == "":
            # Only reachable when the lock's O_CREAT created the file before
            # any reservation under the lock; no send can have happened yet.
            return {
                "version": 1,
                "cap": self.cap,
                "stopped": None,
                "attempts": [],
            }
        try:
            data = json.loads(raw)
        except ValueError:
            raise AcceptanceRefused("acceptance ledger is corrupt; refusing to proceed")
        if (
            not isinstance(data, dict)
            or data.get("version") != 1
            or not isinstance(data.get("attempts"), list)
            or data.get("cap") != self.cap
        ):
            raise AcceptanceRefused(
                "acceptance ledger state is ambiguous (version, cap or shape "
                "mismatch); refusing to proceed"
            )
        return data

    def _write(self, data):
        tmp = self.path + ".tmp.%d" % os.getpid()
        with open(tmp, "w", encoding="utf-8") as handle:
            json.dump(data, handle, separators=(",", ":"))
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp, self.path)
        try:
            dfd = os.open(os.path.dirname(os.path.abspath(self.path)), os.O_RDONLY)
            try:
                os.fsync(dfd)
            finally:
                os.close(dfd)
        except OSError:
            pass

    def reserve(self):
        with _LedgerLock(self.path):
            data = self._read()
            if data.get("stopped") is not None:
                raise AcceptanceStopped(
                    "acceptance run stopped by fatal HTTP response; refusing to send"
                )
            if len(data["attempts"]) >= self.cap:
                raise AcceptanceRefused(
                    "acceptance attempt cap %d exhausted; refusing to send" % self.cap
                )
            n = len(data["attempts"]) + 1
            data["attempts"].append(
                {
                    "n": n,
                    "status": "reserved",
                    "http_status": None,
                    "error_class": None,
                    "requested_model": self.requested_model,
                    "server_model": None,
                }
            )
            self._write(data)
            return n

    def record(self, n, http_status, error_class, server_model):
        with _LedgerLock(self.path):
            data = self._read()
            if not (1 <= n <= len(data["attempts"])):
                raise AcceptanceRefused(
                    "acceptance ledger has no attempt %d; refusing" % n
                )
            entry = data["attempts"][n - 1]
            entry["status"] = "recorded"
            entry["http_status"] = http_status
            entry["error_class"] = error_class
            entry["server_model"] = server_model
            if http_status in FATAL_HTTP_STATUSES:
                data["stopped"] = {
                    "reason": "fatal-http",
                    "status": http_status,
                    "attempt": n,
                }
            self._write(data)


def endpoint_host(endpoint):
    """The host[:port] of the endpoint URL — enough to identify the serving
    machine, never enough to carry a path, a query, or credentials."""
    from urllib.parse import urlsplit

    parts = urlsplit(endpoint)
    host = parts.hostname or ""
    if parts.port:
        return "%s:%d" % (host, parts.port)
    return host


def write_sidecar(path, args):
    record = {
        "endpoint_host": endpoint_host(args.endpoint),
        "model": args.model,
        "temperature": args.temperature,
        "prompt_version": args.prompt_version,
        "parser_version": PARSER_VERSION,
    }
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(record, handle, separators=(",", ":"))


def build_prompt(observation):
    map_graph = observation["map"]
    zone_max = len(map_graph["zones"]) - 1
    resource_max = len(map_graph["resources"]) - 1
    return PROMPT_TEMPLATE % (
        zone_max,
        resource_max,
        json.dumps(observation, separators=(",", ":"), sort_keys=True),
    )


def call_model(args, api_key, observation):
    status = _call_model(args, api_key, observation)
    return status[0]


def _call_model(args, api_key, observation):
    """One chat-completions call. Returns (reply text, server_model).

    server_model is the envelope's "model" value exactly as reported
    (None when missing/malformed); acceptance mode records it truthfully.
    Timeouts are distinguishable from other endpoint errors so the caller
    can apply the one-retry rule; everything else is an endpoint error.
    """
    messages = [
        {"role": "user", "content": build_prompt(observation)},
    ]
    payload = {"model": args.model, "messages": messages, "temperature": args.temperature}
    if getattr(args, "reasoning_effort", None) is not None:
        payload["reasoning_effort"] = args.reasoning_effort
    if getattr(args, "max_completion_tokens", None) is not None:
        payload["max_completion_tokens"] = args.max_completion_tokens
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    if getattr(args, "max_request_bytes", None) is not None and len(body) > args.max_request_bytes:
        raise RequestTooLarge(
            "request body %d bytes exceeds --max-request-bytes %d; not sent"
            % (len(body), args.max_request_bytes)
        )
    request = urllib.request.Request(args.endpoint, data=body, method="POST")
    request.add_header("Content-Type", "application/json")
    request.add_header("Accept", "application/json")
    request.add_header("User-Agent", "lattice-model-agent/%s" % PARSER_VERSION)
    if api_key is not None:
        request.add_header("Authorization", "Bearer " + api_key)

    timeout = args.request_timeout_ms / 1000.0
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read(CONTENT_MAX_BYTES + 1)
    except (socket.timeout, TimeoutError) as error:
        raise ModelTimeout(str(error)) from error
    except urllib.error.HTTPError as error:
        exc = EndpointError("endpoint error: %s" % error)
        try:
            exc.http_status = int(error.code)
        except Exception:
            pass
        raise exc from error
    except urllib.error.URLError as error:
        reason = getattr(error, "reason", None)
        if isinstance(reason, (socket.timeout, TimeoutError)):
            raise ModelTimeout(str(reason)) from error
        raise EndpointError(
            "endpoint error: %s" % (reason if reason is not None else error)
        ) from error
    except Exception as error:  # http.client.RemoteDisconnected and friends
        raise EndpointError("endpoint error: %s" % error) from error

    if len(payload) > CONTENT_MAX_BYTES:
        raise EndpointError(
            "endpoint replied with more than %d bytes" % CONTENT_MAX_BYTES
        )
    try:
        envelope = json.loads(payload)
        content = envelope["choices"][0]["message"]["content"]
    except (ValueError, KeyError, IndexError, TypeError) as error:
        raise ParseFailure(
            "endpoint reply is not a chat completion with a message: %s" % error
        ) from error
    if not isinstance(content, str):
        raise ParseFailure("endpoint reply message content is not a string")
    server_model = envelope.get("model") if isinstance(envelope, dict) else None
    if not isinstance(server_model, str):
        server_model = None
    return content, server_model


class ModelTimeout(Exception):
    http_status = None


class EndpointError(Exception):
    http_status = None


class ParseFailure(Exception):
    http_status = None


class RequestTooLarge(EndpointError):
    """The serialized request body exceeded --max-request-bytes; not sent."""

    http_status = None


def _acceptance_call(ledger, args, api_key, observation):
    """One HTTP attempt under an acceptance ledger (reserve before send).

    Returns (content, server_model) on success. Raises the original
    ModelTimeout/EndpointError/ParseFailure after durably recording the
    outcome; AcceptanceStopped/AcceptanceRefused propagate for die().
    """
    n = ledger.reserve()
    try:
        content, server_model = _call_model(args, api_key, observation)
    except RequestTooLarge as error:
        try:
            ledger.record(n, None, "request-too-large", None)
        except (AcceptanceRefused, AcceptanceStopped) as ledger_error:
            raise AcceptanceRefused(str(ledger_error)) from error
        raise AcceptanceRefused(str(error)) from error
    except (ModelTimeout, EndpointError, ParseFailure) as error:
        error_class = (
            "timeout"
            if isinstance(error, ModelTimeout)
            else ("parse" if isinstance(error, ParseFailure) else "endpoint")
        )
        try:
            ledger.record(n, getattr(error, "http_status", None), error_class, None)
        except (AcceptanceRefused, AcceptanceStopped) as ledger_error:
            raise AcceptanceRefused(str(ledger_error)) from error
        raise
    ledger.record(n, 200, None, server_model)
    return content, server_model


def validate_model_reply(observation, content):
    """Parse and validate a model reply against the adapter-internal shape.

    Returns the validated dict on success; raises ParseFailure on any deviation,
    including an extra key (step or otherwise).
    """
    try:
        reply = json.loads(content)
    except ValueError as error:
        raise ParseFailure("model reply is not JSON: %s" % error) from error
    if not isinstance(reply, dict):
        raise ParseFailure("model reply is not a JSON object")

    action = reply.get("action")
    if action == "wait":
        expected_keys = {"action"}
    elif action == "move":
        expected_keys = {"action", "zone_id"}
    elif action == "collect":
        expected_keys = {"action", "resource_id"}
    else:
        raise ParseFailure("model reply names an unknown action: %r" % (action,))
    if set(reply) != expected_keys:
        raise ParseFailure("model reply carries the wrong keys: %s" % sorted(reply))

    map_graph = observation["map"]
    if action == "move":
        zone_id = reply["zone_id"]
        zone_max = len(map_graph["zones"]) - 1
        if not is_int(zone_id) or zone_id < 0 or zone_id > zone_max:
            raise ParseFailure(
                "model reply zone_id %r is not an integer id in 0..%d"
                % (zone_id, zone_max)
            )
    elif action == "collect":
        resource_id = reply["resource_id"]
        resource_max = len(map_graph["resources"]) - 1
        if not is_int(resource_id) or resource_id < 0 or resource_id > resource_max:
            raise ParseFailure(
                "model reply resource_id %r is not an integer id in 0..%d"
                % (resource_id, resource_max)
            )
    return reply


def wire_action(observation, reply):
    """Translate a validated model reply into the protocol 1 wire envelope."""
    step = observation["step"]
    action = reply["action"]
    if action == "wait":
        return {"type": "action", "step": step, "kind": "Wait"}
    if action == "move":
        return {
            "type": "action",
            "step": step,
            "kind": "Move",
            "zone_id": reply["zone_id"],
        }
    return {
        "type": "action",
        "step": step,
        "kind": "Collect",
        "resource_id": reply["resource_id"],
    }


def own_agent_state(observation):
    agent_id = observation.get("agent_id")
    states = observation.get("agent_states")
    if not is_int(agent_id) or not isinstance(states, list):
        return None
    for state in states:
        if isinstance(state, dict) and state.get("agent_id") == agent_id:
            return state
    return None


def observation_is_usable(message):
    """The parts of an observation this adapter relies on: a non-negative
    integer envelope step, an agent entry of its own, and a map with zones,
    resources and choke points."""
    if not isinstance(message, dict):
        return False
    if message.get("type") != "observation":
        return False
    step = message.get("step")
    if not is_int(step) or step < 0:
        return False
    map_graph = message.get("map")
    if not isinstance(map_graph, dict):
        return False
    for key in ("zones", "resources", "choke_points"):
        if not isinstance(map_graph.get(key), list):
            return False
    return own_agent_state(message) is not None


def main(argv=None):
    args = parse_args(argv if argv is not None else sys.argv[1:])

    # The sidecar is written before the handshake; a write failure is fatal
    # before hello_ack goes out — never a half-initialized run.
    if args.sidecar is not None:
        try:
            write_sidecar(args.sidecar, args)
        except OSError as error:
            return die("cannot write --sidecar %s: %s" % (args.sidecar, error))

    api_key = None
    if args.api_key_env is not None:
        api_key = os.environ.get(args.api_key_env)
        if not api_key:
            return die(
                "--api-key-env names '%s', which is not set to a non-empty value"
                % args.api_key_env
            )

    # The wire is UTF-8 whatever the caller's locale happens to be (spec section 1).
    # newline="\n" pins the terminator to LF on every host; see lattice_agent.py.
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")

    hello = read_json_line()
    if hello is None:
        return (
            0  # Lattice closed stdin before hello: the match is over (spec section 3).
        )
    if hello is False:
        return die("framing violation or unreadable hello on stdin")
    if not isinstance(hello, dict) or hello.get("type") != "hello":
        return die(
            "expected a hello line, got %r"
            % (hello.get("type") if isinstance(hello, dict) else type(hello).__name__,)
        )
    if hello.get("protocol") != PROTOCOL:
        # Exact match or nothing: no downgrade, no guessing (spec section 2).
        return die(
            "hello asked for protocol %r; this agent speaks %d"
            % (hello.get("protocol"), PROTOCOL)
        )

    limits = hello.get("limits")
    step_timeout_ms = None
    if isinstance(limits, dict):
        offered = limits.get("step_timeout_ms")
        if is_int(offered) and offered >= 1:
            step_timeout_ms = offered

    send({"type": "hello_ack", "protocol": PROTOCOL})

    ledger = None
    if args.acceptance_ledger is not None:
        ledger = AcceptanceLedger(
            args.acceptance_ledger, args.acceptance_max_attempts, args.model
        )
        try:
            with _LedgerLock(ledger.path):
                ledger._read()
        except (AcceptanceRefused, AcceptanceStopped) as error:
            return die(str(error))
        except OSError as error:
            return die("cannot open --acceptance-ledger: %s" % error)

    while True:
        message = read_json_line()
        if message is None:
            return 0  # Lattice closed stdin: the match is over (spec section 3).
        if message is False:
            return die("framing violation or unreadable line on stdin")

        if isinstance(message, dict) and message.get("type") == "error":
            # Lattice's own failure report; its fields are diagnostic only and an
            # agent must not branch on them (spec 8.1).
            log("lattice reported an error; waiting for the stream to close")
            continue
        if not observation_is_usable(message):
            return die("malformed observation from Lattice")

        started = time.monotonic()

        state = own_agent_state(message)
        if "transit" in state:
            # Mid-crossing the agent is on an edge, not at a node: the
            # protocol-correct action is Wait (spec 5.4).
            send({"type": "action", "step": message["step"], "kind": "Wait"})
            continue

        def do_call():
            if ledger is None:
                return call_model(args, api_key, message)
            return _acceptance_call(ledger, args, api_key, message)[0]

        try:
            content = do_call()
        except (AcceptanceStopped, AcceptanceRefused) as error:
            return die(str(error))
        except ModelTimeout:
            if not _retry_permitted(args, step_timeout_ms, started):
                return die(
                    "model request timed out after %d ms" % args.request_timeout_ms
                )
            log(
                "model request timed out after %d ms; one retry within the step budget"
                % args.request_timeout_ms
            )
            try:
                content = do_call()
            except (AcceptanceStopped, AcceptanceRefused) as error:
                return die(str(error))
            except ModelTimeout:
                return die(
                    "model request timed out again after %d ms"
                    % args.request_timeout_ms
                )
            except (EndpointError, ParseFailure) as error:
                return die(str(error))
        except (EndpointError, ParseFailure) as error:
            return die(str(error))

        try:
            reply = validate_model_reply(message, content)
        except ParseFailure as error:
            return die(str(error))
        send(wire_action(message, reply))


def _retry_permitted(args, step_timeout_ms, started):
    """One retry on a timeout, only if it fits the budget Lattice offered.
    A handshake that did not make the budget known authorises no retry."""
    if step_timeout_ms is None:
        return False
    elapsed_ms = (time.monotonic() - started) * 1000.0
    return elapsed_ms + args.request_timeout_ms + SAFETY_MARGIN_MS <= step_timeout_ms


if __name__ == "__main__":
    sys.exit(main())
