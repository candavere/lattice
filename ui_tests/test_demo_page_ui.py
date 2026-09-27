"""Tracked Playwright regression for the demo replay page in site/.

Serves the committed site/ directory on a localhost port (no dependency on the
live GitHub Pages URL) and drives it with the bundled ?measure=1 geometry probe
(window.__latticeGeo: exact on-canvas bounds of every room label, agent token
and door pill just drawn, plus per-frame index/perspective).

Covers what the demo simplification stage asserts:
  * one-click perspective chips (ground aside, agent views) + keyboard roving,
  * single-map viewer with a fog badge: infiltration asserts "Recorded
    perception"; demo.jsonl asserts "Reconstructed sightline",
  * no token ever overlaps a room label (every frame, both perspectives),
  * no horizontal overflow of the page or the graph canvas,
  * the fog check: when the recording carries Perceptions, per-room status is
    read from those decision-time records (post-terminal frame has no fog);
    otherwise the old 2-hop reconstruction is used. A "last known" moment is
    discovered from that timeline, required to exist for infiltration, and
    checked to be painted the way the page says it is,
  * door pills appear on hover (pointer cursor),
  * legend renders as a compact aligned item grid (per-item height caps,
    swatch top-aligned with its label, inline code chips never wrap, no
    clipping),
  * no console/page errors.

The frame count and the fog expectation both come from the recording the page
loaded, never from a constant: an engine change that shortens, lengthens or
reshapes an episode moves the frames and the fog moments with it, and the check
follows. What it will not accept is a recording with no "last known" moment at
all, or a page that renders a status the recording does not imply.

Run:  python3 -m unittest discover -s ui_tests -p 'test_*.py' -v
"""

import asyncio
import json
import threading
import unittest
import urllib.request
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from playwright.async_api import async_playwright

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "site"

def measure_params(recording_name="infiltration.jsonl"):
    return f"?measure=1&{recording_name}"


# JS helpers (the room-label badge lives on a ::after, so read it via
# getComputedStyle rather than a selector).
BADGE = "() => getComputedStyle(document.querySelector('.viewer'), '::after').content"

SLIDE = """(t) => { const s = document.querySelector('#scrub-slider');
  s.value = String(t); s.dispatchEvent(new Event('input', {bubbles: true})); }"""

ACTIVE_CHIP = "(document.querySelector('#perspective-chips .chip.active')?.dataset.id ?? null)"

# Wait until the draw loop has repainted frame @t under the given chip.
# Agent views store a numeric perspective in the probe; ground is the string.
def wait_frame(tick, view):
    persp = json.dumps(view) if view == "ground" else str(view)
    return (
        f"() => {{ const g = window.__latticeGeo; "
        f"const act = {ACTIVE_CHIP}; "
        f"return g && g.frame.index === {tick} && g.frame.perspective === {persp}"
        f" && act === {json.dumps(view)}; }}"
    )


def bbox(o):
    if "r" in o:
        return {"x0": o["x"] - o["r"], "y0": o["y"] - o["r"], "x1": o["x"] + o["r"], "y1": o["y"] + o["r"]}
    return {"x0": o["x"], "y0": o["y"], "x1": o["x"] + o["w"], "y1": o["y"] + o["h"]}


def overlaps(a, b):
    return not (a["x1"] <= b["x0"] or b["x1"] <= a["x0"] or a["y1"] <= b["y0"] or b["y1"] <= a["y0"])


# --- what the recording itself implies -------------------------------------
# Prefer decision-time Perceptions when present (mirror of site/app.js).
# Older files without that side-channel fall back to the page's 2-hop
# reconstruction from positions. Either way the UI asserts against the file.

DEFAULT_VISION_HOPS = 2  # the page's fallback when the header records no radius


def fetch_recording(base, name="infiltration.jsonl"):
    """The recording the page loaded, read over the same local server."""
    with urllib.request.urlopen(base + name) as response:
        text = response.read().decode("utf-8")
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def vision_hops(header):
    """Mirror of the page's visionHops(): AgentVision[0], SimulationConfig.Vision, or default."""
    agent_vision = header.get("AgentVision")
    if isinstance(agent_vision, list) and agent_vision and isinstance(agent_vision[0], int) and agent_vision[0] >= 1:
        return agent_vision[0]
    vision = header.get("SimulationConfig", {}).get("Vision")
    if isinstance(vision, int) and vision >= 1:
        return vision
    return DEFAULT_VISION_HOPS


def recorded_frames(recording):
    """Mirror of the page's frame list: the recorded opening placement, then one
    frame per recorded step, each carrying that step's agent states."""
    header = recording[0]
    zone_count = len(header["Map"]["Zones"])
    agents = header["SimulationConfig"]["AgentCount"]
    frames = [[{"AgentId": i, "ZoneId": i % zone_count} for i in range(agents)]]
    for line in recording:
        if line.get("Kind") != "step":
            continue
        observed = line["Result"]["Observations"][0]
        frames.append(observed["AgentStates"])
    return frames


def recorded_steps(recording):
    return [line for line in recording if line.get("Kind") == "step"]


def has_recorded_perceptions(recording):
    return any(step.get("Perceptions") for step in recorded_steps(recording))


def knowledge_status_name(status):
    if status == 0 or status == "Observed":
        return "observed"
    if status == 1 or status == "Stale":
        return "stale"
    return "unknown"


def _adjacency(header):
    adj = {zone["Id"]: [] for zone in header["Map"]["Zones"]}
    for choke in header["Map"]["ChokePoints"]:
        adj[choke["FromZoneId"]].append(choke["ToZoneId"])
        adj[choke["ToZoneId"]].append(choke["FromZoneId"])
    return {zone_id: sorted(neighbours) for zone_id, neighbours in adj.items()}


