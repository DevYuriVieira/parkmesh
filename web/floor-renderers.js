export const FLOOR_META = [
  {
    code: "F0",
    title: "GROUND DECK · STREET ENTRY & CONCOURSE",
    deckFill: "#15181c",
    slabMargin: "#101215",
    asphaltFill: "#1c2026",
    curbStroke: "#2b323b",
    wallStroke: "#323a46",
    gridAlpha: 0.036
  },
  {
    code: "F1",
    title: "LEVEL G1 · HIGH-DENSITY TRANSFER DECK",
    deckFill: "#161a1f",
    slabMargin: "#0f1216",
    asphaltFill: "#1d2229",
    curbStroke: "#2f3844",
    wallStroke: "#394452",
    gridAlpha: 0.04
  },
  {
    code: "F2",
    title: "LEVEL G2 · ROOFTOP ATRIUM DECK",
    deckFill: "#1c2128",
    slabMargin: "#12161c",
    asphaltFill: "#232933",
    curbStroke: "#3b4554",
    wallStroke: "#536073",
    gridAlpha: 0.045
  }
];

function drawHatchPattern(ctx, x, y, w, h, strokeStyle, step = 10) {
  ctx.save();
  ctx.beginPath();
  ctx.rect(x + 2, y + 2, w - 4, h - 4);
  ctx.clip();
  ctx.strokeStyle = strokeStyle;
  ctx.lineWidth = 1;
  for (let sx = x - h; sx < x + w + h; sx += step) {
    ctx.beginPath();
    ctx.moveTo(sx, y + h);
    ctx.lineTo(sx + h, y);
    ctx.stroke();
  }
  ctx.restore();
}

export function drawDeckBackground(ctx, layout, floor) {
  const { width, height, toX, toY } = layout;
  const meta = FLOOR_META[floor] || FLOOR_META[0];

  ctx.fillStyle = meta.slabMargin;
  ctx.fillRect(0, 0, width, height);

  // Outside street apron on Ground Floor (F0) west of x=2.5
  if (floor === 0) {
    ctx.fillStyle = "#121519";
    ctx.fillRect(0, toY(3), toX(2.5), toY(57) - toY(3));

    ctx.fillStyle = "#191d23";
    ctx.fillRect(0, toY(6), toX(2.5), toY(14) - toY(6));
    ctx.fillRect(0, toY(46), toX(2.5), toY(54) - toY(46));

    ctx.fillStyle = "#535d6c";
    ctx.font = "600 8px Cascadia Mono, Consolas, monospace";
    ctx.textAlign = "right";
    ctx.fillText("NORTH ST", toX(1.2), toY(10) - 15);
    ctx.fillText("SOUTH ST", toX(1.2), toY(50) + 21);
  }

  // Architectural concrete garage slab
  const deckLeft = toX(2.2);
  const deckTop = toY(0.5);
  const deckRight = toX(97.8);
  const deckBottom = toY(59.5);
  ctx.fillStyle = meta.deckFill;
  ctx.fillRect(deckLeft, deckTop, deckRight - deckLeft, deckBottom - deckTop);

  // CAD coordinate grid (5m minor, 10m major)
  for (let gx = 5; gx <= 95; gx += 5) {
    const px = toX(gx);
    ctx.beginPath();
    ctx.moveTo(px, deckTop);
    ctx.lineTo(px, deckBottom);
    ctx.strokeStyle = gx % 10 === 0
      ? `rgba(255, 255, 255, ${meta.gridAlpha})`
      : "rgba(255, 255, 255, 0.015)";
    ctx.lineWidth = 1;
    ctx.stroke();

    if (gx % 10 === 0) {
      ctx.fillStyle = "#525c6a";
      ctx.font = "9px Cascadia Mono, Consolas, monospace";
      ctx.textAlign = "center";
      const label = floor === 0 ? `A${gx / 10}` : floor === 1 ? `B${gx / 10}` : `R${gx / 10}`;
      ctx.fillText(label, px, deckTop - 6);
    }
  }

  for (let gy = 5; gy <= 55; gy += 5) {
    const py = toY(gy);
    ctx.beginPath();
    ctx.moveTo(deckLeft, py);
    ctx.lineTo(deckRight, py);
    ctx.strokeStyle = gy % 10 === 0
      ? `rgba(255, 255, 255, ${meta.gridAlpha})`
      : "rgba(255, 255, 255, 0.015)";
    ctx.lineWidth = 1;
    ctx.stroke();

    if (gy % 10 === 0) {
      ctx.fillStyle = "#525c6a";
      ctx.font = "9px Cascadia Mono, Consolas, monospace";
      ctx.textAlign = "right";
      ctx.fillText(`${gy}`, deckLeft - 6, py + 3);
    }
  }

  if (floor === 0) {
    drawGroundPlazaArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom);
  } else if (floor === 1) {
    drawMidDeckArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom);
  } else {
    drawRooftopAtriumArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom);
  }
}

function drawGroundPlazaArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom) {
  const { toX, toY, avgScale } = layout;

  // Open West Terminal Concierge & Pickup Concourses (no bays occupy x=13..39.5 interior!)
  const zones = [
    { x1: 13.5, y1: 14.0, x2: 39.2, y2: 26.2, label: "TERMINAL LOBBY · ELEVATOR & TICKETING", core: true },
    { x1: 13.5, y1: 33.8, x2: 39.2, y2: 46.0, label: "VALET STAGING & EXPRESS PICKUP CONCOURSE", core: false },
    { x1: 58.6, y1: 19.8, x2: 80.5, y2: 20.6, label: "", core: false, divider: true },
    { x1: 58.6, y1: 39.4, x2: 80.5, y2: 40.2, label: "", core: false, divider: true }
  ];

  for (const z of zones) {
    const ix = toX(z.x1);
    const iy = toY(z.y1);
    const iw = toX(z.x2) - ix;
    const ih = toY(z.y2) - iy;

    ctx.fillStyle = z.divider ? "#262d37" : "#181d24";
    ctx.fillRect(ix, iy, iw, ih);
    ctx.strokeStyle = meta.curbStroke;
    ctx.lineWidth = z.divider ? 1 : 1.3;
    ctx.strokeRect(ix, iy, iw, ih);

    if (z.divider) continue;

    drawHatchPattern(
      ctx,
      ix,
      iy,
      iw,
      ih,
      z.core ? "rgba(217, 138, 43, 0.08)" : "rgba(56, 136, 184, 0.06)",
      11
    );

    if (z.core) {
      // Security & elevator reception cabin inside West lobby
      const cabW = iw * 0.34;
      const cabH = ih * 0.44;
      const cabX = ix + iw * 0.08;
      const cabY = iy + ih * 0.16;
      ctx.fillStyle = "#12151a";
      ctx.fillRect(cabX, cabY, cabW, cabH);
      ctx.strokeStyle = "#485261";
      ctx.lineWidth = 1;
      ctx.strokeRect(cabX, cabY, cabW, cabH);

      ctx.fillStyle = "#7c8796";
      ctx.font = `700 ${Math.max(7, Math.round(avgScale * 0.68))}px Cascadia Mono, Consolas, monospace`;
      ctx.textAlign = "center";
      ctx.fillText("LIFT CORE W", cabX + cabW / 2, cabY + cabH / 2 + 2.5);
    }

    // Yellow corner protective bollards
    ctx.fillStyle = "#d98a2b";
    ctx.fillRect(ix + 3, iy + 3, 3.5, 3.5);
    ctx.fillRect(ix + iw - 6.5, iy + 3, 3.5, 3.5);
    ctx.fillRect(ix + 3, iy + ih - 6.5, 3.5, 3.5);
    ctx.fillRect(ix + iw - 6.5, iy + ih - 6.5, 3.5, 3.5);

    ctx.fillStyle = z.core ? "#7c8796" : "#5e6a7a";
    ctx.font = `600 ${Math.max(8, Math.round(avgScale * 0.78))}px Cascadia Mono, Consolas, monospace`;
    ctx.textAlign = "center";
    ctx.fillText(z.label, ix + iw / 2, iy + ih - 9);
  }

  // Ground columns (avoiding all West plaza aisles and horizontal bay openings)
  ctx.fillStyle = "#323944";
  ctx.strokeStyle = "#485261";
  ctx.lineWidth = 0.9;
  for (const colX of [13.5, 27.5, 42.5, 57.5, 72.5, 86.5]) {
    for (const colY of [20.4, 39.6]) {
      if (colX === 42.5 && (colY === 20.4 || colY === 39.6)) continue;
      const cx = toX(colX);
      const cy = toY(colY);
      ctx.fillRect(cx - 3.5, cy - 3.5, 7, 7);
      ctx.strokeRect(cx - 3.5, cy - 3.5, 7, 7);
    }
  }

  ctx.strokeStyle = meta.wallStroke;
  ctx.lineWidth = 2.2;
  ctx.strokeRect(deckLeft, deckTop, deckRight - deckLeft, deckBottom - deckTop);
}

function drawMidDeckArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom) {
  const { toX, toY, avgScale } = layout;

  // Post-tensioned slab expansion joints across F1
  ctx.strokeStyle = "rgba(148, 163, 184, 0.08)";
  ctx.lineWidth = 1;
  for (const jx of [27.5, 72.5]) {
    ctx.beginPath();
    ctx.moveTo(toX(jx), deckTop);
    ctx.lineTo(toX(jx), deckBottom);
    ctx.stroke();
  }

  const islands = [
    { x1: 14.5, y1: 20.6, x2: 45.5, y2: 26.2, label: "G1 ELEVATOR CORE W · TRANSFER", core: true },
    { x1: 54.5, y1: 20.6, x2: 85.5, y2: 26.2, label: "G1 STRUCTURAL CORE NORTH", core: false },
    { x1: 14.5, y1: 33.8, x2: 45.5, y2: 39.4, label: "G1 MEP & VENTILATION PLENUM", core: false },
    { x1: 54.5, y1: 33.8, x2: 85.5, y2: 39.4, label: "G1 STAIRS & LIFT CORE E", core: true }
  ];

  for (const isl of islands) {
    const ix = toX(isl.x1);
    const iy = toY(isl.y1);
    const iw = toX(isl.x2) - ix;
    const ih = toY(isl.y2) - iy;

    ctx.fillStyle = "#191d23";
    ctx.fillRect(ix, iy, iw, ih);
    ctx.strokeStyle = meta.curbStroke;
    ctx.lineWidth = 1.2;
    ctx.strokeRect(ix, iy, iw, ih);

    drawHatchPattern(
      ctx,
      ix,
      iy,
      iw,
      ih,
      isl.core ? "rgba(217, 138, 43, 0.08)" : "rgba(255, 255, 255, 0.025)",
      10
    );

    if (isl.core) {
      ctx.fillStyle = "#d98a2b";
      ctx.fillRect(ix + 3, iy + 3, 3, 3);
      ctx.fillRect(ix + iw - 6, iy + 3, 3, 3);
      ctx.fillRect(ix + 3, iy + ih - 6, 3, 3);
      ctx.fillRect(ix + iw - 6, iy + ih - 6, 3, 3);
    }

    ctx.fillStyle = isl.core ? "#6a7484" : "#515a67";
    ctx.font = `600 ${Math.max(8, Math.round(avgScale * 0.85))}px Cascadia Mono, Consolas, monospace`;
    ctx.textAlign = "center";
    ctx.fillText(isl.label, ix + iw / 2, iy + ih / 2 + 3);
  }

  ctx.fillStyle = "#323944";
  ctx.strokeStyle = "#485261";
  ctx.lineWidth = 0.9;
  for (const colX of [13.5, 27.5, 42.5, 57.5, 72.5, 86.5]) {
    for (const colY of [20.4, 39.6]) {
      const cx = toX(colX);
      const cy = toY(colY);
      ctx.fillRect(cx - 3.5, cy - 3.5, 7, 7);
      ctx.strokeRect(cx - 3.5, cy - 3.5, 7, 7);
    }
  }

  ctx.strokeStyle = meta.wallStroke;
  ctx.lineWidth = 2.2;
  ctx.strokeRect(deckLeft, deckTop, deckRight - deckLeft, deckBottom - deckTop);
}

