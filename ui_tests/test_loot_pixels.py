"""Tracked Playwright regression: the loot diamonds are painted, in the colour
the selected view calls for.

Every other viewer test reads what the page *says*. This one reads what the page
*painted*: `getImageData` on the real canvas, one pixel census per diamond and
one over the whole canvas. The `?measure=1` probe supplies only the coordinates
and the resource ids — where a diamond was placed and which chest it is — and
its own `claimed` / `color` fields are never consulted. A page that recorded a
green diamond it never drew would pass a probe-based check and fail this one,
which is the entire reason it exists.

Why the pixels cannot be matched by a constant:
  * the canvas is drawn in CSS pixels with a DPR transform, so `getImageData`
    addresses backing-store pixels and every probe coordinate is CSS. The boxes
    below are scaled by the measured DPR before a single pixel is read.
  * a stale row is drawn at globalAlpha 0.35, so a claimed chest in a room the
    observer has not reached is a *composite* of COLORS.claimed over that
    room's card, not the colour itself. Every pixel is fitted as a two-source
    composite over a background measured from the room card it sits on, so
    opacity is modelled rather than wished away.
  * the census sits on the diamond's core, half-extent 2 CSS px against a 5 CSS
    px radius, which is more than 3 CSS px inside the shape on every axis — so
    no antialiased boundary pixel can fall inside the box and be mistaken for
    ink, and a floor on the ink is a statement about the fill, not the edge.
  * the Sentry's perception perimeter is Sentry red (`#ff5252`) and is stroked
    across the map; a classifier loose enough to call that amber would let a
    diamond of any colour pass. A control asserts it does not.

Two claims are kept apart, because they fail for different reasons:
  * each visible diamond is painted the colour the selected view's recorded
    claim state calls for, over an ink floor, so a missing diamond, a
    mis-coloured one, and one buried under the card it lives in all fail;
  * the whole canvas carries the green those claims require, so the frame
    cannot be made to pass by painting green somewhere harmless.

Scope: one desktop frame, all three of its perspectives. This is a
single-frame pixel proof, deliberately not an every-frame sweep; the every-frame
semantic sweep is `test_agent_view_honesty.py`, which reads the probe, and
`test_viewer_layout.py`, which sweeps the geometry. The frame itself is
discovered from the recording — a frame where one observer's recorded claims
are a strict subset of the world's — so the check moves with the file instead of
pinned to a tick that a future engine change would make empty.

Run:  python3 -m unittest discover -s ui_tests -p 'test_*.py' -v
"""

import asyncio
import unittest

from playwright.async_api import async_playwright

from test_agent_view_honesty import (
    agent_ids,
    frames_with_claims,
    leak_frames,
    perception_at,
    recorded_claims,
    view_name,
)
from test_demo_page_ui import (
    INK_EPSILON,
    SEGMENT_TOLERANCE,
    SLIDE,
    fetch_recording,
    has_recorded_perception,
    recorded_steps,
    start_server,
    wait_frame,
)

RECORDING = "infiltration.jsonl"
VIEWPORT = {"width": 1440, "height": 900}

# The two candidate diamond colours, as they appear in site/app.js's COLORS.
CLAIMED_RGB = (0x3D, 0xA6, 0x6B)
UNCLAIMED_RGB = (0xE0, 0xAF, 0x68)
# COLORS.sentry, and the same colour as the dashed perception perimeter paints
# it at 0.55 alpha over a room card. Neither may read as a loot colour.
SENTRY_RGB = (0xFF, 0x52, 0x52)

# The diamond's core, in CSS px either side of its centre. drawResources paints
# a diamond of radius 5, so this box lies at least 3 CSS px inside the shape on
# every axis and never reaches an antialiased edge pixel.
CORE_HALF_CSS = 2.0

# A core box this filled with the expected colour is a painted diamond; one with
# no expected-colour ink at all is a diamond that was never painted, is painted
# under the card it lives in, or is painted the other colour. Half the box is
# generous enough to survive the Sentry's dashed perimeter or the Infiltrator's
# extraction marker crossing a row of it, and far too high to be met by nothing.
INK_FLOOR_SHARE = 0.5

# The other candidate's share of the same box. A mis-painted diamond puts all of
# its ink here, so this is the claim that catches a wrong colour even where the
# floor alone might be argued about.
OTHER_CEILING_SHARE = 0.5

