"""Tracked Playwright regression: the agent view paints recorded knowledge only.

Serves the committed site/ directory on a localhost port (no dependency on the
live GitHub Pages URL) and drives it through the bundled ?measure=1 geometry
probe (window.__latticeGeo), reading the recorded fog back out of the committed
site/infiltration.jsonl the same way a reader would.

This is the honest-agent-view stage. The recording itself already verifies; what
was painted on top of it was not always what the recording proves, and three
things are asserted here:

  * **Claim leak.** A chest painted green must be a chest that observer's own
    `PerceptionFilter` reported as already claimed (`VisibleClaims`). The
    omniscient `frame.claims` list is the world's, not the agent's, and the two
    disagree for much of the infiltration recording: at frame 9 the world has
    chest 1 claimed while the Infiltrator's tick-10 perception reports none, and
    at frame 19 the world has [1, 2] against the Infiltrator's empty list. The
    Sentry's own perceptions carry [1] and [1, 2] at those ticks, so the two ego
    views must genuinely differ there — a viewer that leaked the world's claims
    would draw them identical.
  * **The claim metric.** The count in the metrics panel is drawn from the same
    set the diamonds are, and says which set it is: an ego view reports the
    number of claims that observer's own `PerceptionFilter` reported, labelled
    "claims seen", at the terminal frame as everywhere else; ground truth keeps
    the omniscient world count under its own label. A schema-3 file records no
    perception at all, so its ego view is a derivation by this page and is
    labelled as one rather than as something an agent saw.
  * **Terminal frame mix.** At the last frame the fog is the last
    decision-time view (`fresh: false`), so the entity layer has to come from
    the world that decision was made from — frame last-1 — not from the
    post-step terminal world. Those differ: the pre-step world has the Sentry in
    zone 2 and the Infiltrator on score 2 with claims [1, 2]; the post-step world
    has both in zone 1 with the Infiltrator on 3 and claims [1, 2, 0]. Ground
    truth at the same frame stays post-step, because it is labelled ground
    truth.
  * **Split pins.** The playing recording's "View in repository" link must
    resolve to main, the branch the deployed file is on. The benchmark result
    JSONs were genuinely recorded at 6463e486 and must stay pinned there.
  * **Presence leak.** The zone table's occupancy column and the badge painted
    inside each room card are two more readings of one question — who is
    standing here — and both used to be answered from the omniscient
    `frame.agents` while the map above them was painted from the view's fog. A
    room holding a rival the observer has not currently observed therefore read
    "1 agent" under a painted ghost; a room the observer has never reached read
    "empty"; and a mid-transit agent was counted in the room it had left while
    its token was painted in the corridor. Every view now reports occupancy from
    the same fog the canvas was painted from, under a label that says which view
    it is counting, and a room it cannot see is reported as "last known" or
    "unexplored" rather than counted. The assertions are made against the live
    agent tokens read back out of the geometry probe, so a table that merely
    agreed with the page's own counting helper could not pass them.

Every expectation is derived from the recording the page loaded, never from a
constant: the frames, the claim sets, the agent zones and the scores all move
with the file. What will not be accepted is a recording whose recorded claims
never differ from the world's, or a viewer that paints the post-step terminal
world under a decision-time fog.

Run:  python3 -m unittest discover -s ui_tests -p 'test_*.py' -v
"""

import asyncio
import json
import os
import re
import shutil
import tempfile
import unittest

from playwright.async_api import async_playwright

from test_demo_page_ui import (
    SLIDE,
    fetch_recording,
    has_recorded_perception,
    perception_at,
    recorded_steps,
    start_server,
    wait_frame,
)

# The painted diamond colours, as they appear in site/app.js's COLORS.
CLAIMED_COLOR = "#3DA66B"
UNCLAIMED_COLOR = "#E0AF68"

# The commit the two benchmark result JSONs were genuinely recorded at. They are
# immutable there, so the number a reader gets back is the published one.
BENCH_PIN = "6463e486"

# Read-only probes. Each is a plain expression so `page.evaluate` can run it.
CANVAS_LABEL = "(document.querySelector('#viewer-canvas')?.getAttribute('aria-label') ?? '')"
MAP_CAPTION = "(document.querySelector('#map-caption')?.textContent ?? '')"
CLAIMED_STAT = "(document.querySelector('#stat-claimed')?.textContent ?? '')"
CLAIM_LABEL = "(document.querySelector('#stat-claim-label')?.textContent ?? '')"

# What the claim metric has to say it is counting, which is a different question
# in each view and the whole point of the label. Ground truth counts the
# omniscient world list; an ego view on a recording that stores its perceptions
# counts the set that observer's own filter reported, at the decision-time fog
# the canvas painted with it; an ego view on a recording that stores no
# perception counts what this page derived for itself and must never be called
# "seen", because nothing was recorded for anyone to see.
LABEL_GROUND = "claimed in world"
LABEL_SEEN = "claims seen"
LABEL_DERIVED = "claims in derived view"

PROV_HREFS = """() => Array.from(document.querySelectorAll('#prov-open a'))
  .map((a) => a.getAttribute('href') ?? '')"""
BENCH_HREFS = """() => ['#std-link', '#bot-link']
  .map((sel) => document.querySelector(sel)?.getAttribute('href') ?? '')"""

# The painted claim state of every chest, as the page filled it: {id: claimed}
# and {id: colour} in one read, both straight off the probe's own record of the
# diamonds it just drew rather than re-derived from the recording.
_CLAIMS_JS = """() => {
  const g = window.__latticeGeo;
  const claimed = {};
  const colors = {};
  // A page with no per-diamond record reports nothing here rather than throwing,
  // so the failure below is an assertion about the paint, not a JS exception.
  for (const items of Object.values(g.lootItems || {})) {
    for (const item of items) {
      claimed[item.id] = item.claimed;
      colors[item.id] = item.color;
    }
  }
  return {claimed, colors};
}"""

# What the entity layer painted, from the probe's own ink records: {agentId:
# {zone, text, ghost}}. A rival the observer has no memory of is drawn as
# nothing at all; a rival it only remembers is a ghost whose name is an age
# stamp rather than a score.
_AGENTS_JS = """() => {
  const g = window.__latticeGeo;
  const out = {};
  for (const [id, box] of Object.entries(g.names)) {
    out[id] = {zone: box.zone, text: box.text, ghost: false};
  }
  for (const [id, box] of Object.entries(g.ghosts)) {
    out[id] = {zone: box.zone, text: box.text, ghost: true};
  }
  return out;
}"""


# The zone table's own wording, as a name/value pair per room, and the label
# that says which view the occupancy column is counting.
ZONE_ROWS_JS = """() => Array.from(document.querySelectorAll('#zones-body tr')).map((tr) => ({
  room: tr.querySelector('.k')?.textContent ?? '',
  value: tr.querySelector('.v')?.textContent ?? '' }))"""
PRESENCE_LABEL = "(document.querySelector('#zone-presence-label')?.textContent ?? '')"
ZONES_TABLE_ARIA = "(document.querySelector('#zones-table')?.getAttribute('aria-label') ?? '')"

