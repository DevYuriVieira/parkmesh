import { FLOOR_META, getRampSpecs } from "./floor-renderers.js";

export function getCanvasLayout(canvas) {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  const padX = Math.max(46, width * 0.055);
  const padY = Math.max(30, height * 0.055);
  const scaleX = (width - padX * 2) / 100;
  const scaleY = (height - padY * 2) / 60;
  const boxW = Math.max(20, scaleX * 3.0);
  const boxH = Math.max(22, scaleY * 4.9);

  return {
    width,
    height,
    padX,
    padY,
    scaleX,
    scaleY,
    avgScale: Math.min(scaleX, scaleY),
    boxW,
    boxH,
    toX: (x) => padX + x * scaleX,
    toY: (y) => padY + y * scaleY
  };
}

export function getBayGeometry(layout, bay, state) {
  const parent = state.bayParentsById.get(bay.nodeId);
  const bx = layout.toX(bay.x);
  const by = layout.toY(bay.y);

  let opens = Math.abs(bay.y - 4) < 2 || Math.abs(bay.y - 44) < 2 ? "down" : "up";
  if (parent) {
    const dx = bay.x - parent.x;
    const dy = bay.y - parent.y;
    if (Math.abs(dx) > Math.abs(dy)) {
      opens = dx > 0 ? "left" : "right";
    } else {
      opens = dy < 0 ? "down" : "up";
    }
  }

  const isHorizontal = opens === "left" || opens === "right";
  const w = isHorizontal ? Math.max(22, layout.scaleX * 4.9) : layout.boxW;
  const h = isHorizontal ? Math.max(18, layout.scaleY * 2.9) : layout.boxH;

  return { bx, by, w, h, opens, isHorizontal };
}

export function getVmsRect(layout, node) {
  const nx = layout.toX(node.x);
  const ny = layout.toY(node.y);
  // South corridor VMS sits south of road line so it never collides with y=44 bays above
  const signY = node.y >= 48 ? ny + 7 : ny - 18;
  return { nx, ny, signX: nx - 10, signY, w: 20, h: 12 };
}

export function hitTestCanvas(layout, floor, state, canvasX, canvasY) {
  // 1. Moving vehicles on current floor (top-most layer)
  for (const session of state.sessions.values()) {
    if (session.currentFloor !== floor) continue;
    const cx = layout.toX(session.renderX);
    const cy = layout.toY(session.renderY);
    if (Math.hypot(canvasX - cx, canvasY - cy) <= 16) {
      return { kind: "vehicle", session };
    }
  }

  // 2. Clickable ramp portals leading to adjacent floor
  for (const spec of getRampSpecs(layout, floor)) {
    if (spec.targetFloor === null) continue;
    if (
      canvasX >= spec.left &&
      canvasX <= spec.left + spec.w &&
      canvasY >= spec.top &&
      canvasY <= spec.top + spec.h
    ) {
      return { kind: "ramp", ramp: spec };
    }
  }

  // 3. Parking stalls
  for (const bay of state.baysById.values()) {
    if (bay.floor !== floor) continue;
    const geom = getBayGeometry(layout, bay, state);
    if (Math.abs(canvasX - geom.bx) <= geom.w / 2 && Math.abs(canvasY - geom.by) <= geom.h / 2) {
      return { kind: "bay", bay };
    }
  }

  // 4. VMS LED panels
  for (const node of state.nodesById.values()) {
    if (node.floor !== floor || !node.ledPanelId) continue;
    const rect = getVmsRect(layout, node);
    if (
      canvasX >= rect.signX &&
      canvasX <= rect.signX + rect.w &&
      canvasY >= rect.signY &&
      canvasY <= rect.signY + rect.h
    ) {
      return { kind: "vms", node, panelId: node.ledPanelId };
    }
  }

  return null;
}

function lerpAngle(current, target, factor) {
  let diff = target - current;
  while (diff > Math.PI) diff -= Math.PI * 2;
  while (diff < -Math.PI) diff += Math.PI * 2;
  return current + diff * factor;
}

function drawRoundedRectPath(ctx, x, y, w, h, r) {
  const radius = Math.min(r, w / 2, h / 2);
  ctx.beginPath();
  ctx.moveTo(x + radius, y);
  ctx.lineTo(x + w - radius, y);
  ctx.quadraticCurveTo(x + w, y, x + w, y + radius);
  ctx.lineTo(x + w, y + h - radius);
  ctx.quadraticCurveTo(x + w, y + h, x + w - radius, y + h);
  ctx.lineTo(x + radius, y + h);
  ctx.quadraticCurveTo(x, y + h, x, y + h - radius);
  ctx.lineTo(x, y + radius);
  ctx.quadraticCurveTo(x, y, x + radius, y);
  ctx.closePath();
}