function drawRooftopAtriumArchitecture(ctx, layout, meta, deckLeft, deckTop, deckRight, deckBottom) {
  const { toX, toY, avgScale } = layout;

  // Targeted PV solar canopies sheltering North & South central EV / PCD bays (x=35..65)
  for (const canopyY of [1.0, 53.3]) {
    const cx = toX(35.0);
    const cy = toY(canopyY);
    const cw = toX(65.0) - cx;
    const ch = toY(canopyY + 5.7) - cy;
    ctx.fillStyle = "rgba(56, 136, 184, 0.055)";
    ctx.fillRect(cx, cy, cw, ch);
    ctx.strokeStyle = "rgba(56, 136, 184, 0.19)";
    ctx.lineWidth = 1;
    ctx.strokeRect(cx, cy, cw, ch);
    for (let px = 40; px <= 60; px += 5) {
      ctx.beginPath();
      ctx.moveTo(toX(px), cy);
      ctx.lineTo(toX(px), cy + ch);
      ctx.stroke();
    }
  }

  // Concrete median islands splitting opposing horizontal stalls in West (x=18.6) & East (x=80.6) wings
  for (const mx of [18.6, 80.6]) {
    for (const [y1, y2] of [[15.8, 26.2], [33.8, 44.2]]) {
      const rx = toX(mx);
      const ry = toY(y1);
      const rw = toX(mx + 0.8) - rx;
      const rh = toY(y2) - ry;
      ctx.fillStyle = "#262d37";
      ctx.fillRect(rx, ry, rw, rh);
      ctx.strokeStyle = "#485261";
      ctx.lineWidth = 1;
      ctx.strokeRect(rx, ry, rw, rh);

      ctx.fillStyle = "#d98a2b";
      ctx.fillRect(rx + rw / 2 - 2, ry + 2, 4, 4);
      ctx.fillRect(rx + rw / 2 - 2, ry + rh - 6, 4, 4);
    }
  }

  // Central Rooftop Atrium Promenade Platform (x=31.0..69.0, y=13.5..46.5)
  const platX = toX(31.0);
  const platY = toY(13.5);
  const platW = toX(69.0) - platX;
  const platH = toY(46.5) - platY;
  ctx.fillStyle = "#191e26";
  ctx.fillRect(platX, platY, platW, platH);
  ctx.strokeStyle = "#414b59";
  ctx.lineWidth = 1.5;
  ctx.strokeRect(platX, platY, platW, platH);

  ctx.strokeStyle = "rgba(255, 255, 255, 0.025)";
  ctx.lineWidth = 1;
  for (let px = 33; px <= 67; px += 4) {
    ctx.beginPath();
    ctx.moveTo(toX(px), platY);
    ctx.lineTo(toX(px), platY + platH);
    ctx.stroke();
  }

  // Four corner rooftop landscape planter beds
  for (const [bx1, by1, bx2, by2] of [
    [32.0, 14.5, 37.8, 18.8],
    [62.2, 14.5, 68.0, 18.8],
    [32.0, 41.2, 37.8, 45.5],
    [62.2, 41.2, 68.0, 45.5]
  ]) {
    const gx = toX(bx1);
    const gy = toY(by1);
    const gw = toX(bx2) - gx;
    const gh = toY(by2) - gy;
    ctx.fillStyle = "#18241e";
    ctx.fillRect(gx, gy, gw, gh);
    ctx.strokeStyle = "#2d4539";
    ctx.lineWidth = 1;
    ctx.strokeRect(gx, gy, gw, gh);

    ctx.fillStyle = "rgba(72, 148, 110, 0.18)";
    for (let ox = 0.25; ox <= 0.75; ox += 0.25) {
      ctx.beginPath();
      ctx.arc(gx + gw * ox, gy + gh * 0.5, Math.min(gw, gh) * 0.22, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  // Mechanical penthouse compounds flanking skylight well
  for (const compound of [
    { x1: 32.0, y1: 20.5, x2: 37.6, y2: 39.5, label: "ELEV + PV INV" },
    { x1: 62.4, y1: 20.5, x2: 68.0, y2: 39.5, label: "HVAC CHILLER" }
  ]) {
    const cx = toX(compound.x1);
    const cy = toY(compound.y1);
    const cw = toX(compound.x2) - cx;
    const ch = toY(compound.y2) - cy;
    ctx.fillStyle = "#14181e";
    ctx.fillRect(cx, cy, cw, ch);
    ctx.strokeStyle = "#343e4a";
    ctx.lineWidth = 1.1;
    ctx.strokeRect(cx, cy, cw, ch);

    const fanR = Math.min(cw * 0.34, ch * 0.14);
    for (const fyFrac of [0.28, 0.72]) {
      const fcy = cy + ch * fyFrac;
      const fcx = cx + cw / 2;
      ctx.beginPath();
      ctx.arc(fcx, fcy, fanR, 0, Math.PI * 2);
      ctx.strokeStyle = "#3b4654";
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(fcx - fanR * 0.7, fcy);
      ctx.lineTo(fcx + fanR * 0.7, fcy);
      ctx.moveTo(fcx, fcy - fanR * 0.7);
      ctx.lineTo(fcx, fcy + fanR * 0.7);
      ctx.stroke();
    }

    ctx.fillStyle = "#6c7787";
    ctx.font = `600 ${Math.max(7, Math.round(avgScale * 0.65))}px Cascadia Mono, Consolas, monospace`;
    ctx.textAlign = "center";
    ctx.fillText(compound.label, cx + cw / 2, cy + ch / 2 + 2.5);
  }

  // Central Open-Air Skylight Atrium Void
  const voidX = toX(39.2);
  const voidY = toY(18.2);
  const voidW = toX(60.8) - voidX;
  const voidH = toY(41.8) - voidY;
  ctx.fillStyle = "#0a0d11";
  ctx.fillRect(voidX, voidY, voidW, voidH);

  ctx.strokeStyle = "rgba(88, 128, 168, 0.16)";
  ctx.lineWidth = 1;
  for (let mx = 42.8; mx < 60.5; mx += 3.6) {
    ctx.beginPath();
    ctx.moveTo(toX(mx), voidY);
    ctx.lineTo(toX(mx), voidY + voidH);
    ctx.stroke();
  }
  for (let my = 22.0; my < 41.5; my += 3.8) {
    ctx.beginPath();
    ctx.moveTo(voidX, toY(my));
    ctx.lineTo(voidX + voidW, toY(my));
    ctx.stroke();
  }

  ctx.strokeStyle = "#4d596b";
  ctx.lineWidth = 1.6;
  ctx.strokeRect(voidX, voidY, voidW, voidH);
  ctx.strokeStyle = "rgba(217, 138, 43, 0.28)";
  ctx.lineWidth = 0.9;
  ctx.strokeRect(voidX + 3, voidY + 3, voidW - 6, voidH - 6);

  ctx.fillStyle = "rgba(180, 194, 212, 0.55)";
  ctx.font = `700 ${Math.max(8, Math.round(avgScale * 0.85))}px Cascadia Mono, Consolas, monospace`;
  ctx.textAlign = "center";
  ctx.fillText("SKYLIGHT ATRIUM VOID", voidX + voidW / 2, voidY + voidH / 2 - 4);
  ctx.fillStyle = "rgba(130, 144, 164, 0.45)";
  ctx.font = `600 ${Math.max(7, Math.round(avgScale * 0.72))}px Cascadia Mono, Consolas, monospace`;
  ctx.fillText("OPEN DAYLIGHT WELL · F0–F2", voidX + voidW / 2, voidY + voidH / 2 + 10);

  for (const [dx, dy] of [[28, 14.5], [72, 14.5], [28, 45.5], [72, 45.5]]) {
    const gx = toX(dx) - 4;
    const gy = toY(dy) - 4;
    ctx.fillStyle = "#13161b";
    ctx.fillRect(gx, gy, 8, 8);
    ctx.strokeStyle = "#3e4856";
    ctx.lineWidth = 0.8;
    ctx.strokeRect(gx, gy, 8, 8);
  }

  ctx.strokeStyle = meta.wallStroke;
  ctx.lineWidth = 1.5;
  ctx.strokeRect(deckLeft, deckTop, deckRight - deckLeft, deckBottom - deckTop);
  ctx.strokeStyle = "rgba(217, 138, 43, 0.28)";
  ctx.lineWidth = 1;
  ctx.strokeRect(deckLeft - 2.5, deckTop - 2.5, deckRight - deckLeft + 5, deckBottom - deckTop + 5);
}

function isHorizontalCrossRampOrGate(from, to) {
  return from.type === "Ramp" || to.type === "Ramp" || from.type === "Gate" || to.type === "Gate";
}

function computeFloorEdgeLoads(state) {
  const counts = new Map();
  for (const session of state.sessions.values()) {
    const steps = session.route?.steps;
    if (Array.isArray(steps) && steps.length > 0) {
      for (const st of steps) {
        counts.set(st.edgeId, (counts.get(st.edgeId) || 0) + 1);
      }
      continue;
    }

    const ids = session.route?.nodeIds || [];
    for (let i = 0; i < ids.length - 1; i++) {
      const pairKey = `${ids[i]}->${ids[i + 1]}`;
      counts.set(pairKey, (counts.get(pairKey) || 0) + 1);
    }
  }
  return counts;
}

export function drawDrivingCorridors(ctx, layout, floor, state, options) {
  const { toX, toY, avgScale } = layout;
  const meta = FLOOR_META[floor] || FLOOR_META[0];
  const mainRoadWidth = Math.max(20, avgScale * 3.8);
  const centerConnectorWidth = Math.max(14, avgScale * 2.0);

  const roadEdges = [];
  const edgePairKeys = new Set();

  for (const edge of state.edges) {
    const from = state.nodesById.get(edge.fromNodeId);
    const to = state.nodesById.get(edge.toNodeId);
    if (!from || !to) continue;
    if (from.floor !== floor || to.floor !== floor) continue;
    if (to.type === "Bay" || from.type === "Bay") continue;
    if (isHorizontalCrossRampOrGate(from, to)) continue;

    const isCenterVertical = Math.abs(from.x - 50) < 0.5 && Math.abs(to.x - 50) < 0.5;
    const isInteriorWing = floor === 2 && (
      (Math.abs(from.x - 28) < 0.5 && Math.abs(to.x - 28) < 0.5) ||
      (Math.abs(from.x - 72) < 0.5 && Math.abs(to.x - 72) < 0.5) ||
      (Math.abs(from.y - 30) < 0.5 && Math.abs(to.y - 30) < 0.5)
    );
    roadEdges.push({
      edge,
      from,
      to,
      roadW: isCenterVertical
        ? centerConnectorWidth
        : isInteriorWing
          ? Math.max(16, avgScale * 3.1)
          : mainRoadWidth
    });
    edgePairKeys.add(`${edge.fromNodeId}->${edge.toNodeId}`);
  }

  // Pass 1: Curbs
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  for (const { from, to, roadW } of roadEdges) {
    ctx.beginPath();
    ctx.moveTo(toX(from.x), toY(from.y));
    ctx.lineTo(toX(to.x), toY(to.y));
    ctx.strokeStyle = meta.curbStroke;
    ctx.lineWidth = roadW + 3;
    ctx.stroke();
  }

  // Pass 2: Asphalt deck surface
  for (const { from, to, roadW } of roadEdges) {
    ctx.beginPath();
    ctx.moveTo(toX(from.x), toY(from.y));
    ctx.lineTo(toX(to.x), toY(to.y));
    ctx.strokeStyle = meta.asphaltFill;
    ctx.lineWidth = roadW;
    ctx.stroke();
  }

  // Optional Corridor Congestion Heatmap Layer
  if (options?.showHeatmap) {
    const loads = computeFloorEdgeLoads(state);
    for (const { edge, from, to, roadW } of roadEdges) {
      const forwardLoad = loads.get(edge.id) || loads.get(`${edge.fromNodeId}->${edge.toNodeId}`) || 0;
      const revLoad = loads.get(`${edge.toNodeId}->${edge.fromNodeId}`) || 0;
      const load = forwardLoad + revLoad;
      if (load <= 0) continue;

      const heatColor = load >= 4
        ? "rgba(209, 75, 75, 0.34)"
        : load >= 2
          ? "rgba(217, 138, 43, 0.28)"
          : "rgba(47, 158, 102, 0.22)";

      ctx.beginPath();
      ctx.moveTo(toX(from.x), toY(from.y));
      ctx.lineTo(toX(to.x), toY(to.y));
      ctx.strokeStyle = heatColor;
      ctx.lineWidth = roadW * 0.72;
      ctx.stroke();
    }
  }

  // Floor-aligned pedestrian crosswalks
  if (floor === 2) {
    for (const xw of [13.2, 24.8, 75.2, 86.8]) {
      drawVerticalCrosswalk(ctx, toX(xw), toY(27.9), toY(32.1), 10);
    }
    for (const yw of [10, 50]) {
      const py = toY(yw);
      const xStart = toX(48.3);
      const xEnd = toX(51.7);
      ctx.fillStyle = "rgba(226, 232, 240, 0.14)";
      for (let sx = xStart; sx < xEnd - 2; sx += 5) {
        ctx.fillRect(sx, py - 6, 2.5, 12);
      }
    }
  } else if (floor === 0) {
    for (const xw of [14.2, 41.2, 58.0]) {
      drawVerticalCrosswalk(ctx, toX(xw), toY(27.8), toY(32.2), 11);
    }
  } else {
    for (const xw of [14.2, 54.2]) {
      drawVerticalCrosswalk(ctx, toX(xw), toY(27.8), toY(32.2), 12);
    }
  }

  // Pass 3: Lane markings (two-way dashed yellow vs one-way directional chevrons)
  const drawnBidirectional = new Set();
  for (const { edge, from, to, roadW } of roadEdges) {
    const reverseKey = `${edge.toNodeId}->${edge.fromNodeId}`;
    const isTwoWay = edgePairKeys.has(reverseKey);

    const x1 = toX(from.x);
    const y1 = toY(from.y);
    const x2 = toX(to.x);
    const y2 = toY(to.y);

    if (isTwoWay) {
      const pairKey = [edge.fromNodeId, edge.toNodeId].sort().join("--");
      if (drawnBidirectional.has(pairKey)) continue;
      drawnBidirectional.add(pairKey);

      ctx.save();
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.setLineDash([7, 7]);
      ctx.strokeStyle = "rgba(184, 146, 62, 0.35)";
      ctx.lineWidth = 1.1;
      ctx.stroke();
      ctx.restore();
    } else {
      const mx = (x1 + x2) / 2;
      const my = (y1 + y2) / 2;
      const angle = Math.atan2(y2 - y1, x2 - x1);
      const chevronSize = Math.max(4, roadW * 0.16);

      ctx.save();
      ctx.translate(mx, my);
      ctx.rotate(angle);
      ctx.beginPath();
      ctx.moveTo(-chevronSize * 0.7, -chevronSize * 0.8);
      ctx.lineTo(chevronSize * 0.5, 0);
      ctx.lineTo(-chevronSize * 0.7, chevronSize * 0.8);
      ctx.strokeStyle = "rgba(186, 198, 214, 0.24)";
      ctx.lineWidth = 1.5;
      ctx.lineCap = "square";
      ctx.stroke();
      ctx.restore();
    }
  }
}

function drawVerticalCrosswalk(ctx, px, yStart, yEnd, widthPx) {
  ctx.fillStyle = "rgba(226, 232, 240, 0.14)";
  for (let sy = yStart; sy < yEnd - 2; sy += 5) {
    ctx.fillRect(px - widthPx / 2, sy, widthPx, 2.4);
  }
}

function getActiveRampTraffic(floor, state) {
  let westDn = false;
  let eastUp = false;

  for (const s of state.sessions.values()) {
    const ids = s.route?.nodeIds || [];
    for (const id of ids) {
      if (id.startsWith(`F${floor}-RAMP-UP`)) eastUp = true;
      if (id.startsWith(`F${floor}-RAMP-DN`)) westDn = true;
    }
  }

  return { westDn, eastUp };
}

export function getRampSpecs(layout, floor) {
  const { toX, toY } = layout;
  const h = Math.max(36, layout.avgScale * 5.6);
  const cy = toY(30);
  const top = cy - h / 2;

  const eastLeft = Math.min(toX(90), toX(99.5));
  const eastRight = Math.max(toX(90), toX(99.5));
  const westLeft = Math.min(toX(0.5), toX(10));
  const westRight = Math.max(toX(0.5), toX(10));

  return [
    {
      id: `RAMP-EAST-F${floor}`,
      side: "east",
      left: eastLeft,
      top,
      w: eastRight - eastLeft,
      h,
      cy,
      title: floor === 0 ? "RAMP UP" : floor === 1 ? "RAMP G0→G2" : "FROM G1",
      sub: floor === 0 ? "▲ TO F1" : floor === 1 ? "▲ UP TO F2" : "▲ ARRIVAL",
      accentColor: "#d98a2b",
      isDeadEndExit: floor === 2,
      targetFloor: floor === 0 ? 1 : floor === 1 ? 2 : null
    },
    {
      id: `RAMP-WEST-F${floor}`,
      side: "west",
      left: westLeft,
      top,
      w: westRight - westLeft,
      h,
      cy,
      title: floor === 2 ? "RAMP DN" : floor === 1 ? "RAMP G2→G0" : "FROM G1",
      sub: floor === 2 ? "▼ TO F1" : floor === 1 ? "▼ DN TO F0" : "▼ ARRIVAL",
      accentColor: "#3888b8",
      isDeadEndExit: floor === 0,
      targetFloor: floor === 2 ? 1 : floor === 1 ? 0 : null
    }
  ];
}

export function drawFloorRampsAndGates(ctx, layout, floor, state, now, hoveredRampId) {
  const { toX, toY } = layout;
  const rampTraffic = getActiveRampTraffic(floor, state);

  // Entry gates only exist on Ground Floor (F0)
  if (floor === 0) {
    for (const gate of [
      { id: "GATE-N1", y: 10, label: "N1" },
      { id: "GATE-S1", y: 50, label: "S1" }
    ]) {
      const gx0 = toX(0);
      const gxEnd = toX(10);
      const gy = toY(gate.y);
      const laneH = Math.max(20, layout.avgScale * 3.8);
      const flashUntil = state.gateFlashUntil?.get(gate.id) || 0;
      const isOpening = flashUntil > now;
      const openRatio = isOpening ? Math.min(1, (flashUntil - now) / 1400) : 0;

      ctx.fillStyle = "#262c34";
      ctx.fillRect(gx0 - 12, gy - laneH / 2 - 2, gxEnd - gx0 + 12, laneH + 4);
      ctx.fillStyle = "#1c2026";
      ctx.fillRect(gx0 - 12, gy - laneH / 2, gxEnd - gx0 + 12, laneH);

      // Induction loop detector & stop bar
      ctx.strokeStyle = isOpening ? "rgba(47, 158, 102, 0.85)" : "rgba(148, 163, 184, 0.25)";
      ctx.lineWidth = isOpening ? 1.5 : 1;
      ctx.strokeRect(gx0 + 16, gy - laneH * 0.34, 14, laneH * 0.68);

      ctx.fillStyle = "rgba(226, 232, 240, 0.35)";
      ctx.fillRect(gx0 + 14, gy - laneH / 2 + 2, 2, laneH - 4);

      // Gate kiosk booth
      ctx.fillStyle = "#252b34";
      ctx.strokeStyle = isOpening ? "#2f9e66" : "#4b5563";
      ctx.lineWidth = 1.2;
      ctx.fillRect(gx0 - 10, gy - 11, 22, 22);
      ctx.strokeRect(gx0 - 10, gy - 11, 22, 22);

      // Boom barrier arm (rotates open when gate flashes!)
      ctx.save();
      ctx.translate(gx0 + 13.5, gy - 10);
      ctx.rotate(-openRatio * 1.05);
      ctx.fillStyle = "#d98a2b";
      ctx.fillRect(-1.5, 0, 3, 20);
      ctx.fillStyle = "#e3e7ed";
      ctx.fillRect(-1.5, 5, 3, 4);
      ctx.fillRect(-1.5, 13, 3, 4);
      ctx.restore();

      ctx.fillStyle = isOpening ? "#86efac" : "#e3e7ed";
      ctx.font = "700 8.5px Cascadia Mono, Consolas, monospace";
      ctx.textAlign = "center";
      ctx.fillText(gate.label, gx0 + 1, gy + 3);
    }
  }

  for (const spec of getRampSpecs(layout, floor)) {
    const active = spec.side === "east" ? rampTraffic.eastUp : rampTraffic.westDn;
    const isHovered = hoveredRampId === spec.id && spec.targetFloor !== null;
    drawSingleRampBay(ctx, spec, active, isHovered, now);
  }
}

function drawSingleRampBay(ctx, cfg, activeHighlight, isHovered, now) {
  const { left, top, w, h, cy, side, isDeadEndExit, accentColor } = cfg;
  const right = left + w;
  const isEast = side === "east";

  ctx.save();
  const grad = isEast
    ? ctx.createLinearGradient(left, cy, right, cy)
    : ctx.createLinearGradient(right, cy, left, cy);
  grad.addColorStop(0, "#1c2026");
  grad.addColorStop(0.55, "#171a20");
  grad.addColorStop(1, isDeadEndExit ? "#13171c" : "#0b0d10");

  ctx.fillStyle = grad;
  ctx.fillRect(left, top, w, h);

  ctx.strokeStyle = isHovered ? "#e3e7ed" : activeHighlight ? accentColor : "#3a4350";
  ctx.lineWidth = isHovered ? 2.0 : activeHighlight ? 1.8 : 1.3;
  ctx.strokeRect(left, top, w, h);

  ctx.strokeStyle = "rgba(255, 255, 255, 0.08)";
  ctx.lineWidth = 1;
  for (let gx = left + 7; gx < right - 5; gx += 6) {
    ctx.beginPath();
    ctx.moveTo(gx, top + 4);
    ctx.lineTo(gx, top + h - 4);
    ctx.stroke();
  }

  const mouthX = isEast ? left + 1.5 : right - 3.5;
  ctx.fillStyle = accentColor;
  ctx.fillRect(mouthX, top + 2, 2.2, h - 4);

  const pulse = activeHighlight ? (0.65 + 0.35 * Math.sin(now * 0.008)) : 0.92;
  ctx.globalAlpha = pulse;
  ctx.fillStyle = accentColor;
  ctx.font = "700 8.5px Cascadia Mono, Consolas, monospace";
  ctx.textAlign = "center";
  const midX = left + w / 2;
  ctx.fillText(cfg.title, midX, cy - 3);
  ctx.fillStyle = isHovered ? "#ffffff" : "#e3e7ed";
  ctx.font = "700 8px Cascadia Mono, Consolas, monospace";
  ctx.fillText(isHovered ? `GO → F${cfg.targetFloor}` : cfg.sub, midX, cy + 9);
  ctx.restore();
}