# Who is standing where, read off the probe's own ink rather than off any helper
# the page uses to compute it. A live token is one the canvas drew this frame:
# `tokens` holds its centre, `transit` the ones riding a corridor, `ghosts` the
# remembered rivals that are deliberately *not* tokens, and `rooms` the box each
# room card was painted into, so a token can be assigned to the room it is
# physically inside. Tokens that land in no room are corridor travellers and
# belong to no room's occupancy.
PAINTED_PRESENCE_JS = """() => {
  const g = window.__latticeGeo;
  const rooms = g.rooms || {};
  const counts = {};
  const members = {};
  for (const zid of Object.keys(rooms)) { counts[zid] = 0; members[zid] = []; }
  const strays = [];
  for (const [aid, t] of Object.entries(g.tokens || {})) {
    let home = null;
    for (const zid of Object.keys(rooms)) {
      const r = rooms[zid];
      if (t.x >= r.x && t.x <= r.x + r.w && t.y >= r.y && t.y <= r.y + r.h) { home = zid; break; }
    }
    if (home === null) { strays.push(Number(aid)); continue; }
    counts[home] += 1;
    members[home].push(Number(aid));
  }
  const pills = {};
  for (const [zid, p] of Object.entries(g.pills || {})) pills[zid] = p.text;
  return {
    counts,
    members,
    strays,
    pills,
    ghosts: Object.keys(g.ghosts || {}).map(Number),
    transit: Object.keys(g.transit || {}).map(Number),
    status: g.statusByZone || {},
  };
}"""

# What the zone occupancy column has to say it is counting. Which figure is
# honest depends entirely on the selected view, and that difference is the whole
# subject: ground truth may count the world, a recorded ego view may count only
# what that observer's own perception places in the room, a reconstructed view is
# this page's own derivation and must never borrow the word "seen", and a view
# with no perception at all has no count to give.
PRESENCE_GROUND = "agents present in world"
PRESENCE_SEEN = "agents observed in this view"
PRESENCE_DERIVED = "agents in derived view"
PRESENCE_UNAVAILABLE = "presence unavailable"

# The per-room word for a room this view does not currently observe. These are
# the canvas's own words for the same two rooms ("last known" is drawn on a
# stale card, "unexplored" on an unknown one), so the table cannot say a room is
# empty when the card above it says it has never been looked at.
WORD_STALE = "last known"
WORD_UNKNOWN = "unexplored"


def score_of(name_text):
    """The score in a painted agent name ("Infiltrator · 2"), or None.

    Read off the painted text rather than re-derived from the frame — reading
    the frame would prove nothing about what the page drew.
    """
    parts = str(name_text).rsplit("·", 1)
    if len(parts) != 2:
        return None
    try:
        return int(parts[1].strip())
    except ValueError:
        return None


# --- what the recording itself implies -------------------------------------

def frames_with_claims(recording):
    """Mirror of the page's frame list, each with its agent states and claims.

    Frame 0 is the recorded opening placement; frame i after that is the world
    step i produced. The recorded claim list travels with the frame, because
    every leak or mix in this file is a claim list paired with the wrong world.
    """
    header = recording[0]
    zone_count = len(header["Map"]["Zones"])
    agents = header["SimulationConfig"]["AgentCount"]
    frames = [{"agents": [{"AgentId": i, "ZoneId": i % zone_count, "Score": 0, "Transit": None}
                          for i in range(agents)], "claims": []}]
    for line in recorded_steps(recording):
        observed = line["Result"]["Observations"][0]
        frames.append({"agents": observed["AgentStates"],
                       "claims": sorted(observed["Claims"])})
    return frames


def leak_frames(recording, ego_id):
    """Frames where this agent's recorded claims are a strict subset of the
    world's — i.e. where painting the world's claims would tell the observer
    something its own filter never reported.

    Discovered from the file, not hard-coded, and required to be non-empty: a
    recording where the two always agree would make every assertion below pass
    without the viewer ever being honest.
    """
    steps = recorded_steps(recording)
    frames = frames_with_claims(recording)
    found = []
    for frame in range(len(frames)):
        perception = perception_at(steps, frame, ego_id)
        if perception is None:
            continue
        recorded = set(perception.get("VisibleClaims") or [])
        if recorded < set(frames[frame]["claims"]):
            found.append(frame)
    return found


def recorded_claims(perception):
    """The claim ids one recorded perception reports, as strings.

    A JS object's integer keys come back from `page.evaluate` as strings, so
    every id crossing into the probe read is normalised here. Comparing "1"
    against {1} would fail on type while meaning the same chest, and a check
    that fails on type teaches nothing.
    """
    return {str(r) for r in (perception.get("VisibleClaims") or [])}


def visible_chests(perception):
    """The chest ids this perception puts on the map: every resource it reports
    as observed or last-known. A resource it never saw is masked out of the
    drawing entirely, so it is expected to have no diamond at all.

    This is what makes the paint assertions non-vacuous. A page that reported no
    per-diamond state would otherwise pass a check that only ever asks "is any
    painted chest claimed?", by painting nothing.
    """
    return {str(r["ResourceId"]) for r in (perception.get("Resources") or [])
            if r["Status"] != 2}


def assert_paints_exactly(self, painted, perception, where):
    """The painted diamonds are exactly the ones this perception shows."""
    self.assertEqual(
        set(painted), visible_chests(perception),
        f"{where}: the page painted a different set of chests from the one this "
        f"perception shows (painted {sorted(painted)}, perception "
        f"{sorted(visible_chests(perception))})")


def view_name(recording, ego_id):
    roles = recording[0].get("AgentRoles") or []
    return roles[ego_id] if ego_id < len(roles) else f"agent {ego_id}"


def agent_ids(recording):
    return [p["AgentId"] for p in recorded_steps(recording)[0]["Perceptions"]]


# --- what the zone table has to say ---------------------------------------

def presence_of(cell):
    """The occupant count a zone-table cell reports, or None when it declines.

    Parsed from the page's own string so the check is about the wording a
    reader sees, not about an integer handed back by the page. A cell reads
    "<count> agent(s)", "empty", or "—" when the view has no count to give; any
    other shape is returned as a marker so a failing assertion prints what was
    actually painted instead of a bare mismatch.
    """
    head = str(cell).split("·")[0].strip()
    if head == "empty":
        return 0
    if head in ("—", "-", "") or head in (WORD_STALE, WORD_UNKNOWN):
        return None
    if re.fullmatch(r"\d+ agents?", head):
        return int(head.split()[0])
    return "UNREADABLE:" + head


def pill_of(text):
    """The count a room card's occupancy pill shows, or None.

    A capacity-capped room paints "present/max", so the left half is the count
    and the right half is the room's limit, not a second number to compare.
    """
    if text is None:
        return None
    return int(str(text).split("/")[0].strip())


def room_labels(recording):
    """{zone id: the room name both the canvas and the table print}, mirroring
    the page's own `roomLabel`, so table rows can be keyed by id instead of by
    position — a row order that shifted would otherwise silently re-label every
    expectation in the file."""
    out = {}
    for zone in recording[0]["Map"]["Zones"]:
        out[zone["Id"]] = re.sub(r"([a-z])([A-Z])", r"\1 \2", zone["Role"]) if zone.get("Role") \
            else "Room %s" % zone["Id"]
    return out