def _zones_within(adj, origin, hops):
    """Zones within `hops` graph hops of `origin`, inclusive (breadth-first)."""
    seen = {origin: 0}
    frontier = [origin]
    while frontier:
        current = frontier.pop(0)
        if seen[current] >= hops:
            continue
        for neighbour in adj[current]:
            if neighbour not in seen:
                seen[neighbour] = seen[current] + 1
                frontier.append(neighbour)
    return set(seen)


def ego_zone(agents, ego_id):
    """Where the page believes the agent is: the crossing's destination while a
    transit is in flight, otherwise the recorded zone."""
    ego = next((a for a in agents if a["AgentId"] == ego_id), None) or agents[0]
    transit = ego.get("Transit")
    return transit["ToZoneId"] if transit else ego["ZoneId"]


def status_timeline(recording, ego_id):
    """{frame: {zone: 'observed' | 'stale' | 'unknown'}} for one agent view.

    When the file carries Perceptions, those decision-time records are the
    source of truth (the post-terminal frame has no Decide, so no fog).
    Otherwise the page's cumulative 2-hop reconstruction is used.
    """
    header = recording[0]
    zone_ids = [zone["Id"] for zone in header["Map"]["Zones"]]
    frames = recorded_frames(recording)
    steps = recorded_steps(recording)

    if has_recorded_perceptions(recording):
        timeline = {}
        for index in range(len(frames)):
            if index >= len(steps):
                timeline[index] = {zone_id: "observed" for zone_id in zone_ids}
                continue
            perceptions = steps[index].get("Perceptions") or []
            partial = next((p for p in perceptions if p.get("AgentId") == ego_id), None)
            if partial is None and 0 <= ego_id < len(perceptions):
                partial = perceptions[ego_id]
            statuses = {zone_id: "unknown" for zone_id in zone_ids}
            if partial:
                for zone in partial.get("Zones") or []:
                    statuses[zone["ZoneId"]] = knowledge_status_name(zone.get("Status"))
            timeline[index] = statuses
        return timeline

    adj = _adjacency(header)
    hops = vision_hops(header)
    last_seen = {}
    timeline = {}
    for index, agents in enumerate(frames):
        seen = _zones_within(adj, ego_zone(agents, ego_id), hops)
        for zone_id in seen:
            last_seen[zone_id] = index
        timeline[index] = {
            zone_id: "observed" if zone_id in seen
            else "stale" if zone_id in last_seen
            else "unknown"
            for zone_id in zone_ids
        }
    return timeline


def vault_zone(recording):
    """The room the fog callout is about, identified by its recorded role."""
    for zone in recording[0]["Map"]["Zones"]:
        if zone.get("Role") == "TreasureVault":
            return zone["Id"]
    return None


def stale_moments(recording, agent_ids):
    """(view, frame) pairs where an agent view calls a room 'last known' while
    ground truth still calls it observed. Ground truth is the recorded state, so
    every room is observed there by definition. The first pair is the moment the
    page's guided copy points a visitor at."""
    zone = vault_zone(recording)
    if zone is None:
        return []
    moments = []
    for view in agent_ids:
        timeline = status_timeline(recording, view)
        for frame, statuses in timeline.items():
            if statuses[zone] == "stale":
                moments.append((view, frame))
    return sorted(moments, key=lambda pair: (pair[1], pair[0]))


# Pixel census of one room's title box, so "the page says stale" and "the page
# paints stale" stay separate facts. The room title is drawn in COLORS.roomText
# when observed and COLORS.fogText when stale, with fillText and no alpha, so
# every glyph pixel is a straight alpha composite of one of those two colours
# and whatever the card put down underneath. The census works on that model
# rather than on exact colour matches, because a stem narrower than a device
# pixel is plainly visible while painting no fully covered pixel at all. Run
# 36315512777 read 16 fog cores and 3 lit cores for this 14-glyph title at 390px
# on windows-latest against a floor of 14, where the incumbent comment recorded
# 68 for the same title on the reference rasteriser: one core per glyph is not a
# property of a painted glyph, it is a property of a rasteriser wide enough to
# fill a whole pixel.
#
# Two claims are kept apart, because they fail for different reasons:
#
#   * ink: the title has painted enough of itself, measured against the local
#     room-card fill, in the colour it was drawn in;
#   * colour: that ink is the expected title colour and not the other one.
#
# The background reference is measured, never assumed. A stale card is filled
# with COLORS.fogStaleFill, a translucent rgba(26, 31, 44, 0.45) whose
# composite depends on the map behind it, while an observed card is an opaque
# COLORS.roomFill: on this one title the two measure (27, 31, 44, 115) and
# (26, 31, 44, 255), so no constant in this file is the fill at both statuses.
ROOM_TEXT_RGB = (0xC0, 0xCA, 0xF5)  # COLORS.roomText
FOG_TEXT_RGB = (0x5B, 0x6A, 0x8A)  # COLORS.fogText

# How far a pixel may sit from a candidate colour's blend of itself over the
# measured fill and still count as that colour's ink. This is a property of the
# two colours and of 8-bit rounding, not of the rasteriser: every correct glyph
# pixel is an exact composite, and the reference raster at both faces puts all
# of them within 3.0. A *solid* pixel of the other title colour cannot be
# explained as the expected one at all in the fog direction (107), and in the
# lit direction it needs a 7.6 ride, because COLORS.fogText is 43.7% of the way
# along the blend from the fill to COLORS.roomText. 6 sits inside that gap.
SEGMENT_TOLERANCE = 6

# Below this a pixel is the card showing through, not ink. Also 8-bit rounding
# of the fill's own composite, which moves it by up to ~2.
INK_EPSILON = 4

