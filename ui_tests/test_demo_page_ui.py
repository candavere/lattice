"""Tracked Playwright regression for the demo replay page in site/.

Serves the committed site/ directory on a localhost port (no dependency on the
live GitHub Pages URL) and drives it with the bundled ?measure=1 geometry probe
(window.__latticeGeo: exact on-canvas bounds of every room label, agent token
and door pill just drawn, plus per-frame index/perspective).

Covers what the demo simplification stage asserts:
  * one-click perspective chips (ground aside, agent views) + keyboard roving,
  * single-map viewer with the "Reconstructed sightline" badge + moving caption,
  * no token ever overlaps a room label (every tick, both perspectives),
  * no horizontal overflow of the page or the graph canvas,
  * the vault-drift moment: page ticks 10-13 the vault is 'stale' on the
    Sentry view while ground truth still marks it 'observed',
  * door pills appear on hover (pointer cursor),
  * legend renders as a compact aligned item grid (per-item height caps,
    swatch top-aligned with its label, inline code chips never wrap, no
    clipping),
  * no console/page errors.

Run:  python3 -m unittest discover -s tests -p 'test_*.py' -v
"""

import asyncio
import json
import threading
import unittest
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
            for tick in range(0, 24):
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

        # -- vault-drift moment (page ticks 10-13) ---------------------------
        await page.locator('#perspective-chips .chip[data-id="0"]').click()
        await page.evaluate(SLIDE, 11)
        await page.wait_for_function(wait_frame(11, "0"))
        sentry_geo = await page.evaluate("window.__latticeGeo")
        await page.locator('#perspective-chips .chip[data-id="ground"]').click()
        await page.wait_for_function(wait_frame(11, "ground"))
        ground_geo = await page.evaluate("window.__latticeGeo")
        vault_id = max(ground_geo["labels"], key=lambda k: ground_geo["labels"][k]["w"])
        in_both = vault_id in sentry_geo["labels"] and vault_id in ground_geo["labels"]
        results.append(
            (f"[{label}] vault room label present both views at tick 11", in_both,
             f"vault={vault_id}")
        )

        vault_status = {}
        for tick in (11, 10, 12, 13):
            await page.locator('#perspective-chips .chip[data-id="0"]').click()
            await page.evaluate(SLIDE, tick)
            await page.wait_for_function(wait_frame(tick, "0"))
            s = (await page.evaluate("window.__latticeGeo"))["statusByZone"].get(vault_id)
            await page.locator('#perspective-chips .chip[data-id="ground"]').click()
            await page.wait_for_function(wait_frame(tick, "ground"))
            g = (await page.evaluate("window.__latticeGeo"))["statusByZone"].get(vault_id)
            vault_status[f"t{tick}"] = {"sentry": s, "ground": g}
        ok_drift = all(v["ground"] == "observed" and v["sentry"] == "stale" for v in vault_status.values())
        results.append(
            (f"[{label}] vault drift ticks 10-13 (sentry last-known, ground observed)",
             ok_drift, json.dumps(vault_status))
        )

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
        await page.locator('#perspective-chips .chip[data-id="ground"]').click()
        await page.wait_for_function(wait_frame(13, "ground"))
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