# The whole-canvas floor, in device px per expected green diamond at DPR 1. A
# painted diamond covers about 36 CSS px; this asks for a fraction of that, so
# a platform's rasteriser is not the thing being measured.
CANVAS_GREEN_PER_DIAMOND = 8

# A composite only counts as ink once it has committed to the source colour.
# Without it, every pixel equal to a measured card fill is a perfect zero-alpha
# "composite" of that fill with anything at all.
MIN_ALPHA = 0.2

# One JavaScript turn, four jobs. The arithmetic lives here so the census that
# the assertions read and the control that calibrates it cannot drift apart; the
# judgement — which colour is expected, what share of the ink is enough, what
# the frame is supposed to contain — is entirely Python's.
#
#   survey   DPR, the canvas backing store, and for every drawn room its box
#            and the modal colour inside it, plus the device-pixel core box of
#            every diamond the page placed. Probe coordinates only: nothing here
#            reads `claimed` or `color`.
#   census   the buckets for one box, against one measured background.
#   canvas   how many pixels anywhere on the canvas fit a composite of one
#            source colour over ANY of the measured backgrounds.
#   colour   the verdict on one literal pixel, for the calibration control.
PIXEL_CENSUS = r"""(job) => {
  const g = window.__latticeGeo;
  const canvas = document.querySelector('#viewer-canvas');
  const ctx = canvas.getContext('2d');
  const dpr = canvas.width / canvas.getBoundingClientRect().width;

  // Residual, in 8-bit units, of the best composite of `src` over `fill` that
  // could have produced `p`, and the alpha that composite needed. Two sources
  // are the whole model for a loot diamond: the fill of the card it sits on,
  // and the diamond's own colour at whatever alpha the draw used.
  function fit(p, fill, src) {
    let num = 0, den = 0;
    for (let i = 0; i < 3; i += 1) {
      const d = src[i] - fill[i];
      num += (p[i] - fill[i]) * d;
      den += d * d;
    }
    const a = den ? Math.min(1, Math.max(0, num / den)) : 0;
    let r = 0;
    for (let i = 0; i < 3; i += 1) {
      r = Math.max(r, Math.abs(p[i] - (fill[i] + a * (src[i] - fill[i]))));
    }
    return {alpha: a, residual: r};
  }

  function modalOf(data) {
    const counts = new Map();
    for (let i = 0; i < data.length; i += 4) {
      const k = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
      counts.set(k, (counts.get(k) || 0) + 1);
    }
    let best = null, n = -1;
    counts.forEach((c, k) => { if (c > n) { n = c; best = k; } });
    return [(best >> 16) & 255, (best >> 8) & 255, best & 255];
  }

  function readBox(box) {
    const x = Math.max(0, Math.min(canvas.width - 1, Math.round(box[0])));
    const y = Math.max(0, Math.min(canvas.height - 1, Math.round(box[1])));
    const w = Math.max(1, Math.min(canvas.width - x, Math.round(box[2])));
    const h = Math.max(1, Math.min(canvas.height - y, Math.round(box[3])));
    return ctx.getImageData(x, y, w, h).data;
  }

  // Every pixel lands in exactly one bucket: cleared (the canvas was cleared to
  // transparent, which is not ink), the fill showing through (not ink), ink of
  // the expected colour, ink of the other candidate, or indeterminate. A pixel
  // that fits both candidates is indeterminate rather than counted for both, so
  // one ambiguous pixel cannot satisfy two claims.
  function census(data, fill, expected, other, tol, epsilon, minAlpha) {
    let hit = 0, miss = 0, indeterminate = 0, cleared = 0, differing = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] === 0) { cleared += 1; continue; }
      const p = [data[i], data[i + 1], data[i + 2]];
      let near = 0;
      for (let c = 0; c < 3; c += 1) {
        near = Math.max(near, Math.abs(p[c] - fill[c]));
      }
      if (near < epsilon) continue;
      differing += 1;
      const fe = fit(p, fill, expected);
      const fo = fit(p, fill, other);
      const okE = fe.residual <= tol && fe.alpha >= minAlpha;
      const okO = fo.residual <= tol && fo.alpha >= minAlpha;
      if (okE && !okO) hit += 1;
      else if (okO && !okE) miss += 1;
      else indeterminate += 1;
    }
    return {area: data.length / 4, expected: hit, other: miss,
            indeterminate: indeterminate, cleared: cleared, differing: differing};
  }

  if (job.mode === 'survey') {
    const half = job.half;
    const rooms = {};
    const loot = {};
    for (const [zid, r] of Object.entries(g.rooms || {})) {
      const box = [r.x * dpr, r.y * dpr, r.w * dpr, r.h * dpr];
      rooms[zid] = {box: box, fill: modalOf(readBox(box))};
    }
    for (const [zid, items] of Object.entries(g.lootItems || {})) {
      loot[zid] = items.map((it) => ({
        id: String(it.id),
        zone: String(zid),
        box: [(it.x - half) * dpr, (it.y - half) * dpr, 2 * half * dpr, 2 * half * dpr],
      }));
    }
    return {dpr: dpr, cw: canvas.width, ch: canvas.height,
            rooms: rooms, loot: loot, view: g.frame ? g.frame.perspective : null,
            index: g.frame ? g.frame.index : null};
  }

  if (job.mode === 'census') {
    return census(readBox(job.box), job.fill, job.expected, job.other,
                  job.tol, job.epsilon, job.minAlpha);
  }

  if (job.mode === 'canvas') {
    const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    let ink = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] === 0) continue;
      const p = [data[i], data[i + 1], data[i + 2]];
      for (const fill of job.fills) {
        const f = fit(p, fill, job.source);
        if (f.residual <= job.tol && f.alpha >= job.minAlpha) { ink += 1; break; }
      }
    }
    return {ink: ink, area: data.length / 4};
  }

  if (job.mode === 'colour') {
    const c = census(new Uint8ClampedArray(job.pixel), job.fill, job.expected,
                     job.other, job.tol, job.epsilon, job.minAlpha);
    return {expected: c.expected, other: c.other, indeterminate: c.indeterminate};
  }

  return {error: 'unknown job mode'};
}"""


