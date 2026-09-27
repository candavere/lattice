"""Canvas-layout invariants for the replay viewer in site/.

Serves the committed site/ directory on a localhost port and drives the page
with the bundled `?measure=1` geometry probe (window.__latticeGeo), which the
draw code fills with the exact rectangles it just painted: every room box, agent
name ink box, transit caption ink box, room count pill and "last seen" ghost
label, plus the token centres and the canvas CSS size. Nothing in the probe can
change what is drawn — every record is written from the values the draw calls
already computed.

Every tick of BOTH committed recordings (demo.jsonl, infiltration.jsonl) is
swept under EVERY perspective the page offers (ground truth plus one chip per
agent) at desktop, tablet and mobile viewports, and five defect classes are
counted per (recording, view):

  label_overflow  an agent name / ghost label sticking out of the room box it
                  belongs to (a transit token has no room, so its name is held
                  to the canvas and to the room-label bands instead)
  room_overlap    two room boxes intersecting
  caption_label   a transit caption ink box intersecting an agent name ink box
  pill_border     a room count pill not wholly inside its room box with padding
  edge_clip       any room box or ink box crossing the canvas edge
  label_label     an agent name ink box intersecting another name ink box

Report (counts per class, per recording and view, no assertions):

    python3 ui_tests/test_viewer_layout.py --report

Regression test (the same sweep, asserted):

    python3 -m unittest discover -s ui_tests -p 'test_*.py' -v
"""

import asyncio
import json
import unittest

from playwright.async_api import async_playwright

from test_demo_page_ui import SLIDE, wait_frame, start_server

RECORDINGS = ("infiltration", "demo")

# (width, height) viewports the sweep runs at. 1280 is the width the hero GIF is
# captured at, 1440 the default desktop, 1024 the tablet breakpoint, 390 the
# phone.
VIEWPORTS = (
    ("1280x720", {"width": 1280, "height": 720}),
    ("1440x900", {"width": 1440, "height": 900}),
    ("1024x768", {"width": 1024, "height": 768}),
    ("390x844", {"width": 390, "height": 844}),
)

CLASSES = (
    "label_overflow",
    "room_overlap",
    "caption_label",
    "pill_border",
    "edge_clip",
    "label_label",
    "transit_on_room",
    "pill_loot",
    "pill_label",
)

# A pill must clear the room border by this much on every side.
PILL_PAD = 1.0

# Ink boxes are reported one font-size tall and centred on a `middle` baseline
# (see probeInk in site/app.js). Overlap tests use a hair of tolerance so a
# box that merely touches another reports as touching, not as overlapping.
EPS = 0.01


def rect(o):
    return (o["x"], o["y"], o["x"] + o["w"], o["y"] + o["h"])


def intersect(a, b, eps=EPS):
    return not (a[2] <= b[0] + eps or b[2] <= a[0] + eps
                or a[3] <= b[1] + eps or b[3] <= a[1] + eps)


def contains(outer, inner, pad=0.0, eps=EPS):
    return (inner[0] >= outer[0] + pad - eps and inner[1] >= outer[1] + pad - eps
            and inner[2] <= outer[2] - pad + eps and inner[3] <= outer[3] - pad + eps)


def overflow_by(outer, inner):
    """How far `inner` sticks out of `outer`, per side, in css px."""
    return (max(0.0, outer[0] - inner[0]), max(0.0, outer[1] - inner[1]),
            max(0.0, inner[2] - outer[2]), max(0.0, inner[3] - outer[3]))


def overlap_depth(a, b):
    """How deeply two rectangles intersect, per axis, in css px."""
    return (min(a[2], b[2]) - max(a[0], b[0]), min(a[3], b[3]) - max(a[1], b[1]))