export function drawTopDownCar(ctx, cx, cy, w, h, headingRad, bodyColor, trimColor, showHeadlights, alpha = 1) {
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.translate(cx, cy);
  ctx.rotate(headingRad);

  if (showHeadlights) {
    const beamGrad = ctx.createLinearGradient(w * 0.4, 0, w * 1.9, 0);
    beamGrad.addColorStop(0, "rgba(255, 240, 200, 0.16)");
    beamGrad.addColorStop(1, "rgba(255, 240, 200, 0)");
    ctx.fillStyle = beamGrad;
    ctx.beginPath();
    ctx.moveTo(w * 0.42, -h * 0.32);
    ctx.lineTo(w * 1.9, -h * 0.95);
    ctx.lineTo(w * 1.9, h * 0.95);
    ctx.lineTo(w * 0.42, h * 0.32);
    ctx.closePath();
    ctx.fill();
  }

  ctx.fillStyle = "rgba(0, 0, 0, 0.45)";
  drawRoundedRectPath(ctx, -w / 2 + 1, -h / 2 + 1.5, w, h, 3);
  ctx.fill();

  ctx.fillStyle = bodyColor;
  ctx.strokeStyle = trimColor;
  ctx.lineWidth = 1.1;
  drawRoundedRectPath(ctx, -w / 2, -h / 2, w, h, 3);
  ctx.fill();
  ctx.stroke();

  ctx.fillStyle = "#11161d";
  drawRoundedRectPath(ctx, -w * 0.22, -h * 0.36, w * 0.50, h * 0.72, 2);
  ctx.fill();

  ctx.fillStyle = "rgba(255, 255, 255, 0.13)";
  drawRoundedRectPath(ctx, -w * 0.16, -h * 0.28, w * 0.30, h * 0.56, 1.5);
  ctx.fill();

  ctx.fillStyle = "#fde68a";
  ctx.fillRect(w * 0.40, -h * 0.36, Math.max(1.8, w * 0.08), Math.max(2, h * 0.18));
  ctx.fillRect(w * 0.40, h * 0.18, Math.max(1.8, w * 0.08), Math.max(2, h * 0.18));

  ctx.fillStyle = "#e05353";
  ctx.fillRect(-w * 0.48, -h * 0.35, Math.max(1.5, w * 0.07), Math.max(2, h * 0.18));
  ctx.fillRect(-w * 0.48, h * 0.17, Math.max(1.5, w * 0.07), Math.max(2, h * 0.18));

  ctx.restore();
}

