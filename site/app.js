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
      rAgent: Math.max(7, Math.min(11, 9 * s / 60)),
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

  function drawEdges(ctx, map, frame, zoneById, layout) {
    const burst = transitEdges(frame);

    map.ChokePoints.forEach(function (choke) {
      const zoneA = map.Zones[choke.FromZoneId];
      const zoneB = map.Zones[choke.ToZoneId];
      if (!zoneA || !zoneB) return;
      const ends = corridorEndpoints(roomRect(zoneA, layout), roomRect(zoneB, layout));
      if (!ends) return;
      const hot = burst[edgeKey(choke.FromZoneId, choke.ToZoneId)];
      const limited = choke.MaxOccupancy !== UNLIMITED && choke.MaxOccupancy < UNLIMITED;

      ctx.beginPath();
      ctx.moveTo(ends.ax, ends.ay);
      ctx.lineTo(ends.bx, ends.by);
      ctx.strokeStyle = hot ? COLORS.corridorHot : COLORS.corridor;
      ctx.lineWidth = hot ? 4 : 2.5;
      ctx.setLineDash(limited && !hot ? [7, 6] : []);
      ctx.lineCap = 'round';
      ctx.stroke();
      ctx.setLineDash([]);

      // Gate badge: a compact capsule on the corridor, never a circle that
      // could be mistaken for a room.
      if (limited) {
        const midX = (ends.ax + ends.bx) / 2;
        const midY = (ends.ay + ends.by) / 2;
        const label = choke.MaxOccupancy === 0 ? 'LOCKED' : 'CAP ' + choke.MaxOccupancy;
        const bw = label.length * 6.4 + 14;
        roundedRect(ctx, midX - bw / 2, midY - 9, bw, 18, 9);
        ctx.fillStyle = COLORS.gateBg;
        ctx.fill();
        ctx.strokeStyle = hot ? COLORS.corridorHot : COLORS.corridor;
        ctx.lineWidth = 1;
        ctx.stroke();
        ctx.fillStyle = hot ? COLORS.corridorHot : COLORS.gateText;
        ctx.font = 'bold 9px monospace';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(label, midX, midY + 0.5);

        if (choke.Role) {
          ctx.font = '8px monospace';
          ctx.fillStyle = COLORS.mutedText;
          ctx.fillText(spaceCamel(choke.Role), midX, midY + 17);
        }
      }
    });
  }

  // Loot sits inside its room; never floating in open canvas space. The
  // caller passes the current claim set so claimed items dim to green.
  function drawResources(ctx, map, frame, layout) {
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
      const rect = roomRect(zone, layout);
      const y = rect.y + rect.hh - 8;
      const spacing = 14;
      // Anchor the loot row to the room's lower-left corner so it never
      // collides with the centered agent tokens and score labels.
      const startX = rect.x - rect.hw + 9;
      items.forEach(function (res, i) {
        drawDiamond(ctx, startX + i * spacing, y, 5, claimed[res.Id] ? COLORS.claimed : COLORS.unclaimed);
      });
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

  function drawZones(ctx, map, zoneById, layout) {
    const occupied = zoneCounts(map, state.trajectory.frames[state.index]);

    map.Zones.forEach(function (zone) {
      const rect = roomRect(zone, layout);
      roundedRect(ctx, rect.x - rect.hw, rect.y - rect.hh, rect.hw * 2, rect.hh * 2, 9);
      ctx.fillStyle = COLORS.roomFill;
      ctx.fill();
      ctx.strokeStyle = COLORS.roomStroke;
      ctx.lineWidth = 1.6;
      ctx.stroke();

      // Room title strip.
      ctx.fillStyle = COLORS.roomText;
      ctx.font = 'bold 11px monospace';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(roomLabel(zone), rect.x, rect.y - 6);

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

  function drawAgents(ctx, map, frame, zoneById, layout) {
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

      // The Sentry carries a faint dashed one-hop perception perimeter.
      if (role === 'Sentry') {
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