# The ink floor. A painted glyph leaves at least one pixel that differs from
# the fill, so an n-glyph title can never ink fewer than n, and a title painted
# in the wrong colour or not painted at all inks none. The area term is the
# least-inked correct render measured here: the fog title at the 11px design
# face inks 233 of its 1380 device px, which is 0.17, and the other three
# measure 0.19 (fog at 8.3px), 0.235 and 0.267. Divided by an allowance for a
# rasteriser that paints far less than the reference one. That allowance is 16
# because this repository has already recorded a 4.25x spread in fully covered
# cores for this same title between rasterisers (68 against 16 at 8.3px), so 16
# leaves about four times the spread ever seen. The floor is never a platform's
# pass target: the erase control inks exactly 0, and the correct paints here
# clear the floor by 10x to 22x.
INK_DENSITY = 0.17
RASTER_ALLOWANCE = 16
MONO_ADVANCE = 0.6  # a monospace glyph's advance width, in em

# At least this share of the attributable ink must be the expected colour.
# Majority would not do: a whole title painted in the wrong colour plus a few
# stray expected-colour pixels is a majority, and is still a mis-render.
COLOUR_DOMINANCE = 0.85


def blend_fit(pixel, fill, source):
    """(coverage, residual) of `source` over `fill` that best explains `pixel`.

    The page's titles are opaque text on top of the card, so this two-source
    compositing model is the whole of what a glyph pixel is. `residual` is the
    largest channel's error, in 8-bit units.
    """
    num = sum((pixel[i] - fill[i]) * (source[i] - fill[i]) for i in range(3))
    den = sum((source[i] - fill[i]) ** 2 for i in range(3))
    alpha = min(1.0, max(0.0, num / den)) if den else 0.0
    residual = max(abs(pixel[i] - (fill[i] + alpha * (source[i] - fill[i])))
                   for i in range(3))
    return alpha, residual


def title_fill(ring):
    """The local room-card fill under a title, or None if it cannot be read.

    The most common colour on the same card, on the rows the title crosses,
    outside the title's own box -- the same reference at every viewport, and one
    that a case which repaints the box cannot move, since it is read from
    beside the box rather than from inside it.
    """
    counts = {}
    for pixel in ring:
        key = tuple(pixel)
        counts[key] = counts.get(key, 0) + 1
    if not counts:
        return None
    return max(counts.items(), key=lambda kv: (kv[1], kv[0]))[0]


def title_font_px(box, label):
    """The size the title was actually painted at.

    The box the probe reports is the label's measured advance width, so the
    font follows from the glyph count: advance == MONO_ADVANCE * font * glyphs.
    """
    return box["w"] / (MONO_ADVANCE * max(1, len(label or "")))


def min_title_ink(area, label):
    """Ink the title `label` must leave in its own box of `area` device pixels.

    In device pixels, so a denser display is held to proportionally more ink
    without a second constant.
    """
    return max(1, int(round(INK_DENSITY * area / RASTER_ALLOWANCE)))


def ambiguous_coverage(own_rgb, other_rgb):
    """The slice of the coverage range where the two title colours are the same.

    Two candidates are indistinguishable wherever their blended colours are
    within SEGMENT_TOLERANCE of each other, and at coverage `a` they are `a *
    span` apart, so they overlap over a 2 * SEGMENT_TOLERANCE / span of the
    range. Ink in that slice cannot be attributed by colour, and the correct
    colour's budget has to leave room for it.
    """
    span = max(abs(own_rgb[i] - other_rgb[i]) for i in range(3))
    return (2.0 * SEGMENT_TOLERANCE / span) if span else 0.0


def title_census(px, own_rgb, other_rgb):
    """Count the ink in one title box, or None if the fill cannot be read.

    Every pixel is placed in exactly one bucket: cleared (the canvas was
    cleared to transparent, which is not ink), the fill showing through (not
    ink), ink of `own_rgb`, ink of `other_rgb`, or indeterminate. The last is
    everything else that differs from the fill -- notably the agent-coloured
    perception ring, which is drawn over the title box and belongs to neither
    claim. A pixel that fits both candidates is indeterminate too, rather than
    counted for both, so one ambiguous pixel cannot satisfy two claims.
    """
    fill = title_fill(px["ring"])
    if fill is None:
        return None
    own_ink = other_ink = indeterminate = cleared = differing = 0
    for pixel in px["pixels"]:
        if pixel[3] == 0:
            cleared += 1
            continue
        if max(abs(pixel[i] - fill[i]) for i in range(3)) < INK_EPSILON:
            continue
        differing += 1
        _, own_residual = blend_fit(pixel, fill, own_rgb)
        _, other_residual = blend_fit(pixel, fill, other_rgb)
        fits_own = own_residual <= SEGMENT_TOLERANCE
        fits_other = other_residual <= SEGMENT_TOLERANCE
        if fits_own and not fits_other:
            own_ink += 1
        elif fits_other and not fits_own:
            other_ink += 1
        else:
            indeterminate += 1
    area = px["w"] * px["h"]
    attributable = own_ink + other_ink
    dominance = (own_ink / attributable) if attributable else 0.0
    ink_floor = max(len(px["label"] or "") or 1, min_title_ink(area, px["label"]))
    other_budget = max(2, int(round(ambiguous_coverage(own_rgb, other_rgb) * own_ink)))
    return {
        "fill": fill, "area": area, "differing": differing, "cleared": cleared,
        "own_ink": own_ink, "other_ink": other_ink, "indeterminate": indeterminate,
        "ink_floor": ink_floor, "other_budget": other_budget, "dominance": dominance,
        "ink_held": own_ink >= ink_floor,
        "colour_held": other_ink <= other_budget,
        # With no ink there is no mix of colours to be wrong about, so the share
        # abstains and leaves that case to the floor, which is the only claim
        # that can tell an empty box from a painted one.
        "share_held": attributable == 0 or dominance >= COLOUR_DOMINANCE,
    }