export function drawParkingBays(ctx, layout, floor, state, now) {
  const pinnedSession = state.selectedSessionId ? state.sessions.get(state.selectedSessionId) : null;
  const pinnedTargetBayId = pinnedSession?.assignedBayId || null;

  for (const bay of state.baysById.values()) {
    if (bay.floor !== floor) continue;

    const { bx, by, w, h, opens, isHorizontal } = getBayGeometry(layout, bay, state);
    const left = bx - w / 2;
    const right = bx + w / 2;
    const top = by - h / 2;
    const bottom = by + h / 2;

    const isMaint = bay.status === "OutOfService";
    const isLeased = bay.status === "Leased";
    const isOccupied = bay.status === "Occupied";
    const isHovered = state.hoveredBayId === bay.bayId;
    const isSelected = state.selectedBayId === bay.bayId;
    const isPinnedTarget = pinnedTargetBayId === bay.bayId;

    if (isLeased) {
      const pulse = 0.12 + 0.07 * Math.sin(now * 0.006);
      ctx.fillStyle = `rgba(217, 138, 43, ${pulse.toFixed(3)})`;
    } else if (isOccupied) {
      ctx.fillStyle = "rgba(209, 75, 75, 0.10)";
    } else if (isMaint) {
      ctx.fillStyle = "rgba(75, 85, 99, 0.16)";
    } else if (bay.primaryType === "Pcd") {
      ctx.fillStyle = "rgba(56, 136, 184, 0.08)";
    } else if (bay.primaryType === "EV") {
      ctx.fillStyle = "rgba(40, 158, 140, 0.08)";
    } else {
      ctx.fillStyle = floor === 2 ? "#212730" : "#181c21";
    }
    ctx.fillRect(left + 1, top + 1, w - 2, h - 2);

    const stripeColor = isSelected
      ? "#d98a2b"
      : isHovered
        ? "#f1f5f9"
        : isMaint
          ? "#6b7280"
          : isLeased
            ? "#d98a2b"
            : isOccupied
              ? "#8c3b3b"
              : bay.primaryType === "Pcd"
                ? "#3b82b6"
                : bay.primaryType === "EV"
                  ? "#289e8c"
                  : "#4b5563";

    ctx.beginPath();
    if (opens === "down") {
      ctx.moveTo(left + 1, bottom);
      ctx.lineTo(left + 1, top + 1);
      ctx.lineTo(right - 1, top + 1);
      ctx.lineTo(right - 1, bottom);
    } else if (opens === "up") {
      ctx.moveTo(left + 1, top);
      ctx.lineTo(left + 1, bottom - 1);
      ctx.lineTo(right - 1, bottom - 1);
      ctx.lineTo(right - 1, top);
    } else if (opens === "left") {
      ctx.moveTo(left, top + 1);
      ctx.lineTo(right - 1, top + 1);
      ctx.lineTo(right - 1, bottom - 1);
      ctx.lineTo(left, bottom - 1);
    } else {
      ctx.moveTo(right, top + 1);
      ctx.lineTo(left + 1, top + 1);
      ctx.lineTo(left + 1, bottom - 1);
      ctx.lineTo(right, bottom - 1);
    }
    ctx.strokeStyle = stripeColor;
    ctx.lineWidth = isSelected || isHovered ? 2.0 : 1.3;
    if (isMaint) ctx.setLineDash([3, 3]);
    ctx.stroke();
    ctx.setLineDash([]);

    // Wheel-stop concrete curb bar
    ctx.fillStyle = bay.primaryType === "EV" ? "#289e8c" : "#38414e";
    if (opens === "down") {
      ctx.fillRect(left + w * 0.2, top + 3.5, w * 0.6, 2);
    } else if (opens === "up") {
      ctx.fillRect(left + w * 0.2, bottom - 5.5, w * 0.6, 2);
    } else if (opens === "left") {
      ctx.fillRect(right - 5.5, top + h * 0.2, 2, h * 0.6);
    } else {
      ctx.fillRect(left + 3.5, top + h * 0.2, 2, h * 0.6);
    }

    if (isMaint) {
      ctx.save();
      ctx.beginPath();
      ctx.rect(left + 2, top + 2, w - 4, h - 4);
      ctx.clip();
      ctx.strokeStyle = "rgba(217, 138, 43, 0.35)";
      ctx.lineWidth = 2;
      for (let sx = left - h; sx < right + h; sx += 7) {
        ctx.beginPath();
        ctx.moveTo(sx, bottom);
        ctx.lineTo(sx + h, top);
        ctx.stroke();
      }
      ctx.restore();
    } else if (isOccupied) {
      const carLen = Math.min((isHorizontal ? w : h) * 0.66, 24);
      const carWid = bay.primaryType === "Moto"
        ? Math.max(7, (isHorizontal ? h : w) * 0.34)
        : Math.min((isHorizontal ? h : w) * 0.64, 15);
      const parkedHeading =
        opens === "down" ? -Math.PI / 2 :
        opens === "up" ? Math.PI / 2 :
        opens === "left" ? 0 : Math.PI;
      const bodyHex = bay.primaryType === "EV" ? "#4b8078" : bay.primaryType === "SUV" ? "#5d6775" : "#515b69";
      drawTopDownCar(ctx, bx, by, carLen, carWid, parkedHeading, bodyHex, "#94a3b8", false);
    } else {
      ctx.fillStyle = isLeased ? "#f3c78a" : "#c0c9d6";
      ctx.font = "700 8px Cascadia Mono, Consolas, monospace";
      ctx.textAlign = "center";
      ctx.fillText(bay.bayId.replace(`F${floor}-`, ""), bx, by - 1);

      ctx.fillStyle = isLeased ? "#d98a2b" : "#717c8c";
      ctx.font = "600 7px Cascadia Mono, Consolas, monospace";
      const tag = isLeased && bay.leaseExpiresAtUtc
        ? `${Math.max(0, Math.ceil((Date.parse(bay.leaseExpiresAtUtc) - Date.now()) / 1000))}s`
        : bay.primaryType.toUpperCase();
      ctx.fillText(tag, bx, by + 9);
    }

    // Target acquisition reticle around the bay assigned to the pinned vehicle
    if (isPinnedTarget) {
      const pad = 2.5 + 1.5 * Math.sin(now * 0.008);
      ctx.strokeStyle = "#f59e0b";
      ctx.lineWidth = 1.6;
      const c = 5;
      // 4 CAD corner brackets
      ctx.beginPath();
      ctx.moveTo(left - pad, top - pad + c);
      ctx.lineTo(left - pad, top - pad);
      ctx.lineTo(left - pad + c, top - pad);
      ctx.moveTo(right + pad - c, top - pad);
      ctx.lineTo(right + pad, top - pad);
      ctx.lineTo(right + pad, top - pad + c);
      ctx.moveTo(left - pad, bottom + pad - c);
      ctx.lineTo(left - pad, bottom + pad);
      ctx.lineTo(left - pad + c, bottom + pad);
      ctx.moveTo(right + pad - c, bottom + pad);
      ctx.lineTo(right + pad, bottom + pad);
      ctx.lineTo(right + pad, bottom + pad - c);
      ctx.stroke();
    }

    // Ultrasonic LED sensor dot
    const ledX = opens === "left" ? left + 1.5 : opens === "right" ? right - 1.5 : bx;
    const ledY = opens === "down" ? bottom - 1.5 : opens === "up" ? top + 1.5 : by;
    ctx.beginPath();
    ctx.arc(ledX, ledY, 2.2, 0, Math.PI * 2);
    ctx.fillStyle = isMaint
      ? "#6b7280"
      : isOccupied
        ? "#d14b4b"
        : isLeased
          ? "#d98a2b"
          : "#2f9e66";
    ctx.fill();
  }
}

