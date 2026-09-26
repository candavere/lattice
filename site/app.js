/* Lattice — replay viewer.
   Deterministic trajectory (JSON Lines) player rendered to a high-DPI canvas.
   Self-contained ES2017+, zero external dependencies.

   The viewer replays recorded frames only; nothing in the browser is
   recomputed from a simulation. */

(function () {
  'use strict';

  // The guided hero is the infiltration recording (Sentry vs Infiltrator): its
  // "moment to watch" (ticks 9, 19 and 20, the Vault drifting out of the
  // Infiltrator's reconstructed 2-hop sightline) is what the hero copy walks a
  // visitor through. The demo.jsonl (MCTS card) stays reachable from the Preset
  // menu.
  const DEFAULT_TRAJECTORY = './infiltration.jsonl';

  // Pinned revision the page's evidence links and result fetches target.
  // The binary/JSON artifacts are immutable at this SHA.
  const PINNED_SHA = '6463e4865dd4831efe0952d64de0f8bfaf22f9a4';

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
  const measureProbe = {
    on: false,
    labels: {},         // zoneId -> {x,y,w,h}
    tokens: {},         // agentId -> {x,y,r}
    doors: {},          // chokeId -> {x,y,w,h,edge}
    statusByZone: {},   // zoneId -> 'observed' | 'stale' | 'unknown' (per this draw)
  };

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
      updateMapCaption();
      if (perspectiveIsAgent() !== wasAgent) {
        if (perspectiveIsAgent()) startPulse(); else stopPulse();
      }
      scheduleDraw();
      if (perspectiveIsAgent() && !opts.noFade) fadeCanvas();
      updateCanvasLabel();
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

  // The one caption line under the map changes with the perspective chip.
  function updateMapCaption() {
    if (!dom.mapCaption) return;
    const traj = state.trajectory;
    let text;
    if (!traj) {
      text = 'Ground truth: everything in the world.';
    } else if (!perspectiveIsAgent()) {
      text = 'Ground truth: everything in the world.';
    } else {
      const role = traj.header.AgentRoles && traj.header.AgentRoles[state.egoId];
      const name = role || 'Agent ' + state.egoId;
      text = 'What the ' + name + ' could reach (reconstructed 2-hop sightline).';
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

  function updateCanvasLabel() {
    const traj = state.trajectory;
    const label = perspectiveIsAgent()
      ? 'Replay view — ' + egoLabel(traj) + "'s reconstructed 2-hop sightline" +
        ', tick ' + state.index + ' of ' + (traj ? traj.frames.length - 1 : 0)
      : 'Replay view — Ground truth, tick ' + state.index +
        ' of ' + (traj ? traj.frames.length - 1 : 0);
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

    const points = [];
    zones.forEach(function (z) { points.push(z.Position); });
    resources.forEach(function (r) { points.push(r.Position); });
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

    // Rooms are label-sized in screen pixels while corridors scale with the
    // fitted transform, so the margin must reserve the widest possible room
    // half-width or the outermost rooms get clipped at the canvas edge.
    const maxRoomHalf = zones.reduce(function (largest, z) {
      const half = Math.max(ROOM_MIN_WIDTH, roomLabel(z).length * 7.4 + 26) / 2;
      return Math.max(largest, half);
    }, 0);
    const pad = Math.max(46, Math.ceil(maxRoomHalf) + 24);
    const spanX = Math.max(1, maxX - minX);
    const spanY = Math.max(1, maxY - minY);
    const fitW = Math.max(1, w - 2 * pad);
    const fitH = Math.max(1, h - 2 * pad);
    const sx = fitW / spanX;
    const sy = fitH / spanY;
    const s = Math.min(sx, sy);
    const offX = (w - s * spanX) / 2;
    const offY = (h - s * spanY) / 2;

    const layout = {
      minX: minX, minY: minY, spanX: spanX, spanY: spanY, s: s, offX: offX, offY: offY,
      w: w, h: h,
      rAgent: Math.max(7, Math.min(11, 9 * s / 60)),
    };
    state.layouts[w + 'x' + h] = layout;
    return layout;
  }

  function px(pos, layout) {
    return { x: layout.offX + (pos.X - layout.minX) * layout.s, y: layout.offY + (pos.Y - layout.minY) * layout.s };
  }

  function draw() {
    if (!fitCanvas()) return;
    const ctx = dom.canvas.getContext('2d');
    const traj = state.trajectory;

    clearCanvas();
    measureProbe.labels = {};
    measureProbe.tokens = {};
    measureProbe.doors = {};
    measureProbe.statusByZone = {};
    if (!traj || !traj.frames.length) return;

    const frame = traj.frames[state.index];
    const w = dom.canvas.clientWidth;
    const h = dom.canvas.clientHeight;

    // One map. The perspective chip decides whether we draw the ground truth
    // or the observed agent's fog-of-war over the same fitted layout.
    const fog = perspectiveIsAgent()
      ? computePerception(traj, state.index, state.egoId)
      : null;
    drawViewport(ctx, traj, frame, { x: 0, y: 0, w: w, h: h }, fog);

    renderStatus();
    renderMetrics();
    updateSentence();
    updateMapCaption();
    updateCanvasLabel();
    updateTransportDisabled();
    publishMeasureProbe();
  }

  function egoLabel(traj) {
    const roles = traj.header.AgentRoles;
    return roles && roles[state.egoId]
      ? roles[state.egoId].toUpperCase()
      : 'AGENT ' + state.egoId;
  }

  // One full-canvas viewport: a fitted layout and an optional fog policy.
  // No in-canvas header badge — the "RECONSTRUCTED SIGHTLINE" label is the
  // CSS chip on the viewer wrapper, and the caption below the map re-states it.
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
    const found = hitDoor(event.offsetX, event.offsetY, regions);
    if (found !== state.hoverDoorId) {
      state.hoverDoorId = found;
      scheduleDraw();
      if (found !== null) dom.canvas.style.cursor = 'pointer';
      else dom.canvas.style.cursor = '';
    }
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

  function visionHops(traj) {
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

  // Cumulative discovery up to the scrubbed tick: what the observer sees now
  // (Observed), what it remembers (Stale), and what it has never reached
  // (Unknown) — plus ghost memories of rival agents last spotted.
  function computePerception(traj, index, egoId) {
    const map = traj.header.Map;
    const vision = visionHops(traj);
    const egoZone = function (frame) {
      const ego = frame.agents.find(function (a) { return a.AgentId === egoId; }) || frame.agents[0];
      return ego.Transit ? ego.Transit.ToZoneId : ego.ZoneId;
    };

    const lastSeenAt = {};
    const ghosts = {};
    for (let ti = 0; ti <= index; ti += 1) {
      const frame = traj.frames[ti];
      const seen = observedZones(map, egoZone(frame), vision);
      Object.keys(seen).forEach(function (zoneId) { lastSeenAt[zoneId] = ti; });
      frame.agents.forEach(function (agent) {
        if (agent.AgentId === egoId) return;
        if (seen[agent.ZoneId] !== undefined) {
          ghosts[agent.AgentId] = { state: agent, tick: ti };
        }
      });
    }

    const current = observedZones(map, egoZone(traj.frames[index]), vision);
    const zones = {};
    map.Zones.forEach(function (zone) {
      zones[zone.Id] = current[zone.Id] !== undefined
        ? 'observed'
        : lastSeenAt[zone.Id] !== undefined ? 'stale' : 'unknown';
    });

    return { vision: vision, egoId: egoId, zones: zones, ghosts: ghosts };
  }

  function zoneStatus(fog, zoneId) {
    return fog ? fog.zones[zoneId] || 'unknown' : 'observed';
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
     trimmed to the room borders so lines never pierce the cards. */

  const ROOM_HEIGHT = 52;
  const ROOM_MIN_WIDTH = 78;

  function roomLabel(zone) {
    return zone.Role ? spaceCamel(zone.Role) : 'Room ' + zone.Id;
  }

  function spaceCamel(text) {
    return String(text).replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  function roomRect(zone, layout) {
    const center = px(zone.Position, layout);
    const width = Math.max(ROOM_MIN_WIDTH, roomLabel(zone).length * 7.4 + 26);
    return { x: center.x, y: center.y, hw: width / 2, hh: ROOM_HEIGHT / 2 };
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

    // Drop a hover that no longer targets a door (e.g. door left the view).
    if (state.hoverDoorId !== null && !regions[state.hoverDoorId]) {
      state.hoverDoorId = null;
    }
  }

  // Loot sits inside its room; never floating in open canvas space. The
  // caller passes the current claim set so claimed items dim to green.
  function drawResources(ctx, map, frame, layout, fog) {
    const claimed = {};
    frame.claims.forEach(function (id) { claimed[id] = true; });

    const byZone = {};
    map.Resources.forEach(function (res) {
      if (!byZone[res.ZoneId]) byZone[res.ZoneId] = [];
      byZone[res.ZoneId].push(res);
    });

    map.Zones.forEach(function (zone) {
      const items = byZone[zone.Id];
      if (!items || !items.length) return;
      const status = zoneStatus(fog, zone.Id);
      if (status === 'unknown') return; // unexplored rooms reveal nothing
      const rect = roomRect(zone, layout);
      const y = rect.y + rect.hh - 8;
      const spacing = 14;
      // Anchor the loot row to the room's lower-left corner so it never
      // collides with the centered agent tokens and score labels.
      const startX = rect.x - rect.hw + 9;
      ctx.globalAlpha = status === 'stale' ? 0.35 : 1;
      items.forEach(function (res, i) {
        drawDiamond(ctx, startX + i * spacing, y, 5, claimed[res.Id] ? COLORS.claimed : COLORS.unclaimed);
      });
      ctx.globalAlpha = 1;
    });
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
      if (measureProbe.on) measureProbe.statusByZone[zone.Id] = status;

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
      // tokens, occupancy) can never sit on top of the room name.
      ctx.fillStyle = status === 'stale' ? COLORS.fogText : COLORS.roomText;
      ctx.font = 'bold 11px monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const label = roomLabel(zone);
      const labelY = rect.y - rect.hh + 13;
      ctx.fillText(label, rect.x, labelY);
      if (measureProbe.on) {
        const lw = ctx.measureText(label).width;
        measureProbe.labels[zone.Id] = { x: rect.x - lw / 2, y: labelY - 6, w: lw, h: 12 };
      }

      // Occupancy badge: a clear fraction when capped, a plain count when
      // not. Top-right when it clears the room name; otherwise bottom-right,
      // where the loot row (bottom-left) leaves it unimpeded.
      if (status === 'observed') {
        const present = occupied[zone.Id] || 0;
        const capped = zone.MaxOccupancy !== UNLIMITED && zone.MaxOccupancy < UNLIMITED;
        const occ = capped ? present + '/' + zone.MaxOccupancy : String(present);
        ctx.font = '9px monospace';
        const bw = occ.length * 6.4 + 12;
        const lw = ctx.measureText(label).width;
        const collide = rect.x + rect.hw - bw - 5 < rect.x + lw / 2 + 4;
        const bx = rect.x + rect.hw - bw - 5;
        const by = collide
          ? rect.y + rect.hh - 8
          : rect.y - rect.hh + 4;
        roundedRect(ctx, bx, by, bw, 13, 6);
        ctx.fillStyle = present > 0 ? 'rgba(122, 162, 247, 0.25)' : 'rgba(148, 163, 184, 0.15)';
        ctx.fill();
        ctx.fillStyle = COLORS.mutedText;
        ctx.fillText(occ, bx + bw / 2, by + 6.5);
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

  function drawAgents(ctx, map, frame, layout, fog) {
    const roles = state.trajectory ? state.trajectory.header.AgentRoles : null;
    const cfg = state.trajectory ? state.trajectory.header.SimulationConfig : null;
    const pool = frame.agents.slice().sort(function (a, b) { return a.AgentId - b.AgentId; });
    const hopRadius = meanCorridorLength(map, layout) * 0.85;

    // Co-located agents fan out horizontally inside the room.
    const stationedTotal = {};
    const stationedSeen = {};
    pool.forEach(function (agent) {
      if (!agent.Transit) stationedTotal[agent.ZoneId] = (stationedTotal[agent.ZoneId] || 0) + 1;
    });

    pool.forEach(function (agent) {
      // Fog rule: the observer is real-time; rivals render only when their
      // room is inside the current horizon, otherwise as a memory ghost.
      const isEgo = fog && agent.AgentId === fog.egoId;
      if (fog && !isEgo && zoneStatus(fog, agent.ZoneId) !== 'observed') {
        drawGhost(ctx, map, frame, layout, fog, agent, roles);
        return;
      }

      let x = 0, y = 0;
      if (agent.Transit) {
        // Snap strictly to the corridor vector: P(t) = A + t·(B − A), t from
        // the engine's remaining-ticks countdown.
        const zoneA = map.Zones[agent.Transit.FromZoneId];
        const zoneB = map.Zones[agent.Transit.ToZoneId];
        const ends = corridorEndpoints(roomRect(zoneA, layout), roomRect(zoneB, layout));
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
          const q = roomRect(zoneA, layout);
          x = q.x; y = q.y;
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
        y = rect.y + rect.hh - 10 - layout.rAgent;
      }

      const color = agentColor(agent, roles);
      const role = roles && roles[agent.AgentId];

      if (measureProbe.on) {
        measureProbe.tokens[agent.AgentId] = { x: x, y: y, r: layout.rAgent };
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

      // Role/score sits beside the token, never underneath it, so a small
      // room still shows the room name above and loot below unobscured.
      ctx.font = '8.5px monospace';
      ctx.fillStyle = color;
      ctx.textAlign = 'left';
      const label = (role ? role + ' ' : '') + '· ' + agent.Score;
      ctx.fillText(label, x + layout.rAgent + 5, y);
      ctx.textAlign = 'center';

      if (agent.Transit) {
        ctx.font = '8px monospace';
        ctx.fillStyle = COLORS.transitRing;
        ctx.fillText('crossing to ' + map.Zones[agent.Transit.ToZoneId].Id + ' (' + agent.Transit.RemainingTicks + 't)', x, y - layout.rAgent - 8);
      }
    });
  }

  // A stale memory of a rival: translucent token at the zone where the
  // observer last saw it, labeled with the age of that sighting.
  function drawGhost(ctx, map, frame, layout, fog, agent, roles) {
    const ghost = fog.ghosts[agent.AgentId];
    if (!ghost) return; // never spotted — nothing to remember
    const zone = map.Zones[ghost.state.ZoneId];
    if (!zone) return;
    const rect = roomRect(zone, layout);
    // Ghost tokens sit on the same bottom band as live tokens, clear of the
    // room title strip.
    const x = rect.x;
    const y = rect.y + rect.hh - 10 - layout.rAgent;

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
    ctx.font = '8px monospace';
    ctx.fillStyle = COLORS.fogText;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const age = state.index - ghost.tick;
    ctx.fillText('last seen ' + (age === 0 ? 'now' : age + 't ago'), x + layout.rAgent + 5, y);
    ctx.textAlign = 'center';
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

  function renderMetrics() {
    const traj = state.trajectory;
    if (!traj) return;
    const frame = traj.frames[state.index];
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

    const inView = rivalIds.filter(function (a) { return skel.zones[a.ZoneId] === 'observed'; });
    const clauses = [];
    if (inView.length) {
      clauses.push('sees ' + inView.map(function (a) { return roleLabel(a.AgentId); }).join(' and '));
    }
    Object.keys(skel.ghosts).forEach(function (id) {
      const ghost = skel.ghosts[id];
      const age = index - ghost.tick;
      clauses.push('last saw ' + roleLabel(Number(id)) + (age === 0 ? ' moments ago' : ' ' + age + ' ticks ago'));
    });
    if (!clauses.length) clauses.push('no rivals in view');
    return clauses.join('; ');
  }

  function describeFrame() {
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
      sentence = describeFrameAgentSide(traj, index, last, frame, map);
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

  function describeFrameAgentSide(traj, index, last, frame, map) {
    if (!frame.agents || !frame.agents.length) {
      return 'This recorded frame carries no agent states.';
    }
    const skel = computePerception(traj, index, state.egoId);
    let observed = 0, stale = 0, unknown = 0;
    map.Zones.forEach(function (z) {
      if (skel.zones[z.Id] === 'observed') observed += 1;
      else if (skel.zones[z.Id] === 'stale') stale += 1;
      else unknown += 1;
    });
    let s = 'From ' + roleLabel(state.egoId) + "'s recorded position: " +
      observed + ' of ' + map.Zones.length + ' rooms observed, ' +
      stale + ' last known, ' + unknown + ' unexplored';
    const rivals = describeRivalSight(skel, index);
    s += '; ' + rivals + '.';
    return s;
  }

  function updateSentence() {
    const text = describeFrame();
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
        '<a href="https://github.com/candavere/lattice/blob/' + PINNED_SHA + '/' + preset.repoPath + '">View in repository</a>';
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
    };
    const steps = [];
    let final = null;

    decoded.slice(1).forEach(function (rec) {
      if (rec.Kind === 'step') {
        steps.push(rec);
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

    return { header: header, final: final, frames: frames };
  }

  function decodeMap(m) {
    const map = { Zones: [], Resources: [], ChokePoints: [] };
    m.Zones.forEach(function (z) { map.Zones.push({ Id: z.Id, Position: z.Position, MaxOccupancy: z.MaxOccupancy, Role: z.Role || null }); });
    if (m.Resources) m.Resources.forEach(function (r) { map.Resources.push({ Id: r.Id, ZoneId: r.ZoneId, Position: r.Position, Role: r.Role || null }); });
    if (m.ChokePoints) m.ChokePoints.forEach(function (c) { map.ChokePoints.push({ Id: c.Id, FromZoneId: c.FromZoneId, ToZoneId: c.ToZoneId, MaxOccupancy: c.MaxOccupancy, Role: c.Role || null }); });
    return map;
  }
})();