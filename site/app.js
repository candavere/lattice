/* Lattice — replay viewer.
   Deterministic trajectory (JSON Lines) player rendered to a high-DPI canvas.
   Self-contained ES2017+, zero external dependencies.

   The viewer replays recorded frames only; nothing in the browser is
   recomputed from a simulation. */

(function () {
  'use strict';

  // The guided hero is the infiltration recording (Sentry vs Infiltrator): its
  // "moment to watch" (ticks 9, 19 and 20, the Vault recorded as last-known by
  // the Infiltrator's own perception filter) is what the hero copy walks a
  // visitor through. Those tiers are read from the file, not rebuilt here. The
  // demo.jsonl (MCTS card) stays reachable from the Preset menu, and is the case
  // that has to fall back to a reconstruction and say so.
  const DEFAULT_TRAJECTORY = './infiltration.jsonl';

  // Pinned revision for the benchmark evidence links and result fetches. Those
  // JSON artifacts were genuinely recorded at this commit and are immutable
  // there, so the number a reader gets back is the number that was published.
  // It is deliberately NOT used for the trajectory files below: those are
  // re-recorded as the schema advances, and the page plays the copy on main, so
  // a "view in repository" link has to resolve to the file that is playing.
  const PINNED_SHA = '6463e4865dd4831efe0952d64de0f8bfaf22f9a4';

  // The trajectory the page is actually playing. Pages deploys from main, so
  // main is the file on screen and main is the file the link must resolve to.
  const REPO_TRAJECTORY_BLOB = 'https://github.com/candavere/lattice/blob/main/';

  const PRESETS = {
    infiltration: {
      url: './infiltration.jsonl',
      name: 'infiltration.jsonl',
      repoPath: 'site/infiltration.jsonl',
      staticSvg: './infiltration.svg',
      staticName: 'infiltration.svg',
      chipLabel: 'Infiltration',
      scenarioOrder: 0,
      caption: 'Seed 42, Dungeon Infiltration & Sentry Patrol: the Infiltrator raids the Treasure Vault under a patrolling Sentry. Recorded with lattice simulate --seed 42 --scenario infiltration --steps 100 and rendered as an animated SVG with lattice render --format svg.',
      reproduce: 'dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl',
    },
    demo: {
      url: './demo.jsonl',
      name: 'demo.jsonl',
      repoPath: 'site/demo.jsonl',
      staticSvg: './demo.svg',
      staticName: 'demo.svg',
      chipLabel: 'Benchmark',
      scenarioOrder: 1,
      caption: 'Seed 42, MCTS (agent 0) vs Random (agent 1), 27 ticks. Recorded with lattice simulate --seed 42 --agent mcts --steps 30 and rendered as a dependency-free CSS-animated SVG with lattice render --format svg.',
      reproduce: 'dotnet run --project Cli -- simulate --seed 42 --agent mcts --steps 30 --out demo.jsonl',
    },
  };

  // Scenario chip labels are data-driven from the preset table above, never
  // hard-coded in the strip builder.
  const SCENARIO_CHIPS = Object.keys(PRESETS)
    .map(function (key) { return { key: key, preset: PRESETS[key] }; })
    .sort(function (a, b) { return a.preset.scenarioOrder - b.preset.scenarioOrder; });

  // Playback speeds — the same values the old speed <select> carried.
  const SPEEDS = [
    { label: '0.5×', value: 800 },
    { label: '1×', value: 420 },
    { label: '2×', value: 200 },
    { label: '4×', value: 90 },
  ];

  const RESULT_ARTIFACTS = [
    {
      name: 'benchmarks/mcts_evaluation_results.json',
      url: 'https://raw.githubusercontent.com/candavere/lattice/' + PINNED_SHA + '/benchmarks/mcts_evaluation_results.json',
      fallbackUrl: './benchmarks/mcts_evaluation_results.json',
      blob: 'https://github.com/candavere/lattice/blob/' + PINNED_SHA + '/benchmarks/mcts_evaluation_results.json',
      suiteLabel: 'Standard generated maps',
      ids: {
        proto: 'std-proto', delta: 'std-delta', ci: 'std-ci', seeds: 'std-seeds',
        verdict: 'std-verdict', heldout: 'std-heldout', link: 'std-link', commit: 'std-commit',
      },
    },
    {
      name: 'benchmarks/bottleneck_evaluation_results.json',
      url: 'https://raw.githubusercontent.com/candavere/lattice/' + PINNED_SHA + '/benchmarks/bottleneck_evaluation_results.json',
      fallbackUrl: './benchmarks/bottleneck_evaluation_results.json',
      blob: 'https://github.com/candavere/lattice/blob/' + PINNED_SHA + '/benchmarks/bottleneck_evaluation_results.json',
      suiteLabel: 'Procedural bottleneck maps',
      ids: {
        proto: 'bot-proto', delta: 'bot-delta', ci: 'bot-ci', seeds: 'bot-seeds',
        verdict: 'bot-verdict', heldout: 'bot-heldout', link: 'bot-link', commit: 'bot-commit',
      },
    },
  ];

  const UNLIMITED = 2147483647; // MapLimits.Unlimited, as serialized by the writer

  const AGENT_PALETTE = ['#F7768E', '#BB9AF7', '#73DACA', '#FF9E64'];
  const SENTRY_COLOR = '#ff5252';
  const INFILTRATOR_COLOR = '#7c4dff';

  const COLORS = {
    corridor: '#4A5878',
    corridorHot: '#F6C177',
    roomFill: '#1a1f2c',
    roomStroke: '#7AA2F7',
    roomText: '#C0CAF5',
    mutedText: '#94A3B8',
    unclaimed: '#E0AF68',
    claimed: '#3DA66B',
    agentRim: '#FFFFFF',
    transitRing: '#F6C177',
    gateBg: '#16233F',
    gateText: '#E2E8F0',
    sentry: SENTRY_COLOR,
    infiltrator: INFILTRATOR_COLOR,
    perception: 'rgba(255, 82, 82, 0.55)',
    extraction: '#40C4FF',
    fogUnknownFill: '#0b0f1a',
    fogUnknownStroke: '#232c40',
    fogStaleFill: 'rgba(26, 31, 44, 0.45)',
    fogStaleStroke: '#44506e',
    fogText: '#5b6a8a',
    accentBar: '#3b82f6',
  };

  /* ---------------------------------------------------------------- DOM  */

  const dom = {};
  document.addEventListener('DOMContentLoaded', function () {
    dom.canvas = document.getElementById('viewer-canvas');
    dom.hint = document.getElementById('viewer-hint');
    dom.slider = document.getElementById('scrub-slider');
    dom.tickReadout = document.getElementById('tick-readout');
    dom.source = document.getElementById('source-label');
    // The no-canvas fallback link inside <canvas> stays in the tab order
    // even when the canvas renders, as an invisible stop. Remove it.
    if (dom.canvas && dom.canvas.getContext && dom.canvas.getContext('2d')) {
      const fallback = dom.canvas.querySelector('a');
      if (fallback) fallback.setAttribute('tabindex', '-1');
    }
    dom.roster = document.getElementById('roster-label');
    dom.terminal = document.getElementById('terminal-label');
    dom.agentsBody = document.getElementById('agents-body');
    dom.zonesBody = document.getElementById('zones-body');
    dom.statClaimed = document.getElementById('stat-claimed');
    dom.statSteps = document.getElementById('stat-steps');
    dom.statSeed = document.getElementById('stat-seed');
    dom.statAgents = document.getElementById('stat-agents');
    dom.message = document.getElementById('message');
    dom.playBtn = document.getElementById('play-btn');
    dom.stepBackBtn = document.getElementById('step-back-btn');
    dom.stepFwdBtn = document.getElementById('step-fwd-btn');
    dom.fileInput = document.getElementById('file-input');
    dom.staticSvg = document.getElementById('static-svg');
    dom.staticTitle = document.getElementById('static-title');
    dom.staticOpen = document.getElementById('static-open');
    dom.staticOpenLink = document.getElementById('static-open-link');
    dom.staticCaption = document.getElementById('static-caption');
    dom.scenarioChips = document.getElementById('scenario-chips');
    dom.perspectiveChips = document.getElementById('perspective-chips');
    dom.speedChips = document.getElementById('speed-chips');
    dom.mapCaption = document.getElementById('map-caption');
    dom.fogBadge = document.getElementById('fog-badge');
    dom.doorLayer = document.getElementById('door-layer');
    dom.sentence = document.getElementById('tick-sentence');
    dom.legendRoles = document.getElementById('legend-roles');
    dom.provGrid = document.getElementById('prov-grid');
    dom.provOpen = document.getElementById('prov-open');
    dom.provRepro = document.getElementById('prov-repro');

    // Self-test geometry probe: with ?measure=1 the page records the exact
    // bounds of every drawn room label, agent token and door pill each frame
    // so the browser harness can assert "no token overlaps a room label" on
    // the real rendering. Draws nothing; off by default.
    measureProbe.on = /[?&]measure=1/.test(window.location.search);

    buildScenarioChips();
    buildSpeedChips();
    buildPerspectiveChips();

    dom.playBtn.addEventListener('click', togglePlay);
    dom.stepBackBtn.addEventListener('click', function () { pause(); stepBy(-1); });
    dom.stepFwdBtn.addEventListener('click', function () { pause(); stepBy(+1); });
    dom.slider.addEventListener('input', function () { pause(); setIndex(Number(dom.slider.value)); });
    dom.fileInput.addEventListener('change', handleFileChoice);
    document.addEventListener('dragover', preventDefaultFileDrop);
    document.addEventListener('drop', handleDrop);
    dom.canvas.addEventListener('pointermove', onCanvasPointerMove);
    dom.canvas.addEventListener('pointerdown', function (e) { onCanvasPointerMove(e); });
    dom.canvas.addEventListener('pointerleave', clearHoverDoor);
    dom.canvas.addEventListener('focus', onCanvasFocus);
    dom.canvas.addEventListener('blur', clearHoverDoor);
    window.addEventListener('resize', onViewportResize);
    if (typeof reducedMotionQuery.addEventListener === 'function') {
      reducedMotionQuery.addEventListener('change', function () {
        if (reducedMotionQuery.matches && state.playing) pause();
        updateTransportDisabled();
        scheduleDraw();
      });
    }

    loadResults();
    loadDefault();
  });

  /* ------------------------------------------------------- viewer state  */

  const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');

  const state = {
    trajectory: null,   // { header, final, frames[] }
    index: 0,
    playing: false,
    timer: null,
    cadenceMs: 420,
    layouts: {},        // per-viewport-size layout cache: "WxH" -> layout
    canv: null,         // { cssW, cssH } of last fitted size
    needsDraw: false,
    perspective: 'ground', // 'ground' | <agentId number> — one map, one perspective
    egoId: 0,           // observed-agent slot used by the agent perspective
    hoverDoorId: null,  // chokepoint whose capacity pill is hovered/focused
    pulseStart: 0,      // performance.now() origin of the ego radar pulse
    pulseTimer: null,   // interval driving the radar pulse while fog is shown
  };

  // Geometry probe backing the `?measure=1` browser harness (see DOM wiring).
  // Everything here is observation only: the records are written from the same
  // values the draw calls already computed, so a probe run can never change
  // what is painted. `rooms`/`names`/`captions`/`pills`/`ghosts` are the
  // rectangles the layout invariants are asserted against — room boxes, agent
  // name ink boxes, transit caption ink boxes, room count pills and the
  // "last seen" ghost labels.
  const measureProbe = {
    on: false,
    labels: {},         // zoneId -> {x,y,w,h}
    tokens: {},         // agentId -> {x,y,r}
    doors: {},          // chokeId -> {x,y,w,h,edge}
    statusByZone: {},   // zoneId -> 'observed' | 'stale' | 'unknown' (per this draw)
    rooms: {},          // zoneId -> {x,y,w,h,cx,cy,label,status}
    names: {},          // agentId -> {x,y,w,h,text,zone} (zone null while in transit)
    ghosts: {},         // agentId -> {x,y,w,h,text,zone}
    captions: {},       // agentId -> {x,y,w,h,text}
    pills: {},          // zoneId -> {x,y,w,h,text,where}
    loot: {},           // zoneId -> {x,y,w,h} the unclaimed/claimed loot row
    lootItems: {},      // zoneId -> [{id,x,y,claimed,color}] each painted diamond
    transit: {},        // agentId -> true (drawn on a corridor, not in a room)
  };

  // Ink box of text drawn at (x, y) under the context's current font, align
  // and baseline. The box is one font-size tall and centred on a `middle`
  // baseline, which is deliberately generous for a 8-11px monospace face: the
  // harness then fails on any near-miss, not only on a clean ink overlap.
  // Test hook only — the production draw path never calls it.
  function probeInk(ctx, text, x, y, dict, key, extra) {
    if (!measureProbe.on || !dict || key === undefined) return;
    const w = ctx.measureText(String(text)).width;
    const size = parseFloat(/([0-9.]+)px/.exec(ctx.font || '11px')[1]) || 11;
    const x0 = ctx.textAlign === 'right' ? x - w
      : ctx.textAlign === 'center' ? x - w / 2
      : x;
    const y0 = ctx.textBaseline === 'middle' ? y - size / 2
      : ctx.textBaseline === 'top' ? y
      : y - size;
    const box = { x: x0, y: y0, w: w, h: size, text: String(text) };
    if (extra) Object.keys(extra).forEach(function (k) { box[k] = extra[k]; });
    dict[key] = box;
  }

  // The painted vertical extent of a run of text drawn on a `middle` baseline:
  // the glyphs' own ascent and descent, measured from where the alphabetic
  // baseline sits under that anchor, and never less than the 1.3x-font-size
  // line box. A box one font-size tall clips the ascenders and descenders off
  // a small face, which is exactly what a census of the painted label must not
  // do — at a phone's font scale it left the glyph cores out of the window.
  // Test hook only — the production draw path never calls it.
  function probeTextLines(ctx, text, anchorY, size) {
    const m = ctx.measureText(String(text));
    const exact = typeof m.actualBoundingBoxAscent === 'number'
      && typeof m.actualBoundingBoxDescent === 'number'
      && typeof m.fontBoundingBoxAscent === 'number'
      && typeof m.fontBoundingBoxDescent === 'number';
    // Where the alphabetic baseline falls under a `middle` anchor, and how far
    // the glyphs reach either side of it.
    const shift = exact ? (m.fontBoundingBoxAscent - m.fontBoundingBoxDescent) / 2 : size * 0.3;
    const up = exact ? m.actualBoundingBoxAscent : size * 0.8;
    const down = exact ? m.actualBoundingBoxDescent : size * 0.2;
    const inkTop = anchorY + shift - up;
    const inkBottom = anchorY + shift + down;
    // Widen to the fallback line box about the same centre, so the box holds
    // every glyph pixel whatever the face reports.
    const centre = (inkTop + inkBottom) / 2;
    const half = Math.max((inkBottom - inkTop) / 2, Math.ceil(size * 1.3) / 2);
    return { top: centre - half, bottom: centre + half };
  }

  /* ------------------------------------------------------- trajectory IO  */

  function loadDefault() {
    fetch(DEFAULT_TRAJECTORY)
      .then(function (res) {
        if (!res.ok) throw new Error('HTTP ' + res.status + ' while fetching ' + DEFAULT_TRAJECTORY);
        return res.text();
      })
      .then(function (text) {
        const traj = parseTrajectory(text);
        adoptTrajectory(traj, PRESETS.infiltration.name, 'infiltration');
        showMessage('loaded preset recording ' + PRESETS.infiltration.name + ' (seed ' + traj.header.Seed + ')', false);
      })
      .catch(function (err) {
        dropTrajectory();
        dom.source.textContent = 'infiltration unavailable — drop a .jsonl recording to play';
        showMessage('could not load ' + DEFAULT_TRAJECTORY + ' (' + err.message + ')', true);
      });
  }

  function preventDefaultFileDrop(event) {
    event.preventDefault();
  }

  function handleDrop(event) {
    event.preventDefault();
    const file = event.dataTransfer && event.dataTransfer.files && event.dataTransfer.files[0];
    if (file) readTrajectoryFile(file);
  }

  function handleFileChoice(event) {
    const file = event.target.files && event.target.files[0];
    if (file) readTrajectoryFile(file);
    event.target.value = '';
  }

  function loadPreset(key) {
    const preset = PRESETS[key];
    if (!preset) return;
    fetch(preset.url)
      .then(function (res) {
        if (!res.ok) throw new Error('HTTP ' + res.status + ' while fetching ' + preset.url);
        return res.text();
      })
      .then(function (text) {
        const traj = parseTrajectory(text);
        adoptTrajectory(traj, preset.name, key);
        showMessage('loaded preset recording ' + preset.name + ' (seed ' + traj.header.Seed + ')', false);
      })
      .catch(function (err) {
        showMessage('could not load preset ' + preset.name + ' (' + err.message + ')', true);
      });
  }

  function readTrajectoryFile(file) {
    const reader = new FileReader();
    reader.onload = function () {
      try {
        const traj = parseTrajectory(String(reader.result));
        adoptTrajectory(traj, file.name, 'custom');
        showMessage('loaded ' + file.name + ' (seed ' + traj.header.Seed + ')', false);
      } catch (err) {
        showMessage('could not parse ' + file.name + ': ' + err.message, true);
      }
    };
    reader.onerror = function () {
      showMessage('could not read ' + file.name, true);
    };
    reader.readAsText(file, 'utf-8');
  }

  function adoptTrajectory(traj, fileName, presetKey) {
    state.trajectory = traj;
    state.index = 0;
    state.playing = false;
    state.layouts = {};
    stopTimer();
    populatePerspectiveChips(traj);
    dom.playBtn.textContent = 'Play';
    dom.slider.max = String(Math.max(0, traj.frames.length - 1));
    dom.slider.value = '0';
    dom.source.textContent = fileName;
    dom.fileInput.title = fileName;
    if (presetKey && PRESETS[presetKey]) {
      setScenarioChip(presetKey);
      setStaticPreset(presetKey);
    }
    const roster = traj.header.AgentRoles;
    dom.roster.textContent = traj.header.Scenario
      ? traj.header.Scenario + ' · ' + (roster && roster.length ? roster.join(' vs ') : '')
      : (roster && roster.length ? roster.join(' vs ') : '');
    hideDom(dom.hint);
    renderProvenance(traj, fileName, presetKey);
    renderLegendRoles(traj);
    if (state.perspective !== 'ground') startPulse(); else stopPulse();
    scheduleDraw();
  }

  // The perspective strip: a mandatory "Ground truth" chip plus one chip per
  // recorded agent, labelled by role. Default stays the current logic — the
  // Sentry when one is recorded (its reconstructed 2-hop sightline is the
  // hero's moment to watch); the choice is never locked.
  function buildPerspectiveChips() {
    // Startup state (no recording yet): only the "Ground truth" chip exists.
    rebuildChips(dom.perspectiveChips, [{ value: 'ground', label: 'Ground truth' }], 'ground', function (value) {
      if (!state.trajectory) return;
      setPerspective(value);
      scheduleDraw();
    });
  }

  function populatePerspectiveChips(traj) {
    const roles = traj.header.AgentRoles || [];
    const agents = traj.frames.length ? traj.frames[0].agents : [];
    const options = [{ value: 'ground', label: 'Ground truth' }];
    agents.slice().sort(function (a, b) { return a.AgentId - b.AgentId; }).forEach(function (agent) {
      const role = roles[agent.AgentId];
      options.push({
        value: agent.AgentId,
        label: role || 'Agent ' + agent.AgentId,
      });
    });
    const sentryIndex = roles.indexOf('Sentry');
    const infiltratorIndex = roles.indexOf('Infiltrator');
    const defaultPerspective = sentryIndex >= 0
      ? sentryIndex
      : (infiltratorIndex >= 0 ? infiltratorIndex : (agents.length ? agents[0].AgentId : 'ground'));
    setPerspective(defaultPerspective, { silent: true });
    rebuildChips(dom.perspectiveChips, options, defaultPerspective, function (value) {
      setPerspective(value);
      scheduleDraw();
    });
  }

  function setStaticPreset(key) {
    const preset = PRESETS[key];
    if (!preset) return;
    if (dom.staticSvg.getAttribute('data') !== preset.staticSvg) {
      dom.staticSvg.data = preset.staticSvg;
    }
    dom.staticCaption.innerHTML = preset.caption;
    dom.staticTitle.textContent = 'Static render of ' + preset.name;
    dom.staticOpenLink.textContent = 'Open ' + preset.staticName;
    dom.staticOpenLink.href = preset.staticSvg;
    dom.staticOpen.textContent = 'Open ' + preset.staticName + ' directly';
    dom.staticOpen.href = preset.staticSvg;
  }

  /* ------------------------------------------------------------- chips  */

  // Build a single-select chip group (roving tabindex, arrow keys, aria).
  // Radiogroup semantics with expensive-explicit state: the selected chip is
  // aria-pressed=true AND aria-checked=true; arrows move selection+focus.
  function buildChipGroup(container, options, selectedValue, onSelect, dataAttr) {
    container.innerHTML = '';
    options.forEach(function (opt, idx) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'chip' + (opt.value === selectedValue ? ' active' : '');
      chip.textContent = String(opt.label);
      chip.setAttribute('role', 'radio');
      chip.setAttribute('aria-checked', opt.value === selectedValue ? 'true' : 'false');
      chip.setAttribute('aria-pressed', opt.value === selectedValue ? 'true' : 'false');
      if (dataAttr) chip.setAttribute('data-' + dataAttr, String(opt.value));
      chip.tabIndex = opt.value === selectedValue ? 0 : -1;
      if (opt.title) chip.title = opt.title;
      container.appendChild(chip);
    });
    restoreChipAccess(container);
  }

  // The chips in a group get roving keyboard navigation: Left/Up and
  // Right/Down step selection, Home/End jump to the ends. Selecting a chip
  // fires its onChange, exactly like picking the old <select> option would.
  function restoreChipAccess(container) {
    const chips = Array.prototype.slice.call(container.querySelectorAll('.chip'));
    chips.forEach(function (chip, idx) {
      chip.addEventListener('click', function () {
        selectChip(container, chip);
      });
      chip.addEventListener('keydown', function (event) {
        let next = null;
        if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = chips[idx - 1];
        else if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = chips[idx + 1];
        else if (event.key === 'Home') next = chips[0];
        else if (event.key === 'End') next = chips[chips.length - 1];
        if (!next) return;
        event.preventDefault();
        selectChip(container, next, true);
      });
    });
  }

  function selectChip(container, chip, focus) {
    const prev = container.querySelector('.chip.active');
    if (prev && prev !== chip) {
      prev.classList.remove('active');
      prev.setAttribute('aria-checked', 'false');
      prev.setAttribute('aria-pressed', 'false');
      prev.tabIndex = -1;
    }
    chip.classList.add('active');
    chip.setAttribute('aria-checked', 'true');
    chip.setAttribute('aria-pressed', 'true');
    chip.tabIndex = 0;
    if (focus) chip.focus();
    if (container._onSelect) container._onSelect(chip.dataset.id);
  }

  function rebuildChips(container, options, selectedValue, onSelect) {
    container._onSelect = onSelect;
    buildChipGroup(container, options, selectedValue, null, 'id');
  }

  function buildScenarioChips() {
    const options = SCENARIO_CHIPS.map(function (entry) {
      return { value: entry.key, label: entry.preset.chipLabel };
    });
    rebuildChips(dom.scenarioChips, options, 'infiltration', function (key) {
      if (state.perspective || state.trajectory) loadPreset(key);
    });
  }

  function setScenarioChip(key) {
    selectChipSync(dom.scenarioChips, key);
  }

  function buildSpeedChips() {
    const options = SPEEDS.map(function (s) {
      return { value: String(s.value), label: s.label };
    });
    rebuildChips(dom.speedChips, options, String(state.cadenceMs), function (value) {
      state.cadenceMs = parseInt(value, 10) || state.cadenceMs;
      if (state.playing) startTimer(); // keep autoplay cadence current
    });
  }

  // select a chip by data-id value without firing its onSelect (used when the
  // program, not the visitor, changes state — preset load, cadence default).
  function selectChipSync(container, value) {
    const chips = Array.prototype.slice.call(container.querySelectorAll('.chip'));
    const target = chips.filter(function (c) { return c.dataset.id === String(value); })[0];
    if (target) {
      const prev = container.querySelector('.chip.active');
      if (prev && prev !== target) prev.classList.remove('active');
      if (!target.classList.contains('active')) {
        target.classList.add('active');
        target.setAttribute('aria-checked', 'true');
        target.setAttribute('aria-pressed', 'true');
      } else {
        target.setAttribute('aria-checked', 'true');
        target.setAttribute('aria-pressed', 'true');
      }
      if (prev && prev !== target) {
        prev.setAttribute('aria-checked', 'false');
        prev.setAttribute('aria-pressed', 'false');
        prev.tabIndex = -1;
      }
      target.tabIndex = 0;
    }
  }

  /* ----------------------------------------------------------- perspective  */

  function onViewportResize() {
    scheduleDraw();
  }

  // The single map's perspective: is the current chip a recorded agent?
  function perspectiveIsAgent() {
    return typeof state.perspective === 'number';
  }

  function setPerspective(value, opts) {
    opts = opts || {};
    const wasAgent = perspectiveIsAgent();
    // Chips hand us dataset ids (strings); adopt the same shape as the old
    // numeric ego id and keep 'ground' as the only sentinel.
    let v = value;
    if (v === 'ground') v = 'ground';
    else if (typeof v === 'string' && /^-?\d+$/.test(v)) v = parseInt(v, 10);
    if (v === 'ground' || (typeof v === 'number' && isFinite(v))) {
      state.perspective = v;
    }
    if (perspectiveIsAgent()) state.egoId = state.perspective;
    if (!opts.silent) {
      refreshChipActive(dom.perspectiveChips, String(state.perspective));
      if (perspectiveIsAgent() !== wasAgent) {
        if (perspectiveIsAgent()) startPulse(); else stopPulse();
      }
      scheduleDraw();
      if (perspectiveIsAgent() && !opts.noFade) fadeCanvas();
      // The caption, the fog chip and the canvas label are all written by
      // draw(), from the fog it actually drew: they cannot be set from here,
      // or they would describe a frame that has not been painted yet.
    }
  }

  function refreshChipActive(container, value) {
    const chips = Array.prototype.slice.call(container.querySelectorAll('.chip'));
    chips.forEach(function (chip) {
      const on = chip.dataset.id === String(value);
      chip.classList.toggle('active', on);
      chip.setAttribute('aria-checked', on ? 'true' : 'false');
      chip.setAttribute('aria-pressed', on ? 'true' : 'false');
      chip.tabIndex = on ? 0 : -1;
    });
  }

  // What the fog chip says, and the one caption line under the map. Both are
  // derived from the fog object itself, so the badge can never claim a source
  // the frame is not drawn from.
  function fogBadgeText(fog) {
    if (!fog) return null;
    return fog.source === 'recorded' ? 'Recorded perception' : 'Reconstructed sightline';
  }

  function updateFogBadge(fog) {
    if (!dom.fogBadge) return;
    const text = fogBadgeText(fog);
    if (text) {
      if (dom.fogBadge.textContent !== text) dom.fogBadge.textContent = text;
      dom.fogBadge.removeAttribute('hidden');
    } else {
      dom.fogBadge.setAttribute('hidden', '');
    }
  }

  function updateMapCaption(fog) {
    if (!dom.mapCaption) return;
    const traj = state.trajectory;
    let text;
    if (!traj || !perspectiveIsAgent()) {
      text = 'Ground truth: everything in the world.';
    } else if (!fog) {
      text = 'No recorded perception for this frame: everything in the world.';
    } else {
      const role = traj.header.AgentRoles && traj.header.AgentRoles[state.egoId];
      const name = role || 'Agent ' + state.egoId;
      if (fog.source === 'recorded') {
        text = fog.fresh
          ? 'What the ' + name + ' perceived when it chose (tick ' + fog.tick +
            '), read from the recording.'
          : 'Last decision-time view (tick ' + fog.tick + '): the episode ended here, so no ' +
            name + ' ever decided from this frame — the fog shown is the last one it acted on.';
      } else {
        text = 'What the ' + name + ' could reach (reconstructed ' + fog.vision +
          '-hop sightline) — derived by this page, not recorded.';
      }
    }
    if (dom.mapCaption.textContent !== text) dom.mapCaption.textContent = text;
  }

  // Short cross-fade when the visitor switches perspective (<=250ms; the
  // global reduced-motion rule collapses it to 0.01ms).
  function fadeCanvas() {
    if (reducedMotionQuery.matches) return;
    if (!dom.canvas || dom.canvas._fading) return;
    dom.canvas._fading = true;
    dom.canvas.classList.add('perspective-fade');
    window.setTimeout(function () {
      dom.canvas.classList.remove('perspective-fade');
      dom.canvas._fading = false;
    }, 240);
  }

  function updateCanvasLabel(fog) {
    const traj = state.trajectory;
    let label;
    if (!perspectiveIsAgent()) {
      label = 'Replay view — Ground truth, tick ' + state.index +
        ' of ' + (traj ? traj.frames.length - 1 : 0);
    } else if (!fog) {
      label = 'Replay view — no recorded perception for tick ' + state.index +
        ' of ' + (traj ? traj.frames.length - 1 : 0);
    } else {
      const source = fog.source === 'recorded'
        ? "recorded perception at tick " + fog.tick + (fog.fresh ? '' : ' (last decision-time view)')
        : "reconstructed " + fog.vision + "-hop sightline";
      label = 'Replay view — ' + egoLabel(traj) + "'s " + source +
        ', tick ' + state.index + ' of ' + (traj ? traj.frames.length - 1 : 0);
    }
    if (dom.canvas && dom.canvas.getAttribute('aria-label') !== label) {
      dom.canvas.setAttribute('aria-label', label);
    }
  }

  function dropTrajectory() {
    stopTimer();
    stopPulse();
    state.trajectory = null;
    state.playing = false;
    state.layouts = {};
    dom.agentsBody.innerHTML = '';
    dom.zonesBody.innerHTML = '';
    dom.terminal.textContent = '';
    dom.tickReadout.textContent = '—';
    dom.statClaimed.textContent = '—';
    dom.statSteps.textContent = '—';
    dom.statSeed.textContent = '—';
    dom.statAgents.textContent = '—';
    resetPerspectiveChips();
    dom.legendRoles.innerHTML = '';
    dom.provGrid.innerHTML = '';
    dom.provOpen.innerHTML = '';
    dom.provRepro.innerHTML = '';
    dom.sentence.textContent = 'Load a recording to see its frames described here.';
    if (dom.fogBadge) {
      dom.fogBadge.setAttribute('hidden', '');
      dom.fogBadge.textContent = '';
    }
    updateTransportDisabled();
    clearCanvas();
    showDom(dom.hint);
  }

  // With no trajectory loaded the only honest perspective is "Ground truth".
  function resetPerspectiveChips() {
    state.perspective = 'ground';
    refreshChipActive(dom.perspectiveChips, 'ground');
    updateMapCaption();
    updateCanvasLabel();
  }

  /* ------------------------------------------------- geometry + drawing  */

  function fitCanvas() {
    const dpr = window.devicePixelRatio || 1;
    const cssW = dom.canvas.clientWidth;
    const cssH = dom.canvas.clientHeight;
    if (!cssW || !cssH) return false;
    if (state.canv && state.canv.cssW === cssW && state.canv.cssH === cssH && dom.canvas.width === Math.round(cssW * dpr)) {
      return true;
    }
    dom.canvas.width = Math.round(cssW * dpr);
    dom.canvas.height = Math.round(cssH * dpr);
    const ctx = dom.canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    state.canv = { cssW: cssW, cssH: cssH, context: ctx };
    return true;
  }

  function ensureLayout(traj, regionW, regionH) {
    const map = traj.header.Map;
    const zones = map.Zones;
    const resources = map.Resources;

    const hasPosition = function (p) {
      return p && typeof p.X === 'number' && typeof p.Y === 'number';
    };
    const isZero = function (p) {
      return !hasPosition(p) || (p.X === 0 && p.Y === 0);
    };
    const allZero = zones.length > 0 && zones.every(function (z) { return isZero(z.Position); });

    // Missing or clustered coordinates collapse the map onto a point; fall
    // back to a structured multi-row tactical grid so corridors connect
    // cleanly without diagonal crisscrossing.
    let clustered = allZero;
    const seen = {};
    zones.forEach(function (z) {
      if (!hasPosition(z.Position)) { clustered = true; return; }
      const key = z.Position.X + ':' + z.Position.Y;
      if (seen[key]) clustered = true;
      seen[key] = true;
    });

    if (clustered) {
      const cols = Math.max(1, Math.ceil(Math.sqrt(zones.length)));
      zones.forEach(function (zone, idx) {
        zone.Position = { X: (idx % cols) * 4, Y: Math.floor(idx / cols) * 4 };
      });
      resources.forEach(function (res) {
        const home = zones[res.ZoneId % zones.length].Position;
        res.Position = { X: home.X, Y: home.Y };
      });
    }

    // Only the zones set the drawn extent: loot is painted inside its room
    // (see drawResources), so a resource's recorded position must not be
    // allowed to shrink the map around it.
    const points = [];
    zones.forEach(function (z) { points.push(z.Position); });
    if (!points.length) return null;

    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    points.forEach(function (p) {
      if (p.X < minX) minX = p.X;
      if (p.X > maxX) maxX = p.X;
      if (p.Y < minY) minY = p.Y;
      if (p.Y > maxY) maxY = p.Y;
    });

    const w = regionW, h = regionH;
    const cached = state.layouts[w + 'x' + h];
    if (cached && cached.w === w && cached.h === h && cached.minX === minX && cached.maxX === maxX &&
        cached.minY === minY && cached.maxY === maxY) {
      return cached;
    }

    const spanX = Math.max(1, maxX - minX);
    const spanY = Math.max(1, maxY - minY);
    const text = roomTextWidths(traj, zones);
    const room = roomForCanvas(w, h, zones, spanX, spanY, text,
      maxNameLines(traj, roomInnerWidth(text)), text.marker);
    const s = room.s;
    const offX = (w - s * spanX) / 2;
    const offY = (h - s * spanY) / 2;

    const layout = {
      minX: minX, minY: minY, spanX: spanX, spanY: spanY, s: s, offX: offX, offY: offY,
      w: w, h: h,
      roomW: room.w, roomH: room.h, titlePx: room.titlePx, namePx: room.namePx,
      pillPx: room.pillPx, pad: ROOM_PAD_X * room.k, nameLines: room.lines,
      k: room.k, marker: room.marker,
      rAgent: room.rAgent,
    };
    layout.bands = roomBands(layout);
    state.layouts[w + 'x' + h] = layout;
    return layout;
  }

  /* ------------------------------------------------------- room layout  */

  /* Rooms are the viewer's boxes, and every room on a map shares one width,
     one height and one font scale, so a room can never be narrower than the
     text it has to hold. The scale itself is the one number that keeps the
     map honest: rooms are rectangles, so the drawn extent is the zone grid
     plus one room box at each end, and the scale is the largest that still
     fits the canvas *and* still keeps ROOM_GAP between neighbouring boxes.
     Both bounds are derived from the recording — there are no per-map
     numbers — and both are monotone in the scale, so a bisection lands on the
     answer. This runs once per canvas size, never per frame. */

  // The most lines any room's name band ever needs, over the whole recording.
  // Names pack left to right along a line and wrap only when the next one will
  // not fit, so a room with two short names needs one line and a room with two
  // long ones needs two. The bound is every agent a frame can put in one room:
  // those standing there, plus — in a fogged view — every rival remembered
  // there instead. Once per recording, from the labels the page will draw.
  function maxNameLines(traj, inner) {
    if (typeof traj._maxNameLines === 'number') return traj._maxNameLines;
    const roles = traj.header.AgentRoles || [];
    const label = function (id) {
      return roles[id] ? roles[id] + ' · 00' : '· 00';
    };
    const widths = [];
    for (var i = 0; i < roles.length; i += 1) {
      widths.push(label(i).length * ROOM_CHAR * ROOM_NAME_PX + NAME_GAP);
    }
    const packed = function (sizes) {
      let lines = 1;
      let used = 0;
      sizes.forEach(function (w) {
        if (used > 0 && used + w > inner) { lines += 1; used = w; } else { used += w; }
      });
      return lines;
    };
    let most = 1;
    traj.frames.forEach(function (frame) {
      const inRoom = {};
      let present = 0;
      frame.agents.forEach(function (a) {
        if (a.Transit) return; // a corridor token is not in a room
        present += 1;
        (inRoom[a.ZoneId] = inRoom[a.ZoneId] || []).push(widths[a.AgentId] || 0);
      });
      // A room can hold every agent on the frame: those standing in it plus,
      // in a fogged view, every rival remembered there instead. That is the
      // bound; the per-room live sets are checked too, since how a greedy pack
      // breaks depends on the order it sees.
      const everyone = widths.slice(0, present);
      most = Math.max(most, packed(everyone));
      Object.keys(inRoom).forEach(function (z) {
        most = Math.max(most, packed(inRoom[z]));
      });
    });
    traj._maxNameLines = most;
    return most;
  }

  // The two text widths a room has to hold, in px at the design font sizes:
  // its own title, and the widest agent name the roster can produce — the same
  // string drawAgents paints, with room for a two-digit score. Both come from
  // the recording, so a map with longer names gets wider rooms rather than
  // clipped labels. `marker` is the strip the extraction diamond takes above a
  // token, which a room only has to carry when the roster has a role that
  // draws one.
  function roomTextWidths(traj, zones) {
    let titleChars = 0;
    zones.forEach(function (z) {
      titleChars = Math.max(titleChars, roomLabel(z).length);
    });
    const roles = traj.header.AgentRoles || [];
    let nameChars = 0;
    let marker = 0;
    for (var i = 0; i < roles.length; i += 1) {
      nameChars = Math.max(nameChars, (roles[i] ? roles[i] + ' · 00' : '· 00').length);
      if (roles[i] === 'Infiltrator') marker = EXTRACTION_MARKER;
    }
    return { titleChars: titleChars, nameChars: nameChars, marker: marker };
  }

  // One room box at font scale k: the wider of the title and the name, plus
  // inner padding, and just tall enough for the title band, the name lines, the
  // strip the extraction marker takes above a token, and the token row. The
  // whole card scales with k, so a room is never narrower than the text it
  // holds at the font size it is holding it at.
  function roomBoxAt(k, text, lines, radius, marker) {
    const titlePx = ROOM_TITLE_PX * k;
    const namePx = ROOM_NAME_PX * k;
    const w = k * Math.max(ROOM_MIN_WIDTH,
      text.titleChars * ROOM_CHAR * ROOM_TITLE_PX + 2 * ROOM_PAD_X,
      text.nameChars * ROOM_CHAR * ROOM_NAME_PX + 2 * ROOM_PAD_X);
    const h = Math.max(ROOM_HEIGHT, ROOM_BAND_FOOT + titlePx / 2 + 2 * radius + marker
      + lines * namePx + (lines - 1) * NAME_LINE_GAP);
    return { w: w, h: h, titlePx: titlePx, namePx: namePx, pillPx: ROOM_PILL_PX * k };
  }

  function agentRadius(s) {
    return Math.max(AGENT_MIN_R, Math.min(AGENT_MAX_R, 9 * s / 60));
  }

  // The room's inner width at full font size — what the name band packs into.
  function roomInnerWidth(text) {
    return Math.max(ROOM_MIN_WIDTH,
      text.titleChars * ROOM_CHAR * ROOM_TITLE_PX + 2 * ROOM_PAD_X,
      text.nameChars * ROOM_CHAR * ROOM_NAME_PX + 2 * ROOM_PAD_X) - 2 * ROOM_PAD_X;
  }

  // Largest scale at which the zone grid plus one room box at each end still
  // fits the canvas.
  function fitScale(w, h, box, spanX, spanY) {
    const sx = (w - box.w - 2 * ROOM_EDGE) / spanX;
    const sy = (h - box.h - 2 * ROOM_EDGE) / spanY;
    return Math.max(1e-6, Math.min(sx, sy));
  }

  // Smallest scale at which no two room boxes come within ROOM_GAP of each
  // other. Room centres are the recorded grid times the scale, so a pair is
  // clear as soon as it separates on either axis and the bound is analytic.
  function separationScale(zones, box) {
    let need = 0;
    for (var i = 0; i < zones.length; i += 1) {
      for (var j = i + 1; j < zones.length; j += 1) {
        const dx = Math.abs(zones[i].Position.X - zones[j].Position.X);
        const dy = Math.abs(zones[i].Position.Y - zones[j].Position.Y);
        const sx = dx > 0 ? (box.w + ROOM_GAP) / dx : Infinity;
        const sy = dy > 0 ? (box.h + ROOM_GAP) / dy : Infinity;
        const pair = Math.min(sx, sy);
        if (pair > need) need = pair;
      }
    }
    return need;
  }

  // The room box and the fitted scale for one canvas size: the largest font
  // scale whose rooms both fit the canvas and keep their distance, or the
  // largest scale below 1 that does when the canvas is too small for them at
  // full size (a phone). A room is never narrower than the label it holds,
  // because the box is derived from the label and the scale only ever shrinks
  // both together.
  //
  // The token radius follows the fitted scale and the card has to be tall
  // enough for the token that scale allows, so box, scale and radius are
  // settled together. Only ever grown, and only while the growth is still
  // called for, so the card that comes out is never one label too short.
  function roomForCanvas(w, h, zones, spanX, spanY, text, lines, marker) {
    const fits = function (k) {
      const box = roomBoxAt(k, text, lines, AGENT_MIN_R, marker);
      return fitScale(w, h, box, spanX, spanY) >= separationScale(zones, box);
    };
    let k = 1;
    if (!fits(1)) {
      let lo = 0;
      let hi = 1; // fits(lo) holds: an infinitesimal room always separates
      for (var i = 0; i < 24; i += 1) {
        const mid = (lo + hi) / 2;
        if (fits(mid)) lo = mid; else hi = mid;
      }
      k = lo;
    }
    let box = roomBoxAt(k, text, lines, AGENT_MIN_R, marker);
    let s = fitScale(w, h, box, spanX, spanY);
    for (var round = 0; round < 3; round += 1) {
      const grown = roomBoxAt(k, text, lines, agentRadius(s), marker);
      if (grown.h <= box.h) break;
      box = grown;
      s = fitScale(w, h, box, spanX, spanY);
    }
    return { k: k, s: s, w: box.w, h: box.h, rAgent: agentRadius(s),
             titlePx: box.titlePx, namePx: box.namePx, pillPx: box.pillPx,
             lines: lines, marker: marker };
  }

  // Where each band sits inside a room, measured from the room's centre. The
  // card is exactly as tall as these bands (see roomBoxAt), so a label can
  // never fall out of the box that was sized for it. The token and loot rows
  // keep the offsets the viewer has always used; the name band sits on the
  // token row, above the strip the extraction marker takes, so a name reads as
  // the token's caption and never lands on the marker.
  function roomBands(layout) {
    const hh = layout.roomH / 2;
    const titleY = -hh + ROOM_BAND_HEAD;
    const tokenY = hh - 10 - layout.rAgent;
    const gapTop = titleY + layout.titlePx / 2;
    const gapBottom = tokenY - layout.rAgent - layout.marker;
    const block = layout.namePx * layout.nameLines + (layout.nameLines - 1) * NAME_LINE_GAP;
    const nameTop = Math.max(gapTop + 2, gapBottom - block);
    return {
      titleY: titleY,
      nameTop: nameTop,
      nameStep: layout.namePx + NAME_LINE_GAP,
      tokenY: tokenY,
      lootY: hh - 8,
      pillY: hh - 8 - 6.5,
    };
  }

  function px(pos, layout) {
    return { x: layout.offX + (pos.X - layout.minX) * layout.s, y: layout.offY + (pos.Y - layout.minY) * layout.s };
  }

  function draw() {
    if (!fitCanvas()) return;
    const ctx = dom.canvas.getContext('2d');
    const traj = state.trajectory;
    // Cost of this frame, for the ?measure=1 harness only: a perf mark costs
    // the production path one predictable branch.
    const drawnAt = measureProbe.on ? performance.now() : 0;

    clearCanvas();
    measureProbe.labels = {};
    measureProbe.tokens = {};
    measureProbe.doors = {};
    measureProbe.statusByZone = {};
    measureProbe.rooms = {};
    measureProbe.names = {};
    measureProbe.ghosts = {};
    measureProbe.captions = {};
    measureProbe.pills = {};
    measureProbe.loot = {};
    measureProbe.lootItems = {};
    measureProbe.transit = {};
    if (!traj || !traj.frames.length) return;

    const w = dom.canvas.clientWidth;
    const h = dom.canvas.clientHeight;

    // One map. The perspective chip decides whether we draw the ground truth
    // or the observed agent's fog-of-war over the same fitted layout.
    const fog = perspectiveIsAgent()
      ? computePerception(traj, state.index, state.egoId)
      : null;
    // The world this view is painted from, which is the scrubbed frame for
    // ground truth and for every fresh recorded frame, and the frame the last
    // decision was made from at the terminal frame. The fog and the entities
    // then come from one world, so the page cannot show a decision-time
    // perception painted on top of a world no agent ever saw.
    const worldIndex = fog && fog.worldIndex !== undefined ? fog.worldIndex : state.index;
    const frame = traj.frames[worldIndex];
    drawViewport(ctx, traj, frame, { x: 0, y: 0, w: w, h: h }, fog);

    renderStatus();
    renderMetrics(frame);
    updateSentence(fog);
    updateMapCaption(fog);
    updateFogBadge(fog);
    updateCanvasLabel(fog);
    updateTransportDisabled();
    publishMeasureProbe();
    if (drawnAt) measureProbe.frame.ms = performance.now() - drawnAt;
  }

  function egoLabel(traj) {
    const roles = traj.header.AgentRoles;
    return roles && roles[state.egoId]
      ? roles[state.egoId].toUpperCase()
      : 'AGENT ' + state.egoId;
  }

  // One full-canvas viewport: a fitted layout and an optional fog policy.
  // No in-canvas header badge — the fog-source chip is a real element on the
  // viewer wrapper (so it is in the accessibility tree), and the caption below
  // the map re-states it in words.
  function drawViewport(ctx, traj, frame, region, fog) {
    const layout = ensureLayout(traj, region.w, region.h);
    if (!layout) return;

    ctx.save();
    ctx.beginPath();
    ctx.rect(region.x, region.y, region.w, region.h);
    ctx.clip();
    ctx.translate(region.x, region.y);

    const map = traj.header.Map;
    drawEdges(ctx, map, frame, layout, fog);
    drawResources(ctx, map, frame, layout, fog);
    drawZones(ctx, map, frame, layout, fog);
    drawAgents(ctx, map, frame, layout, fog);
    if (fog) drawHorizonRing(ctx, map, frame, layout, fog);

    ctx.restore();
  }

  /* ------------------------------------------------- door hover / focus  */

  // Doors reveal their capacity & type pill on hover/focus/tap of that edge.
  // Hit-test against the pill rectangles stored by drawEdges for this tick.
  function onCanvasPointerMove(event) {
    if (!state.trajectory || !state.trajectory.header.Map) return;
    const map = state.trajectory.header.Map;
    const regions = map._doorRegions;
    if (!regions) return;
    setHoverDoor(hitDoor(event.offsetX, event.offsetY, regions));
  }

  // One place that owns "which door is hovered". The canvas pointer path and
  // the per-door hit targets both go through it, so a hover revealed one way
  // is cleared the same way whichever pointer leaves.
  function setHoverDoor(id) {
    if (state.hoverDoorId === id) return;
    state.hoverDoorId = id;
    if (dom.canvas) dom.canvas.style.cursor = id === null ? '' : 'pointer';
    scheduleDraw();
  }

  // A transparent hit target per door, laid over the canvas at the pill's own
  // box. It gives every capacity gate a real element to point at — which is
  // what the browser test hovers instead of re-deriving canvas geometry from
  // the probe — and it is the same rect the canvas hit test already used, so
  // the pointer path has one source of truth. Not focusable and hidden from
  // assistive tech: the canvas carries the accessible description, and seven
  // more tab stops over content the canvas already describes would be noise.
  function syncDoorLayer(regions, map) {
    if (!dom.doorLayer) return;
    const signature = Object.keys(regions).sort().map(function (id) {
      const r = regions[id];
      return id + ':' + Math.round(r.x) + ',' + Math.round(r.y) + ',' + Math.round(r.w) + ',' + Math.round(r.h);
    }).join('|');
    if (dom.doorLayer._signature === signature) return;
    dom.doorLayer._signature = signature;
    dom.doorLayer.textContent = '';

    Object.keys(regions).forEach(function (id) {
      const r = regions[id];
      const choke = map.ChokePoints.filter(function (c) { return String(c.Id) === String(id); })[0];
      const hit = document.createElement('div');
      hit.className = 'door-hit';
      hit.dataset.door = String(id);
      hit.style.left = r.x + 'px';
      hit.style.top = r.y + 'px';
      hit.style.width = r.w + 'px';
      hit.style.height = r.h + 'px';
      if (choke) {
        const label = choke.MaxOccupancy === 0 ? 'locked' : 'capacity ' + choke.MaxOccupancy;
        hit.title = 'Gate ' + choke.FromZoneId + '–' + choke.ToZoneId + ' · ' + label;
      }
      hit.addEventListener('mouseenter', function () { setHoverDoor(Number(id)); });
      hit.addEventListener('mouseleave', function () { setHoverDoor(null); });
      hit.addEventListener('click', function () {
        setHoverDoor(state.hoverDoorId === Number(id) ? null : Number(id));
      });
      dom.doorLayer.appendChild(hit);
    });
  }

  function onCanvasFocus() {
    if (!state.trajectory || !state.trajectory.header.Map) return;
    const map = state.trajectory.header.Map;
    const regions = map._doorRegions;
    if (!regions) return;
    // No pointer on the canvas during focus; reveal the door whose state
    // just changed (a traversal this tick), if any — the one auto-shown.
    const burst = transitEdges(state.trajectory.frames[state.index]);
    let picked = null;
    Object.keys(regions).forEach(function (chokeId) {
      const choke = map.ChokePoints[chokeId];
      if (choke && burst[edgeKey(choke.FromZoneId, choke.ToZoneId)]) picked = chokeId;
    });
    if (picked !== state.hoverDoorId) {
      state.hoverDoorId = picked;
      scheduleDraw();
    }
  }

  function clearHoverDoor() {
    if (state.hoverDoorId !== null) {
      state.hoverDoorId = null;
      dom.canvas.style.cursor = '';
      scheduleDraw();
    }
  }

  function hitDoor(pxx, pyy, regions) {
    const ids = Object.keys(regions);
    for (var j = 0; j < ids.length; j += 1) {
      const r = regions[ids[j]];
      if (pxx >= r.x && pxx <= r.x + r.w && pyy >= r.y && pyy <= r.y + r.h) return ids[j];
    }
    return null;
  }

  /* -------------------------------------------- measure probe (test hook)  */

  // Present only when the page is opened with ?measure=1: exposes the exact
  // on-canvas bounds of every room label, agent token and door pill that was
  // just drawn. The browser harness uses this to prove tokens never overlap a
  // room label, and no node clips at the canvas edge. No-op otherwise.
  function publishMeasureProbe() {
    measureProbe.frame = {
      index: state.index,
      perspective: state.perspective,
      canvas: state.canv || null,
    };
    if (measureProbe.on) {
      window.__latticeGeo = measureProbe;
    }
  }

  /* ---------------------------------------------------- fog of war (ego)  */

  // KnowledgeStatus travels as System.Text.Json's default enum form: an
  // integer. Environment/PerceptionFilter.cs declares Observed=0, Stale=1,
  // Unknown=2, and the recorded fog is only as good as that declaration, so
  // the mapping is named here once rather than spelled as bare numbers at each
  // use. The engine filters on the zone an element is in, so a resource and
  // its room always carry the same status; the per-resource read is still taken
  // from the recording, because that is what the recording says.
  const KNOWLEDGE_OBSERVED = 0;
  const KNOWLEDGE_NAME = { 0: 'observed', 1: 'stale', 2: 'unknown' };

  function visionHops(traj) {
    // The cone the recording declares for the observer, when it declares one.
    // The agents each carry their own filter, so this is per-agent, not the
    // simulation's core config (which stays unbounded by design).
    const declared = traj.header.AgentVision;
    if (declared && typeof declared[state.egoId] === 'number') return declared[state.egoId];
    const cfg = traj.header.SimulationConfig;
    if (cfg && typeof cfg.Vision === 'number' && cfg.Vision >= 1) return cfg.Vision;
    return 2; // both tactical roster roles perceive two graph hops
  }

  function adjacency(map) {
    if (map._adj) return map._adj;
    const adj = {};
    map.Zones.forEach(function (z) { adj[z.Id] = []; });
    map.ChokePoints.forEach(function (c) {
      adj[c.FromZoneId].push(c.ToZoneId);
      adj[c.ToZoneId].push(c.FromZoneId);
    });
    Object.keys(adj).forEach(function (k) { adj[k].sort(function (a, b) { return a - b; }); });
    map._adj = adj;
    return adj;
  }

  // Zones within `vision` graph hops of `fromZone` (inclusive), BFS.
  function observedZones(map, fromZone, vision) {
    const adj = adjacency(map);
    const seen = {};
    seen[fromZone] = 0;
    const frontier = [fromZone];
    while (frontier.length) {
      const current = frontier.shift();
      const depth = seen[current];
      if (depth >= vision) continue;
      adj[current].forEach(function (next) {
        if (seen[next] === undefined) {
          seen[next] = depth + 1;
          frontier.push(next);
        }
      });
    }
    return seen;
  }

  // The recorded decision-time view of frame `index`, or null when the file
  // carries none for it.
  //
  // Frame i is the world AFTER step i, so the decision made from it is step
  // i+1 — and that is the perception recorded on step line i+1. The final
  // frame is the exception the viewer must not paper over: the episode ended
  // there, so no agent ever decided from that world and no perception was ever
  // recorded for it. The last view that WAS decided on is shown, flagged
  // `fresh: false` so the caption can label it as the last decision-time view
  // rather than present it as a live reading of the terminal frame.
  //
  // `worldIndex` is the frame whose world the entity layer is painted from.
  // On a fresh frame that is the scrubbed frame itself, because frame i is the
  // world the tick i+1 decision was taken from. At the terminal frame it is
  // the frame before, because that is the world the last decision was taken
  // from — the post-step terminal world is one no agent ever saw.
  function perceptionAt(traj, index, egoId) {
    const recorded = traj.perceptions;
    if (!recorded || !recorded.length) return null;
    const step = index + 1;
    if (step <= recorded.length) {
      const row = recorded[step - 1];
      return row ? { row: row, tick: step, fresh: true, egoId: egoId, worldIndex: index } : null;
    }
    if (index === recorded.length) {
      const row = recorded[recorded.length - 1];
      return row ? { row: row, tick: recorded.length, fresh: false, egoId: egoId, worldIndex: index - 1 } : null;
    }
    return null;
  }

  // The fog object every draw call reads. `source` is what the badge and the
  // caption are honest about: 'recorded' means these are the values the
  // agent's own filter produced, 'reconstructed' means this page re-derived
  // them from omniscient positions and is guessing on the recording's behalf.
  function computePerception(traj, index, egoId) {
    const recorded = perceptionAt(traj, index, egoId);
    if (recorded) return fogFromRecording(recorded);
    // A file that carries perceptions is a file whose fog is a record; if this
    // particular frame has none (a recording that does not verify), showing a
    // reconstruction here would pass a guess off as a record. Ground truth is
    // the honest fallback.
    if (traj.hasPerceptions) return null;
    return reconstructPerception(traj, index, egoId);
  }

  // Straight off the recording: the three data tiers, the rivals it saw, and
  // the ticks it saw them on. Nothing is inferred here.
  function fogFromRecording(view) {
    const me = view.row.find(function (p) { return p.AgentId === view.egoId; });
    if (!me) return null;

    const zones = {};
    const resources = {};
    const sightings = {};
    const ghosts = {};
    // Which resources the observer's own filter reported as already claimed,
    // and nothing else. Not the omniscient claim list: the recording states
    // this per agent, and a chest an agent had no way of seeing is a chest it
    // cannot paint green. Absent on a pre-schema-4 row, which then means
    // "claimed none", not "claimed all".
    const claims = {};
    (me.VisibleClaims || []).forEach(function (id) { claims[id] = true; });
    (me.Zones || []).forEach(function (zone) {
      zones[zone.ZoneId] = KNOWLEDGE_NAME[zone.Status] || 'unknown';
    });
    (me.Resources || []).forEach(function (res) {
      resources[res.ResourceId] = KNOWLEDGE_NAME[res.Status] || 'unknown';
    });
    (me.Agents || []).forEach(function (agent) {
      if (agent.AgentId === view.egoId) return;
      const status = KNOWLEDGE_NAME[agent.Status] || 'unknown';
      sightings[agent.AgentId] = { status: status, state: agent.LastKnownState, tick: agent.LastSeenTick };
      // A sighting the observer has: the rival is drawn live, not as a ghost.
      if (agent.Status !== KNOWLEDGE_OBSERVED && agent.LastKnownState) {
        ghosts[agent.AgentId] = {
          state: agent.LastKnownState,
          // The frame that sighting was made from, so "last seen N t ago"
          // counts the same way it does on the reconstruction path. The
          // recorded tick is the decision it was seen on; frame = tick - 1.
          frame: agent.LastSeenTick - 1,
        };
      }
    });

    return {
      source: 'recorded',
      vision: me.Vision,
      egoId: view.egoId,
      tick: view.tick,
      fresh: view.fresh,
      worldIndex: view.worldIndex,
      claims: claims,
      zones: zones,
      resources: resources,
      sightings: sightings,
      ghosts: ghosts,
    };
  }

  // Cumulative discovery up to the scrubbed tick, for a recording with no
  // perception field: what the observer could reach now (Observed), what it
  // remembers (Stale), and what it has never reached (Unknown) — plus ghost
  // memories of rival agents last spotted. This is a page-side derivation, not
  // a record, and every string it reaches says so.
  function reconstructPerception(traj, index, egoId) {
    const map = traj.header.Map;
    const vision = visionHops(traj);
    const egoZone = function (frame) {
      const ego = frame.agents.find(function (a) { return a.AgentId === egoId; }) || frame.agents[0];
      return ego.Transit ? ego.Transit.ToZoneId : ego.ZoneId;
    };

    const lastSeenAt = {};
    const ghosts = {};
    const sightings = {};
    for (let ti = 0; ti <= index; ti += 1) {
      const frame = traj.frames[ti];
      const seen = observedZones(map, egoZone(frame), vision);
      Object.keys(seen).forEach(function (zoneId) { lastSeenAt[zoneId] = ti; });
      frame.agents.forEach(function (agent) {
        if (agent.AgentId === egoId) return;
        if (seen[agent.ZoneId] !== undefined) {
          ghosts[agent.AgentId] = { state: agent, frame: ti };
          sightings[agent.AgentId] = { status: 'observed', state: agent, frame: ti };
        } else if (ghosts[agent.AgentId]) {
          sightings[agent.AgentId] = {
            status: 'stale', state: ghosts[agent.AgentId].state, frame: ghosts[agent.AgentId].frame,
          };
        } else {
          sightings[agent.AgentId] = { status: 'unknown', state: null, frame: -1 };
        }
      });
    }

    const current = observedZones(map, egoZone(traj.frames[index]), vision);
    const zones = {};
    const resources = {};
    // This reconstruction's own knowledge, which is the omniscient walk it
    // has just made: there is no recorded per-agent claim set on a pre-schema-4
    // file to narrow it, and the badge on this view already says the fog was
    // derived by the page. The recorded path above is the one that must not
    // guess, and it does not.
    const claims = {};
    traj.frames[index].claims.forEach(function (id) { claims[id] = true; });
    map.Zones.forEach(function (zone) {
      zones[zone.Id] = current[zone.Id] !== undefined
        ? 'observed'
        : lastSeenAt[zone.Id] !== undefined ? 'stale' : 'unknown';
    });
    map.Resources.forEach(function (res) { resources[res.Id] = zones[res.ZoneId]; });

    return {
      source: 'reconstructed',
      vision: vision,
      egoId: egoId,
      tick: index + 1,
      fresh: true,
      worldIndex: index,
      claims: claims,
      zones: zones,
      resources: resources,
      sightings: sightings,
      ghosts: ghosts,
    };
  }

  function zoneStatus(fog, zoneId) {
    return fog ? fog.zones[zoneId] || 'unknown' : 'observed';
  }

  // A resource's tier, from the recording where it has one. A reconstructed
  // fog reads it off the room, which is what the reconstruction can know.
  function resourceStatus(fog, res) {
    if (!fog) return 'observed';
    return fog.resources[res.Id] || zoneStatus(fog, res.ZoneId);
  }

  // Live rival, or a memory of one? From the recorded sighting where the file
  // has one, from the room tier otherwise.
  function rivalStatus(fog, agent) {
    if (!fog) return 'observed';
    if (fog.sightings && fog.sightings[agent.AgentId]) return fog.sightings[agent.AgentId].status;
    return zoneStatus(fog, agent.ZoneId);
  }

  // The observer's horizon: a dashed sight ring plus a slow radar pulse.
  function drawHorizonRing(ctx, map, frame, layout, fog) {
    const ego = frame.agents.find(function (a) { return a.AgentId === fog.egoId; });
    if (!ego || ego.Transit) return;
    const rect = roomRect(map.Zones[ego.ZoneId], layout);
    const base = meanCorridorLength(map, layout) * fog.vision * 0.55;
    const color = agentColor(ego, state.trajectory && state.trajectory.header.AgentRoles);

    ctx.beginPath();
    ctx.arc(rect.x, rect.y, base, 0, Math.PI * 2);
    ctx.strokeStyle = color;
    ctx.globalAlpha = 0.5;
    ctx.lineWidth = 1.1;
    ctx.setLineDash([6, 6]);
    ctx.stroke();
    ctx.setLineDash([]);

    const phase = ((performance.now() - state.pulseStart) % 1800) / 1800;
    ctx.beginPath();
    ctx.arc(rect.x, rect.y, base * (0.25 + 0.75 * phase), 0, Math.PI * 2);
    ctx.globalAlpha = 0.28 * (1 - phase);
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  function clearCanvas() {
    const ctx = dom.canvas.getContext('2d');
    ctx.clearRect(0, 0, dom.canvas.clientWidth, dom.canvas.clientHeight);
  }

  function edgeKey(from, to) {
    return from < to ? from + ':' + to : to + ':' + from;
  }

  function transitEdges(frame) {
    const edges = {};
    frame.agents.forEach(function (a) {
      if (a.Transit) edges[edgeKey(a.Transit.FromZoneId, a.Transit.ToZoneId)] = true;
    });
    return edges;
  }

  /* A room is a rounded rectangle centered on its zone anchor; corridors are
     trimmed to the room borders so lines never pierce the cards. Every room
     on a map shares the layout's one width, height and font scale, so the box
     is always sized to the text it holds and no two boxes can overlap. */

  const ROOM_HEIGHT = 52;      // the card, at full font size
  const ROOM_MIN_WIDTH = 78;
  const ROOM_GAP = 12;         // clear space between two room boxes
  const ROOM_EDGE = 10;        // clear space between the outermost box and the canvas
  const ROOM_TITLE_PX = 11;
  const ROOM_NAME_PX = 8.5;
  const ROOM_PILL_PX = 9;
  const ROOM_CHAR = 0.62;      // monospace advance as a fraction of the font size
  const ROOM_PAD_X = 13;       // inner padding, per side
  const ROOM_BAND_HEAD = 13;   // title band, from the top of the card
  const ROOM_BAND_FOOT = 25;   // token row + loot row, from the middle of the card
  const NAME_LINE_GAP = 3;
  const NAME_GAP = 4;           // clear space between two names on one line
  const EXTRACTION_MARKER = 12; // strip above a token the cyan diamond takes
  const AGENT_MIN_R = 7;
  const AGENT_MAX_R = 11;
  const CAPTION_PX = 8;
  const LANE_GAP = 4;          // clear space between a token and a lane's label

  function roomLabel(zone) {
    return zone.Role ? spaceCamel(zone.Role) : 'Room ' + zone.Id;
  }

  function spaceCamel(text) {
    return String(text).replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  function roomRect(zone, layout) {
    const center = px(zone.Position, layout);
    return { x: center.x, y: center.y, hw: layout.roomW / 2, hh: layout.roomH / 2 };
  }

  // Where the segment between two room centers enters/leaves each room rect.
  function corridorEndpoints(aRect, bRect) {
    const dx = bRect.x - aRect.x;
    const dy = bRect.y - aRect.y;
    if (dx === 0 && dy === 0) return null;
    const trimA = Math.min(aRect.hw / Math.max(1e-6, Math.abs(dx)), aRect.hh / Math.max(1e-6, Math.abs(dy)), 1);
    const trimB = Math.min(bRect.hw / Math.max(1e-6, Math.abs(dx)), bRect.hh / Math.max(1e-6, Math.abs(dy)), 1);
    return {
      ax: aRect.x + dx * trimA, ay: aRect.y + dy * trimA,
      bx: bRect.x - dx * trimB, by: bRect.y - dy * trimB,
    };
  }

  function roundedRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y);
    ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r);
    ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h);
    ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + r);
    ctx.arcTo(x, y, x + r, y, r);
    ctx.closePath();
  }

  // The engine's transit cost, mirrored: ceil(manhattan distance / speed).
  function transitTotalTicks(map, cfg, fromZoneId, toZoneId) {
    const speed = cfg && typeof cfg.TransitSpeed === 'number' ? cfg.TransitSpeed : 8;
    if (speed <= 0) return 1;
    const a = map.Zones[fromZoneId].Position;
    const b = map.Zones[toZoneId].Position;
    const distance = Math.abs(a.X - b.X) + Math.abs(a.Y - b.Y);
    return Math.max(1, Math.ceil(distance / speed));
  }

  function drawEdges(ctx, map, frame, layout, fog) {
    const burst = transitEdges(frame);
    const leveledDoors = {}; // chokeId -> pill rect, for hover/focus hit tests

    map.ChokePoints.forEach(function (choke) {
      const zoneA = map.Zones[choke.FromZoneId];
      const zoneB = map.Zones[choke.ToZoneId];
      if (!zoneA || !zoneB) return;
      const ends = corridorEndpoints(roomRect(zoneA, layout), roomRect(zoneB, layout));
      if (!ends) return;
      const statA = zoneStatus(fog, choke.FromZoneId);
      const statB = zoneStatus(fog, choke.ToZoneId);
      const bothUnknown = statA === 'unknown' && statB === 'unknown';
      const anyUnknown = statA === 'unknown' || statB === 'unknown';
      const anyStale = statA === 'stale' || statB === 'stale';
      const hot = !anyUnknown && burst[edgeKey(choke.FromZoneId, choke.ToZoneId)];
      const limited = choke.MaxOccupancy !== UNLIMITED && choke.MaxOccupancy < UNLIMITED;

      ctx.beginPath();
      ctx.moveTo(ends.ax, ends.ay);
      ctx.lineTo(ends.bx, ends.by);
      ctx.strokeStyle = bothUnknown ? COLORS.fogUnknownStroke
        : anyUnknown ? COLORS.fogStaleStroke
        : hot ? COLORS.corridorHot
        : anyStale ? COLORS.fogStaleStroke
        : COLORS.corridor;
      ctx.lineWidth = hot ? 4 : 2.5;
      ctx.setLineDash(limited && !hot ? [7, 6] : []);
      ctx.lineCap = 'round';
      if (bothUnknown) ctx.setLineDash([3, 6]);
      ctx.stroke();
      ctx.setLineDash([]);

      // Capacity gate pill: hidden by default to keep the graph calm. It
      // auto-shows only when a door's state changes on the current tick (an
      // agent crosses that edge now), and on hover/focus/tap of the edge.
      // "Its state changes" here means traversal — the only per-tick change
      // the recording carries for a door.
      if (limited && !anyUnknown) {
        const midX = (ends.ax + ends.bx) / 2;
        const midY = (ends.ay + ends.by) / 2;
        const label = choke.MaxOccupancy === 0 ? 'LOCKED' : 'CAP ' + choke.MaxOccupancy;
        const bw = label.length * 6.4 + 14;
        const pillRect = { x: midX - bw / 2, y: midY - 9, w: bw + 4, h: 26 };
        leveledDoors[choke.Id] = pillRect;
        if (measureProbe.on) {
          measureProbe.doors[choke.Id] = {
            x: pillRect.x, y: pillRect.y, w: pillRect.w, h: pillRect.h,
            edge: choke.FromZoneId + ':' + choke.ToZoneId,
          };
        }
        const showPill = hot || state.hoverDoorId === choke.Id;
        if (showPill) {
          roundedRect(ctx, midX - bw / 2, midY - 9, bw, 18, 9);
          ctx.fillStyle = state.hoverDoorId === choke.Id ? COLORS.accentBar : COLORS.gateBg;
          ctx.fill();
          ctx.strokeStyle = hot ? COLORS.corridorHot : anyStale ? COLORS.fogStaleStroke : COLORS.corridor;
          ctx.lineWidth = 1;
          ctx.stroke();
          ctx.fillStyle = hot ? COLORS.corridorHot : anyStale ? COLORS.fogText : COLORS.gateText;
          ctx.font = 'bold 9px monospace';
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(label, midX, midY + 0.5);

          if (choke.Role) {
            ctx.font = '8px monospace';
            ctx.fillStyle = COLORS.mutedText;
            ctx.globalAlpha = anyStale ? 0.55 : 1;
            ctx.fillText(spaceCamel(choke.Role), midX, midY + 17);
            ctx.globalAlpha = 1;
          }
        }
      }
    });

    // Persist this tick's door hit regions so a pointer move/focus can reveal
    // the pill for the edge under it without recomputing the whole graph.
    const regions = {};
    Object.keys(leveledDoors).forEach(function (id) { regions[id] = leveledDoors[id]; });
    map._doorRegions = regions;
    syncDoorLayer(regions, map);

    // Drop a hover that no longer targets a door (e.g. door left the view).
    if (state.hoverDoorId !== null && !regions[state.hoverDoorId]) {
      state.hoverDoorId = null;
    }
  }

  // Loot sits inside its room; never floating in open canvas space. Whether a
  // chest paints as claimed is the fog's answer, never the world's: with an
  // ego view the only claim set that may be shown is the one that agent's own
  // filter reported, so a chest it had no way of seeing stays amber. Ground
  // truth (no fog) is the omniscient claim list, which is what it is labelled.
  function drawResources(ctx, map, frame, layout, fog) {
    const claimed = fog ? fog.claims : claimSet(frame);

    const byZone = {};
    map.Resources.forEach(function (res) {
      if (!byZone[res.ZoneId]) byZone[res.ZoneId] = [];
      byZone[res.ZoneId].push(res);
    });

    map.Zones.forEach(function (zone) {
      const items = byZone[zone.Id];
      if (!items || !items.length) return;
      // The recording states each resource's tier, so an unexplored chest is
      // left out even in a room that is. (The engine filters on the room, so
      // in practice the two agree; the per-resource read is what the file
      // says, and the layout is unchanged either way.)
      const shown = items.filter(function (res) { return resourceStatus(fog, res) !== 'unknown'; });
      if (!shown.length) return;
      const rect = roomRect(zone, layout);
      const y = rect.y + layout.bands.lootY;
      const spacing = 14;
      // Anchor the loot row to the room's lower-left corner so it never
      // collides with the centered agent tokens and the count pill.
      const startX = rect.x - rect.hw + layout.pad - 4;
      ctx.globalAlpha = resourceStatus(fog, shown[0]) === 'stale' ? 0.35 : 1;
      shown.forEach(function (res, i) {
        drawDiamond(ctx, startX + i * spacing, y, 5, claimed[res.Id] ? COLORS.claimed : COLORS.unclaimed);
      });
      ctx.globalAlpha = 1;
      if (measureProbe.on) {
        const endX = startX + (shown.length - 1) * spacing;
        measureProbe.loot[zone.Id] = { x: startX - 5, y: y - 5, w: endX - startX + 10, h: 10 };
        // Per-diamond, with the colour actually filled: the harness needs to
        // assert the claim state the page painted, not one it could have read
        // off the recording for itself.
        measureProbe.lootItems[zone.Id] = shown.map(function (res, i) {
          return {
            id: res.Id,
            x: startX + i * spacing,
            y: y,
            claimed: !!claimed[res.Id],
            color: claimed[res.Id] ? COLORS.claimed : COLORS.unclaimed,
          };
        });
      }
    });
  }

  function claimSet(frame) {
    const claimed = {};
    frame.claims.forEach(function (id) { claimed[id] = true; });
    return claimed;
  }

  function drawDiamond(ctx, x, y, r, color) {
    ctx.beginPath();
    ctx.moveTo(x, y - r);
    ctx.lineTo(x + r * 0.72, y);
    ctx.lineTo(x, y + r);
    ctx.lineTo(x - r * 0.72, y);
    ctx.closePath();
    ctx.fillStyle = color;
    ctx.fill();
  }

  function drawZones(ctx, map, frame, layout, fog) {
    const occupied = zoneCounts(map, frame);

    map.Zones.forEach(function (zone) {
      const rect = roomRect(zone, layout);
      const status = zoneStatus(fog, zone.Id);
      const borderRadius = 9;
      if (measureProbe.on) {
        measureProbe.statusByZone[zone.Id] = status;
        measureProbe.rooms[zone.Id] = {
          x: rect.x - rect.hw, y: rect.y - rect.hh, w: rect.hw * 2, h: rect.hh * 2,
          cx: rect.x, cy: rect.y, label: roomLabel(zone), status: status,
        };
      }

      if (status === 'unknown') {
        // Shrouded silhouette: the observer has never reached this room.
        roundedRect(ctx, rect.x - rect.hw, rect.y - rect.hh, rect.hw * 2, rect.hh * 2, borderRadius);
        ctx.fillStyle = COLORS.fogUnknownFill;
        ctx.fill();
        ctx.strokeStyle = COLORS.fogUnknownStroke;
        ctx.lineWidth = 1.4;
        ctx.setLineDash([4, 4]);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.fillStyle = COLORS.fogText;
        ctx.font = 'bold 10px monospace';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText('unexplored', rect.x, rect.y);
        return;
      }

      roundedRect(ctx, rect.x - rect.hw, rect.y - rect.hh, rect.hw * 2, rect.hh * 2, borderRadius);
      ctx.fillStyle = status === 'stale' ? COLORS.fogStaleFill : COLORS.roomFill;
      ctx.fill();
      ctx.strokeStyle = status === 'stale' ? COLORS.fogStaleStroke : COLORS.roomStroke;
      ctx.lineWidth = status === 'stale' ? 1.3 : 1.6;
      ctx.stroke();

      // Room title strip on a dedicated top band, so the labels below (agent
      // names, tokens, occupancy) can never sit on top of the room name.
      ctx.fillStyle = status === 'stale' ? COLORS.fogText : COLORS.roomText;
      ctx.font = 'bold ' + layout.titlePx + 'px monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const label = roomLabel(zone);
      const labelY = rect.y + layout.bands.titleY;
      ctx.fillText(label, rect.x, labelY);
      if (measureProbe.on) {
        const lw = ctx.measureText(label).width;
        const line = probeTextLines(ctx, label, labelY, layout.titlePx);
        measureProbe.labels[zone.Id] = {
          x: rect.x - lw / 2, y: line.top, w: lw, h: line.bottom - line.top,
        };
      }

      // Occupancy badge: a clear fraction when capped, a plain count when
      // not. It lives in the bottom-right corner, on the loot row the loot
      // never reaches, with the card's own padding all round.
      if (status === 'observed') {
        const present = occupied[zone.Id] || 0;
        const capped = zone.MaxOccupancy !== UNLIMITED && zone.MaxOccupancy < UNLIMITED;
        const occ = capped ? present + '/' + zone.MaxOccupancy : String(present);
        const pillH = 13 * layout.k;
        ctx.font = layout.pillPx + 'px monospace';
        const bw = occ.length * ROOM_CHAR * layout.pillPx + 12 * layout.k;
        const bx = rect.x + rect.hw - layout.pad - bw;
        const by = rect.y + layout.bands.pillY - pillH / 2 + 6.5 * layout.k;
        roundedRect(ctx, bx, by, bw, pillH, pillH / 2);
        ctx.fillStyle = present > 0 ? 'rgba(122, 162, 247, 0.25)' : 'rgba(148, 163, 184, 0.15)';
        ctx.fill();
        ctx.fillStyle = COLORS.mutedText;
        ctx.fillText(occ, bx + bw / 2, by + pillH / 2);
        if (measureProbe.on) {
          measureProbe.pills[zone.Id] = {
            x: bx, y: by, w: bw, h: pillH, text: occ, where: 'bottom',
          };
        }
      } else {
        ctx.font = '8px monospace';
        ctx.fillStyle = COLORS.fogText;
        ctx.fillText('last known', rect.x, rect.y);
      }
    });
  }

  function zoneCounts(map, frame) {
    const counts = {};
    map.Zones.forEach(function (z) { counts[z.Id] = 0; });
    if (frame) frame.agents.forEach(function (a) { counts[a.ZoneId] = (counts[a.ZoneId] || 0) + 1; });
    return counts;
  }

  function zoneUnclaimed(map, frame) {
    const claimed = {};
    if (frame) frame.claims.forEach(function (id) { claimed[id] = true; });
    const left = {};
    map.Zones.forEach(function (z) { left[z.Id] = 0; });
    map.Resources.forEach(function (res) {
      if (!claimed[res.Id]) left[res.ZoneId] = (left[res.ZoneId] || 0) + 1;
    });
    return left;
  }

  function agentColor(agent, roles) {
    const role = roles && roles[agent.AgentId];
    if (role === 'Sentry') return COLORS.sentry;
    if (role === 'Infiltrator') return COLORS.infiltrator;
    return AGENT_PALETTE[agent.AgentId % AGENT_PALETTE.length];
  }

  function meanCorridorLength(map, layout) {
    let total = 0, count = 0;
    map.ChokePoints.forEach(function (choke) {
      const a = px(map.Zones[choke.FromZoneId].Position, layout);
      const b = px(map.Zones[choke.ToZoneId].Position, layout);
      total += Math.hypot(b.x - a.x, b.y - a.y);
      count += 1;
    });
    return count ? total / count : layout.rAgent * 6;
  }

  // Where every name this frame paints goes, before anything is drawn: the
  // live agents' role/score and the remembered rivals' age stamp each take a
  // slot on their own room's name band, packed left to right and wrapped only
  // when the next label will not fit inside the card. Every line is then
  // centred as a row, so two names in one room can never overlap and none can
  // leave the room. The card is always tall enough for the worst case over the
  // whole recording (see maxNameLines). Agents in transit are not here: their
  // labels ride the corridor lanes instead. O(agents + rooms) per frame.
  function planNames(ctx, pool, map, layout, fog, roles) {
    const slots = {};
    const byRoom = {};
    const inner = layout.roomW - 2 * layout.pad;
    const saved = ctx.font;
    ctx.font = layout.namePx + 'px monospace';

    pool.forEach(function (agent) {
      if (agent.Transit) return;
      const isEgo = fog && agent.AgentId === fog.egoId;
      const ghosted = fog && !isEgo && rivalStatus(fog, agent) !== 'observed';
      let zoneId = agent.ZoneId;
      let text;
      if (ghosted) {
        const ghost = fog.ghosts[agent.AgentId];
        if (!ghost) return; // never spotted — nothing to remember
        zoneId = ghost.state.ZoneId;
        const age = state.index - ghost.frame;
        text = 'last seen ' + (age === 0 ? 'now' : age + 't ago');
      } else {
        const role = roles && roles[agent.AgentId];
        text = (role ? role + ' ' : '') + '· ' + agent.Score;
      }
      const item = { id: agent.AgentId, w: ctx.measureText(text).width, text: text };
      (byRoom[zoneId] = byRoom[zoneId] || []).push(item);
    });

    Object.keys(byRoom).forEach(function (zoneId) {
      const zone = map.Zones[zoneId];
      if (!zone) return;
      const rect = roomRect(zone, layout);
      const lines = [];
      let row = [];
      let used = 0;
      byRoom[zoneId].forEach(function (item) {
        if (row.length && used + NAME_GAP + item.w > inner) {
          lines.push(row); row = []; used = 0;
        }
        used += (row.length ? NAME_GAP : 0) + item.w;
        row.push(item);
      });
      if (row.length) lines.push(row);
      lines.forEach(function (line, li) {
        const total = line.reduce(function (t, item) { return t + item.w; }, 0)
          + NAME_GAP * (line.length - 1);
        let x = rect.x - total / 2;
        line.forEach(function (item) {
          slots[item.id] = {
            x: x + item.w / 2,
            y: rect.y + layout.bands.nameTop + layout.namePx / 2 + li * layout.bands.nameStep,
            text: item.text,
            zone: zoneId,
          };
          x += item.w + NAME_GAP;
        });
      });
    });
    ctx.font = saved;
    return slots;
  }

  // The agent states this view may paint. The ego is the world being painted;
  // a rival is wherever the recording last knew it, and only a rival the
  // recording marks observed is drawn live at all (a stale one is a ghost, an
  // unknown one is hidden — see drawGhost). On a fresh frame this is a no-op,
  // because the recorded LastKnownState of an observed rival is that frame's
  // own world. At the terminal frame it is what keeps a rival off a position
  // its observer never saw.
  function paintedAgents(frame, fog) {
    if (!fog) return frame.agents;
    return frame.agents.map(function (agent) {
      if (agent.AgentId === fog.egoId) return agent;
      const sight = fog.sightings && fog.sightings[agent.AgentId];
      if (sight && sight.status === 'observed' && sight.state) return sight.state;
      return agent;
    });
  }

  function drawAgents(ctx, map, frame, layout, fog) {
    const roles = state.trajectory ? state.trajectory.header.AgentRoles : null;
    const cfg = state.trajectory ? state.trajectory.header.SimulationConfig : null;
    const pool = paintedAgents(frame, fog).slice().sort(function (a, b) { return a.AgentId - b.AgentId; });
    const hopRadius = meanCorridorLength(map, layout) * 0.85;
    const nameSlots = planNames(ctx, pool, map, layout, fog, roles);

    // Co-located agents fan out horizontally inside the room.
    const stationedTotal = {};
    const stationedSeen = {};
    let transitLane = 0;
    pool.forEach(function (agent) {
      if (!agent.Transit) stationedTotal[agent.ZoneId] = (stationedTotal[agent.ZoneId] || 0) + 1;
    });

    pool.forEach(function (agent) {
      // Fog rule: the observer is real-time; a rival renders live only while
      // the recording says it is inside the observer's horizon, and otherwise
      // as a memory ghost at the room it was last seen in.
      const isEgo = fog && agent.AgentId === fog.egoId;
      if (fog && !isEgo && rivalStatus(fog, agent) !== 'observed') {
        drawGhost(ctx, map, frame, layout, fog, agent, roles, nameSlots);
        return;
      }

      let x = 0, y = 0;
      let transitEnds = null;
      const color = agentColor(agent, roles);
      const role = roles && roles[agent.AgentId];
      const label = (role ? role + ' ' : '') + '· ' + agent.Score;
      if (agent.Transit) {
        // Snap strictly to the corridor vector: P(t) = A + t·(B − A), t from
        // the engine's remaining-ticks countdown.
        const zoneA = map.Zones[agent.Transit.FromZoneId];
        const zoneB = map.Zones[agent.Transit.ToZoneId];
        const rectA = roomRect(zoneA, layout);
        const rectB = roomRect(zoneB, layout);
        const ends = corridorEndpoints(rectA, rectB);
        transitEnds = ends;
        const total = transitTotalTicks(map, cfg, agent.Transit.FromZoneId, agent.Transit.ToZoneId);
        let t = Math.min(1, Math.max(0, (total - agent.Transit.RemainingTicks + 1) / total));
        if (ends) {
          // The token travels the corridor but stops a token's width short of
          // the destination room, so its circle never intrudes on the room's
          // title band mid-arrival. The next tick it is stationed inside.
          const leg = Math.hypot(ends.bx - ends.ax, ends.by - ends.ay);
          if (leg > 0) {
            const maxT = Math.max(0, 1 - (layout.rAgent + 3) / leg);
            t = Math.min(t, maxT);
          }
          x = ends.ax + (ends.bx - ends.ax) * t;
          y = ends.ay + (ends.by - ends.ay) * t;
        } else {
          x = rectA.x; y = rectA.y;
        }
        ctx.beginPath();
        ctx.arc(x, y, layout.rAgent + 4, 0, Math.PI * 2);
        ctx.strokeStyle = COLORS.transitRing;
        ctx.lineWidth = 1.5;
        ctx.setLineDash([3, 3]);
        ctx.stroke();
        ctx.setLineDash([]);
      } else {
        const zone = map.Zones[agent.ZoneId];
        if (!zone) return;
        const rect = roomRect(zone, layout);
        const mates = stationedTotal[agent.ZoneId] || 1;
        const slot = stationedSeen[agent.ZoneId] || 0;
        stationedSeen[agent.ZoneId] = slot + 1;
        x = rect.x + (slot - (mates - 1) / 2) * (layout.rAgent * 2.4);
        // Tokens live on a bottom band inside the room, clear of the room
        // title strip at the top — a token never sits on a room label.
        y = rect.y + layout.bands.tokenY;
      }

      if (measureProbe.on) {
        measureProbe.tokens[agent.AgentId] = { x: x, y: y, r: layout.rAgent };
        if (agent.Transit) measureProbe.transit[agent.AgentId] = true;
      }

      // The Sentry carries a faint dashed perception perimeter — in ground
      // truth only; the ego viewport replaces it with the horizon ring.
      if (role === 'Sentry' && !fog) {
        ctx.beginPath();
        ctx.arc(x, y, hopRadius, 0, Math.PI * 2);
        ctx.strokeStyle = COLORS.perception;
        ctx.lineWidth = 1.2;
        ctx.setLineDash([5, 5]);
        ctx.stroke();
        ctx.setLineDash([]);
      }

      // The Infiltrator carries a cyan extraction marker above the token.
      if (role === 'Infiltrator') {
        drawDiamond(ctx, x, y - layout.rAgent - 7, 4.5, COLORS.extraction);
      }

      ctx.beginPath();
      ctx.arc(x, y, layout.rAgent, 0, Math.PI * 2);
      ctx.fillStyle = color;
      ctx.fill();
      ctx.strokeStyle = COLORS.agentRim;
      ctx.lineWidth = 1.5;
      ctx.stroke();

      ctx.fillStyle = COLORS.agentRim;
      ctx.font = 'bold 9px monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(agent.AgentId), x, y);

      if (agent.Transit) {
        // In transit the label has no room to live in, so it rides the
        // corridor: the name in one lane beside the axis, the caption in the
        // lane on the other side of it.
        const caption = 'crossing to ' + map.Zones[agent.Transit.ToZoneId].Id
          + ' (' + agent.Transit.RemainingTicks + 't)';
        const lane = transitLane;
        transitLane += 1;
        const at = transitLanes(ctx, map, layout, transitEnds, x, y, lane, label, caption);
        ctx.font = layout.namePx + 'px monospace';
        ctx.fillStyle = color;
        ctx.textAlign = 'center';
        ctx.fillText(label, at.nameX, at.nameY);
        if (measureProbe.on) {
          probeInk(ctx, label, at.nameX, at.nameY, measureProbe.names, agent.AgentId,
            { zone: null });
        }
        ctx.font = CAPTION_PX * layout.k + 'px monospace';
        ctx.fillStyle = COLORS.transitRing;
        ctx.fillText(caption, at.capX, at.capY);
        if (measureProbe.on) {
          probeInk(ctx, caption, at.capX, at.capY, measureProbe.captions, agent.AgentId);
        }
        return;
      }

      // In a room: role/score goes on the room's own name band, inside the
      // card by construction — never beside the token, which is what let a
      // name run out of its room and into the next one.
      const slot = nameSlots[agent.AgentId];
      if (!slot) return;
      ctx.font = layout.namePx + 'px monospace';
      ctx.fillStyle = color;
      ctx.textAlign = 'center';
      ctx.fillText(label, slot.x, slot.y);
      if (measureProbe.on) {
        probeInk(ctx, label, slot.x, slot.y, measureProbe.names, agent.AgentId,
          { zone: agent.ZoneId });
      }
    });
  }

  // Where a transit label goes. Names and captions take opposite sides of the
  // corridor axis and step outwards by lane, so a caption can never land on a
  // name and two of either can never land on each other. Each lane is then
  // walked outwards from the axis until the label's own box is clear of every
  // room box and still inside the canvas: a label follows its token, but can
  // never end up painted on a room or off the edge. A bounded walk of a few
  // steps, one pass over the rooms per step, so a frame still costs
  // O(rooms + agents).
  function transitLanes(ctx, map, layout, ends, x, y, lane, name, caption) {
    const saved = ctx.font;
    ctx.font = layout.namePx + 'px monospace';
    const nameSize = layout.namePx;
    const nameHalf = ctx.measureText(name).width / 2;
    ctx.font = CAPTION_PX * layout.k + 'px monospace';
    const capSize = CAPTION_PX * layout.k;
    const capHalf = ctx.measureText(caption).width / 2;
    ctx.font = saved;

    const out = { nameX: x, nameY: y, capX: x, capY: y };
    if (!ends) return out;
    const ax = ends.bx - ends.ax;
    const ay = ends.by - ends.ay;
    const leg = Math.hypot(ax, ay);
    if (!leg) return out;
    const u = { x: ax / leg, y: ay / leg };
    const n = { x: -u.y, y: u.x };
    const side = lane % 2 === 0 ? 1 : -1;
    const depth = Math.floor(lane / 2);
    // Lanes are a whole label width apart, so two labels in the same lane
    // column are clear of each other whichever way the corridor runs.
    const step = nameHalf + capHalf + LANE_GAP;

    // Slide the labels' shared anchor along the axis so their own extent stays
    // inside the gap between the two rooms the token is travelling between.
    // One anchor for both, so the only thing separating the name from the
    // caption is how far out their lanes sit.
    const half = Math.max(nameHalf, capHalf);
    const t = (x - ends.ax) * u.x + (y - ends.ay) * u.y;
    const lo = Math.min(half / leg, 0.5);
    const hi = Math.max(1 - half / leg, 0.5);
    const tc = Math.min(hi, Math.max(lo, t));
    const p = { x: ends.ax + ax * tc, y: ends.ay + ay * tc };

    const at = function (sign, size, halfW, avoid) {
      const first = layout.rAgent + LANE_GAP + size / 2 + depth * step;
      // The label is painted unrotated, so its box on the canvas is the box the
      // room and label tests have to keep clear.
      for (var i = 0; i < 8; i += 1) {
        const off = first + i * (size + LANE_GAP);
        const cx = p.x + n.x * sign * off;
        const cy = p.y + n.y * sign * off;
        if (cx - halfW < 1 || cy - size / 2 < 1
            || cx + halfW > layout.w - 1 || cy + size / 2 > layout.h - 1) break;
        if (labelHitsRoom(cx, cy, halfW, size / 2, map, layout)) continue;
        if (avoid && boxesHit(cx, cy, halfW, size / 2, avoid)) continue;
        return { x: cx, y: cy };
      }
      // Nowhere clear on this side (a canvas too small for the lane): keep the
      // label beside its token, inside the canvas, as the viewer always has.
      return {
        x: clamp(x + n.x * sign * (layout.rAgent + LANE_GAP), halfW + 1, layout.w - halfW - 1),
        y: clamp(y + n.y * sign * (layout.rAgent + LANE_GAP), size / 2 + 1, layout.h - size / 2 - 1),
      };
    };
    const nameAt = at(side, nameSize, nameHalf, null);
    const capAt = at(-side, capSize, capHalf,
      { x: nameAt.x, y: nameAt.y, hw: nameHalf, hh: nameSize / 2 });
    out.nameX = nameAt.x; out.nameY = nameAt.y;
    out.capX = capAt.x; out.capY = capAt.y;
    return out;
  }

  function boxesHit(cx, cy, half, across, box) {
    return cx - half < box.x + box.hw && cx + half > box.x - box.hw
      && cy - across < box.y + box.hh && cy + across > box.y - box.hh;
  }

  // Does the label box centred on (cx, cy), half-extents (half, across), land
  // on any room box?
  function labelHitsRoom(cx, cy, half, across, map, layout) {
    const x0 = cx - half;
    const x1 = cx + half;
    const y0 = cy - across;
    const y1 = cy + across;
    for (var i = 0; i < map.Zones.length; i += 1) {
      const r = roomRect(map.Zones[i], layout);
      if (x0 < r.x + r.hw && x1 > r.x - r.hw && y0 < r.y + r.hh && y1 > r.y - r.hh) return true;
    }
    return false;
  }

  function clamp(v, lo, hi) {
    return hi < lo ? (lo + hi) / 2 : Math.min(hi, Math.max(lo, v));
  }

  // A stale memory of a rival: translucent token at the zone where the
  // observer last saw it, labeled with the age of that sighting.
  function drawGhost(ctx, map, frame, layout, fog, agent, roles, nameSlots) {
    const ghost = fog.ghosts[agent.AgentId];
    if (!ghost) return; // never spotted — nothing to remember
    const zone = map.Zones[ghost.state.ZoneId];
    if (!zone) return;
    const slot = nameSlots[agent.AgentId];
    if (!slot) return;
    const rect = roomRect(zone, layout);
    // Ghost tokens sit on the same bottom band as live tokens, clear of the
    // room title strip, and the age stamp shares the room's name band.
    const x = rect.x;
    const y = rect.y + layout.bands.tokenY;

    ctx.globalAlpha = 0.4;
    ctx.beginPath();
    ctx.arc(x, y, layout.rAgent, 0, Math.PI * 2);
    ctx.fillStyle = agentColor(agent, roles);
    ctx.fill();
    ctx.globalAlpha = 0.75;
    ctx.strokeStyle = COLORS.agentRim;
    ctx.lineWidth = 1;
    ctx.setLineDash([3, 3]);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.font = layout.namePx + 'px monospace';
    ctx.fillStyle = COLORS.fogText;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(slot.text, slot.x, slot.y);
    if (measureProbe.on) {
      probeInk(ctx, slot.text, slot.x, slot.y, measureProbe.ghosts, agent.AgentId,
        { zone: ghost.state.ZoneId });
    }
    ctx.globalAlpha = 1;
  }

  /* ------------------------------------------------------- status/metrics  */

  function renderStatus() {
    const traj = state.trajectory;
    if (!traj || !traj.frames.length) return;
    const last = traj.frames.length - 1;
    dom.slider.max = String(last);
    dom.slider.value = String(state.index);
    dom.tickReadout.textContent = state.index + ' / ' + last;

    const fin = traj.final && traj.final.Metrics ? traj.final.Metrics : null;
    if (state.index === last && fin) {
      dom.terminal.textContent = fin.Reason + (fin.WinnerAgentId !== null && fin.WinnerAgentId !== undefined ? ' — winner agent ' + fin.WinnerAgentId : '');
      dom.terminal.className = 'terminal ' + (fin.Reason === 'tick-limit' ? 'limit' : 'ok');
    } else {
      dom.terminal.textContent = '';
      dom.terminal.className = 'terminal';
    }
  }

  // The panel reads the same world the canvas was painted from, not the
  // scrubbed index: at the terminal frame in an ego view those differ, and a
  // count of claims taken from the post-step world beside a fog taken from the
  // decision-time one would be exactly the mix this is here to remove.
  function renderMetrics(frame) {
    const traj = state.trajectory;
    if (!traj) return;
    if (!frame) frame = traj.frames[state.index];
    const map = traj.header.Map;
    const fin = traj.final && traj.final.Metrics ? traj.final.Metrics : null;

    dom.statSeed.textContent = String(traj.header.Seed);
    dom.statAgents.textContent = String(map.Zones ? traj.header.SimulationConfig.AgentCount : 0);

    let claimed = 0;
    if (frame) claimed = frame.claims.length;
    dom.statClaimed.textContent = frame ? claimed + ' / ' + map.Resources.length : '—';
    dom.statSteps.textContent = fin ? fin.TotalSteps + ' (limit ' + traj.header.SimulationConfig.MaxTicks + ')' : String(state.index);

    const crowd = zoneCounts(map, frame);
    const roles = traj.header.AgentRoles;
    let rows = '';
    frame.agents.slice().sort(function (a, b) { return a.AgentId - b.AgentId; }).forEach(function (agent) {
      const transit = agent.Transit
        ? '→' + agent.Transit.ToZoneId + ' (' + agent.Transit.RemainingTicks + 't)'
        : 'idle';
      const role = roles && roles[agent.AgentId] ? ' · ' + roles[agent.AgentId] : '';
      rows += '<tr><td class="k">A' + agent.AgentId + role + ' · zone ' + agent.ZoneId + '</td><td class="v">' + transit + '</td></tr>';
    });
    dom.agentsBody.innerHTML = rows;

    let zrows = '';
    map.Zones.slice().sort(function (a, b) { return a.Id - b.Id; }).forEach(function (zone) {
      const room = zone.Role ? spaceCamel(zone.Role) : 'Room ' + zone.Id;
      const present = crowd[zone.Id] || 0;
      const loot = zoneUnclaimed(map, frame)[zone.Id] || 0;
      zrows += '<tr><td class="k">' + room + '</td><td class="v">' +
        (present ? present + (present === 1 ? ' agent' : ' agents') : 'empty') +
        ' · ' + (loot ? loot + ' loot' : 'no loot') + '</td></tr>';
    });
    dom.zonesBody.innerHTML = zrows;
  }

  /* ------------------------------------------- plain-language tick sentence  */

  function roleLabel(agentId) {
    const roles = state.trajectory && state.trajectory.header.AgentRoles;
    return roles && roles[agentId] ? 'Agent ' + agentId + ' (' + roles[agentId] + ')' : 'Agent ' + agentId;
  }

  function zoneName(zoneId) {
    const zone = state.trajectory && state.trajectory.header.Map && state.trajectory.header.Map.Zones[zoneId];
    return zone ? roomLabel(zone) : 'room ' + zoneId;
  }

  function describeRivalSight(skel, index) {
    const traj = state.trajectory;
    const frame = traj.frames[index];
    const ego = frame.agents.find(function (a) { return a.AgentId === state.egoId; });
    if (!ego) return 'the observed agent is absent from this frame';
    const rivalIds = frame.agents.filter(function (a) { return a.AgentId !== state.egoId; });
    if (!rivalIds.length) return 'no rival agents in this recording';

    const inView = rivalIds.filter(function (a) { return rivalStatus(skel, a) === 'observed'; });
    const clauses = [];
    if (inView.length) {
      clauses.push('sees ' + inView.map(function (a) { return roleLabel(a.AgentId); }).join(' and '));
    }
    Object.keys(skel.ghosts).forEach(function (id) {
      const ghost = skel.ghosts[id];
      const age = index - ghost.frame;
      clauses.push('last saw ' + roleLabel(Number(id)) + (age === 0 ? ' moments ago' : ' ' + age + ' ticks ago'));
    });
    if (!clauses.length) clauses.push('no rivals in view');
    return clauses.join('; ');
  }

  function describeFrame(fog) {
    const traj = state.trajectory;
    if (!traj || !traj.frames.length) return 'No recording loaded.';
    const last = traj.frames.length - 1;
    const index = state.index;
    const frame = traj.frames[index];
    const map = traj.header.Map;
    let sentence;

    if (!perspectiveIsAgent()) {
      if (!frame.agents || !frame.agents.length) {
        sentence = 'This recorded frame carries no agent states.';
      } else {
        const spots = frame.agents.map(function (a) {
          if (a.Transit) return roleLabel(a.AgentId) + ' crossing ' + zoneName(a.Transit.FromZoneId) + ' to ' + zoneName(a.Transit.ToZoneId);
          return roleLabel(a.AgentId) + ' in ' + zoneName(a.ZoneId);
        });
        sentence = spots.join('; ') + '; ' + frame.claims.length + ' of ' + map.Resources.length +
          ' resource' + (map.Resources.length === 1 ? '' : 's') + ' claimed.';
      }
    } else {
      sentence = describeFrameAgentSide(traj, index, last, frame, map, fog);
    }

    if (index === last && traj.final && traj.final.Metrics) {
      const fin = traj.final.Metrics;
      const winner = fin.WinnerAgentId !== null && fin.WinnerAgentId !== undefined
        ? ': ' + roleLabel(fin.WinnerAgentId) + ' wins' : '';
      const scores = Array.isArray(fin.FinalScores) && fin.FinalScores.length
        ? ' ' + fin.FinalScores.join('–') : '';
      sentence += ' Recording ends at tick ' + fin.TotalSteps + ' (' + fin.Reason + ')' + winner + scores + '.';
    }

    return 'Recorded frame · tick ' + index + ' of ' + last + ' — ' + sentence;
  }

  function describeFrameAgentSide(traj, index, last, frame, map, fog) {
    if (!frame.agents || !frame.agents.length) {
      return 'This recorded frame carries no agent states.';
    }
    const skel = fog;
    if (!skel) {
      return 'This recording carries no decision-time perception for this frame, so nothing is ' +
        'masked: ' + frame.agents.length + ' agent(s) shown as recorded.';
    }
    let observed = 0, stale = 0, unknown = 0;
    map.Zones.forEach(function (z) {
      if (skel.zones[z.Id] === 'observed') observed += 1;
      else if (skel.zones[z.Id] === 'stale') stale += 1;
      else unknown += 1;
    });
    let s = (skel.source === 'recorded'
      ? 'Recorded at tick ' + skel.tick + ', what ' + roleLabel(state.egoId) + ' perceived when it chose: '
      : 'Derived by this page, what ' + roleLabel(state.egoId) + ' could reach: ') +
      observed + ' of ' + map.Zones.length + ' rooms observed, ' +
      stale + ' last known, ' + unknown + ' unexplored';
    const rivals = describeRivalSight(skel, index);
    s += '; ' + rivals + '.';
    if (skel.source === 'recorded' && !skel.fresh) {
      s += ' This is the last decision-time view in the recording: the episode ended at tick ' +
        skel.tick + ', so no decision was made from this frame.';
    }
    return s;
  }

  function updateSentence(fog) {
    const text = describeFrame(fog);
    if (dom.sentence.textContent !== text) {
      dom.sentence.textContent = text;
    }
    // Polite live region while the user drives; mute during autoplay so a
    // screen reader is not spammed on every tick.
    dom.sentence.setAttribute('aria-live', state.playing ? 'off' : 'polite');
  }

  /* ----------------------------------------------------------- provenance  */

  function renderProvenance(traj, fileName, presetKey) {
    const hdr = traj.header;
    const cfg = hdr.SimulationConfig || {};
    const isBuiltIn = !!(presetKey && PRESETS[presetKey]);
    const schema = typeof hdr.SchemaVersion === 'number' ? hdr.SchemaVersion : 0;

    const rows = [
      ['Recording', fileName + (isBuiltIn ? ' (built-in)' : ' (local file)')],
      ['Seed', String(hdr.Seed)],
      ['Scenario', hdr.Scenario ? hdr.Scenario : 'sampling run (no scenario)'],
      ['Agent roles', hdr.AgentRoles && hdr.AgentRoles.length ? hdr.AgentRoles.join(' vs ') : 'not recorded in the header'],
      ['Wire schema', 'v' + schema + (schema === 0 ? ' — recorded before the current v2 stamp' : '')],
      ['Ticks', String(Math.max(0, traj.frames.length - 1)) + ' recorded'],
      ['Config', 'agents ' + (cfg.AgentCount !== undefined ? cfg.AgentCount : '?') +
        ' · max ticks ' + (cfg.MaxTicks !== undefined ? cfg.MaxTicks : '?') +
        ' · vision ' + (cfg.Vision !== undefined ? cfg.Vision : '?')],
      ['Rooms / resources / gates',
        hdr.Map.Zones.length + ' / ' + hdr.Map.Resources.length + ' / ' + hdr.Map.ChokePoints.length],
      ['Decision-time perception', traj.hasPerceptions
        ? 'recorded — each agent\'s own view at its decision, cone per agent ' +
          ((hdr.AgentVision || []).join(' / ')) + ' hops'
        : 'not recorded — the agent-view sightline is reconstructed by this page'],
    ];

    let html = '';
    rows.forEach(function (row) {
      html += '<dt>' + row[0] + '</dt><dd>' + row[1] + '</dd>';
    });
    dom.provGrid.innerHTML = html;

    let openHtml = '';
    if (isBuiltIn) {
      const preset = PRESETS[presetKey];
      openHtml = '<a href="./' + preset.name + '">Open ' + preset.name + '</a> · ' +
        '<a href="' + REPO_TRAJECTORY_BLOB + preset.repoPath + '">View in repository</a>';
      dom.provOpen.innerHTML = openHtml;
      dom.provRepro.textContent = preset.reproduce;
    } else {
      openHtml = '<span class="local-note">Local file — not in the repository.</span>';
      dom.provOpen.innerHTML = openHtml;
      let commands = 'dotnet run --project Cli -- replay "' + fileName + '" --verify';
      if (hdr.Scenario && !hdr.DynamicRules) {
        commands = 'dotnet run --project Cli -- simulate --seed ' + hdr.Seed +
          ' --scenario ' + hdr.Scenario +
          (cfg.MaxTicks ? ' --steps ' + cfg.MaxTicks : '') +
          ' --out verified.jsonl\n' + commands;
      } else if (hdr.DynamicRules) {
        commands = 'dotnet run --project Cli -- simulate --seed ' + hdr.Seed +
          ' --rules <recorded-rules-file> --out verified.jsonl\n' + commands;
      }
      dom.provRepro.textContent = commands;
    }
  }

  // Role legend chips, grounded in the loaded recording's roster (or the
  // viewer's default palette when the header records no roles).
  function renderLegendRoles(traj) {
    const agents = traj.frames.length ? traj.frames[0].agents : [];
    const roles = traj.header.AgentRoles || [];
    let html = '';
    agents.slice().sort(function (a, b) { return a.AgentId - b.AgentId; }).forEach(function (agent) {
      const color = agentColor(agent, roles);
      const name = roles[agent.AgentId] ? roleLabel(agent.AgentId) : 'Agent ' + agent.AgentId;
      html += '<li><span class="swatch" style="background:' + color + '"></span>' + name + '</li>';
    });
    dom.legendRoles.innerHTML = html || '<li>No agents recorded.</li>';
  }

  /* ------------------------------------------- committed results panel  */

  function fmt2(value) {
    return (value >= 0 ? '+' : '') + value.toFixed(2);
  }

  function fmtCi(lo, up) {
    return '[' + lo.toFixed(2) + ', ' + up.toFixed(2) + ']';
  }

  function loadResults() {
    dom.resultsStatus = document.getElementById('results-status');
    let pending = RESULT_ARTIFACTS.length;
    const failures = [];
    let usedFallback = 0;

    RESULT_ARTIFACTS.forEach(function (artifact) {
      fetchArtifact(artifact.url)
        .then(function (json) {
          renderResult(json, artifact, false);
        })
        .catch(function (err) {
          // The page is being viewed somewhere the pinned network revision
          // cannot be reached (offline, corporate egress, ...). Fall back to
          // the committed same-origin copy and say so honestly: the numbers
          // still come from this repository's recorded study, not from a fresh
          // simulation, but the byte-for-byte provenance links to the pinned
          // revision are unavailable from this machine.
          if (!artifact.fallbackUrl) throw err;
          return fetchArtifact(artifact.fallbackUrl).then(function (json) {
            usedFallback += 1;
            renderResult(json, artifact, true);
          });
        })
        .catch(function (err) {
          failures.push(artifact.name + '(' + err.message + ')');
        })
        .then(function () {
          afterEach();
        });
    });

    function fetchArtifact(url) {
      return fetch(url).then(function (res) {
        if (!res.ok) throw new Error('HTTP ' + res.status + ' for ' + url);
        return res.json();
      });
    }

    function afterEach() {
      pending -= 1;
      if (pending > 0) return;
      if (failures.length) {
        dom.resultsStatus.className = 'result-status error';
        dom.resultsStatus.textContent = 'Could not load ' + failures.join(', ') +
          ' from the pinned revision or its committed same-origin copy. Numbers are not shown — open the artifacts directly.';
        return;
      }
      dom.resultsStatus.className = 'result-status ok';
      let note = 'Committed values shown, loaded from the artifacts above and pinned at ' + PINNED_SHA.slice(0, 7) + '.';
      if (usedFallback) {
        note += ' ' + usedFallback + ' artifact' + (usedFallback > 1 ? 's shown from the committed ' : ' shown from the committed ') +
          'same-origin copy — the pinned network revision was not reachable from this machine, so those blob links point at the copy committed in this repository.';
      }
      dom.resultsStatus.textContent = note;
    }
  }

  function renderResult(json, artifact, fromFallback) {
    if (!json || !Array.isArray(json.Studies)) throw new Error('unexpected artifact structure');
    const dev = json.Studies.find(function (s) { return s.Suite === 'dev'; }) || json.Studies[0];
    const heldout = json.Studies.find(function (s) { return s.Suite === 'heldout'; });
    const stats = dev && dev.Statistics;
    if (!stats) throw new Error('no study statistics in artifact');

    const ids = artifact.ids;
    const link = document.getElementById(ids.link);
    // If we served the committed same-origin copy, point the link at that
    // committed file rather than the pinned-revision blob that this machine
    // could not reach — the bytes are identical, and the provenance is honest.
    link.setAttribute('href', fromFallback ? artifact.fallbackUrl : artifact.blob);

    document.getElementById(ids.proto).textContent =
      dev.TargetPolicy + ' (' + dev.RolloutsPerAction + ' rollouts/action) vs ' +
      dev.BaselinePolicy + ' · mirror-seated · max ' + dev.MaxStepsPerMatch + ' steps/match · ' + dev.Suite + ' suite';

    document.getElementById(ids.delta).textContent = fmt2(stats.MeanDelta);
    document.getElementById(ids.ci).textContent = fmtCi(stats.CiLower95, stats.CiUpper95);
    document.getElementById(ids.seeds).textContent =
      stats.Seeds + ' seeds · ' + stats.Matches + ' mirror-seated matches';

    const verdict = document.getElementById(ids.verdict);
    verdict.textContent = (dev.Passed ? 'PASS — ' : 'FAIL — ') + dev.Decision;
    verdict.className = 'result-verdict ' + (dev.Passed ? 'pass' : 'fail');

    if (heldout && heldout.Statistics) {
      const hs = heldout.Statistics;
      document.getElementById(ids.heldout).textContent = 'Held-out suite (same file): ' +
        (heldout.Passed ? 'PASS' : 'FAIL') + ' Δ ' + fmt2(hs.MeanDelta) + ' · ' + fmtCi(hs.CiLower95, hs.CiUpper95);
    }

    document.getElementById(ids.commit).textContent =
      artifact.name + ' · study recorded at ' + json.CommitSha + ' · pinned at ' + PINNED_SHA.slice(0, 7);
  }

  /* ----------------------------------------------------------- transport  */

  function stepBy(delta) {
    const traj = state.trajectory;
    if (!traj || !traj.frames.length) return;
    const last = traj.frames.length - 1;
    setIndex(Math.max(0, Math.min(last, state.index + delta)));
  }

  function setIndex(i) {
    const traj = state.trajectory;
    if (!traj || !traj.frames.length) return;
    state.index = Math.max(0, Math.min(traj.frames.length - 1, i));
    scheduleDraw();
  }

  function togglePlay() {
    if (state.playing) { pause(); return; }
    const traj = state.trajectory;
    if (!traj || !traj.frames.length || dom.playBtn.disabled) return;
    if (reducedMotionQuery.matches) {
      showMessage('Reduced-motion: autoplay is off — use +1 Tick / −1 Tick or the scrubber', false);
      return;
    }
    if (state.index >= traj.frames.length - 1) setIndex(0);
    state.playing = true;
    dom.playBtn.textContent = 'Pause';
    startTimer();
    updateTransportDisabled();
  }

  function pause() {
    state.playing = false;
    dom.playBtn.textContent = 'Play';
    stopTimer();
    updateTransportDisabled();
  }

  function startTimer() {
    stopTimer();
    state.timer = setTimeout(tick, state.cadenceMs);
  }

  function stopTimer() {
    if (state.timer !== null) {
      clearTimeout(state.timer);
      state.timer = null;
    }
  }

  function tick() {
    if (!state.playing) return;
    const traj = state.trajectory;
    if (!traj) { pause(); return; }
    if (state.index >= traj.frames.length - 1) { pause(); return; }
    state.index += 1;
    scheduleDraw();
    state.timer = setTimeout(tick, state.cadenceMs);
  }

  // A control only looks active when its action is currently available.
  function updateTransportDisabled() {
    const has = state.trajectory && state.trajectory.frames.length > 0;
    const last = has ? state.trajectory.frames.length - 1 : 0;
    dom.playBtn.disabled = !has || reducedMotionQuery.matches;
    dom.playBtn.title = reducedMotionQuery.matches
      ? 'Autoplay is disabled with reduced motion; step with +1 Tick / −1 Tick or the scrubber'
      : '';
    dom.stepBackBtn.disabled = !has || state.index === 0;
    dom.stepFwdBtn.disabled = !has || state.index >= last;
    dom.slider.disabled = !has;
  }

  /* --------------------------------------------------- radar pulse loop  */

  function startPulse() {
    stopPulse();
    if (reducedMotionQuery.matches) return;
    state.pulseStart = performance.now();
    state.pulseTimer = setInterval(function () {
      if (state.trajectory && perspectiveIsAgent()) scheduleDraw();
    }, 90);
  }

  function stopPulse() {
    if (state.pulseTimer) {
      clearInterval(state.pulseTimer);
      state.pulseTimer = null;
    }
  }

  /* --------------------------------------------------------------- misc  */

  let drawQueued = false;
  function scheduleDraw() {
    if (drawQueued) return;
    drawQueued = true;
    window.requestAnimationFrame(function () {
      drawQueued = false;
      draw();
    });
  }

  function showMessage(text, isError) {
    dom.message.className = isError ? 'error' : 'success';
    dom.message.textContent = text;
    dom.message.style.display = 'block';
    if (!isError) {
      window.clearTimeout(showMessage.timerId);
      showMessage.timerId = window.setTimeout(function () { dom.message.style.display = 'none'; }, 4000);
    }
  }

  function showDom(el) { el.removeAttribute('hidden'); }
  function hideDom(el) { el.setAttribute('hidden', ''); }

  /* ------------------------------------------------------- parsing (JSONL)  */

  function parseTrajectory(text) {
    const lines = String(text).split(/\r?\n/).map(function (l) { return l.trim(); }).filter(function (l) { return l.length > 0; });
    if (!lines.length) throw new Error('empty recording');

    const decoded = lines.map(function (line) { return JSON.parse(line); });
    if (decoded[0].Kind !== 'header') throw new Error('first line must be a header');
    if (!decoded[0].Map || !decoded[0].Map.Zones) throw new Error('header carries no Map');

    const header = {
      Seed: decoded[0].Seed,
      Map: decodeMap(decoded[0].Map),
      SimulationConfig: decoded[0].SimulationConfig || {},
      Scenario: decoded[0].Scenario || null,
      AgentRoles: decoded[0].AgentRoles || null,
      SchemaVersion: decoded[0].SchemaVersion,
      DynamicRules: decoded[0].DynamicRules || null,
      AgentVision: decoded[0].AgentVision || null,
    };
    const steps = [];
    // Schema 4 records, per step, what each agent's own perception filter
    // produced at the moment it decided. Index i is step i+1 — the decision
    // made FROM the world that frame i shows. A recording without the field
    // (every pre-schema-4 file, demo.jsonl included) leaves this null and the
    // viewer falls back to reconstructing a sightline, saying so.
    const perceptions = [];
    let hasPerceptions = false;
    let final = null;

    decoded.slice(1).forEach(function (rec) {
      if (rec.Kind === 'step') {
        steps.push(rec);
        if (rec.Perceptions && rec.Perceptions.length) {
          hasPerceptions = true;
          perceptions[steps.length - 1] = rec.Perceptions;
        }
      } else if (rec.Kind === 'final') {
        final = rec;
      }
    });

    const cfg = header.SimulationConfig;
    const agentCount = typeof cfg.AgentCount === 'number' ? cfg.AgentCount : 1;
    const zoneCount = header.Map.Zones.length;
    const initial = [];
    for (let i = 0; i < agentCount; i += 1) {
      initial.push({ AgentId: i, ZoneId: i % zoneCount, Score: 0, Transit: null });
    }

    const frames = [{ agents: initial, claims: [] }];
    steps.forEach(function (step) {
      const obs = step.Result && step.Result.Observations ? step.Result.Observations[0] : null;
      const agents = (obs && obs.AgentStates) ? obs.AgentStates : [];
      const claims = (obs && obs.Claims) ? obs.Claims : [];
      frames.push({ agents: agents, claims: claims, info: step.Result && step.Result.Info });
    });

    return {
      header: header,
      final: final,
      frames: frames,
      // Cached once per recording: the fog source is a property of the file,
      // not of the frame, and the draw path asks for it on every repaint.
      hasPerceptions: hasPerceptions,
      perceptions: hasPerceptions ? perceptions : null,
    };
  }

  function decodeMap(m) {
    const map = { Zones: [], Resources: [], ChokePoints: [] };
    m.Zones.forEach(function (z) { map.Zones.push({ Id: z.Id, Position: z.Position, MaxOccupancy: z.MaxOccupancy, Role: z.Role || null }); });
    if (m.Resources) m.Resources.forEach(function (r) { map.Resources.push({ Id: r.Id, ZoneId: r.ZoneId, Position: r.Position, Role: r.Role || null }); });
    if (m.ChokePoints) m.ChokePoints.forEach(function (c) { map.ChokePoints.push({ Id: c.Id, FromZoneId: c.FromZoneId, ToZoneId: c.ToZoneId, MaxOccupancy: c.MaxOccupancy, Role: c.Role || null }); });
    return map;
  }
})();