function directionArrow(direction) {
  switch (direction) {
    case "Left": return "←";
    case "Right": return "→";
    case "UpRamp": return "▲";
    case "DownRamp": return "▼";
    case "Arrive": return "●";
    default: return "→";
  }
}

export function drawNodesAndVmsSigns(ctx, layout, floor, state, now, hoveredVmsId) {
  for (const node of state.nodesById.values()) {
    if (node.floor !== floor || !node.ledPanelId) continue;

    const { nx, signX, signY, w, h } = getVmsRect(layout, node);
    const panel = state.ledPanels.get(node.ledPanelId);
    const flashing = panel && panel.flashUntil > now;
    const isHovered = hoveredVmsId === node.ledPanelId;

    ctx.fillStyle = flashing ? "#24190a" : "#121519";
    ctx.strokeStyle = isHovered ? "#e3e7ed" : flashing ? "#d98a2b" : "#37404d";
    ctx.lineWidth = isHovered ? 1.5 : 1.1;
    ctx.fillRect(signX, signY, w, h);
    ctx.strokeRect(signX, signY, w, h);

    ctx.fillStyle = flashing ? "#f59e0b" : "#6c7685";
    ctx.font = "700 8.5px Cascadia Mono, Consolas, monospace";
    ctx.textAlign = "center";
    ctx.fillText(directionArrow(panel?.direction), nx, signY + 9);
  }
}

