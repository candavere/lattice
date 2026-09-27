#!/usr/bin/env python3
"""A minimal Lattice external agent: standard library only, Python 3.8+.

It speaks protocol 1 (docs/EXTERNAL_AGENT_PROTOCOL.md) on stdin/stdout: one JSON
value per line, LF-terminated, UTF-8, and nothing else on stdout ever.

The policy is deliberately simple and completely deterministic: collect the
resource in the zone you are standing in, otherwise take the first hop towards
the nearest unclaimed resource, otherwise wait. It exists to show the shape of
an agent, not to win. See README.md beside this file for how to score it.
"""

import json
import sys
from collections import deque

PROTOCOL = 1  # the only version Lattice speaks in v3.0 (spec section 2)


def log(message):
    """Diagnostics go to stderr and nowhere else: stdout is the protocol."""
    sys.stderr.write("lattice_agent: %s\n" % message)
    sys.stderr.flush()


def send(message):
    """Write one protocol line, then flush it.

    The flush is not optional. Lattice reads with a step timeout, so a buffered
    line is indistinguishable from an agent that stopped answering, and the match
    is scored a loss (spec section 7).
    """
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def adjacency(map_graph):
    """The undirected zone graph, read off the choke points.

    A Move is granted only between two zones a choke connects, in either
    direction, so the choke list is exactly the adjacency to plan over (spec 6.3).
    """
    edges = {}
    for choke in map_graph.get("choke_points", []):
        here, there = choke["from_zone_id"], choke["to_zone_id"]
        edges.setdefault(here, []).append(there)
        edges.setdefault(there, []).append(here)
    for zone_id in edges:
        edges[zone_id].sort()  # ascending, so the successor order is fixed
    return edges


def first_hop(edges, start, targets):
    """The zone one hop from `start` that begins a shortest path to a target.

    Breadth-first, so the hop is on a shortest path. Ties between equally near
    targets go to whichever the traversal reaches first over the sorted adjacency,
    which is fixed -- so the policy is deterministic without the tie-break
    promising an ordering it does not implement (spec 5.2 asks for unclaimed
    resources in ascending id order, which `decide` does apply).
    """
    if start in targets:
        return None
    seen = {start}
    queue = deque([(start, None)])
    while queue:
        zone_id, hop = queue.popleft()
        for neighbour in edges.get(zone_id, ()):
            if neighbour in seen:
                continue
            step = neighbour if hop is None else hop
            if neighbour in targets:
                return step
            seen.add(neighbour)
            queue.append((neighbour, step))
    return None


def decide(observation):
    """The single action for this observation: pure, so the same observation
    always produces the same action and a run is reproducible from its seed."""
    mine = next(s for s in observation["agent_states"] if s["agent_id"] == observation["agent_id"])
    step = observation["step"]

    # Mid-crossing the agent is on an edge, not at a node: zone_id is still the
    # departure node and transit describes the crossing. The honest action is to
    # wait (spec 5.4).
    if "transit" in mine:
        return {"type": "action", "step": step, "kind": "Wait"}

    here = mine["zone_id"]
    claimed = set(observation["claims"])
    wanted = [r for r in observation["map"]["resources"] if r["id"] not in claimed]

    # Standing on one, so take it. Collect carries no zone_id: the wire must not
    # include a field the action would ignore (spec section 6.1).
    for resource in sorted(wanted, key=lambda r: r["id"]):
        if resource["zone_id"] == here:
            return {"type": "action", "step": step, "kind": "Collect", "resource_id": resource["id"]}

    hop = first_hop(adjacency(observation["map"]), here, {r["zone_id"] for r in wanted})
    if hop is None:
        return {"type": "action", "step": step, "kind": "Wait"}  # nothing reachable left
    return {"type": "action", "step": step, "kind": "Move", "zone_id": hop}


def main():
    # The wire is UTF-8 whatever the caller's locale happens to be (spec section 1).
    #
    # newline="\n" is load-bearing on Windows, not decoration. A text stream left
    # at the default newline=None translates every "\n" written to it into
    # os.linesep, which is "\r\n" on Windows. Lattice reads one LF-terminated line
    # per message and rejects a CR anywhere in a line as malformed_json
    # (spec section 1), so on Windows that translation fails the very first
    # hello_ack and every match ends as a wire failure. Naming the newline pins
    # the terminator to LF on every host.
    #
    # stdin keeps the default: Lattice writes bare LF, and universal newlines
    # turns a CRLF into a single LF, so there is nothing to strip here.
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")

    while True:
        # readline rather than iteration: one line in, one line out, with nothing
        # read ahead of the exchange being answered.
        line = sys.stdin.readline()
        if line == "":
            return 0  # Lattice closed stdin: the match is over (spec section 3).
        if line.endswith("\n"):
            line = line[:-1]
        if line.endswith("\r") or line == "":
            # A blank line, or a CR-terminated one, is a framing violation rather
            # than whitespace to tidy away (spec section 1).
            log("framing violation on stdin; stopping")
            return 1
        try:
            message = json.loads(line)
        except ValueError as error:
            log("unreadable line from Lattice: %s" % error)
            return 1

        kind = message.get("type")
        if kind == "hello":
            if message.get("protocol") != PROTOCOL:
                # Exact match or nothing: no downgrade, no guessing (spec section 2).
                log("hello asked for protocol %r; this agent speaks %d"
                    % (message.get("protocol"), PROTOCOL))
                return 1
            send({"type": "hello_ack", "protocol": PROTOCOL})
        elif kind == "observation":
            send(decide(message))
        else:
            # `error` lands here when a match fails, and its fields are diagnostic
            # only: an agent must not parse them or branch on them (spec 8.1).
            # A message outside the five-type catalogue is not something a
            # conforming agent can act on either, so ignoring it is all it can do.
            log("ignoring a %r message" % kind)


if __name__ == "__main__":
    sys.exit(main())
