/* Lattice — replay viewer.
   Deterministic trajectory (JSON Lines) player rendered to a high-DPI canvas.
   Self-contained ES2017+, zero external dependencies. */

(function () {
  'use strict';

  const DEFAULT_TRAJECTORY = './demo.jsonl';
  const PRESETS = {
    demo: { url: './demo.jsonl', name: 'demo.jsonl', staticSvg: './demo.svg', caption: 'Seed 42, MCTS vs Random, 30 ticks. Recorded with lattice simulate and rendered as a dependency-free CSS-animated SVG with lattice render --format svg.' },
    infiltration: { url: './infiltration.jsonl', name: 'infiltration.jsonl', staticSvg: './infiltration.svg', caption: 'Seed 42, Dungeon Infiltration & Sentry Patrol: the Infiltrator raids the Treasure Vault under a patrolling Sentry. Recorded with lattice simulate --scenario infiltration and rendered as an animated SVG with lattice render --format svg.' },
  };
  const UNLIMITED = 2147483647; // MapLimits.Unlimited, as serialized by the writer
  const TRANSIT_FRACTION = 0.5;  // where a transiting token is drawn between its endpoints

  const AGENT_PALETTE = ['#F7768E', '#BB9AF7', '#73DACA', '#FF9E64'];

  const COLORS = {
    edge: '#8A8678',
    edgeAtBurst: '#F6C177',
    zoneFill: '#10131F',
    zoneStroke: '#7AA2F7',
    zoneText: '#C0CAF5',
    mutedText: '#94A3B8',
    unclaimed: '#E0AF68',
    claimed: '#3DA66B',
    agentRim: '#FFFFFF',
    transitRing: '#F6C177',
    badgeBg: '#16233F',
    badgeText: '#E2E8F0',
    telltale: 'rgba(15, 23, 42, 0.55)',
  };

  /* ---------------------------------------------------------------- DOM  */

  const dom = {};
  document.addEventListener('DOMContentLoaded', function () {
    dom.canvas = document.getElementById('viewer-canvas');
    dom.hint = document.getElementById('viewer-hint');
    dom.slider = document.getElementById('scrub-slider');
    dom.tickReadout = document.getElementById('tick-readout');
    dom.source = document.getElementById('source-label');
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
    dom.staticCaption = document.getElementById('static-caption');

    dom.playBtn.addEventListener('click', togglePlay);
    dom.stepBackBtn.addEventListener('click', function () { pause(); stepBy(-1); });
    dom.stepFwdBtn.addEventListener('click', function () { pause(); stepBy(+1); });
    dom.slider.addEventListener('input', function () { pause(); setIndex(Number(dom.slider.value)); });
    dom.speedSelect.addEventListener('change', function () { applyCadence(); });
    dom.fileInput.addEventListener('change', handleFileChoice);
    dom.presetSelect.addEventListener('change', handlePresetChoice);
    document.addEventListener('dragover', preventDefaultFileDrop);
    document.addEventListener('drop', handleDrop);
    window.addEventListener('resize', scheduleDraw);

    loadDefault();
  });

  /* ------------------------------------------------------- viewer state  */

  const state = {
    trajectory: null,   // { header, steps, final, frames[], fileName, maxTicks }
    index: 0,
    playing: false,
    timer: null,
    cadenceMs: 420,
    layout: null,       // { minX, minY, spanX, spanY, sx, sy, offX, offY, rZone, rAgent }
    canv: null,         // { cssW, cssH } of last fitted size
    needsDraw: false,
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
        adoptTrajectory(traj, 'demo.jsonl');
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
    stopTimer();
    dom.playBtn.textContent = 'Play';
    dom.slider.max = String(Math.max(0, traj.frames.length - 1));
    dom.slider.value = '0';
    dom.source.textContent = fileName;
    dom.fileInput.title = fileName;
    if (presetKey && PRESETS[presetKey]) {
      dom.presetSelect.value = presetKey;
      dom.staticSvg.data = PRESETS[presetKey].staticSvg;
      dom.staticCaption.innerHTML = PRESETS[presetKey].caption;
    } else {
      dom.presetSelect.value = 'custom';
    }
    const roster = traj.header.AgentRoles;
    dom.roster.textContent = traj.header.Scenario
      ? traj.header.Scenario + ' · ' + (roster && roster.length ? roster.join(' vs ') : '')
      : '';
    hideDom(dom.hint);
    scheduleDraw();
  }

  function dropTrajectory() {
    stopTimer();
    state.trajectory = null;
    state.playing = false;
    state.layout = null;
    dom.agentsBody.innerHTML = '';
    dom.zonesBody.innerHTML = '';
    dom.terminal.textContent = '';
    dom.tickReadout.textContent = '—';
    dom.statClaimed.textContent = '—';
    dom.statSteps.textContent = '—';
    dom.statSeed.textContent = '—';
    dom.statAgents.textContent = '—';
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

  function ensureLayout(traj) {
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

    if (allZero) {
      const cx = dom.canvas.clientWidth / 2;
      const cy = dom.canvas.clientHeight / 2;
      const radius = Math.min(cx, cy) * 0.7;
      zones.forEach(function (zone, idx) {
        const angle = (2 * Math.PI * idx) / zones.length - Math.PI / 2;
        zone.Position = {
          X: Math.round(cx + radius * Math.cos(angle)),
          Y: Math.round(cy + radius * Math.sin(angle)),
        };
      });
      resources.forEach(function (res, idx) {
        res.Position = {
          X: zones[res.ZoneId % zones.length].Position.X,
          Y: zones[res.ZoneId % zones.length].Position.Y,
        };
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

    const w = dom.canvas.clientWidth, h = dom.canvas.clientHeight;
    const cached = state.layout;
    if (cached && cached.w === w && cached.h === h && cached.minX === minX && cached.maxX === maxX &&
        cached.minY === minY && cached.maxY === maxY) {
      return cached;
    }

    const pad = 46;
    const spanX = Math.max(1, maxX - minX);
    const spanY = Math.max(1, maxY - minY);
    const regionW = Math.max(1, w - 2 * pad);
    const regionH = Math.max(1, h - 2 * pad);
    const sx = regionW / spanX;
    const sy = regionH / spanY;
    const s = Math.min(sx, sy);
    const offX = (w - s * spanX) / 2;
    const offY = (h - s * spanY) / 2;

    state.layout = {
      minX: minX, minY: minY, spanX: spanX, spanY: spanY, s: s, offX: offX, offY: offY,
      w: w, h: h,
      rZone: Math.max(9, 12 * s / 20),
      rAgent: Math.max(6, 7 * s / 20),
      rRes: Math.max(3, 5 * s / 20),
    };
    return state.layout;
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

    const layout = ensureLayout(traj);
    if (!layout) return;

    const map = traj.header.Map;
    const frame = traj.frames[state.index];
    const zoneById = {};
    map.Zones.forEach(function (z) { zoneById[z.Id] = z.Position; });

    drawEdges(ctx, map, frame, zoneById, layout);
    drawResources(ctx, map, frame, layout);
    drawZones(ctx, map, zoneById, layout);
    drawAgents(ctx, map, frame, zoneById, layout);

    renderStatus();
    renderMetrics();
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

  function drawEdges(ctx, map, frame, zoneById, layout) {
    const burst = transitEdges(frame);

    map.ChokePoints.forEach(function (choke) {
      const from = zoneById[choke.FromZoneId];
      const to = zoneById[choke.ToZoneId];
      if (!from || !to) return;
      const a = px(from, layout);
      const b = px(to, layout);
      const hot = burst[edgeKey(choke.FromZoneId, choke.ToZoneId)];
      const limited = choke.MaxOccupancy !== UNLIMITED && choke.MaxOccupancy < UNLIMITED;

      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.strokeStyle = hot ? COLORS.edgeAtBurst : COLORS.edge;
      ctx.lineWidth = hot ? 5 : (limited ? 3 : 2.5);
      ctx.setLineDash([]);
      if (limited && !hot) { ctx.setLineDash([7, 6]); }
      ctx.lineCap = 'round';
      ctx.stroke();
      ctx.setLineDash([]);

      if (limited) {
        const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
        ctx.fillStyle = COLORS.badgeBg;
        ctx.strokeStyle = COLORS.edge;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(mid.x, mid.y, layout.rRes + 4, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
        ctx.fillStyle = hot ? COLORS.edgeAtBurst : COLORS.zoneText;
        ctx.font = 'bold 10px ' + 'monospace';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(String(choke.MaxOccupancy), mid.x, mid.y);

        if (choke.Role) {
          ctx.font = '7px ' + 'monospace';
          ctx.fillStyle = COLORS.mutedText;
          ctx.fillText(choke.Role, mid.x, mid.y + layout.rRes + 11);
        }
      }
    });
  }

  function drawResources(ctx, map, frame, layout) {
    const claimed = {};
    frame.claims.forEach(function (id) { claimed[id] = true; });
    map.Resources.forEach(function (res) {
      const p = px(res.Position, layout);
      ctx.beginPath();
      ctx.arc(p.x, p.y, layout.rRes, 0, Math.PI * 2);
      ctx.fillStyle = claimed[res.Id] ? COLORS.claimed : COLORS.unclaimed;
      ctx.fill();
    });
  }

  function drawZones(ctx, map, zoneById, layout) {
    const occupied = zoneCounts(map, state.trajectory.frames[state.index]);
    const available = zoneUnclaimed(map, state.trajectory.frames[state.index]);

    map.Zones.forEach(function (zone) {
      const p = px(zone.Position, layout);
      ctx.beginPath();
      ctx.arc(p.x, p.y, layout.rZone, 0, Math.PI * 2);
      ctx.fillStyle = COLORS.zoneFill;
      ctx.fill();
      ctx.strokeStyle = COLORS.zoneStroke;
      ctx.lineWidth = 2;
      ctx.stroke();

      ctx.fillStyle = COLORS.zoneText;
      ctx.font = '13px ' + 'monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(zone.Id), p.x, p.y);

      const top = p.y - layout.rZone - 8;
      ctx.textAlign = 'center';
      const n = occupied[zone.Id] || 0;
      const r = available[zone.Id] || 0;
      ctx.font = '10px ' + 'monospace';
      ctx.fillStyle = COLORS.zoneText;
      ctx.fillText('ρ' + n, p.x, top);
      ctx.font = '9px ' + 'monospace';
      ctx.fillStyle = COLORS.unclaimed;
      ctx.fillText('◆' + (r > 0 ? '' + r : ''), p.x, top + 12);

      if (zone.Role) {
        ctx.font = '8px ' + 'monospace';
        ctx.fillStyle = COLORS.zoneText;
        ctx.fillText(zone.Role, p.x, p.y + layout.rZone + 12);
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

  function drawAgents(ctx, map, frame, zoneById, layout) {
    const roles = state.trajectory ? state.trajectory.header.AgentRoles : null;
    const pool = frame.agents.slice().sort(function (a, b) { return a.AgentId - b.AgentId; });
    pool.forEach(function (agent, i) {
      const zonePos = zoneById[agent.ZoneId];
      if (!zonePos) return;
      let x = 0, y = 0;
      if (agent.Transit) {
        const from = zoneById[agent.Transit.FromZoneId];
        const to = zoneById[agent.Transit.ToZoneId];
        const a1 = from ? px(from, layout) : null;
        const a2 = to ? px(to, layout) : null;
        if (a1 && a2) {
          x = a1.x + (a2.x - a1.x) * TRANSIT_FRACTION;
          y = a1.y + (a2.y - a1.y) * TRANSIT_FRACTION;
        } else {
          const q = px(zonePos, layout);
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
        const q = px(zonePos, layout);
        x = q.x; y = q.y;
      }

      ctx.beginPath();
      ctx.arc(x, y, layout.rAgent, 0, Math.PI * 2);
      ctx.fillStyle = AGENT_PALETTE[agent.AgentId % AGENT_PALETTE.length];
      ctx.fill();
      ctx.strokeStyle = COLORS.agentRim;
      ctx.lineWidth = 1.5;
      ctx.stroke();

      ctx.fillStyle = COLORS.agentRim;
      ctx.font = 'bold 9px ' + 'monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(agent.AgentId), x, y);

      ctx.font = '9px ' + 'monospace';
      ctx.fillText(String(agent.Score), x, y - layout.rAgent - 4);

      const role = roles && roles[agent.AgentId];
      if (role) {
        ctx.font = '8px ' + 'monospace';
        ctx.fillStyle = AGENT_PALETTE[agent.AgentId % AGENT_PALETTE.length];
        ctx.fillText(role, x, y + layout.rAgent + 12);
      }

      if (agent.Transit) {
        const to = agent.Transit.ToZoneId;
        ctx.fillStyle = COLORS.edgeAtBurst;
        ctx.fillText('→' + to + ' (' + agent.Transit.RemainingTicks + ')', x, y + layout.rAgent + 11);
      }
    });
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
      const room = zone.Role ? ' · ' + zone.Role : '';
      zrows += '<tr><td class="k">Z' + zone.Id + room + '</td><td class="v">ρ' + (crowd[zone.Id] || 0) +
        ' · ◆' + (zoneUnclaimed(map, frame)[zone.Id] || 0) + '</td></tr>';
    });
    dom.zonesBody.innerHTML = zrows;
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
    if (!traj || !traj.frames.length) return;
    if (state.index >= traj.frames.length - 1) setIndex(0);
    state.playing = true;
    dom.playBtn.textContent = 'Pause';
    startTimer();
  }

  function pause() {
    state.playing = false;
    dom.playBtn.textContent = 'Play';
    stopTimer();
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
    dom.message.style.display = isError ? 'block' : 'block';
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