def rgb(colour):
    return list(colour)


def over(fill, src, alpha):
    """The composite `src` at `alpha` over `fill`, as a flat RGBA pixel."""
    return [int(round(src[i] * alpha + fill[i] * (1 - alpha))) for i in range(3)] + [255]


def chosen_frame(recording, egos):
    """The frame this test reads: one where painting the world's claims would
    be visible, discovered from the file rather than pinned to a tick.

    It has to be a frame some observer's own recorded claims are a strict
    subset of the world's, *and* that observer is shown at least one of them as
    claimed. So the frame carries a chest a leaking viewer would paint in the
    other colour and this viewer must not, and a chest that has to be green and
    has to be green as pixels. The last such frame is taken, so the check
    follows the episode as it grows or shrinks.
    """
    steps = recorded_steps(recording)
    found = None
    for ego in egos:
        for frame in leak_frames(recording, ego):
            perception = perception_at(steps, frame, ego)
            if perception is None:
                continue
            claimed = recorded_claims(perception)
            visible = {str(r["ResourceId"]) for r in (perception.get("Resources") or [])
                       if r["Status"] != 2}
            if claimed and (claimed & visible):
                found = frame
    return found


async def show(page, tick, view):
    await page.locator(f'#perspective-chips .chip[data-id="{view}"]').click()
    await page.evaluate(SLIDE, tick)
    await page.wait_for_function(wait_frame(tick, view))