def classify(geo):
    """Defect classes found in one painted frame, as (counts, examples)."""
    counts = dict.fromkeys(CLASSES, 0)
    examples = {c: [] for c in CLASSES}

    def hit(cls, detail):
        counts[cls] += 1
        if len(examples[cls]) < 3:
            examples[cls].append(detail)

    canvas = geo.get("frame", {}).get("canvas") or {}
    cw, ch = canvas.get("cssW"), canvas.get("cssH")
    rooms = {str(z): rect(r) for z, r in (geo.get("rooms") or {}).items()}
    names = geo.get("names") or {}
    ghosts = geo.get("ghosts") or {}
    captions = geo.get("captions") or {}
    pills = geo.get("pills") or {}

    # -- room boxes against each other ---------------------------------------
    room_ids = sorted(rooms)
    for i, a in enumerate(room_ids):
        for b in room_ids[i + 1:]:
            if intersect(rooms[a], rooms[b]):
                ov = overlap_depth(rooms[a], rooms[b])
                hit("room_overlap",
                    "zone %s vs %s overlap %.1fpx (x,y=%s)"
                    % (a, b, max(0.0, min(ov)), ",".join("%.1f" % v for v in ov)))

    # -- agent name labels ---------------------------------------------------
    name_boxes = {k: rect(v) for k, v in names.items()}
    for aid, box in sorted(name_boxes.items()):
        label = names[aid].get("text", "?")
        zone = names[aid].get("zone")
        zone = None if zone is None else str(zone)
        if zone is not None and zone in rooms:
            if not contains(rooms[zone], box):
                ov = overflow_by(rooms[zone], box)
                hit("label_overflow",
                    "agent %s %r out of zone %s by %.1fpx (l,t,r,b=%s)"
                    % (aid, label, zone, max(ov),
                       ",".join("%.1f" % v for v in ov)))
        else:
            # In transit: no room owns the label, so it may not sit on a room's
            # title band, and must not leave the canvas.
            for zid, rbox in sorted(rooms.items()):
                r = (geo.get("rooms") or {})[zid]
                title = rect((geo.get("labels") or {}).get(
                    zid, {"x": r["cx"], "y": r["cy"], "w": 0, "h": 0}))
                if intersect(box, title):
                    hit("label_overflow",
                        "transit agent %s %r over zone %s title band" % (aid, label, zid))
                    break
    for aid, box in sorted(ghosts.items()):
        zone = ghosts[aid].get("zone")
        zone = None if zone is None else str(zone)
        if zone in rooms and not contains(rooms[zone], box):
            ov = overflow_by(rooms[zone], box)
            hit("label_overflow",
                "ghost agent %s %r out of zone %s by %.1fpx"
                % (aid, ghosts[aid].get("text"), zone, max(ov)))

    aids = sorted(name_boxes)
    for i, a in enumerate(aids):
        for b in aids[i + 1:]:
            if intersect(name_boxes[a], name_boxes[b]):
                hit("label_label", "agent %s name vs agent %s name" % (a, b))

    # -- a transit label must not be painted on a room box either ------------
    for kind, boxes in (("name", name_boxes), ("caption", {k: rect(v) for k, v in captions.items()})):
        for aid, box in sorted(boxes.items()):
            if names.get(aid, {}).get("zone") is not None and kind == "name":
                continue  # in a room already checked against its own card
            for zid, rbox in sorted(rooms.items()):
                if intersect(box, rbox):
                    hit("transit_on_room", "transit %s of agent %s over room %s"
                        % (kind, aid, zid))

    # -- transit captions ----------------------------------------------------
    for aid, box in sorted(captions.items()):
        cbox = rect(box)
        for other, nbox in sorted(name_boxes.items()):
            if intersect(cbox, nbox):
                hit("caption_label", "agent %s caption %r vs agent %s name %r"
                    % (aid, box.get("text"), other, names[other].get("text")))
        for other, gbox in sorted((k, rect(v)) for k, v in ghosts.items()):
            if intersect(cbox, gbox):
                hit("caption_label", "agent %s caption %r vs agent %s ghost %r"
                    % (aid, box.get("text"), other, ghosts[other].get("text")))

    # -- room count pills ----------------------------------------------------
    loot = {str(z): rect(v) for z, v in (geo.get("loot") or {}).items()}
    for zid, pill in sorted(pills.items()):
        pbox = rect(pill)
        if zid in rooms and not contains(rooms[zid], pbox, PILL_PAD):
            ov = overflow_by(rooms[zid], pbox)
            hit("pill_border",
                "zone %s pill %r (%s) by %.1fpx (l,t,r,b=%s)"
                % (zid, pill.get("text"), pill.get("where"), max(ov),
                   ",".join("%.1f" % v for v in ov)))
        if zid in loot and intersect(pbox, loot[zid]):
            hit("pill_loot", "zone %s pill %r over the loot row" % (zid, pill.get("text")))
        for aid, nbox in sorted(name_boxes.items()):
            if names[aid].get("zone") == zid or str(names[aid].get("zone")) == zid:
                if intersect(pbox, nbox):
                    hit("pill_label", "zone %s pill %r over agent %s name"
                        % (zid, pill.get("text"), aid))

    # -- canvas edge ---------------------------------------------------------
    if cw and ch:
        canvas_box = (0.0, 0.0, float(cw), float(ch))
        for zid, rbox in sorted(rooms.items()):
            if not contains(canvas_box, rbox):
                hit("edge_clip", "zone %s box outside canvas %sx%s" % (zid, cw, ch))
        for kind, boxes in (("name", name_boxes), ("caption", {k: rect(v) for k, v in captions.items()}),
                            ("ghost", {k: rect(v) for k, v in ghosts.items()}),
                            ("pill", {k: rect(v) for k, v in pills.items()})):
            for aid, box in sorted(boxes.items()):
                if not contains(canvas_box, box):
                    hit("edge_clip", "%s %s outside canvas %sx%s" % (kind, aid, cw, ch))
        for aid, t in sorted((geo.get("tokens") or {}).items()):
            tb = (t["x"] - t["r"], t["y"] - t["r"], t["x"] + t["r"], t["y"] + t["r"])
            if not contains(canvas_box, tb):
                hit("edge_clip", "token %s outside canvas %sx%s" % (aid, cw, ch))

    return counts, examples