def world_index_of(steps, frame):
    """Which frame the page's entity layer is painted from at scrubber `frame`.

    The scrubbed world for a fresh decision-time view, and the last decision's
    world at the terminal frame — the same pairing the fog carries in
    `worldIndex`, so the two can be read side by side.
    """
    return frame if frame < len(steps) else len(steps) - 1


def recorded_presence(steps, frame, ego_id, frame_agents, zone_ids):
    """The occupancy a schema-4 observer's own perception supports, or None when
    the file records no perception for that frame.

    Counted from the recording and nothing else: the observer wherever the
    painted world has it standing, and a rival only where its perception entry
    reports it currently observed — a stale entry is a memory of a room, not an
    occupant of it, and an unknown one is no occupant of anything. A room the
    perception does not report as observed can hold nobody in this figure,
    because the view does not know what is in it.
    """
    perception = perception_at(steps, frame, ego_id)
    if perception is None:
        return None
    status = {z["ZoneId"]: z["Status"] for z in (perception.get("Zones") or [])}
    counts = {z: 0 for z in zone_ids}
    ego = frame_agents.get(ego_id)
    if ego and not ego.get("Transit") and status.get(ego["ZoneId"]) == 0:
        counts[ego["ZoneId"]] += 1
    for entry in (perception.get("Agents") or []):
        if entry["AgentId"] == ego_id or entry["Status"] != 0:
            continue
        last = entry.get("LastKnownState")
        if not last or last.get("Transit"):
            continue
        if status.get(last["ZoneId"]) != 0:
            continue
        counts[last["ZoneId"]] = counts.get(last["ZoneId"], 0) + 1
    return counts


def frame_agent_map(frames, index):
    return {a["AgentId"]: a for a in frames[index]["agents"]}


async def show(page, tick, view):
    """Select the perspective, slide to `tick`, and wait for that frame painted.

    The chip click and the slide each schedule a draw, and `wait_frame` checks
    the frame index, the probe's perspective and the active chip together, so
    this cannot read a frame the page has not finished drawing.
    """
    await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
    await page.evaluate(SLIDE, tick)
    await page.wait_for_function(wait_frame(tick, view))


