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
    args = parser.parse_args(argv)
    if args.request_timeout_ms < 1:
        parser.error("--request-timeout-ms must be a positive integer")
    return args


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
    """One chat-completions call. Returns the reply text, or raises.

    Timeouts are distinguishable from other endpoint errors so the caller can
    apply the one-retry rule; everything else is an endpoint error.
    """
    messages = [
        {"role": "user", "content": build_prompt(observation)},
    ]
    body = json.dumps(
        {"model": args.model, "messages": messages, "temperature": args.temperature},
        separators=(",", ":"),
    ).encode("utf-8")
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
    return content


class ModelTimeout(Exception):
    pass


class EndpointError(Exception):
    pass


class ParseFailure(Exception):
    pass


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

        try:
            content = call_model(args, api_key, message)
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
                content = call_model(args, api_key, message)
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
