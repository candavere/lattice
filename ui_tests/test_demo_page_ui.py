"""Tracked Playwright regression for the demo replay page in site/.

Serves the committed site/ directory on a localhost port (no dependency on the
live GitHub Pages URL) and drives it with the bundled ?measure=1 geometry probe
(window.__latticeGeo: exact on-canvas bounds of every room label, agent token
and door pill just drawn, plus per-frame index/perspective).

Covers what the demo simplification stage asserts:
  * one-click perspective chips (ground aside, agent views) + keyboard roving,
  * single-map viewer with the "Reconstructed sightline" badge + moving caption,
  * no token ever overlaps a room label (every frame, both perspectives),
  * no horizontal overflow of the page or the graph canvas,
  * the fog reconstruction: the per-room status the page renders on every frame
    of both agent views is re-derived from the recording and compared zone by
    zone, and a "last known" moment (one agent view's room stale while ground
    truth still calls it observed) is discovered from the recording, required to
    exist, and checked to be painted the way the page says it is,
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

MEASURE_PARAMS = "?measure=1&infiltration.jsonl"

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
# The page reconstructs an agent's sight from recorded positions (see the
# "reconstructed sightline" copy) with a vision radius in graph hops. These
# helpers re-derive that from the recording alone, so the UI assertions below
# can be made against the file instead of against a hand-picked tick: if the
# engine moves the episode, the derived frames and fog moments move with it.

DEFAULT_VISION_HOPS = 2  # the page's fallback when the header records no radius


def fetch_recording(base, name="infiltration.jsonl"):
    """The recording the page loaded, read over the same local server."""
    with urllib.request.urlopen(base + name) as response:
        text = response.read().decode("utf-8")
    return [json.loads(line) for line in text.splitlines() if line.strip()]


def vision_hops(header):
    """Mirror of the page's visionHops(): the recorded radius, else its default."""
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
    """{frame: {zone: 'observed' | 'stale' | 'unknown'}} for one agent view,
    with the page's cumulative discovery: a room that has been reachable at any
    earlier frame but is not now reads 'last known'."""
    header = recording[0]
    adj = _adjacency(header)
    hops = vision_hops(header)
    zone_ids = [zone["Id"] for zone in header["Map"]["Zones"]]
    last_seen = {}
    timeline = {}
    for index, agents in enumerate(recorded_frames(recording)):
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


# Pixel census of one room's label box, so "the page says stale" and "the page
# paints stale" are separate facts. The room title is drawn in COLORS.roomText
# when observed and COLORS.fogText when stale; both are opaque, so the glyph
# cores land on the exact colour while only the antialiased edges blend.
ROOM_TEXT_RGB = (0xC0, 0xCA, 0xF5)  # COLORS.roomText
FOG_TEXT_RGB = (0x5B, 0x6A, 0x8A)  # COLORS.fogText
LABEL_CORE_PIXELS = 20  # glyph cores seen for every label in the sweep
COLOUR_TOLERANCE = 6

ROOM_LABEL_PIXELS = """(zoneId) => {
  const g = window.__latticeGeo;
  const box = g.labels[zoneId];
  if (!box) return { zoneId: zoneId, box: null, counts: {} };
  const canvas = document.querySelector('#viewer-canvas');
  const dpr = canvas.width / canvas.getBoundingClientRect().width;
  const ctx = canvas.getContext('2d');
  const x = Math.round(box.x * dpr), y = Math.round(box.y * dpr);
  const w = Math.max(1, Math.round(box.w * dpr)), h = Math.max(1, Math.round(box.h * dpr));
  const img = ctx.getImageData(x, y, w, h).data;
  const counts = {};
  for (let i = 0; i < img.length; i += 4) {
    const key = img[i] + ',' + img[i + 1] + ',' + img[i + 2];
    counts[key] = (counts[key] || 0) + 1;
  }
  return { zoneId: zoneId, box: box, counts: counts };
}"""