class TestLootPixels(unittest.TestCase):
    """One desktop frame of `site/infiltration.jsonl`, read as pixels."""

    def _drive(self, body):
        httpd, port = start_server()
        base = f"http://127.0.0.1:{port}/"
        try:

            async def run():
                async with async_playwright() as p:
                    browser = await p.chromium.launch()
                    try:
                        context = await browser.new_context(
                            viewport=VIEWPORT, reduced_motion="reduce")
                        page = await context.new_page()
                        errors = []
                        page.on("console",
                                lambda m: errors.append(m.text) if m.type == "error" else None)
                        page.on("pageerror", lambda e: errors.append(str(e)))
                        await page.goto(base + "?measure=1&" + RECORDING,
                                        wait_until="networkidle")
                        await page.wait_for_function(
                            "window.__latticeGeo && window.__latticeGeo.frame")
                        recording = fetch_recording(base, RECORDING)
                        return await body(page, recording, errors)
                    finally:
                        await browser.close()

            return asyncio.run(run())
        finally:
            httpd.shutdown()
            httpd.server_close()

    def test_the_pixel_classifier_does_not_read_sentry_red_as_a_loot_colour(self):
        """Calibration, run through the same census the assertions use.

        The Sentry's perception perimeter is Sentry red and is stroked across
        the map, in observed rooms, right where loot is. A classifier that let
        red count as amber would make a mis-painted diamond look partly right,
        and one that let it count as green would satisfy the whole-canvas floor
        with no chest painted at all. Both verdicts are asserted on the solid
        colour *and* on the 0.55-alpha composite the ring actually lays down.
        """

        async def body(page, recording, errors):
            frame_fill = [26, 31, 44]  # COLORS.roomFill, an observed room card
            for name, pixel in (
                    ("sentry red", list(SENTRY_RGB) + [255]),
                    ("sentry ring over a card", over(frame_fill, SENTRY_RGB, 0.55))):
                for other_name, other in (("amber", rgb(UNCLAIMED_RGB)),
                                          ("green", rgb(CLAIMED_RGB))):
                    with self.subTest(pixel=name, as_=other_name):
                        verdict = await page.evaluate(PIXEL_CENSUS, {
                            "mode": "colour", "pixel": pixel, "fill": frame_fill,
                            "expected": rgb(CLAIMED_RGB), "other": rgb(UNCLAIMED_RGB),
                            "tol": SEGMENT_TOLERANCE, "epsilon": INK_EPSILON,
                            "minAlpha": MIN_ALPHA,
                        })
                        self.assertEqual(verdict["expected"] + verdict["other"], 0,
                                         f"{name} counted as a loot colour "
                                         f"({verdict}); it is neither")
            # And the census is not simply refusing everything: the two real
            # loot colours must still classify as themselves against this fill,
            # or the calibration above would prove nothing.
            for own, as_name in ((rgb(CLAIMED_RGB), "green"), (rgb(UNCLAIMED_RGB), "amber")):
                verdict = await page.evaluate(PIXEL_CENSUS, {
                    "mode": "colour", "pixel": own + [255], "fill": frame_fill,
                    "expected": own, "other": rgb(CLAIMED_RGB if as_name == "amber" else UNCLAIMED_RGB),
                    "tol": SEGMENT_TOLERANCE, "epsilon": INK_EPSILON,
                    "minAlpha": MIN_ALPHA,
                })
                with self.subTest(pixel=as_name):
                    self.assertEqual(verdict["expected"], 1,
                                     f"{as_name} did not classify as itself over the "
                                     f"room fill ({verdict})")
            return errors

        self._no_console_errors(self._drive(body))

    def test_painted_diamonds_match_the_selected_views_claim_state(self):
        """The frame, every perspective, read out of the canvas itself."""

        async def body(page, recording, errors):
            self.assertTrue(has_recorded_perception(recording),
                            f"{RECORDING} records no perception, so it cannot be "
                            "the schema-4 ego view this test is about")
            steps = recorded_steps(recording)
            frames = frames_with_claims(recording)
            resources = recording[0]["Map"]["Resources"]
            total = len(resources)
            ids = agent_ids(recording)
            self.assertTrue(ids, "the recording has no agents")

            frame = chosen_frame(recording, ids)
            self.assertIsNotNone(
                frame,
                "no frame in the recording shows this observer a claimed chest "
                "that the world does not, so there is nothing here to prove")
            world = {str(r) for r in frames[frame]["claims"]}
            self.assertTrue(world, f"frame {frame} has no claims in the world")

            # What each view must show, read from the recording. Ground truth
            # masks nothing, so it draws every chest; an ego view draws what
            # that observer's own filter reported.
            expected = {"ground": (world, {str(r["Id"]) for r in resources})}
            for ego in ids:
                perception = perception_at(steps, frame, ego)
                expected[str(ego)] = (
                    recorded_claims(perception),
                    {str(r["ResourceId"]) for r in (perception.get("Resources") or [])
                     if r["Status"] != 2},
                )

            for view in ["ground"] + [str(e) for e in ids]:
                wanted_claimed, wanted_visible = expected[view]
                where = "ground truth" if view == "ground" else view_name(recording, int(view))
                await show(page, frame, view)

                survey = await page.evaluate(PIXEL_CENSUS, {
                    "mode": "survey", "half": CORE_HALF_CSS})
                with self.subTest(view=where, subject="frame"):
                    self.assertEqual((survey["index"], str(survey["view"])),
                                     (frame, view),
                                     "the census read a frame other than the one "
                                     "the page was asked to paint")
                    self.assertGreater(survey["cw"], 0)

                painted = {item["id"]: (zid, item["box"])
                           for zid, items in survey["loot"].items() for item in items}
                with self.subTest(view=where, subject="which diamonds"):
                    self.assertEqual(
                        set(painted), wanted_visible,
                        f"{where} at frame {frame}: the page placed a different set "
                        f"of diamonds from the one this view is supposed to show "
                        f"(placed {sorted(painted)}, expected {sorted(wanted_visible)})")
                self.assertTrue(painted, f"{where} placed no diamond at all")

                fills = {zid: room["fill"] for zid, room in survey["rooms"].items()}

                for rid, (zid, box) in sorted(painted.items()):
                    fill = fills.get(zid)
                    self.assertIsNotNone(fill, f"{where}: no room {zid} to measure")
                    own = rgb(CLAIMED_RGB) if rid in wanted_claimed else rgb(UNCLAIMED_RGB)
                    other = rgb(UNCLAIMED_RGB) if rid in wanted_claimed else rgb(CLAIMED_RGB)
                    verdict = await page.evaluate(PIXEL_CENSUS, {
                        "mode": "census", "box": box, "fill": fill,
                        "expected": own, "other": other,
                        "tol": SEGMENT_TOLERANCE, "epsilon": INK_EPSILON,
                        "minAlpha": MIN_ALPHA,
                    })
                    area = verdict["area"]
                    floor = max(1, int(round(INK_FLOOR_SHARE * area)))
                    ceiling = int(round(OTHER_CEILING_SHARE * area))
                    label = "claimed" if rid in wanted_claimed else "unclaimed"
                    with self.subTest(view=where, chest=rid, subject="paint"):
                        self.assertGreaterEqual(
                            verdict["expected"], floor,
                            f"{where} at frame {frame}: chest {rid} is {label} in "
                            f"this view and no loot colour was painted in its core "
                            f"box {box} — {verdict} over fill {fill} at DPR "
                            f"{survey['dpr']}")
                    with self.subTest(view=where, chest=rid, subject="colour"):
                        self.assertLessEqual(
                            verdict["other"], ceiling,
                            f"{where} at frame {frame}: chest {rid} is {label} in "
                            f"this view but {verdict['other']} of its {area} core "
                            f"pixels are the other loot colour — {verdict}")

                # The whole canvas, which no diamond-local check can be
                # satisfied by accident.
                green_diamonds = wanted_claimed & set(painted)
                canvas = await page.evaluate(PIXEL_CENSUS, {
                    "mode": "canvas", "fills": [fills[z] for z in sorted(fills)],
                    "source": rgb(CLAIMED_RGB), "tol": SEGMENT_TOLERANCE,
                    "minAlpha": MIN_ALPHA,
                })
                floor = CANVAS_GREEN_PER_DIAMOND * survey["dpr"] ** 2
                with self.subTest(view=where, subject="canvas green"):
                    if green_diamonds:
                        self.assertGreaterEqual(
                            canvas["ink"], floor * len(green_diamonds),
                            f"{where} at frame {frame}: {len(green_diamonds)} chest(s) "
                            f"are claimed in this view and each reads green in its own "
                            f"core, but the whole canvas carries only {canvas['ink']} "
                            f"green pixel(s) of the {floor * len(green_diamonds):.0f} "
                            "required")
                    else:
                        self.assertEqual(
                            canvas["ink"], 0,
                            f"{where} at frame {frame}: no chest is claimed in this "
                            f"view, yet the canvas carries {canvas['ink']} green "
                            "pixel(s)")
            return errors

        self._no_console_errors(self._drive(body))

    def _no_console_errors(self, errors):
        self.assertEqual(errors, [], "the page logged errors")


if __name__ == "__main__":
    unittest.main()