export function drawActiveRoutesAndVehicles(ctx, layout, floor, state, now, options) {
  const { toX, toY } = layout;

  // Telemetry vector routes (animated along travel direction)
  if (options?.showRoutes !== false) {
    for (const session of state.sessions.values()) {
      const nodeIds = session.route?.nodeIds || [];
      const isFlash = session.rerouteFlashUntil > now;
      const isPinned = state.selectedSessionId === session.vehicleSessionId;

      ctx.save();
      ctx.beginPath();
      let started = false;
      for (const id of nodeIds) {
        const node = state.nodesById.get(id);
        if (!node || node.floor !== floor) {
          started = false;
          continue;
        }
        if (!started) {
          ctx.moveTo(toX(node.x), toY(node.y));
          started = true;
        } else {
          ctx.lineTo(toX(node.x), toY(node.y));
        }
      }
      ctx.setLineDash(isPinned ? [6, 4] : [4, 4]);
      ctx.lineDashOffset = -(now * (isPinned ? 0.036 : 0.02));
      ctx.strokeStyle = isFlash
        ? "rgba(209, 75, 75, 0.9)"
        : isPinned
          ? "rgba(217, 138, 43, 0.92)"
          : "rgba(56, 136, 184, 0.62)";
      ctx.lineWidth = isPinned ? 2.6 : isFlash ? 2.2 : 1.5;
      ctx.stroke();
      ctx.restore();
    }
  }

  // Smooth departure ghosts pulling out toward ramps/exits
  if (Array.isArray(state.departingGhosts)) {
    for (let i = state.departingGhosts.length - 1; i >= 0; i--) {
      const g = state.departingGhosts[i];
      if (g.waypointIdx >= g.waypoints.length) {
        state.departingGhosts.splice(i, 1);
        continue;
      }
      const target = g.waypoints[g.waypointIdx];
      const dx = target.x - g.x;
      const dy = target.y - g.y;
      const dist = Math.hypot(dx, dy);
      if (dist < 0.6) {
        g.waypointIdx++;
        if (g.waypointIdx >= g.waypoints.length) {
          state.departingGhosts.splice(i, 1);
          continue;
        }
      } else {
        const step = Math.min(dist, 0.24);
        g.x += (dx / dist) * step;
        g.y += (dy / dist) * step;
        g.headingRad = lerpAngle(g.headingRad, Math.atan2(dy, dx), 0.35);
      }

      if (g.floor === floor) {
        const fade = Math.max(0.2, 1 - g.waypointIdx / Math.max(1, g.waypoints.length));
        drawTopDownCar(ctx, toX(g.x), toY(g.y), 17, 9, g.headingRad, "#5d6775", "#94a3b8", false, fade * 0.78);
      }
    }
  }

  // Active moving vehicles
  for (const session of state.sessions.values()) {
    const dx = session.targetX - session.renderX;
    const dy = session.targetY - session.renderY;
    if (Math.hypot(dx, dy) > 0.05) {
      const desiredHeading = Math.atan2(dy, dx);
      session.headingRad = lerpAngle(session.headingRad ?? desiredHeading, desiredHeading, 0.25);
    }
    session.renderX += dx * 0.18;
    session.renderY += dy * 0.18;

    if (session.currentFloor !== floor) continue;

    const cx = toX(session.renderX);
    const cy = toY(session.renderY);
    const rerouted = session.rerouteFlashUntil > now;
    const isPinned = state.selectedSessionId === session.vehicleSessionId;
    const isHovered = state.hoveredSessionId === session.vehicleSessionId;

    const isMoto = session.vehicleType === "Moto";
    const isSuv = session.vehicleType === "SUV";
    const isEv = session.vehicleType === "EV";
    const vLen = isMoto ? 12 : isSuv ? 22 : 18;
    const vWid = isMoto ? 6 : isSuv ? 12 : 10;

    const bodyColor = rerouted
      ? "#d14b4b"
      : isEv
        ? "#289e8c"
        : isSuv
          ? "#b8893a"
          : "#4d91c4";
    const trimColor = isPinned ? "#fde68a" : rerouted ? "#fca5a5" : "#e2e8f0";

    if (isPinned || isHovered) {
      ctx.beginPath();
      ctx.arc(cx, cy, 15, 0, Math.PI * 2);
      ctx.strokeStyle = isPinned ? "#d98a2b" : "#9aa3b0";
      ctx.lineWidth = isPinned ? 1.6 : 1.1;
      ctx.stroke();
    }

    drawTopDownCar(ctx, cx, cy, vLen, vWid, session.headingRad ?? 0, bodyColor, trimColor, true);

    const tag = session.vehicleSessionId.slice(-4);
    ctx.fillStyle = isPinned ? "#24190a" : "rgba(14, 16, 19, 0.88)";
    ctx.fillRect(cx - 14, cy - 22, 28, 11);
    ctx.strokeStyle = isPinned ? "#d98a2b" : rerouted ? "#d14b4b" : "#37404d";
    ctx.lineWidth = 0.8;
    ctx.strokeRect(cx - 14, cy - 22, 28, 11);

    ctx.fillStyle = isPinned ? "#fde68a" : "#e3e7ed";
    ctx.font = "700 8px Cascadia Mono, Consolas, monospace";
    ctx.textAlign = "center";
    ctx.fillText(tag, cx, cy - 13.5);
  }
}