def count_near(counts, rgb):
    """Pixels in the census within COLOUR_TOLERANCE of `rgb` on every channel."""
    total = 0
    for key, count in counts.items():
        pixel = tuple(int(channel) for channel in key.split(","))
        if all(abs(pixel[i] - rgb[i]) <= COLOUR_TOLERANCE for i in range(3)):
            total += count
    return total


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


async def run_viewport(browser, base, label, viewport, reduced):
    results = []
    context = await browser.new_context(
        viewport=viewport, reduced_motion="reduce" if reduced else "no-preference"
    )
    page = await context.new_page()

    errors = []
    page.on("console", lambda m: errors.append(f"console:{m.type}:{m.text}") if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(f"pageerror:{e}"))

    try:
        await page.goto(base + MEASURE_PARAMS, wait_until="networkidle")
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
        recording = fetch_recording(base)
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
        ok_default = active == ["Sentry"] and "Reconstructed sightline" in badge
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
        ok_sentry = str(caption).startswith("What the Sentry could reach") and "Reconstructed sightline" in badge
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
        # which is a failure rather than a pass.
        zone = vault_zone(recording)
        moments = stale_moments(recording, agent_ids)
        results.append((
            f"[{label}] recording has a 'last known' moment to demonstrate",
            zone is not None and bool(moments),
            f"vault zone {zone}, moments {moments}" if zone is not None
            else "no zone carries the TreasureVault role"))
        if zone is None or not moments:
            results.append((f"[{label}] vault last-known moment renders as recorded", False,
                            "no moment discovered, so there is nothing to check"))
        else:
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
            # when observed and COLORS.fogText when last-known, so the glyph
            # cores tell us what the page actually put on the canvas.
            await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
            await page.evaluate(SLIDE, tick)
            await page.wait_for_function(wait_frame(tick, str(view)))
            agent_px = await page.evaluate(ROOM_LABEL_PIXELS, room)
            await page.locator('#perspective-chips .chip[data-id="ground"]').click()
            await page.wait_for_function(wait_frame(tick, "ground"))
            ground_px = await page.evaluate(ROOM_LABEL_PIXELS, room)
            agent_dim = count_near(agent_px["counts"], FOG_TEXT_RGB)
            agent_bright = count_near(agent_px["counts"], ROOM_TEXT_RGB)
            ground_dim = count_near(ground_px["counts"], FOG_TEXT_RGB)
            ground_bright = count_near(ground_px["counts"], ROOM_TEXT_RGB)
            ok_paint = (agent_px["box"] is not None and ground_px["box"] is not None
                        and agent_dim >= LABEL_CORE_PIXELS and agent_bright == 0
                        and ground_bright >= LABEL_CORE_PIXELS and ground_dim == 0)
            results.append((
                f"[{label}] stale room painted dim on view {view} and lit on ground "
                f"truth at frame {tick}",
                ok_paint,
                f"view {view}: dim {agent_dim} bright {agent_bright} | "
                f"ground: dim {ground_dim} bright {ground_bright}"))

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
        # does not exist.
        pill_tick = min(13, frame_count - 1)
        await page.locator('#perspective-chips .chip[data-id="ground"]').click()
        await page.evaluate(SLIDE, pill_tick)
        await page.wait_for_function(wait_frame(pill_tick, "ground"))
        doors = (await page.evaluate("window.__latticeGeo"))["doors"]
        ndoors = len(doors)
        cursor = False
        if doors:
            d = next(iter(doors.values()))
            xy = await page.evaluate(
                """() => { const r = document.querySelector('#viewer-canvas').getBoundingClientRect();
                return [r.left, r.top]; }"""
            )
            await page.mouse.move(xy[0] + d["x"] + d["w"] / 2, xy[1] + d["y"] + 12, steps=2)
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
    def _check(self, viewport, reduced):
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


if __name__ == "__main__":
    unittest.main(verbosity=2)