class TestAgentViewHonesty(unittest.TestCase):
    """Everything here drives the committed recording through the page."""

    def _drive(self, body, name="infiltration.jsonl"):
        """Run `await body(page, recording, errors)` against the named committed
        file and return the console-error list it collected on the way, so each
        test can assert the page was quiet as well as correct."""
        httpd, port = start_server()
        base = f"http://127.0.0.1:{port}/"
        try:
            async def run():
                async with async_playwright() as p:
                    browser = await p.chromium.launch()
                    try:
                        context = await browser.new_context(
                            viewport={"width": 1440, "height": 900},
                            reduced_motion="reduce")
                        page = await context.new_page()
                        errors = []
                        page.on("console",
                                lambda m: errors.append(m.text) if m.type == "error" else None)
                        page.on("pageerror", lambda e: errors.append(str(e)))
                        # Reduced motion, so the agent view's radar pulse cannot
                        # redraw the canvas between a repaint and a read.
                        await page.goto(base + "?measure=1&" + name,
                                        wait_until="networkidle")
                        await page.wait_for_function(
                            "window.__latticeGeo && window.__latticeGeo.frame")
                        # The page always opens the default preset, so a different
                        # committed recording has to be selected through the chip
                        # a visitor would use, not smuggled in on the query string.
                        if name != "infiltration.jsonl":
                            key = name.split(".")[0]
                            await page.locator(
                                f'#scenario-chips .chip[data-id="{key}"]').click()
                            await page.wait_for_function(
                                "() => document.querySelector('#source-label')"
                                ".textContent === %s" % json.dumps(name))
                        recording = fetch_recording(base, name)
                        return await body(page, recording, errors)
                    finally:
                        await browser.close()

            return asyncio.run(run())
        finally:
            httpd.shutdown()
            httpd.server_close()

    def _no_console_errors(self, errors):
        self.assertEqual(errors, [], "the page logged errors")

    async def _load_variant(self, page, lines, name):
        """Hand the page a controlled variant of a committed recording, through
        the file input a visitor would use.

        Some states the viewer has to survive are absent from both committed
        files — a schema-4 frame whose perceptions are missing, a rival the
        observer has only a memory of, a rival it has never seen. Asserting on
        them needs a recording that contains them, and the committed ones are
        immutable evidence, so the variant is written to a temp file and the
        temp directory is removed again. Nothing under site/ is touched.
        """
        tmp = tempfile.mkdtemp()
        try:
            path = os.path.join(tmp, name)
            with open(path, "w", encoding="utf-8") as handle:
                for line in lines:
                    handle.write(json.dumps(line) + "\n")
            await page.set_input_files("#file-input", path)
            await page.wait_for_function(
                "() => document.querySelector('#source-label').textContent === %s"
                % json.dumps(name))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    # -- the moments this stage is about exist -------------------------------

    def test_recording_contains_claims_an_agent_never_saw(self):
        """The moments the assertions below rest on must exist in the file, or
        they would pass vacuously. The frames named in the stage spec are
        required to be among the ones discovered, so this cannot drift into
        checking a different moment than the one under repair."""

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            self.assertEqual(len(steps[0]["Perceptions"]), 2,
                             "expected a two-agent recording")
            for ego in agent_ids(recording):
                with self.subTest(view=view_name(recording, ego)):
                    self.assertTrue(
                        leak_frames(recording, ego),
                        f"{view_name(recording, ego)}'s recorded claims never "
                        "differ from the world's, so there is nothing here to "
                        "be honest about")
            self.assertIn(9, leak_frames(recording, agent_ids(recording)[1]))
            self.assertIn(19, leak_frames(recording, agent_ids(recording)[1]))
            return errors

        self._no_console_errors(self._drive(body))

    # -- claim leak ----------------------------------------------------------

    def test_agent_view_paints_only_its_own_recorded_claims(self):
        """Every painted diamond must agree with that observer's own
        `VisibleClaims`, on every frame of its view, in colour as well as in
        fact.

        Both directions matter. A chest the recording never reported as claimed
        must not be green — that is the leak. A chest it did report must not be
        amber — that would be the viewer hiding knowledge the agent genuinely
        had, which is a different dishonesty and just as wrong.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            for ego in agent_ids(recording):
                where = view_name(recording, ego)
                for frame in range(len(steps) + 1):
                    perception = perception_at(steps, frame, ego)
                    if perception is None:
                        continue
                    await show(page, frame, str(ego))
                    painted = await page.evaluate(_CLAIMS_JS)
                    expected = recorded_claims(perception)
                    assert_paints_exactly(self, painted["claimed"], perception,
                                          f"{where} at frame {frame}")
                    for rid, is_claimed in painted["claimed"].items():
                        with self.subTest(view=where, frame=frame, chest=rid):
                            self.assertEqual(
                                is_claimed, rid in expected,
                                f"{where} at frame {frame}: chest {rid} painted "
                                f"{'claimed' if is_claimed else 'unclaimed'}, but the "
                                f"recording's VisibleClaims is {sorted(expected)}")
                        with self.subTest(view=where, frame=frame, chest=rid, colour=True):
                            self.assertEqual(
                                painted["colors"][rid],
                                CLAIMED_COLOR if rid in expected else UNCLAIMED_COLOR,
                                f"{where} at frame {frame}: chest {rid} painted in "
                                "the wrong colour for the claim state recorded")
            return errors

        self._no_console_errors(self._drive(body))

    def test_the_two_ego_views_differ_on_claims_at_the_named_frames(self):
        """Frames 9 and 19, both ego views and ground truth, side by side.

        At frame 19 the Sentry's tick-20 perception carries [1, 2] and the
        Infiltrator's carries none, while the world has [1, 2] for both. A viewer
        that painted the world's claims would render the two views identically
        here, which is the defect in its most visible form. Ground truth keeps
        the omniscient list, because that is what it is labelled.
        """

        async def body(page, recording, errors):
            frames = frames_with_claims(recording)
            for frame in (9, 19):
                world = {str(r) for r in frames[frame]["claims"]}
                self.assertTrue(world, f"frame {frame} has no claims in the world")
                painted_by_view = {}
                for ego in agent_ids(recording):
                    await show(page, frame, str(ego))
                    painted = (await page.evaluate(_CLAIMS_JS))["claimed"]
                    perception = perception_at(recorded_steps(recording), frame, ego)
                    assert_paints_exactly(self, painted, perception,
                                          f"frame {frame} {view_name(recording, ego)}")
                    recorded = recorded_claims(perception)
                    with self.subTest(frame=frame, view=view_name(recording, ego)):
                        self.assertEqual(
                            {rid for rid, on in painted.items() if on}, recorded,
                            f"frame {frame} {view_name(recording, ego)}: painted "
                            f"claims disagree with the recording's VisibleClaims")
                    painted_by_view[ego] = painted

                # The two views must not be the same picture, and at least one
                # of them must have a green chest, or this proves nothing.
                self.assertTrue(
                    any(on for painted in painted_by_view.values() for on in painted.values()),
                    f"frame {frame}: no view painted a claimed chest at all")
                self.assertNotEqual(
                    [dict(p) for p in painted_by_view.values()],
                    [dict(p) for p in painted_by_view.values()][1:],
                    f"frame {frame}: both ego views painted identical claim states, "
                    "so the recording's two per-agent views are not being told apart")

                await show(page, frame, "ground")
                truth = (await page.evaluate(_CLAIMS_JS))["claimed"]
                with self.subTest(frame=frame, view="ground"):
                    self.assertEqual({rid for rid, on in truth.items() if on}, world)
                self.assertEqual(
                    set(truth), {str(r["Id"]) for r in recording[0]["Map"]["Resources"]},
                    f"frame {frame} ground truth: every chest in the map is drawn, "
                    "because ground truth masks nothing")
            return errors

        self._no_console_errors(self._drive(body))

    # -- the claim metric ---------------------------------------------------

    def test_claim_metric_counts_what_the_selected_view_saw(self):
        """The number in the metrics panel, and the label that says what it
        counts, on every frame of every view.

        The panel is a readout of the same world the canvas was painted from,
        so in an ego view it has to be a readout of the *observer's* claim set
        — the recording's `VisibleClaims` — and not of `frame.claims`, which is
        the world's. The two disagree for much of this file, so a panel counting
        the world would show a number no agent ever had, under a label that
        claims an agent did. Ground truth keeps the world list, and says so in
        its own words so the two numbers can never be read as the same figure.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            total = len(recording[0]["Map"]["Resources"])

            for ego in agent_ids(recording):
                where = view_name(recording, ego)
                for frame in range(len(steps) + 1):
                    perception = perception_at(steps, frame, ego)
                    if perception is None:
                        continue
                    await show(page, frame, str(ego))
                    with self.subTest(view=where, frame=frame):
                        self.assertEqual(
                            await page.evaluate(CLAIM_LABEL), LABEL_SEEN,
                            f"{where} at frame {frame}: the claim metric is not "
                            "labelled as what the observer saw")
                        self.assertEqual(
                            await page.evaluate(CLAIMED_STAT),
                            f"{len(recorded_claims(perception))} / {total}",
                            f"{where} at frame {frame}: the claim metric is not "
                            f"the count of VisibleClaims "
                            f"{sorted(recorded_claims(perception))}")

            for frame in range(len(steps) + 1):
                await show(page, frame, "ground")
                with self.subTest(view="ground", frame=frame):
                    self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_GROUND)
                    self.assertEqual(await page.evaluate(CLAIMED_STAT),
                                     f"{len(frames[frame]['claims'])} / {total}")
            return errors

        self._no_console_errors(self._drive(body))

    def test_claim_metric_follows_perspective_and_scrubbing(self):
        """Switching view and dragging the scrubber must not leave the previous
        view's number or its label behind.

        The metric and the label are two separate strings, so a page that
        rewrote one and not the other would show, say, "claims seen 2 / 3" over
        a ground-truth frame. Each step below ends on a frame whose two numbers
        genuinely differ from the step before, and reads both strings
        immediately after the repaint `wait_frame` has confirmed.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            total = len(recording[0]["Map"]["Resources"])
            ids = agent_ids(recording)
            # Frames where the two ego views and ground truth really do disagree,
            # so a stale readout is detectable rather than merely possible.
            moments = [f for f in leak_frames(recording, ids[1]) if f > 0]
            self.assertTrue(moments, "no frame distinguishes the views here")
            frame = moments[-1]

            expected = {
                "ground": (LABEL_GROUND, f"{len(frames[frame]['claims'])} / {total}"),
            }
            for ego in ids:
                expected[str(ego)] = (
                    LABEL_SEEN,
                    f"{len(recorded_claims(perception_at(steps, frame, ego)))} / {total}")

            for view in ["ground"] + [str(e) for e in ids] + ["ground"] + [str(e) for e in ids]:
                await show(page, frame, view)
                with self.subTest(view=view, where="label"):
                    self.assertEqual(await page.evaluate(CLAIM_LABEL),
                                     expected[view][0],
                                     f"{view} at frame {frame}: a stale claim label")
                with self.subTest(view=view, where="count"):
                    self.assertEqual(await page.evaluate(CLAIMED_STAT),
                                     expected[view][1],
                                     f"{view} at frame {frame}: a stale claim count")

            # And the same while scrubbing across the recording, since the
            # scrubber is the other way a frame changes under a live metric.
            for tick in [0, frame, len(steps), 0]:
                await show(page, tick, str(ids[0]))
                with self.subTest(view=str(ids[0]), tick=tick, where="label"):
                    self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_SEEN)
                want = perception_at(steps, tick, ids[0])
                want = len(recorded_claims(want)) if want else None
                with self.subTest(view=str(ids[0]), tick=tick, where="count"):
                    self.assertEqual(await page.evaluate(CLAIMED_STAT),
                                     f"{want} / {total}" if want is not None else "—")
            return errors

        self._no_console_errors(self._drive(body))

    def test_reconstructed_ego_view_is_not_labelled_as_seen(self):
        """`site/demo.jsonl` is schema v3: it records no perception, so its ego
        view is a 2-hop sightline this page derived for itself. Nothing about it
        was recorded, and nothing in it was ever seen by an agent.

        The file is pinned at v3 on purpose: `NoPerceptionNotice` fires for a
        recording at schema 4 or later that records no perception, so migrating
        it would turn this example of a *silently* read pre-perception recording
        into one the reader is obliged to flag. The assertion therefore checks
        the field rather than the version, so it keeps protecting the property
        even if the pin is ever revisited.

        The metric must say that in words — never "claims seen", never the
        ground-truth label — and the number it shows must be the derivation's
        own: the claims of the chests inside the derived sightline. Counting
        every claim in the world under a derived label would still be a
        spectator's number wearing the page's own clothes.
        """

        async def body(page, recording, errors):
            self.assertFalse(has_recorded_perception(recording),
                             "demo.jsonl now records perceptions, so it is no "
                             "longer the reconstruction this asserts")
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            total = len(recording[0]["Map"]["Resources"])
            zones = {z["Id"]: z for z in recording[0]["Map"]["Zones"]}
            adjacency = {z["Id"]: set() for z in zones.values()}
            for c in recording[0]["Map"]["ChokePoints"]:
                adjacency[c["FromZoneId"]].add(c["ToZoneId"])
                adjacency[c["ToZoneId"]].add(c["FromZoneId"])

            vision = 2  # the viewer's own fallback cone for a file that declares none

            def within_vision(frame, ego):
                """Zones within `vision` hops of `ego`, BFS — the same walk the
                page makes, so the count is derived from the file rather than
                from whatever the page happens to print."""
                agent = next(a for a in frames[frame]["agents"] if a["AgentId"] == ego)
                start = agent["Transit"]["ToZoneId"] if agent["Transit"] else agent["ZoneId"]
                depth = {start: 0}
                frontier = [start]
                while frontier:
                    here = frontier.pop(0)
                    if depth[here] >= vision:
                        continue
                    for nxt in sorted(adjacency[here]):
                        if nxt not in depth:
                            depth[nxt] = depth[here] + 1
                            frontier.append(nxt)
                return set(depth)

            resources = recording[0]["Map"]["Resources"]
            ids = [a["AgentId"] for a in frames[0]["agents"]]
            self.assertTrue(ids, "the recording has no agents")
            checked = 0
            for frame in range(len(steps) + 1):
                world = set(frames[frame]["claims"])
                if not world:
                    continue
                for ego in ids:
                    in_sight = {r["Id"] for r in resources
                                if r["ZoneId"] in within_vision(frame, ego)}
                    await show(page, frame, str(ego))
                    with self.subTest(view=str(ego), frame=frame):
                        self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_DERIVED,
                                         f"ego {ego} at frame {frame}: a derived "
                                         "sightline must not be labelled as seen")
                        self.assertEqual(
                            await page.evaluate(CLAIMED_STAT),
                            f"{len(world & in_sight)} / {total}",
                            f"ego {ego} at frame {frame}: the derived count must "
                            "cover the world's claims inside the derived sightline")
                    checked += 1
            self.assertTrue(checked, "no frame in demo.jsonl has a claim to report")

            await show(page, len(steps), "ground")
            self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_GROUND)
            self.assertEqual(await page.evaluate(CLAIMED_STAT),
                             f"{len(frames[len(steps)]['claims'])} / {total}")
            return errors

        self._no_console_errors(self._drive(body, "demo.jsonl"))

    # -- terminal frame -----------------------------------------------------

    def test_terminal_frame_paints_the_world_the_last_decision_came_from(self):
        """The last frame, both ego views.

        The fog there is the last decision-time view, so the tokens, the scores,
        the claim colours and the metrics must all be the ones of the world that
        decision was made from (frame last-1) — never the post-step terminal
        world, which no agent ever saw.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            last = len(steps)
            pre = frames[last - 1]    # the world the last decision was made from
            post = frames[last]        # the world that last step produced
            # If these agreed, nothing below could detect the mix at all.
            self.assertNotEqual([a["ZoneId"] for a in pre["agents"]],
                                [a["ZoneId"] for a in post["agents"]],
                                "the pre-step and post-step terminal worlds are "
                                "identical, so this cannot detect the mix")
            self.assertNotEqual([a["Score"] for a in pre["agents"]],
                                [a["Score"] for a in post["agents"]])
            self.assertNotEqual(pre["claims"], post["claims"])
            total = len(recording[0]["Map"]["Resources"])

            for ego in agent_ids(recording):
                where = view_name(recording, ego)
                perception = perception_at(steps, last, ego)
                self.assertIsNotNone(perception, f"{where} has no last decision-time view")
                await show(page, last, str(ego))
                painted = await page.evaluate(_AGENTS_JS)

                ego_pre = next(a for a in pre["agents"] if a["AgentId"] == ego)
                with self.subTest(view=where, subject="ego"):
                    self.assertEqual(painted[str(ego)]["zone"], ego_pre["ZoneId"],
                                     f"{where} drawn outside the decision-time world")
                    self.assertEqual(score_of(painted[str(ego)]["text"]), ego_pre["Score"],
                                     f"{where}'s painted score is not the "
                                     "decision-time score")

                # Rivals: where the recording last knew each one, and nothing
                # else. An observed rival is drawn live at its LastKnownState; a
                # stale one is a ghost; one never seen is not drawn at all.
                for rival in perception["Agents"]:
                    if rival["AgentId"] == ego:
                        continue
                    rid = str(rival["AgentId"])
                    known = rival.get("LastKnownState")
                    with self.subTest(view=where, subject="rival " + rid):
                        if known is None:
                            self.assertNotIn(rid, painted,
                                             f"{where} drew a rival it never saw")
                            continue
                        self.assertIn(rid, painted,
                                      f"{where} dropped a rival it had seen")
                        self.assertEqual(painted[rid]["zone"], known["ZoneId"],
                                         f"{where} placed rival {rid} at "
                                         f"{painted[rid]['zone']}, not the "
                                         f"recorded last-known {known['ZoneId']}")
                        self.assertEqual(painted[rid]["ghost"], rival["Status"] != 0)
                        if not painted[rid]["ghost"]:
                            self.assertEqual(score_of(painted[rid]["text"]), known["Score"])

                # Claim colours and the metric come from the same perception,
                # and the metric says so: the terminal ego frame is no more
                # entitled to the world's claim list than any other ego frame.
                claims = (await page.evaluate(_CLAIMS_JS))["claimed"]
                assert_paints_exactly(self, claims, perception,
                                      f"terminal frame, {where}")
                recorded_here = recorded_claims(perception)
                for rid, is_claimed in claims.items():
                    with self.subTest(view=where, subject="claim " + str(rid)):
                        self.assertEqual(is_claimed, rid in recorded_here)
                with self.subTest(view=where, subject="metric"):
                    self.assertEqual(await page.evaluate(CLAIMED_STAT),
                                     f"{len(recorded_here)} / {total}")
                    self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_SEEN)

                # And the frame is still labelled as what it is.
                with self.subTest(view=where, subject="caption"):
                    self.assertIn("Last decision-time view", await page.evaluate(MAP_CAPTION))
                with self.subTest(view=where, subject="label"):
                    self.assertIn("last decision-time view", await page.evaluate(CANVAS_LABEL))

            # Ground truth at the same frame stays post-step: it is labelled
            # ground truth, and that is the honest reading of it.
            await show(page, last, "ground")
            truth = await page.evaluate(_AGENTS_JS)
            for agent in post["agents"]:
                with self.subTest(view="ground", subject="agent " + str(agent["AgentId"])):
                    self.assertEqual(truth[str(agent["AgentId"])]["zone"], agent["ZoneId"])
                    self.assertEqual(score_of(truth[str(agent["AgentId"])]["text"]),
                                     agent["Score"])
            with self.subTest(view="ground", subject="metric"):
                self.assertEqual(await page.evaluate(CLAIMED_STAT), f"{len(post['claims'])} / {total}")
                self.assertEqual(await page.evaluate(CLAIM_LABEL), LABEL_GROUND)
            with self.subTest(view="ground", subject="caption"):
                self.assertIn("Ground truth", await page.evaluate(MAP_CAPTION))
            return errors

        self._no_console_errors(self._drive(body))

    # -- split pins ---------------------------------------------------------

    def test_playing_recording_links_to_main_benchmarks_stay_pinned(self):
        """The trajectory on screen is main's, so its repository link must be
        main's. The benchmark result JSONs were recorded at 6463e486 and are
        immutable there, so those links must not move."""

        async def body(page, recording, errors):
            prov = await page.evaluate(PROV_HREFS)
            blob = [href for href in prov if "/blob/" in href]
            self.assertEqual(len(blob), 1,
                             f"expected exactly one repository blob link, got {prov}")
            self.assertTrue(
                blob[0].endswith("/blob/main/site/infiltration.jsonl"),
                f"the playing recording's repository link is {blob[0]}, which does "
                "not resolve to the file being played")
            self.assertNotIn(BENCH_PIN, blob[0],
                             "a playing recording is not pinned to a commit; Pages "
                             "deploys it from main")

            for href in await page.evaluate(BENCH_HREFS):
                with self.subTest(href=href):
                    self.assertIn(
                        BENCH_PIN, href,
                        "a benchmark result JSON link lost its pin; that artifact "
                        "is immutable at 6463e486 and the number a reader gets back "
                        "has to be the published one")
            return errors

        self._no_console_errors(self._drive(body))

    # -- zone-table presence must agree with the painted map ----------------

    async def assert_presence_matches_paint(self, page, recording, where,
                                            frame, view, expect_label=None,
                                            expect_counts=None):
        """One frame in one view, checked three ways at once.

        The zone table, the occupancy badge painted inside each room card, and
        the live agent tokens actually drawn on the canvas are three renderings
        of the same fact. The tokens are the ground for this: they are read back
        out of the probe's record of its own ink, and a token is assigned to the
        room whose painted box contains it, so a table that disagreed with the
        map would be caught even if the page's own counting helper agreed with
        itself. `expect_counts`, when given, is the figure derived independently
        from the recording, so the check is not merely self-consistent.
        """
        rows = await page.evaluate(ZONE_ROWS_JS)
        painted = await page.evaluate(PAINTED_PRESENCE_JS)
        labels = room_labels(recording)
        self.assertEqual(len(rows), len(labels),
                         f"{where}: the table has {len(rows)} rows for "
                         f"{len(labels)} rooms")
        table = {}
        for row in rows:
            zid = next((z for z, name in labels.items() if name == row["room"]), None)
            self.assertIsNotNone(
                zid, f"{where}: no room in the recording is named {row['room']!r}, "
                     "so the table row cannot be keyed to a zone")
            table[zid] = row["value"]

        if expect_label is not None:
            # Checked after the per-room comparisons below: whether the table
            # agrees with the map is the invariant, and the label is the wording
            # that has to go with it.
            pass

        for zid, cell in sorted(table.items()):
            shown = presence_of(cell)
            badge = pill_of(painted["pills"].get(str(zid)))
            status = painted["status"].get(str(zid), "unknown")

            if status != "observed":
                # A room this view has not looked at carries no count at all, and
                # the canvas draws no badge on it. The table must decline in the
                # canvas's own words rather than call it empty.
                with self.subTest(where=where, zone=zid, subject="unobserved room"):
                    self.assertIsNone(
                        shown,
                        f"{where}: room {zid} ({labels[zid]}) is painted "
                        f"{status!r} but the table reads {cell!r}, which claims "
                        "something about a room the view cannot see")
                    self.assertTrue(
                        cell.startswith(
                            WORD_STALE if status == "stale" else WORD_UNKNOWN),
                        f"{where}: room {zid} reads {cell!r}, not the canvas's own "
                        f"word for a {status!r} room")
                self.assertIsNone(
                    badge,
                    f"{where}: room {zid} is painted {status!r} yet carries an "
                    f"occupancy badge reading {painted['pills'].get(str(zid))!r}")
                continue

            with self.subTest(where=where, zone=zid, subject="table vs tokens"):
                self.assertEqual(
                    shown, painted["counts"].get(str(zid), 0),
                    f"{where}: room {zid} ({labels[zid]}) reads {cell!r} but the "
                    f"canvas painted {painted['counts'].get(str(zid), 0)} live "
                    f"token(s) inside it (members {painted['members'].get(str(zid))}, "
                    f"corridor travellers {painted['strays']}, ghosts "
                    f"{painted['ghosts']})")
            if expect_counts is not None:
                with self.subTest(where=where, zone=zid, subject="table vs recording"):
                    self.assertEqual(
                        shown, expect_counts.get(zid, 0),
                        f"{where}: room {zid} ({labels[zid]}) reads {cell!r}, not the "
                        f"{expect_counts.get(zid, 0)} occupant(s) the recording puts "
                        "in that room for this view")
            if badge is not None:
                with self.subTest(where=where, zone=zid, subject="badge vs tokens"):
                    self.assertEqual(
                        badge, painted["counts"].get(str(zid), 0),
                        f"{where}: the occupancy badge on room {zid} shows "
                        f"{painted['pills'].get(str(zid))!r} while the room holds "
                        f"{painted['counts'].get(str(zid), 0)} painted token(s)")
                if shown is not None:
                    with self.subTest(where=where, zone=zid, subject="badge vs table"):
                        self.assertEqual(badge, shown,
                                         f"{where}: room {zid} has a badge saying "
                                         f"{badge} and a table row saying {shown!r}")
        if expect_label is not None:
            self.assertEqual(await page.evaluate(PRESENCE_LABEL), expect_label,
                             f"{where}: the zone table does not say which view it "
                             "is counting")
        return painted

    def test_zone_table_presence_agrees_with_the_painted_tokens(self):
        """The core honesty invariant, over both committed recordings, every
        view and every frame.

        `renderMetrics` used to take its occupancy column from
        `zoneCounts(map, frame)` — the omniscient world — while the canvas above
        it was painted from the view's fog. Any room holding a rival the
        observer has not currently observed therefore read "1 agent" under a
        painted ghost, or "empty" under a painted token. The table has to follow
        the fog, and the two ego views have to be able to disagree.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            ego_ids = agent_ids(recording) if has_recorded_perception(recording) \
                else [a["AgentId"] for a in frames[0]["agents"]]
            leaked = 0
            for frame in range(len(steps) + 1):
                for view in ["ground"] + [str(e) for e in ego_ids]:
                    await show(page, frame, view)
                    painted = await self.assert_presence_matches_paint(
                        page, recording, f"{recording[0]['Scenario']} {view} f{frame}",
                        frame, view)
                    # A token that is not in any room is a corridor traveller, and
                    # a ghost is a memory: neither may be counted as an occupant.
                    self.assertTrue(
                        set(painted["strays"]).issubset(set(painted["transit"])),
                        f"a token was painted outside every room that is not in "
                        f"transit: strays {painted['strays']}, transit "
                        f"{painted['transit']}")
            self.assertTrue(True, "presence agreement exercised")
            return errors

        self._no_console_errors(self._drive(body))
        self._no_console_errors(self._drive(body, "demo.jsonl"))

    def test_ground_truth_zone_table_counts_the_world_and_says_so(self):
        """Ground truth is the one view allowed to count the world, and it has
        to say that in words. The count is the world's own agents standing in
        each room at that frame — an agent crossing a corridor is drawn between
        rooms, so it is not an occupant of either, and its crossing is reported
        in the agents table above with destination and countdown."""

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            zone_ids = [z["Id"] for z in recording[0]["Map"]["Zones"]]
            checked = 0
            for frame in range(len(steps) + 1):
                world = {z: 0 for z in zone_ids}
                for agent in frames[frame]["agents"]:
                    if agent.get("Transit"):
                        continue
                    world[agent["ZoneId"]] = world.get(agent["ZoneId"], 0) + 1
                await show(page, frame, "ground")
                await self.assert_presence_matches_paint(
                    page, recording, f"ground f{frame}", frame, "ground",
                    expect_label=PRESENCE_GROUND, expect_counts=world)
                checked += 1
            self.assertTrue(checked, "no ground-truth frame was checked")
            return errors

        self._no_console_errors(self._drive(body))
        self._no_console_errors(self._drive(body, "demo.jsonl"))

    def test_recorded_ego_view_counts_only_what_the_perception_places(self):
        """Both schema-4 ego views, every frame, counted from the file.

        A recorded perception is a statement about what one observer knew at its
        decision. The occupancy figure has to be read out of it — the observer
        wherever the painted world has it, and a rival only where its entry
        reports it currently observed — and never out of `frame.agents`.
        """

        async def body(page, recording, errors):
            self.assertTrue(has_recorded_perception(recording),
                            "infiltration.jsonl no longer records perceptions")
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            zone_ids = [z["Id"] for z in recording[0]["Map"]["Zones"]]
            crossing = False
            for frame in range(len(steps) + 1):
                index = world_index_of(steps, frame)
                agents = frame_agent_map(frames, index)
                for ego in agent_ids(recording):
                    want = recorded_presence(steps, frame, ego, agents, zone_ids)
                    self.assertIsNotNone(
                        want, f"{view_name(recording, ego)} at frame {frame}: the "
                              "file records no perception to count from")
                    await show(page, frame, str(ego))
                    await self.assert_presence_matches_paint(
                        page, recording,
                        f"{view_name(recording, ego)} f{frame}", frame, str(ego),
                        expect_label=PRESENCE_SEEN, expect_counts=want)
                    # Where the world and the recorded view genuinely differ, the
                    # two ego views must not end up with the same figure.
                    world = {z: 0 for z in zone_ids}
                    for agent in frames[index]["agents"]:
                        if not agent.get("Transit"):
                            world[agent["ZoneId"]] = world.get(agent["ZoneId"], 0) + 1
                    # This file's recorded placements agree with the world in
                    # every frame, so the rule under repair is not "the two views
                    # disagree" here — it is that an agent crossing a corridor
                    # belongs to no room, which the world count gets wrong
                    # whenever one is moving. Require such a frame to exist, or
                    # the check above would pass without ever being tested.
                    if any(agent.get("Transit") for agent in frames[index]["agents"]):
                        crossing = True
            self.assertTrue(
                crossing,
                "no frame in this recording has an agent crossing a corridor, so "
                "the rule that a corridor traveller occupies no room was never "
                "exercised")
            return errors

        self._no_console_errors(self._drive(body))

    def test_a_remembered_ghost_is_not_counted_as_present(self):
        """A rival the observer has only a memory of, and one it has never seen.

        A remembered ghost is drawn as a faded age stamp in the room the rival
        was last seen in. Counting it would claim an occupant the canvas
        deliberately refused to draw as live; a rival the observer has never
        seen is not drawn anywhere and must not be counted anywhere.

        Neither committed recording contains a rival an observer has lost: in
        `infiltration.jsonl` every recorded rival sighting is currently
        observed, and `demo.jsonl`'s four rooms all sit inside the page's
        two-hop cone, so no ghost is ever drawn. Asserting the rule needs a
        recording that contains one, and the committed files are immutable
        evidence, so the memory cases run on a controlled variant of the
        schema-4 file: one step whose rival is reported stale with a last-known
        position, and one whose rival is reported unknown with no position.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            self.assertTrue(has_recorded_perception(recording),
                            "infiltration.jsonl no longer records perceptions")
            stale_at, unknown_at = 5, 15
            ids = agent_ids(recording)
            self.assertGreater(len(ids), 1, "this fixture needs a rival to hide")

            lines = [recording[0]]
            for i, step in enumerate(steps):
                step = dict(step)
                if i in (stale_at, unknown_at):
                    step["Perceptions"] = [dict(p) for p in step["Perceptions"]]
                    for perception in step["Perceptions"]:
                        perception["Agents"] = [dict(a) for a in perception["Agents"]]
                        for entry in perception["Agents"]:
                            if entry["AgentId"] == perception["AgentId"]:
                                continue
                            if i == stale_at:
                                entry["Status"] = 1      # last known, not live
                                entry["LastSeenTick"] = 1
                            else:
                                entry["Status"] = 2      # never seen it at all
                                entry["LastKnownState"] = None
                                entry["LastSeenTick"] = 0
                lines.append(step)
            lines.append(recording[-1])
            await self._load_variant(page, lines, "hidden_rivals.jsonl")

            for frame, kind in ((stale_at, "stale ghost"),
                                (unknown_at, "unknown rival")):
                for ego in ids:
                    # The world really does have that rival standing in a room,
                    # or a table that counted it would not be detectably wrong.
                    with self.subTest(subject=kind, view=str(ego), frame=frame,
                                      where="fixture is meaningful"):
                        rivals = [a for a in frames[frame]["agents"]
                                  if a["AgentId"] != ego]
                        self.assertTrue(
                            any(not a.get("Transit") for a in rivals),
                            f"at frame {frame} the world has no stationary rival, "
                            "so a leaked count could not be detected here")
                    await show(page, frame, str(ego))
                    painted = await self.assert_presence_matches_paint(
                        page, recording, f"{kind} ego {ego} f{frame}", frame,
                        str(ego), expect_label=PRESENCE_SEEN)
                    seated = {str(a) for ids_ in painted["members"].values()
                              for a in ids_}
                    with self.subTest(subject=kind, view=str(ego), frame=frame,
                                      where="rival is not a live token"):
                        for other in ids:
                            if other == ego:
                                continue
                            self.assertNotIn(
                                str(other), seated,
                                f"{kind}: rival {other} was painted as a live "
                                f"token; live tokens {sorted(seated)}")
                    if kind == "stale ghost":
                        with self.subTest(subject=kind, view=str(ego), frame=frame,
                                          where="a ghost is drawn"):
                            self.assertEqual(
                                len(painted["ghosts"]), len(ids) - 1,
                                "a rival reported stale with a last-known position "
                                "must be drawn as a ghost; ghosts seen: "
                                f"{painted['ghosts']}")
                    else:
                        with self.subTest(subject=kind, view=str(ego), frame=frame,
                                          where="nothing is drawn"):
                            self.assertEqual(
                                painted["ghosts"], [],
                                "a rival with no last-known position must not be "
                                f"drawn at all; ghosts seen: {painted['ghosts']}")
            return errors

        self._no_console_errors(self._drive(body))

    def test_stale_and_unexplored_rooms_decline_to_be_counted(self):
        """The dimmer half of the same rule, driven off the committed files.

        A stale room is drawn "last known" and an unexplored one "unexplored".
        The table has to use the canvas's own words for them, must not put a
        count against them — "empty" above all, which is a claim about a room
        this view cannot see — and must not hang a badge on them.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            recorded = has_recorded_perception(recording)
            label = PRESENCE_SEEN if recorded else PRESENCE_DERIVED
            ids = (agent_ids(recording) if recorded
                   else [a["AgentId"] for a in frames[0]["agents"]])
            zone_ids = [z["Id"] for z in recording[0]["Map"]["Zones"]]
            dim = 0
            for frame in range(len(steps) + 1):
                index = world_index_of(steps, frame)
                agents = frame_agent_map(frames, index)
                for ego in ids:
                    await show(page, frame, str(ego))
                    want = (recorded_presence(steps, frame, ego, agents, zone_ids)
                            if recorded else None)
                    painted = await self.assert_presence_matches_paint(
                        page, recording,
                        f"{recording[0]['Scenario']} {ego} f{frame}", frame, str(ego),
                        expect_label=label, expect_counts=want)
                    for zone, status in painted["status"].items():
                        if status == "observed":
                            continue
                        dim += 1
                        with self.subTest(view=str(ego), frame=frame, zone=zone):
                            self.assertNotIn(
                                str(zone), painted["pills"],
                                f"room {zone} is painted {status!r} but carries an "
                                "occupancy badge "
                                f"{painted['pills'].get(str(zone))!r}")
            return errors, dim

        seen = 0
        for name in ("infiltration.jsonl", "demo.jsonl"):
            errors, dim = self._drive(body, name)
            self._no_console_errors(errors)
            seen += dim
        self.assertTrue(
            seen, "no stale or unexplored room was ever painted, so the "
            "decline-to-count rule was never exercised")

    def test_terminal_frame_presence_comes_from_the_last_decision_world(self):
        """The last frame, in an ego view, is the last decision-time view: the
        occupancy column must come from the world that decision was made from
        (frame last-1), not from the post-step terminal world that the canvas
        did not paint."""

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            zone_ids = [z["Id"] for z in recording[0]["Map"]["Zones"]]
            last = len(steps)
            for ego in agent_ids(recording):
                want = recorded_presence(
                    steps, last, ego, frame_agent_map(frames, last - 1), zone_ids)
                await show(page, last, str(ego))
                await self.assert_presence_matches_paint(
                    page, recording, f"{view_name(recording, ego)} terminal",
                    last, str(ego), expect_label=PRESENCE_SEEN, expect_counts=want)
            return errors

        self._no_console_errors(self._drive(body))

    def test_ego_view_with_no_recorded_perception_declines_to_count(self):
        """A schema-4 file that is missing one step's perceptions.

        The file still records perceptions, so the page must not silently
        reconstruct a sightline for the missing frame and pass the guess off as
        a record — and it must not fall back on `frame.agents` either, because
        that is the world's. There is no honest count here, so the column has to
        say so instead of inventing one.

        The fixture is a copy of the committed recording with one step's
        `Perceptions` removed, built in a temp directory and loaded through the
        page's own file input. No committed recording is touched.
        """

        async def body(page, recording, errors):
            steps = recorded_steps(recording)
            zone_ids = [z["Id"] for z in recording[0]["Map"]["Zones"]]
            frames = frames_with_claims(recording)
            self.assertTrue(has_recorded_perception(recording),
                            "infiltration.jsonl no longer records perceptions")

            blind = 1
            lines = [recording[0]]
            doctored = []
            for i, step in enumerate(steps):
                step = dict(step)
                if i == blind:
                    step.pop("Perceptions", None)
                doctored.append(step)
                lines.append(step)
            lines.append(recording[-1])
            await self._load_variant(page, lines, "blind_frame.jsonl")

            # The page shows `steps[f]` at scrubber position f, so blinding step
            # index `blind` is what leaves that frame with no recorded perception.
            # Checked against the variant that was actually loaded, not against
            # the pristine file: the guard would otherwise pass while the page
            # was still reading a perception that is not there.
            frame = blind
            self.assertIsNone(
                perception_at(doctored, frame, 0),
                "the variant still records a perception at the blind frame")
            self.assertIsNotNone(
                perception_at(steps, frame, 0),
                "the committed file already lacked a perception here, so this "
                "test is no longer exercising the missing-perception path")
            await show(page, frame, "0")
            rows = await page.evaluate(ZONE_ROWS_JS)
            self.assertEqual(await page.evaluate(PRESENCE_LABEL), PRESENCE_UNAVAILABLE)
            for row in rows:
                with self.subTest(zone=row["room"]):
                    self.assertIsNone(
                        presence_of(row["value"]),
                        f"{row['room']}: with no recorded perception this view has "
                        f"no count to give, but the table says {row['value']!r}")
            # And the world, which is right there in the file, was not borrowed.
            world = {z: 0 for z in zone_ids}
            for agent in frames[frame]["agents"]:
                if not agent.get("Transit"):
                    world[agent["ZoneId"]] = world.get(agent["ZoneId"], 0) + 1
            self.assertTrue(any(world.values()),
                            "the blind frame has no agents at all, so this test "
                            "cannot detect a leaked world count")
            return errors

        self._no_console_errors(self._drive(body))


if __name__ == "__main__":
    unittest.main()