def title_colours(px, top=5):
    """The box's most common colours, for a failure message."""
    counts = {}
    for pixel in px["pixels"]:
        key = tuple(pixel)
        counts[key] = counts.get(key, 0) + 1
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))[:top]
    return ", ".join("%s x%d" % ("/".join(str(c) for c in colour), n)
                      for colour, n in ranked)


def title_painted(px, own_rgb, other_rgb, where="?"):
    """Is this room title painted in `own_rgb`, and not in `other_rgb`?

    Three separate claims, and each is reported on its own so a failure says
    which one broke and by how much:

      ink     the title inked at least its floor in the expected colour;
      colour  no more ink than the irreducibly ambiguous share is in the other
              title colour;
      share   at least COLOUR_DOMINANCE of the attributable ink is expected.

    Returns (held, detail).
    """
    if px["box"] is None:
        return False, "[%s] the room title was not painted on this view" % where
    census = title_census(px, own_rgb, other_rgb)
    if census is None:
        return False, ("[%s] could not read the room-card fill beside the title "
                       "box, so no ink can be attributed to it" % where)
    ink_held = census["ink_held"]
    colour_held = census["colour_held"]
    share_held = census["share_held"]
    detail = ("[%s] %.2fpx title, box %dx%d (%d device px), fill %s from %d px: "
              "%d of %s ink (floor %d), %d of %s (budget %d), share %.3f "
              "(floor %.2f), %d differing, %d indeterminate, %d cleared"
              " | colours %s | claims ink %s, colour %s, share %s"
              % (where, title_font_px(px["box"], px["label"]), px["w"], px["h"],
                 census["area"], census["fill"], len(px["ring"]),
                 census["own_ink"], own_rgb, census["ink_floor"],
                 census["other_ink"], other_rgb, census["other_budget"],
                 census["dominance"], COLOUR_DOMINANCE,
                 census["differing"], census["indeterminate"], census["cleared"],
                 title_colours(px),
                 "ok" if ink_held else "FAILED", "ok" if colour_held else "FAILED",
                 "ok" if share_held else "FAILED"))
    return ink_held and colour_held and share_held, detail


# One probe for both callers: the viewport sweep reads a title, and the negative
# control damages a title first and reads it in the same JavaScript turn, so the
# census cannot be taken between a repaint and the damage. `mode` is empty for
# a plain read; the control passes 'lit-row', 'band' or 'erase' together with the
# colour to paint, which is always the *other* title colour.
TITLE_CENSUS = """(job) => {
  const g = window.__latticeGeo;
  const box = g.labels[job.zoneId];
  const room = g.rooms[job.zoneId] || {};
  const canvas = document.querySelector('#viewer-canvas');
  const dpr = canvas.width / canvas.getBoundingClientRect().width;
  const ctx = canvas.getContext('2d');
  if (!box) return { box: null, label: '', w: 0, h: 0, pixels: [], ring: [] };
  const x = Math.round(box.x * dpr), y = Math.round(box.y * dpr);
  const w = Math.max(1, Math.round(box.w * dpr)), h = Math.max(1, Math.round(box.h * dpr));
  if (job.mode) {
    if (job.mode === 'erase') {
      ctx.clearRect(x, y, w, h);
    } else {
      ctx.fillStyle = job.colour;
      // One device row along the top of the box, which carries slack above the
      // glyphs: lit ink is added without erasing a single fog pixel.
      if (job.mode === 'lit-row') ctx.fillRect(x, y, w, 1);
      else ctx.fillRect(x, y, w, h);
    }
  }
  const at = (ax, ay) => Array.from(ctx.getImageData(ax, ay, 1, 1).data);
  const pixels = [];
  for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) pixels.push(at(x + i, y + j));
  // The fill reference: this card, on the rows the title crosses, beside the
  // title box. The whole card is the fallback for a title as wide as its card.
  const rx = Math.round(room.x * dpr), ry = Math.round(room.y * dpr);
  const rw = Math.max(1, Math.round(room.w * dpr)), rh = Math.max(1, Math.round(room.h * dpr));
  const bx = x - rx, by = y - ry;
  const ring = [];
  for (let cy = 0; cy < rh; cy++) {
    const titleRow = by + cy >= 0 && by + cy < h;
    for (let dx = 0; dx < rw; dx++) {
      const inBox = dx >= bx && dx < bx + w && cy >= by && cy < by + h;
      if (inBox) continue;
      if (titleRow || !ring.length) ring.push(at(rx + dx, ry + cy));
    }
  }
  return { box: box, label: room.label || '', dpr: dpr,
           w: w, h: h, pixels: pixels, ring: ring };
}"""


def mispaint_job(zone_id, mode=None, colour=ROOM_TEXT_RGB):
    """The census job for a plain read, or for one of the control's damages."""
    return {"zoneId": str(zone_id), "mode": mode,
            "colour": "rgb(%d, %d, %d)" % colour}



class _SiteHandler(SimpleHTTPRequestHandler):
    def __init__(self, *args, directory=None, **kwargs):
        super().__init__(*args, directory=str(directory), **kwargs)

    def log_message(self, format, *args):
        pass


def start_server():
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), partial(_SiteHandler, directory=SITE))
    port = httpd.server_address[1]
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    return httpd, port