export function drawCadOverlay(ctx, layout, floor, state, hoverInfo) {
  const { width, height, scaleX } = layout;
  const meta = FLOOR_META[floor] || FLOOR_META[0];

  let free = 0;
  let leased = 0;
  let occupied = 0;
  let maint = 0;
  for (const bay of state.baysById.values()) {
    if (bay.floor !== floor) continue;
    if (bay.status === "Free") free++;
    else if (bay.status === "Leased") leased++;
    else if (bay.status === "Occupied") occupied++;
    else maint++;
  }

  // Dynamic floor CAD title & live counters stamp (bottom-right)
  const stampText = `${meta.code} // ${free} FREE · ${leased} LSD · ${occupied} OCC · ${maint} MAINT`;
  const stampW = Math.max(256, stampText.length * 5.8 + 18);
  const stampH = 24;
  const sx = width - stampW - 12;
  const sy = height - stampH - 8;
  ctx.fillStyle = "rgba(16, 18, 21, 0.92)";
  ctx.fillRect(sx, sy, stampW, stampH);
  ctx.strokeStyle = "#272d36";
  ctx.lineWidth = 1;
  ctx.strokeRect(sx, sy, stampW, stampH);

  ctx.fillStyle = "#8a94a3";
  ctx.font = "600 8.5px Cascadia Mono, Consolas, monospace";
  ctx.textAlign = "center";
  ctx.fillText(stampText, sx + stampW / 2, sy + 15.5);

  // Architectural 10m graphic scale bar (top-right inside deck frame)
  const barPx = scaleX * 10;
  const barX = width - barPx - 14;
  const barY = 16;
  ctx.strokeStyle = "#485261";
  ctx.lineWidth = 1.2;
  ctx.beginPath();
  ctx.moveTo(barX, barY - 3);
  ctx.lineTo(barX, barY + 2);
  ctx.lineTo(barX + barPx, barY + 2);
  ctx.lineTo(barX + barPx, barY - 3);
  ctx.stroke();
  ctx.fillStyle = "#6c7685";
  ctx.font = "600 8px Cascadia Mono, Consolas, monospace";
  ctx.textAlign = "right";
  ctx.fillText("SCALE 10m", barX - 6, barY + 2);

  // Hovered item CAD inspector tooltip (bottom-left)
  let tipText = null;
  let tipAccent = "#d98a2b";
  if (hoverInfo?.kind === "vehicle") {
    const s = hoverInfo.session;
    tipText = `VEHICLE ${s.vehicleSessionId} · ${s.vehicleType.toUpperCase()} -> ${s.assignedBayId} [FENCE #${s.fencingToken}] · CLICK TO FOLLOW`;
    tipAccent = "#3888b8";
  } else if (hoverInfo?.kind === "ramp") {
    const r = hoverInfo.ramp;
    tipText = `RAMP PORTAL ${r.title} · CLICK TO SWITCH TO FLOOR F${r.targetFloor}`;
    tipAccent = r.accentColor;
  } else if (hoverInfo?.kind === "bay") {
    const b = hoverInfo.bay;
    const ttl = b.status === "Leased" && b.leaseExpiresAtUtc
      ? ` (${Math.max(0, Math.ceil((Date.parse(b.leaseExpiresAtUtc) - Date.now()) / 1000))}s)`
      : "";
    const sess = b.currentSessionId ? ` · ${b.currentSessionId}` : "";
    const fence = b.fencingToken > 0 ? ` · FENCE #${b.fencingToken}` : "";
    tipText = `${b.bayId} · ${b.primaryType.toUpperCase()} · ${b.status}${ttl}${sess}${fence} · CLICK INSPECT / SHIFT+CLICK MAINT`;
    tipAccent = "#d98a2b";
  } else if (hoverInfo?.kind === "vms") {
    const panel = state.ledPanels.get(hoverInfo.panelId);
    const dir = panel?.direction || "Idle";
    tipText = `VMS LED ${hoverInfo.panelId} @ NODE ${hoverInfo.node.id} · DIR: ${dir}`;
    tipAccent = "#e09f3e";
  }

  if (tipText) {
    const tipW = Math.min(width - 28, Math.max(220, tipText.length * 6.0 + 18));
    const tx = 12;
    const ty = height - 32;

    ctx.fillStyle = "rgba(16, 18, 21, 0.94)";
    ctx.fillRect(tx, ty, tipW, 24);
    ctx.strokeStyle = tipAccent;
    ctx.lineWidth = 1;
    ctx.strokeRect(tx, ty, tipW, 24);

    ctx.fillStyle = "#e3e7ed";
    ctx.font = "600 9px Cascadia Mono, Consolas, monospace";
    ctx.textAlign = "left";
    ctx.fillText(tipText, tx + 9, ty + 15);
  }
}