async def sweep(browser, base, viewport):
    """Full sweep: both recordings, every view, every tick, one viewport."""
    context = await browser.new_context(viewport=viewport, reduced_motion="no-preference")
    page = await context.new_page()
    report = {}
    errors = []
    page.on("console", lambda m: errors.append("console:%s:%s" % (m.type, m.text))
            if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append("pageerror:%s" % e))
    try:
        await page.goto(base + "?measure=1", wait_until="networkidle")
        await page.wait_for_function("window.__latticeGeo && window.__latticeGeo.frame")

        for rec in RECORDINGS:
            await page.locator('#scenario-chips .chip[data-id="%s"]' % rec).click()
            await page.wait_for_function(
                "() => document.querySelector('#source-label').textContent === %s"
                % json.dumps("%s.jsonl" % rec))
            await page.wait_for_function(
                "() => document.querySelectorAll('#perspective-chips .chip').length >= 2")

            views = await page.locator("#perspective-chips .chip").evaluate_all(
                "els => els.map(e => e.dataset.id)")
            frame_max = int(await page.evaluate("() => document.querySelector('#scrub-slider').max"))

            for view in views:
                await page.locator('#perspective-chips .chip[data-id="%s"]' % view).click()
                await page.evaluate(SLIDE, 0)
                await page.wait_for_function(wait_frame(0, view))
                totals = dict.fromkeys(CLASSES, 0)
                examples = {c: [] for c in CLASSES}
                for tick in range(0, frame_max + 1):
                    await page.evaluate(SLIDE, tick)
                    await page.wait_for_function(wait_frame(tick, view))
                    geo = await page.evaluate("window.__latticeGeo")
                    counts, ex = classify(geo)
                    for c in CLASSES:
                        totals[c] += counts[c]
                        for line in ex[c]:
                            if len(examples[c]) < 4 and line not in examples[c]:
                                examples[c].append("tick %d: %s" % (tick, line))
                report["%s/%s" % (rec, view)] = {
                    "ticks": frame_max + 1, "counts": totals, "examples": examples,
                }
    finally:
        await context.close()
    return report, errors


def run_sweep(viewport):
    httpd, port = start_server()
    base = "http://127.0.0.1:%d/" % port

    async def run():
        async with async_playwright() as p:
            browser = await p.chromium.launch()
            try:
                return await sweep(browser, base, viewport)
            finally:
                await browser.close()

    try:
        return asyncio.run(run())
    finally:
        httpd.shutdown()
        httpd.server_close()


class TestViewerLayout(unittest.TestCase):
    """The layout invariants, at every committed viewport."""

    def _check(self, name, viewport):
        report, errors = run_sweep(viewport)
        for key, entry in sorted(report.items()):
            with self.subTest(viewport=name, sweep=key):
                bad = {c: n for c, n in entry["counts"].items() if n}
                self.assertEqual(
                    bad, {},
                    "%s %d ticks: %s" % (key, entry["ticks"],
                                         "; ".join(
                                             "%s=%d [%s]" % (c, n, " | ".join(entry["examples"][c]))
                                             for c, n in bad.items())))
        self.assertEqual(errors, [], "console/page errors")

    def test_viewport_1280x720(self):
        self._check("1280x720", {"width": 1280, "height": 720})

    def test_viewport_1440x900(self):
        self._check("1440x900", {"width": 1440, "height": 900})

    def test_viewport_1024x768(self):
        self._check("1024x768", {"width": 1024, "height": 768})

    def test_viewport_390x844(self):
        self._check("390x844", {"width": 390, "height": 844})


def main():
    import argparse

    ap = argparse.ArgumentParser()
    ap.add_argument("--report", action="store_true", help="print per-class counts")
    ap.add_argument("--viewport", default="all")
    args = ap.parse_args()
    wanted = [v for v in VIEWPORTS if args.viewport in ("all", v[0])]
    grand = 0
    for name, viewport in wanted:
        report, errors = run_sweep(viewport)
        print("== viewport %s" % name)
        header = "  %-28s %5s " % ("recording/view", "ticks") + " ".join("%17s" % c for c in CLASSES)
        print(header)
        for key, entry in sorted(report.items()):
            row = "  %-28s %5d " % (key, entry["ticks"])
            row += " ".join("%17d" % entry["counts"][c] for c in CLASSES)
            print(row)
            grand += sum(entry["counts"].values())
            if args.report:
                for c in CLASSES:
                    for line in entry["examples"][c]:
                        print("       %s: %s" % (c, line))
        if errors:
            print("  console/page errors: %s" % errors)
            grand += len(errors)
    print("TOTAL DEFECTS: %d" % grand)
    return 0 if grand == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