async def run_viewport(browser, base, label, viewport, reduced,
                       recording_name="infiltration.jsonl",
                       expected_badge="Recorded perception",
                       require_stale_moment=True):
    results = []
    context = await browser.new_context(
        viewport=viewport, reduced_motion="reduce" if reduced else "no-preference"
    )
    page = await context.new_page()

    errors = []
    page.on("console", lambda m: errors.append(f"console:{m.type}:{m.text}") if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(f"pageerror:{e}"))

    try:
        await page.goto(base + measure_params(recording_name), wait_until="networkidle")
        await page.wait_for_function(
            "window.__latticeGeo && window.__latticeGeo.frame && "
            "document.querySelectorAll('#perspective-chips .chip').length >= 3"
        )

        # -- structure -------------------------------------------------------
        chips = (
            await page.locator("#scenario-chips .chip").count(),
            await page.locator("#perspective-chips .chip").count(),
            await page.locator("#speed-chips .chip").count(),
        )
        results.append((f"[{label}] chip rows scenario/px/speed", chips == (2, 3, 4), str(chips)))
        ncanv = await page.locator("canvas").count()
        results.append((f"[{label}] single canvas", ncanv == 1, f"{ncanv} canvas" if ncanv != 1 else "1 canvas"))

        # -- the recording, read from the same server the page read it from ---
        # Frame count and fog expectation both come from here, never from a
        # constant: an engine change that reshapes the episode moves them.
        recording = fetch_recording(base, recording_name)
        frame_count = len(recorded_frames(recording))
        slider_max = int(await page.evaluate("() => document.querySelector('#scrub-slider').max"))
        results.append((
            f"[{label}] page loaded every frame of the recording",
            slider_max == frame_count - 1,
            f"slider max {slider_max}, recording has {frame_count} frame(s) (0..{frame_count - 1})",
        ))

        # -- default perspective + badge ------------------------------------
        active = await page.locator("#perspective-chips .chip.active").all_text_contents()
        badge = await page.evaluate(BADGE)
        ok_default = active == ["Sentry"] and expected_badge in badge
        results.append((f"[{label}] default Sentry chip + badge", ok_default, f"active={active} badge={badge}"))

        # -- one-click chips: ground aside, then back to Sentry --------------
        await page.locator('#perspective-chips .chip[data-id="ground"]').click()
        await page.wait_for_function(wait_frame(0, "ground"))
        caption = await page.locator("#map-caption").text_content()
        badge = await page.evaluate(BADGE)
        ok_ground = str(caption).startswith("Ground truth:") and badge == "none"
        results.append((f"[{label}] one-click ground chip (caption+no badge)", ok_ground, caption))

        await page.locator('#perspective-chips .chip[data-id="0"]').click()
        await page.wait_for_function(wait_frame(0, "0"))
        caption = await page.locator("#map-caption").text_content()
        badge = await page.evaluate(BADGE)
        ok_sentry = str(caption).startswith("What the Sentry") and expected_badge in badge
        results.append((f"[{label}] one-click Sentry chip (caption+badge)", ok_sentry, caption))

        # -- keyboard roving: focus Infiltrator chip, ArrowLeft -> Sentry ----
        await page.locator('#perspective-chips .chip[data-id="1"]').focus()
        await page.keyboard.press("ArrowLeft")
        await page.wait_for_timeout(80)
        a11y = await page.evaluate(
            """() => ({
              active: document.querySelector('#perspective-chips .chip.active')?.dataset.id,
              checked: document.querySelector('#perspective-chips .chip.active')?.getAttribute('aria-checked'),
              tabindex0: document.querySelector('#perspective-chips .chip[tabindex="0"]')?.dataset.id,
            })"""
        )
        ok_a11y = a11y == {"active": "0", "checked": "true", "tabindex0": "0"}
        results.append((f"[{label}] keyboard ArrowLeft roving roundtrip", ok_a11y, json.dumps(a11y)))

        # -- token/label overlap, all ticks, both perspectives ---------------
        worst = None
        for view in ("ground", "0"):
            await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
            await page.evaluate(SLIDE, 0)
            await page.wait_for_function(wait_frame(0, view))
            for tick in range(0, frame_count):
                await page.evaluate(SLIDE, tick)
                await page.wait_for_function(wait_frame(tick, view))
                geo = await page.evaluate("window.__latticeGeo")
                labels = [bbox(v) for v in (geo or {}).get("labels", {}).values()]
                tokens = [bbox(v) for v in (geo or {}).get("tokens", {}).values()]
                for t in tokens:
                    for l in labels:
                        if overlaps(t, l):
                            worst = f"{view} tick {tick}: token {t} vs label {l}"
        results.append((f"[{label}] no token/label overlap", worst is None, worst or "NONE"))

        # -- fog: the room status the page renders, every frame, every view ---
        # Re-derived from the recording, so this asserts "the page renders what
        # the file implies" rather than "the page renders what it used to".
        agent_ids = list(range(recording[0]["SimulationConfig"]["AgentCount"]))
        zone_ids = [str(zone["Id"]) for zone in recording[0]["Map"]["Zones"]]
        expected = {view: status_timeline(recording, view) for view in agent_ids}
        ground_want = {zone_id: "observed" for zone_id in zone_ids}  # the recorded state
        rendered = {}
        worst_fog = None
        for view in [str(v) for v in agent_ids] + ["ground"]:
            await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
            await page.evaluate(SLIDE, 0)
            await page.wait_for_function(wait_frame(0, view))
            for tick in range(0, frame_count):
                await page.evaluate(SLIDE, tick)
                await page.wait_for_function(wait_frame(tick, view))
                statuses = (await page.evaluate("window.__latticeGeo"))["statusByZone"]
                rendered[(view, tick)] = statuses
                want = (ground_want if view == "ground"
                        else {str(z): s for z, s in expected[int(view)][tick].items()})
                if statuses != want:
                    worst_fog = f"view {view} frame {tick}: page {statuses} != recording {want}"
        results.append((
            f"[{label}] rendered room status matches the recording, "
            f"{frame_count} frames x {len(agent_ids) + 1} views",
            worst_fog is None, worst_fog or "NONE"))

        # -- the "last known" moment, discovered from the recording -----------
        # Found, not hard-coded: the first frame where some agent view calls the
        # vault last-known while ground truth still calls it observed. A
        # recording with no such moment leaves the page nothing to demonstrate,
        # which is a failure for infiltration rather than a pass.
        zone = vault_zone(recording)
        moments = stale_moments(recording, agent_ids)
        if require_stale_moment:
            results.append((
                f"[{label}] recording has a 'last known' moment to demonstrate",
                zone is not None and bool(moments),
                f"vault zone {zone}, moments {moments}" if zone is not None
                else "no zone carries the TreasureVault role"))
        if require_stale_moment and (zone is None or not moments):
            results.append((f"[{label}] vault last-known moment renders as recorded", False,
                            "no moment discovered, so there is nothing to check"))
        elif moments and zone is not None:
            view, tick = moments[0]
            room = str(zone)
            view_status = rendered[(str(view), tick)].get(room)
            ground_status = rendered[("ground", tick)].get(room)
            results.append((
                f"[{label}] vault last known on view {view} at frame {tick}, "
                f"observed on ground truth",
                view_status == "stale" and ground_status == "observed",
                f"view {view}={view_status} ground={ground_status}"))

            # The room label is drawn only for a room the view has reached, so
            # its presence on both views is the first half of "the page shows
            # this room as dimmed here and lit there".
            in_both = room in rendered[(str(view), tick)] and room in rendered[("ground", tick)]
            results.append((f"[{label}] vault room label present both views at frame {tick}",
                            in_both, f"vault={room}"))

            # Second half: the paint. The room title is drawn in COLORS.roomText
            # when observed and COLORS.fogText when last-known, so the ink census
            # below tells us what the page actually put on the canvas.
            await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
            await page.evaluate(SLIDE, tick)
            await page.wait_for_function(wait_frame(tick, str(view)))
            agent_px = await page.evaluate(TITLE_CENSUS, mispaint_job(room))
            await page.locator('#perspective-chips .chip[data-id="ground"]').click()
            await page.wait_for_function(wait_frame(tick, "ground"))
            ground_px = await page.evaluate(TITLE_CENSUS, mispaint_job(room))
            # The paint, judged by the shared predicate the negative control
            # also drives: a stale room shows the fog colour and none of the lit
            # one here, and the reverse on ground truth.
            agent_held, agent_detail = title_painted(
                agent_px, FOG_TEXT_RGB, ROOM_TEXT_RGB, f"{label} view {view}")
            ground_held, ground_detail = title_painted(
                ground_px, ROOM_TEXT_RGB, FOG_TEXT_RGB, f"{label} ground")
            ok_paint = agent_held and ground_held
            results.append((
                f"[{label}] stale room painted dim on view {view} and lit on ground "
                f"truth at frame {tick}",
                ok_paint,
                f"view {view}: {agent_detail} | ground: {ground_detail}"))

        # -- no horizontal overflow ------------------------------------------
        ov = await page.evaluate(
            """() => {
              const r = document.querySelector('#viewer-canvas').getBoundingClientRect();
              return { docW: document.documentElement.scrollWidth, winW: window.innerWidth,
                       left: r.left, right: r.right };
            }"""
        )
        ok_ov = ov["docW"] <= ov["winW"] and ov["left"] >= 0 and ov["right"] <= ov["winW"] + 1
        results.append((f"[{label}] no horizontal overflow", ok_ov, json.dumps(ov)))

        # -- door pills on hover ---------------------------------------------
        # Any mid-episode frame will do; it is clamped to the recording rather
        # than assumed, so a shorter episode cannot strand this on a frame that
        # does not exist. Scroll the canvas into view first — small viewports
        # otherwise miss the hit target.
        pill_tick = min(13, frame_count - 1)
        await page.locator('#perspective-chips .chip[data-id="ground"]').click()
        await page.evaluate(SLIDE, pill_tick)
        await page.wait_for_function(wait_frame(pill_tick, "ground"))
        doors = (await page.evaluate("window.__latticeGeo"))["doors"]
        ndoors = len(doors)
        cursor = False
        canvas = page.locator("#viewer-canvas")
        await canvas.scroll_into_view_if_needed()
        if doors:
            d = next(iter(doors.values()))
            await canvas.hover(position={"x": d["x"] + d["w"] / 2, "y": d["y"] + 12})
            await page.wait_for_timeout(150)
            cursor = await page.evaluate("(document.querySelector('canvas').style.cursor === 'pointer')")
        results.append((f"[{label}] door pills hover", ndoors == 7 and cursor, f"doors={ndoors} cursor={cursor}"))

        # -- legend: compact aligned item grid ------------------------------
        # Each item is one unit [16px swatch][bold label + description flowing as
        # one paragraph]; swatch top-aligned to the first text line; grid rows
        # sized to content. Also opens the Agent-view details so its items are
        # measured too.
        legend_probe = """() => {
          const section = document.querySelector('.legend');
          const details = document.querySelector('.legend-details');
          if (details && !details.open) details.open = true;
          const containers = Array.from(section.querySelectorAll('.legend-grid, #legend-roles'));
          const items = [];
          for (const ul of containers) {
            for (const li of ul.children) {
              if (li.tagName !== 'LI') continue;
              const r = li.getBoundingClientRect();
              const sw = li.querySelector('.swatch');
              let label = li.querySelector('b');
              if (!label) {
                label = Array.from(li.childNodes).reverse()
                  .find((n) => n.nodeType === 3 && n.textContent.trim());
              }
              const gridR = ul.getBoundingClientRect();
              let labelTop = label
                ? (label.getBoundingClientRect
                    ? label.getBoundingClientRect().top
                    : (() => { const r = document.createRange(); r.selectNodeContents(label);
                        return r.getBoundingClientRect().top; })())
                : r.top;
              items.push({
                text: (li.textContent || '').trim().slice(0, 36),
                h: Math.round(r.height * 100) / 100,
                swatchTop: sw ? Math.round(sw.getBoundingClientRect().top * 100) / 100 : null,
                labelTop: label ? Math.round(labelTop * 100) / 100 : null,
                bottom: Math.round(r.bottom * 100) / 100,
                gridBottom: Math.round(gridR.bottom * 100) / 100,
                mono: Array.from(li.querySelectorAll('.mono')).map((m) => m.getClientRects().length),
                visible: r.width > 0 && r.height > 0,
              });
            }
          }
          const secR = section.getBoundingClientRect();
          return {
            items,
            gridBottoms: containers.map((u) => Math.round(u.getBoundingClientRect().bottom * 100) / 100),
            sectionBottom: Math.round(secR.bottom * 100) / 100,
            sectionHeight: Math.round(secR.height * 100) / 100,
          };
        }"""
        legend = await page.evaluate(legend_probe)
        cap = 110 if viewport["width"] >= 1280 else 160
        bad_legend = []
        if not legend["items"]:
            bad_legend.append("no legend items found")
        for it in legend["items"]:
            if not it["visible"]:
                continue
            if it["h"] > cap:
                bad_legend.append(f"{it['text']}: height {it['h']}>{cap}")
            if it["swatchTop"] is not None and it["labelTop"] is not None and abs(it["swatchTop"] - it["labelTop"]) > 6:
                bad_legend.append(f"{it['text']}: swatch-top {it['swatchTop']} vs label-top {it['labelTop']}")
            if any(c != 1 for c in it["mono"]):
                bad_legend.append(f"{it['text']}: mono rects {it['mono']}")
            if it["bottom"] > it["gridBottom"] + 1:
                bad_legend.append(f"{it['text']}: item bottom {it['bottom']} > grid bottom {it['gridBottom']}")
        for gb in legend["gridBottoms"]:
            if gb > legend["sectionBottom"] + 1:
                bad_legend.append(f"grid bottom {gb} > legend section bottom {legend['sectionBottom']}")
        results.append(
            (f"[{label}] legend compact aligned grid (height<={cap}px, swatch~label, "
             f"mono nowrap, nothing clipped)",
             not bad_legend, "; ".join(bad_legend) or f"all {len(legend['items'])} items ok"))
        legend_shots = ROOT / "ui_tests" / "screenshots"
        legend_shots.mkdir(parents=True, exist_ok=True)
        shot = legend_shots / f"legend_{viewport['width']}x{viewport['height']}.png"
        await page.locator(".legend").screenshot(path=str(shot))

        # -- console/page errors ---------------------------------------------
        results.append((f"[{label}] no console/page errors", not errors, json.dumps(errors)))
    finally:
        await context.close()
    return results


