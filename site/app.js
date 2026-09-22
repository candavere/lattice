/* Lattice — replay viewer.
   Deterministic trajectory (JSON Lines) player rendered to a high-DPI canvas.
   Self-contained ES2017+, zero external dependencies.

   The viewer replays recorded frames only; nothing in the browser is
   recomputed from a simulation. */

(function () {
  'use strict';

  const DEFAULT_TRAJECTORY = './demo.jsonl';

  // Pinned revision the page's evidence links and result fetches target.
  // The binary/JSON artifacts are immutable at this SHA.
  const PINNED_SHA = '6463e4865dd4831efe0952d64de0f8bfaf22f9a4';

  const PRESETS = {
    demo: {
      url: './demo.jsonl',
      name: 'demo.jsonl',
      repoPath: 'site/demo.jsonl',
      staticSvg: './demo.svg',
      staticName: 'demo.svg',
      caption: 'Seed 42, MCTS (agent 0) vs Random (agent 1), 30 ticks. Recorded with lattice simulate --seed 42 --agent mcts --steps 30 and rendered as a dependency-free CSS-animated SVG with lattice render --format svg.',
      reproduce: 'dotnet run --project Cli -- simulate --seed 42 --agent mcts --steps 30 --out demo.jsonl',
    },
    infiltration: {
      url: './infiltration.jsonl',
      name: 'infiltration.jsonl',
      repoPath: 'site/infiltration.jsonl',
      staticSvg: './infiltration.svg',
      staticName: 'infiltration.svg',
      caption: 'Seed 42, Dungeon Infiltration & Sentry Patrol: the Infiltrator raids the Treasure Vault under a patrolling Sentry. Recorded with lattice simulate --seed 42 --scenario infiltration --steps 100 and rendered as an animated SVG with lattice render --format svg.',
      reproduce: 'dotnet run --project Cli -- simulate --seed 42 --scenario infiltration --steps 100 --out infiltration.jsonl',
    },
  };

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
    fogDivider: '#1e293b',
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
    dom.speedSelect = document.getElementById('speed-select');
    dom.fileInput = document.getElementById('file-input');
    dom.presetSelect = document.getElementById('preset-select');
    dom.staticSvg = document.getElementById('static-svg');
    dom.staticTitle = document.getElementById('static-title');
    dom.staticOpen = document.getElementById('static-open');
    dom.staticOpenLink = document.getElementById('static-open-link');
    dom.staticCaption = document.getElementById('static-caption');
    dom.viewGround = document.getElementById('view-ground');
    dom.viewAgent = document.getElementById('view-agent');
    dom.viewSplit = document.getElementById('view-split');
    dom.egoSelect = document.getElementById('ego-agent-select');
    dom.sentence = document.getElementById('tick-sentence');
    dom.legendRoles = document.getElementById('legend-roles');
    dom.provGrid = document.getElementById('prov-grid');
    dom.provOpen = document.getElementById('prov-open');
    dom.provRepro = document.getElementById('prov-repro');

    dom.viewGround.addEventListener('click', function () { setViewMode('ground'); });
    dom.viewAgent.addEventListener('click', function () { setViewMode('agent'); });
    dom.viewSplit.addEventListener('click', function () { setViewMode('split'); });
    dom.egoSelect.addEventListener('change', function () {
      const picked = parseInt(dom.egoSelect.value, 10);
      if (Number.isInteger(picked)) {
        state.egoId = picked;
        scheduleDraw();
      }
    });

    dom.playBtn.addEventListener('click', togglePlay);
    dom.stepBackBtn.addEventListener('click', function () { pause(); stepBy(-1); });
    dom.stepFwdBtn.addEventListener('click', function () { pause(); stepBy(+1); });
    dom.slider.addEventListener('input', function () { pause(); setIndex(Number(dom.slider.value)); });
    dom.speedSelect.addEventListener('change', function () { applyCadence(); });
    dom.fileInput.addEventListener('change', handleFileChoice);
    dom.presetSelect.addEventListener('change', handlePresetChoice);
    document.addEventListener('dragover', preventDefaultFileDrop);
    document.addEventListener('drop', handleDrop);
    window.addEventListener('resize', onViewportResize);
    if (typeof reducedMotionQuery.addEventListener === 'function') {
      reducedMotionQuery.addEventListener('change', function () {
        if (reducedMotionQuery.matches && state.playing) pause();
        updateTransportDisabled();
        scheduleDraw();
      });
    }

    refreshViewButtons();
    loadResults();
    loadDefault();
  });

  /* ------------------------------------------------------- viewer state  */

  const narrowQuery = window.matchMedia('(max-width: 640px)');
  const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');

  const state = {
    trajectory: null,   // { header, steps, final, frames[], fileName, maxTicks }
    index: 0,
    playing: false,
    timer: null,
    cadenceMs: 420,
    layouts: {},        // per-viewport-size layout cache: "WxH" -> layout
    canv: null,         // { cssW, cssH } of last fitted size
    needsDraw: false,
    viewMode: narrowQuery.matches ? 'ground' : 'split', // 'ground' | 'agent' | 'split'
    lastFocus: 'ground', // last single-view choice, preserved across breakpoints
    egoId: 0,           // observed-agent slot for the Agent view
    pulseStart: 0,      // performance.now() origin of the ego radar pulse
    pulseTimer: null,   // interval driving the radar pulse while fog is shown
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
        adoptTrajectory(traj, PRESETS.demo.name, 'demo');
        showMessage('loaded built-in demo recording (seed ' + traj.header.Seed + ')', false);
      })
      .catch(function (err) {
        dropTrajectory();
        dom.source.textContent = 'demo unavailable — drop a .jsonl recording to play';
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

  function handlePresetChoice(event) {
    const key = event.target.value;
    if (key === 'custom') return;
    loadPreset(key);
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
    populateEgoSelect(traj);
    dom.playBtn.textContent = 'Play';
    dom.slider.max = String(Math.max(0, traj.frames.length - 1));
    dom.slider.value = '0';
    dom.source.textContent = fileName;
    dom.fileInput.title = fileName;
    if (presetKey && PRESETS[presetKey]) {
      dom.presetSelect.value = presetKey;
      setStaticPreset(presetKey);
    } else {
      dom.presetSelect.value = 'custom';
      // A local file never changes the built-in static render.
    }
    const roster = traj.header.AgentRoles;
    dom.roster.textContent = traj.header.Scenario
      ? traj.header.Scenario + ' · ' + (roster && roster.length ? roster.join(' vs ') : '')
      : (roster && roster.length ? roster.join(' vs ') : '');
    hideDom(dom.hint);
    renderProvenance(traj, fileName, presetKey);
    renderLegendRoles(traj);
    if (state.viewMode !== 'ground') startPulse();
    scheduleDraw();
  }

  // The observed-agent dropdown: "Agent 0 (Sentry)" style options, defaulting
  // to the Infiltrator when the trajectory carries that roster.
  function populateEgoSelect(traj) {
    const roles = traj.header.AgentRoles || [];
    const agents = traj.frames.length ? traj.frames[0].agents : [];
    dom.egoSelect.innerHTML = '';
    agents.forEach(function (agent) {
      const option = document.createElement('option');
      const role = roles[agent.AgentId];
      option.value = String(agent.AgentId);
      option.textContent = 'Agent ' + agent.AgentId + (role ? ' (' + role + ')' : '');
      dom.egoSelect.appendChild(option);
    });
    const infiltratorIndex = roles.indexOf('Infiltrator');
    state.egoId = infiltratorIndex >= 0 ? infiltratorIndex : 0;
    dom.egoSelect.value = String(state.egoId);
    dom.egoSelect.disabled = agents.length < 2;
  }

  function setStaticPreset(key) {
    const preset = PRESETS[key];
    if (!preset) return;
    dom.staticSvg.data = preset.staticSvg;
    dom.staticCaption.innerHTML = preset.caption;
    dom.staticTitle.textContent = 'Static render of ' + preset.name;
    dom.staticOpenLink.textContent = 'Open ' + preset.staticName;
    dom.staticOpenLink.href = preset.staticSvg;
    dom.staticOpen.textContent = 'Open ' + preset.staticName + ' directly';
    dom.staticOpen.href = preset.staticSvg;
  }

  /* ----------------------------------------------------------- view mode  */

  function onViewportResize() {
    refreshViewButtons();
    scheduleDraw();
  }

  // The view actually rendered: on narrow screens a chosen 'split' mode
  // degrades to the user's last single-view choice rather than trapping them.
  function effectiveView() {
    if (state.viewMode === 'split' && narrowQuery.matches) return state.lastFocus;
    return state.viewMode;
  }

  function setViewMode(mode) {
    if (mode === 'ground' || mode === 'agent') state.lastFocus = mode;
    state.viewMode = mode;
    refreshViewButtons();
    if (effectiveView() === 'ground') stopPulse(); else startPulse();
    scheduleDraw();
  }

  function setPressed(el, isPressed) {
    el.classList.toggle('active', isPressed);
    el.setAttribute('aria-pressed', isPressed ? 'true' : 'false');
  }

  function refreshViewButtons() {
    const eff = effectiveView();
    setPressed(dom.viewGround, eff === 'ground');
    setPressed(dom.viewAgent, eff === 'agent');
    setPressed(dom.viewSplit, state.viewMode === 'split');
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
    dom.egoSelect.innerHTML = '';
    dom.egoSelect.disabled = true;
    dom.legendRoles.innerHTML = '';
    dom.provGrid.innerHTML = '';
    dom.provOpen.innerHTML = '';
    dom.provRepro.innerHTML = '';
    dom.sentence.textContent = 'Load a recording to see its frames described here.';
    updateTransportDisabled();
    clearCanvas();
    showDom(dom.hint);
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

    const pad = 46;
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
    if (!traj || !traj.frames.length) return;

    const frame = traj.frames[state.index];
    const w = dom.canvas.clientWidth;
    const h = dom.canvas.clientHeight;
    const view = effectiveView();

    if (view === 'split') {
      const split = Math.floor(w / 2);
      drawViewport(ctx, traj, frame, { x: 0, y: 0, w: split - 5, h: h }, null, 'Ground truth');
      ctx.strokeStyle = COLORS.fogDivider;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(split, 6);
      ctx.lineTo(split, h - 6);
      ctx.stroke();
      drawViewport(ctx, traj, frame, { x: split + 5, y: 0, w: w - split - 5, h: h },
        computePerception(traj, state.index, state.egoId),
        'Agent view · ' + egoLabel(traj));
    } else if (view === 'agent') {
      drawViewport(ctx, traj, frame, { x: 0, y: 0, w: w, h: h },
        computePerception(traj, state.index, state.egoId),
        'Agent view · ' + egoLabel(traj));
    } else {
      drawViewport(ctx, traj, frame, { x: 0, y: 0, w: w, h: h }, null, 'Ground truth');
    }

    renderStatus();
    renderMetrics();
    updateSentence();
    updateTransportDisabled();
  }

  function egoLabel(traj) {
    const roles = traj.header.AgentRoles;
    return roles && roles[state.egoId]
      ? roles[state.egoId].toUpperCase()
      : 'AGENT ' + state.egoId;
  }

  // One rectangular viewport: its own layout, its own fog policy, and an
  // optional header badge identifying what it shows.
  function drawViewport(ctx, traj, frame, region, fog, badge) {
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

    if (badge) {
      ctx.font = 'bold 10px monospace';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      const tw = ctx.measureText(badge).width;
      ctx.fillStyle = 'rgba(15, 23, 42, 0.85)';
      ctx.fillRect(region.x + 8, region.y + 6, tw + 14, 18);
      ctx.strokeStyle = COLORS.fogDivider;
      ctx.strokeRect(region.x + 8, region.y + 6, tw + 14, 18);
      ctx.fillStyle = fog ? COLORS.extraction : COLORS.gateText;
      ctx.fillText(badge, region.x + 15, region.y + 15);
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

      // Gate badge: a compact capsule on the corridor, never a circle that
      // could be mistaken for a room. Hidden while either side is unexplored.
      if (limited && !anyUnknown) {
        const midX = (ends.ax + ends.bx) / 2;
        const midY = (ends.ay + ends.by) / 2;
        const label = choke.MaxOccupancy === 0 ? 'LOCKED' : 'CAP ' + choke.MaxOccupancy;
        const bw = label.length * 6.4 + 14;
        roundedRect(ctx, midX - bw / 2, midY - 9, bw, 18, 9);
        ctx.fillStyle = COLORS.gateBg;
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
    });
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

      // Room title strip.
      ctx.fillStyle = status === 'stale' ? COLORS.fogText : COLORS.roomText;
      ctx.font = 'bold 11px monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(roomLabel(zone), rect.x, rect.y - 6);

      if (status === 'observed') {
        // Occupancy badge: a clear fraction when capped, a plain count when not.
        const present = occupied[zone.Id] || 0;
        const capped = zone.MaxOccupancy !== UNLIMITED && zone.MaxOccupancy < UNLIMITED;
        const occ = capped ? present + '/' + zone.MaxOccupancy : String(present);
        ctx.font = '9px monospace';
        const bw = occ.length * 6.4 + 12;
        roundedRect(ctx, rect.x + rect.hw - bw - 5, rect.y - rect.hh + 4, bw, 13, 6);
        ctx.fillStyle = present > 0 ? 'rgba(122, 162, 247, 0.25)' : 'rgba(148, 163, 184, 0.15)';
        ctx.fill();
        ctx.fillStyle = COLORS.mutedText;
        ctx.fillText(occ, rect.x + rect.hw - bw / 2 - 5, rect.y - rect.hh + 10.5);
      } else {
        ctx.font = '8px monospace';
        ctx.fillStyle = COLORS.fogText;
        ctx.fillText('last known', rect.x, rect.y + 8);
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
        const t = Math.min(1, Math.max(0, (total - agent.Transit.RemainingTicks + 1) / total));
        if (ends) {
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
        y = rect.y + 2;
      }

      const color = agentColor(agent, roles);
      const role = roles && roles[agent.AgentId];

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

      ctx.font = '8.5px monospace';
      ctx.fillStyle = color;
      const label = (role ? role + ' ' : '') + '· ' + agent.Score;
      ctx.fillText(label, x, y + layout.rAgent + 10);

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
    const x = rect.x;
    const y = rect.y + 2;

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
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const age = state.index - ghost.tick;
    ctx.fillText('last seen ' + (age === 0 ? 'now' : age + 't ago'), x, y + layout.rAgent + 10);
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
    const view = effectiveView();
    const map = traj.header.Map;
    let sentence;

    if (view === 'split') {
      // A split is the comparison; describe the agent's side, the part that
      // differs from the ground truth on the left.
      sentence = describeFrameAgentSide(traj, index, last, frame, map);
    } else if (view === 'ground') {
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
    const traj = state.trajectory;
    const text = describeFrame();
    if (dom.sentence.textContent !== text) {
      dom.sentence.textContent = text;
    }
    // Polite live region while the user drives; mute during autoplay so a
    // screen reader is not spammed on every tick.
    dom.sentence.setAttribute('aria-live', state.playing ? 'off' : 'polite');
    const view = effectiveView();
    dom.canvas.setAttribute('aria-label', 'Replay view: ' +
      (view === 'split' ? 'side-by-side comparison of ground truth and agent view' :
        view === 'agent' ? 'Agent view — ' + egoLabel(traj) : 'Ground truth') +
      ', tick ' + state.index + ' of ' + (traj ? traj.frames.length - 1 : 0));
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

  function applyCadence() {
    const value = parseInt(dom.speedSelect.value, 10);
    if (isFinite(value) && value > 0) state.cadenceMs = value;
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
      if (state.trajectory && effectiveView() !== 'ground') scheduleDraw();
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

    return { header: header, steps: steps, final: final, frames: frames, maxTicks: cfg.MaxTicks || 0 };
  }

  function decodeMap(m) {
    const map = { Zones: [], Resources: [], ChokePoints: [] };
    m.Zones.forEach(function (z) { map.Zones.push({ Id: z.Id, Position: z.Position, MaxOccupancy: z.MaxOccupancy, Role: z.Role || null }); });
    if (m.Resources) m.Resources.forEach(function (r) { map.Resources.push({ Id: r.Id, ZoneId: r.ZoneId, Position: r.Position, Role: r.Role || null }); });
    if (m.ChokePoints) m.ChokePoints.forEach(function (c) { map.ChokePoints.push({ Id: c.Id, FromZoneId: c.FromZoneId, ToZoneId: c.ToZoneId, MaxOccupancy: c.MaxOccupancy, Role: c.Role || null }); });
    return map;
  }
})();