class TestDemoPageUI(unittest.TestCase):
    def _check(self, viewport, reduced, recording_name="infiltration.jsonl",
               expected_badge="Recorded perception", require_stale_moment=True):
        httpd, port = start_server()
        base = f"http://127.0.0.1:{port}/"
        try:
            async def run():
                async with async_playwright() as p:
                    browser = await p.chromium.launch()
                    try:
                        return await run_viewport(
                            browser, base,
                            "desktop" if viewport["width"] >= 1280 else "mobile",
                            viewport, reduced,
                            recording_name=recording_name,
                            expected_badge=expected_badge,
                            require_stale_moment=require_stale_moment,
                        )
                    finally:
                        await browser.close()

            results = asyncio.run(run())
        finally:
            httpd.shutdown()
            httpd.server_close()
        for name, ok, detail in results:
            with self.subTest(check=name):
                self.assertTrue(ok, detail)

    def test_desktop_1440x900(self):
        self._check({"width": 1440, "height": 900}, reduced=False)

    def test_mobile_390x844(self):
        self._check({"width": 390, "height": 844}, reduced=False)

    def test_mobile_390x844_reduced_motion(self):
        self._check({"width": 390, "height": 844}, reduced=True)

    def test_demo_jsonl_badge_is_reconstructed_sightline(self):
        """demo.jsonl has no Perceptions; the page must say Reconstructed sightline."""
        httpd, port = start_server()
        base = f"http://127.0.0.1:{port}/"
        try:
            async def run():
                async with async_playwright() as p:
                    browser = await p.chromium.launch()
                    try:
                        context = await browser.new_context(viewport={"width": 1440, "height": 900})
                        page = await context.new_page()
                        await page.goto(base + measure_params("infiltration.jsonl"), wait_until="networkidle")
                        await page.wait_for_function(
                            "window.__latticeGeo && window.__latticeGeo.frame && "
                            "document.querySelectorAll('#perspective-chips .chip').length >= 3"
                        )
                        await page.locator('#scenario-chips .chip[data-id="demo"]').click()
                        await page.wait_for_function(
                            "() => document.querySelector('#source-label')?.textContent?.includes('demo.jsonl')"
                        )
                        await page.wait_for_function(
                            "window.__latticeGeo && window.__latticeGeo.frame && "
                            "document.querySelectorAll('#perspective-chips .chip').length >= 2"
                        )
                        agent = page.locator('#perspective-chips .chip:not([data-id="ground"])').first
                        await agent.click()
                        await page.wait_for_timeout(150)
                        badge = await page.evaluate(BADGE)
                        recording = fetch_recording(base, "demo.jsonl")
                        self.assertFalse(
                            has_recorded_perceptions(recording),
                            "demo.jsonl unexpectedly carries Perceptions")
                        self.assertIn("Reconstructed sightline", badge, badge)
                    finally:
                        await browser.close()

            asyncio.run(run())
        finally:
            httpd.shutdown()
            httpd.server_close()

    def test_title_census_rejects_a_mispainted_title(self):
        """The ink floor and the colour claims must not make the census lenient.

        The stale-room check trusts a room title to be painted fog-dim on the
        agent's view and lit on ground truth by counting the ink in the title's
        box against the fill of the card it sits on. Its floor is a fraction of
        the box, deliberately far below what a correct paint leaves, so this
        pins the other half of that bargain: a title with a full row of the
        *wrong* colour added and every one of its correct pixels kept, a title
        painted in the wrong colour throughout, and a title painted in nothing
        at all, must each still be rejected, and each for the reason it should
        be. Without this, a floor low enough to survive a sub-pixel rasteriser
        would also cost the check its ability to see a mis-render.

        The verdict comes from `title_painted`, the same predicate the viewport
        sweep asserts with, and the case is damaged and read in one JavaScript
        turn so no repaint can slip between them. The context asks for reduced
        motion because that is what makes the canvas hold still: on an agent
        view the page's radar pulse redraws every 90ms (site/app.js, startPulse),
        which silently restores a damaged band before the census can see it.
        Each case is also read twice, 200ms apart, so a census taken over a
        moving canvas fails here instead of passing by luck.
        """
        httpd, port = start_server()
        base = f"http://127.0.0.1:{port}/"

        async def run():
            async with async_playwright() as p:
                browser = await p.chromium.launch()
                try:
                    context = await browser.new_context(
                        viewport={"width": 390, "height": 844},
                        reduced_motion="reduce")
                    page = await context.new_page()
                    await page.goto(base + measure_params("infiltration.jsonl"), wait_until="networkidle")
                    await page.wait_for_function(
                        "window.__latticeGeo && window.__latticeGeo.frame")
                    recording = fetch_recording(base)
                    agents = list(range(recording[0]["SimulationConfig"]["AgentCount"]))
                    zone = vault_zone(recording)
                    self.assertIsNotNone(zone, "no zone carries the TreasureVault role")
                    moments = stale_moments(recording, agents)
                    self.assertTrue(moments, "the recording has no 'last known' moment")
                    view, tick = moments[0]
                    room = str(zone)
                    where = f"control view {view}"

                    async def repaint():
                        # Each mis-paint is destructive, so every case starts
                        # from a frame the page has drawn itself. Stepping off
                        # the tick and back forces that repaint: a draw already
                        # sitting on this tick may be a no-op.
                        await page.evaluate(SLIDE, 0)
                        await page.wait_for_function(wait_frame(0, str(view)))
                        await page.evaluate(SLIDE, tick)
                        await page.wait_for_function(wait_frame(tick, str(view)))

                    async def damaged(mode):
                        """Damage the band and census it in one turn, then prove
                        the canvas did not move under the reading."""
                        px = await page.evaluate(
                            TITLE_CENSUS, mispaint_job(room, mode, ROOM_TEXT_RGB))
                        held, detail = title_painted(px, FOG_TEXT_RGB, ROOM_TEXT_RGB, where)
                        await page.wait_for_timeout(200)
                        again = await page.evaluate(TITLE_CENSUS, mispaint_job(room))
                        self.assertEqual(
                            [tuple(p) for p in px["pixels"]],
                            [tuple(p) for p in again["pixels"]],
                            f"control: the canvas was repainted under the {mode} "
                            f"census, so this case proves nothing")
                        return held, detail, title_census(px, FOG_TEXT_RGB, ROOM_TEXT_RGB)

                    await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
                    await page.evaluate(SLIDE, tick)
                    await page.wait_for_function(wait_frame(tick, str(view)))

                    # A correctly painted stale title is held, and the reason has
                    # to be the ink: it clears the floor, and none of it is the
                    # lit colour.
                    px = await page.evaluate(TITLE_CENSUS, mispaint_job(room))
                    held, detail = title_painted(px, FOG_TEXT_RGB, ROOM_TEXT_RGB, where)
                    good = title_census(px, FOG_TEXT_RGB, ROOM_TEXT_RGB)
                    self.assertIsNotNone(good, "the control could not read the card fill")
                    self.assertTrue(held, f"control: a correct paint must be held: {detail}")
                    self.assertGreaterEqual(good["own_ink"], good["ink_floor"], detail)
                    self.assertEqual(good["other_ink"], 0, detail)
                    self.assertEqual(good["dominance"], 1.0, detail)

                    # One device row of the lit colour, added on top of the slack
                    # above the glyphs. Every fog pixel survives, so the ink floor
                    # is still met and only the colour claim can reject it --
                    # which is what keeps that claim load-bearing.
                    await repaint()
                    held, detail, census = await damaged("lit-row")
                    self.assertGreaterEqual(
                        census["own_ink"], census["ink_floor"],
                        f"the lit row must not disturb the fog ink: {detail}")
                    self.assertGreater(
                        census["other_ink"], 0,
                        "the lit row must ink some of the wrong colour")
                    self.assertGreater(
                        census["other_ink"], census["other_budget"],
                        f"the lit row must break the colour budget: {detail}")
                    self.assertFalse(
                        held, f"one lit row with the fog glyphs kept must be "
                              f"rejected: {detail}")

                    # The whole band repainted in the lit colour: no fog ink is
                    # left at all, so the floor is the claim that rejects it.
                    await repaint()
                    held, detail, census = await damaged("band")
                    self.assertEqual(census["own_ink"], 0, f"band: {detail}")
                    self.assertGreater(
                        census["other_ink"], census["other_budget"],
                        f"band: {detail}")
                    self.assertFalse(
                        held, f"a title repainted lit must be rejected: {detail}")

                    # And a title painted in nothing at all: the band is cleared
                    # to the page behind the canvas, so there is no other colour
                    # either and the ink floor is all that is left.
                    await repaint()
                    held, detail, census = await damaged("erase")
                    self.assertEqual(census["own_ink"], 0, f"erased band: {detail}")
                    self.assertEqual(census["other_ink"], 0, f"erased band: {detail}")
                    self.assertEqual(
                        census["cleared"], census["area"],
                        f"the erase case must clear the whole box: {detail}")
                    self.assertFalse(
                        held, f"an unpainted title must be rejected: {detail}")
                    await context.close()
                finally:
                    await browser.close()

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main(verbosity=2)