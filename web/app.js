import { SignalRLiteClient } from "./signalr-lite.js";
import {
  FLOOR_META,
  drawDeckBackground,
  drawDrivingCorridors,
  drawFloorRampsAndGates
} from "./floor-renderers.js";
import {
  getCanvasLayout,
  hitTestCanvas,
  drawParkingBays,
  drawNodesAndVmsSigns,
  drawActiveRoutesAndVehicles,
  drawCadOverlay
} from "./canvas-view.js";

const state = {
  tenantId: "default",
  selectedFloor: 0,
  nodesById: new Map(),
  edges: [],
  baysById: new Map(),
  bayParentsById: new Map(),
  sessions: new Map(),
  ledPanels: new Map(),
  gateFlashUntil: new Map(),
  departingGhosts: [],
  metrics: null,
  autoDemo: false,
  demoTimer: null,
  demoCounter: 1,
  hoveredBayId: null,
  hoveredSessionId: null,
  hoveredRampId: null,
  hoveredVmsId: null,
  hoveredItem: null,
  selectedBayId: null,
  selectedSessionId: null,
  options: {
    showRoutes: true,
    showHeatmap: false,
    autoFollowFloor: false
  }
};

const TENANT_REGEX = /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/;

const canvas = document.getElementById("garageCanvas");
const ctx = canvas.getContext("2d");
const connDot = document.getElementById("connDot");
const connLabel = document.getElementById("connLabel");
const eventFeed = document.getElementById("eventFeed");
const inspectorEmptyEl = document.getElementById("inspectorEmpty");
const inspectorContentEl = document.getElementById("inspectorContent");
const fleetChipsEl = document.getElementById("fleetChips");
const fleetCountCodeEl = document.getElementById("fleetCountCode");

const client = new SignalRLiteClient("/hubs/parking");

function apiFetch(url, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set("X-Tenant-Id", state.tenantId);
  return fetch(url, { ...options, headers });
}

client.onStateChange((connState) => {
  connDot.className = `dot ${connState === "connected" ? "connected" : connState === "disconnected" ? "disconnected" : ""}`;
  connLabel.textContent = connState === "connected" ? "SIGNALR LIVE" : connState === "disconnected" ? "RECONNECTING" : "ERROR";
});

client.on("RouteAssigned", (session) => {
  upsertSession(session, false);
  const bay = state.baysById.get(session.assignedBayId);
  if (bay) {
    bay.fencingToken = session.fencingToken;
  }
  const startNodeId = session.route?.nodeIds?.[0];
  if (startNodeId === "GATE-N1" || startNodeId === "GATE-S1") {
    state.gateFlashUntil.set(startNodeId, performance.now() + 1400);
  }
  logEvent(
    `Assigned ${session.vehicleSessionId} (${session.vehicleType}) -> ${session.assignedBayId} [fence:${session.fencingToken}]`,
    "assigned",
    { sessionId: session.vehicleSessionId, bayId: session.assignedBayId }
  );
  refreshLiveUi();
});

client.on("RouteReassigned", (payload) => {
  const displacedBay = state.baysById.get(payload.displacedBayId);
  const trigger = displacedBay?.status === "OutOfService" ? "MAINT" : "INTRUDER";
  if (payload.newSession) {
    const wasSelected = state.selectedSessionId === payload.displacedSessionId;
    upsertSession(payload.newSession, true);
    const nextBay = state.baysById.get(payload.newSession.assignedBayId);
    if (nextBay) {
      nextBay.fencingToken = payload.newSession.fencingToken;
    }
    if (wasSelected) {
      state.selectedSessionId = payload.newSession.vehicleSessionId;
    }
    logEvent(
      `${trigger} @ ${payload.displacedBayId} -> rerouted ${payload.displacedSessionId} to ${payload.newSession.assignedBayId}`,
      "reroute",
      { sessionId: payload.newSession.vehicleSessionId, bayId: payload.newSession.assignedBayId }
    );
  } else {
    state.sessions.delete(payload.displacedSessionId);
    if (state.selectedSessionId === payload.displacedSessionId) {
      state.selectedSessionId = null;
    }
    logEvent(
      `${trigger} @ ${payload.displacedBayId} -> no alternative bay for ${payload.displacedSessionId}`,
      "reroute",
      { bayId: payload.displacedBayId }
    );
  }
  refreshLiveUi();
});

client.on("BayStateChanged", (delta) => {
  const bay = state.baysById.get(delta.bayId);
  if (!bay) return;

  const wasOccupied = bay.status === "Occupied";
  bay.status = delta.status;
  bay.currentSessionId = delta.sessionId;
  bay.leaseExpiresAtUtc = delta.leaseExpiresAtUtc;

  if (wasOccupied && delta.status === "Free") {
    spawnDepartureGhost(bay);
  }

  if (delta.status === "Free" || delta.status === "OutOfService") {
    for (const [sid, s] of state.sessions.entries()) {
      if (s.assignedBayId === delta.bayId) {
        state.sessions.delete(sid);
        if (state.selectedSessionId === sid) {
          state.selectedSessionId = null;
        }
      }
    }
  }

  if (delta.status === "Occupied" || delta.status === "Free" || delta.status === "OutOfService") {
    logEvent(`Bay ${delta.bayId} -> ${delta.status}`, delta.status.toLowerCase(), { bayId: delta.bayId });
  }

  refreshLiveUi();
});

client.on("VehicleMoved", (delta) => {
  const s = state.sessions.get(delta.vehicleSessionId);
  if (!s) return;

  s.lastKnownNodeId = delta.currentNodeId;
  s.targetX = delta.x;
  s.targetY = delta.y;
  s.currentFloor = delta.floor;

  if (state.options.autoFollowFloor && state.selectedSessionId === delta.vehicleSessionId && state.selectedFloor !== delta.floor) {
    selectFloor(delta.floor);
  }

  updateFloorBadges();
  if (state.selectedSessionId === delta.vehicleSessionId) {
    renderInspectorBar();
  }
});

client.on("LedPanelUpdated", (panel) => {
  state.ledPanels.set(panel.panelId, {
    ...panel,
    flashUntil: performance.now() + 2500
  });
});

client.on("MetricsTick", (metrics) => {
  state.metrics = metrics;
  renderMetrics(metrics);
});

function spawnDepartureGhost(bay) {
  const parent = state.bayParentsById.get(bay.nodeId);
  const exitPt = bay.floor === 0
    ? { x: 0, y: bay.y < 30 ? 10 : 50 }
    : { x: 5, y: 30 };

  const waypoints = [];
  if (parent) {
    waypoints.push({ x: parent.x, y: parent.y });
    if (bay.floor === 0) {
      waypoints.push({ x: 10, y: parent.y < 30 ? 10 : 50 });
    } else {
      waypoints.push({ x: 10, y: parent.y });
      waypoints.push({ x: 10, y: 30 });
    }
  }
  waypoints.push(exitPt);

  const first = waypoints[0];
  state.departingGhosts.push({
    x: bay.x,
    y: bay.y,
    floor: bay.floor,
    headingRad: Math.atan2(first.y - bay.y, first.x - bay.x),
    waypoints,
    waypointIdx: 0
  });
}

function computeRouteInitialHeading(session, startNode) {
  const nodeIds = session.route?.nodeIds || [];
  if (nodeIds.length >= 2) {
    const n0 = state.nodesById.get(nodeIds[0]);
    const n1 = state.nodesById.get(nodeIds[1]);
    if (n0 && n1 && (n0.x !== n1.x || n0.y !== n1.y)) {
      return Math.atan2(n1.y - n0.y, n1.x - n0.x);
    }
  }
  return startNode && startNode.x > 80 ? Math.PI : 0;
}

function upsertSession(session, isRerouted) {
  const startNode = state.nodesById.get(session.lastKnownNodeId || session.route.nodeIds[0]);
  const existing = state.sessions.get(session.vehicleSessionId);
  const initialHeading = computeRouteInitialHeading(session, startNode);

  state.sessions.set(session.vehicleSessionId, {
    ...session,
    renderX: existing ? existing.renderX : startNode ? startNode.x : 0,
    renderY: existing ? existing.renderY : startNode ? startNode.y : 10,
    targetX: startNode ? startNode.x : 0,
    targetY: startNode ? startNode.y : 10,
    headingRad: existing ? existing.headingRad : initialHeading,
    currentFloor: startNode ? startNode.floor : 0,
    rerouteFlashUntil: isRerouted ? performance.now() + 3000 : 0,
    stepCursor: existing && existing.assignedBayId === session.assignedBayId ? existing.stepCursor : 0
  });
}

function logEvent(text, kind, target = null) {
  const li = document.createElement("li");
  li.className = kind;
  const ts = new Date().toLocaleTimeString([], { hour12: false });
  li.textContent = `[${ts}] ${text}`;

  if (target?.bayId || target?.sessionId) {
    li.classList.add("clickable");
    li.title = "Click to jump to bay/vehicle";
    li.addEventListener("click", () => {
      if (target.sessionId && state.sessions.has(target.sessionId)) {
        selectSession(target.sessionId, true);
      } else if (target.bayId) {
        const b = state.baysById.get(target.bayId);
        if (b) {
          selectFloor(b.floor);
          state.selectedSessionId = null;
          state.selectedBayId = b.bayId;
          renderInspectorBar();
        }
      }
    });
  }

  eventFeed.prepend(li);
  while (eventFeed.children.length > 35) {
    eventFeed.removeChild(eventFeed.lastChild);
  }
}

function applySnapshot(snapshot) {
  state.nodesById.clear();
  for (const node of snapshot.nodes) {
    state.nodesById.set(node.id, node);
  }

  state.edges = snapshot.edges;
  state.baysById.clear();
  for (const bay of snapshot.bays) {
    state.baysById.set(bay.bayId, bay);
  }

  state.bayParentsById.clear();
  for (const edge of snapshot.edges) {
    const fromNode = state.nodesById.get(edge.fromNodeId);
    const toNode = state.nodesById.get(edge.toNodeId);
    if (fromNode && toNode && toNode.type === "Bay") {
      state.bayParentsById.set(toNode.id, fromNode);
    }
  }

  state.sessions.clear();
  for (const session of snapshot.activeSessions) {
    upsertSession(session, false);
  }

  state.ledPanels.clear();
  for (const panel of snapshot.ledPanels || []) {
    state.ledPanels.set(panel.panelId, { ...panel, flashUntil: 0 });
  }

  if (snapshot.metrics) {
    state.metrics = snapshot.metrics;
    renderMetrics(snapshot.metrics);
  }

  if (state.selectedBayId && !state.baysById.has(state.selectedBayId)) {
    state.selectedBayId = null;
  }
  if (state.selectedSessionId && !state.sessions.has(state.selectedSessionId)) {
    state.selectedSessionId = null;
  }

  refreshLiveUi();
}

function refreshLiveUi() {
  updateFloorBadges();
  renderFleetChips();
  renderInspectorBar();
}

function updateFloorBadges() {
  for (let floor = 0; floor < 3; floor++) {
    let freeCount = 0;
    for (const bay of state.baysById.values()) {
      if (bay.floor === floor && bay.status === "Free") freeCount++;
    }
    const freeEl = document.getElementById(`floorBadge${floor}`);
    if (freeEl) freeEl.textContent = String(freeCount);

    let carCount = 0;
    for (const s of state.sessions.values()) {
      if (s.currentFloor === floor) carCount++;
    }
    const carEl = document.getElementById(`floorCars${floor}`);
    if (carEl) {
      carEl.textContent = `●${carCount}`;
      carEl.classList.toggle("hidden", carCount === 0);
    }
  }
}

function renderMetrics(m) {
  const used = m.leasedBays + m.occupiedBays;
  const pct = m.totalBays > 0 ? Math.round((used / m.totalBays) * 100) : 0;

  document.getElementById("kpiOccupancy").textContent = `${pct}%`;
  document.getElementById("kpiActive").textContent = String(m.activeSessions);
  document.getElementById("kpiReroutes").textContent = String(m.reroutesTotal);
  document.getElementById("kpiContention").textContent = String(m.contentionRetriesTotal);

  document.getElementById("astarLatency").textContent =
    `${m.aStarLatency.p50Ms.toFixed(2)} / ${m.aStarLatency.p95Ms.toFixed(2)} / ${m.aStarLatency.p99Ms.toFixed(2)} ms`;
  document.getElementById("redisLatency").textContent =
    `${m.redisLeaseLatency.p50Ms.toFixed(2)} / ${m.redisLeaseLatency.p95Ms.toFixed(2)} ms`;
  document.getElementById("channelStats").textContent =
    `${m.telemetry.enqueuedCount} / ${m.telemetry.confirmedTransitionsCount}`;
  document.getElementById("bayCounts").textContent =
    `${m.freeBays} / ${m.leasedBays} / ${m.occupiedBays} / ${m.outOfServiceBays ?? 0}`;
}

function renderFleetChips() {
  const active = Array.from(state.sessions.values());
  fleetCountCodeEl.textContent = `${active.length} LIVE`;

  if (active.length === 0) {
    const empty = document.createElement("span");
    empty.className = "fleet-empty";
    empty.textContent = "No vehicles currently navigating";
    fleetChipsEl.replaceChildren(empty);
    return;
  }

  const now = performance.now();
  const chips = active.map((s) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn fleet-chip";
    if (state.selectedSessionId === s.vehicleSessionId) btn.classList.add("selected");
    if (s.rerouteFlashUntil > now) btn.classList.add("rerouted");
    btn.textContent = `${s.vehicleSessionId.slice(-6)} · F${s.currentFloor}→${s.assignedBayId.replace(/^F\d-/, "")}`;
    btn.title = `${s.vehicleSessionId} (${s.vehicleType}) -> ${s.assignedBayId}. Click to follow.`;
    btn.addEventListener("click", () => selectSession(s.vehicleSessionId, true));
    return btn;
  });

  fleetChipsEl.replaceChildren(...chips);
}

function makeInspectorButton(label, cls, onClick) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = `btn small ${cls}`;
  btn.textContent = label;
  btn.addEventListener("click", onClick);
  return btn;
}

function renderInspectorBar() {
  const session = state.selectedSessionId ? state.sessions.get(state.selectedSessionId) : null;
  const bay = !session && state.selectedBayId ? state.baysById.get(state.selectedBayId) : null;

  if (!session && !bay) {
    inspectorEmptyEl.classList.remove("hidden");
    inspectorContentEl.classList.add("hidden");
    inspectorContentEl.replaceChildren();
    return;
  }

  inspectorEmptyEl.classList.add("hidden");
  inspectorContentEl.classList.remove("hidden");

  const meta = document.createElement("div");
  meta.className = "inspector-meta";
  const actions = document.createElement("div");
  actions.className = "inspector-actions";

  if (bay) {
    const idEl = document.createElement("span");
    idEl.className = "insp-id";
    idEl.textContent = bay.bayId;

    const statusEl = document.createElement("span");
    statusEl.className = `insp-pill ${bay.status.toLowerCase()}`;
    statusEl.textContent = bay.status.toUpperCase();

    const typeEl = document.createElement("span");
    typeEl.className = "insp-pill";
    typeEl.textContent = bay.primaryType.toUpperCase();

    meta.append(idEl, statusEl, typeEl);

    if (bay.currentSessionId) {
      const sessEl = document.createElement("span");
      sessEl.className = "insp-pill";
      sessEl.textContent = bay.currentSessionId;
      meta.append(sessEl);
    }

    if (bay.fencingToken > 0) {
      const fenceEl = document.createElement("span");
      fenceEl.className = "insp-pill";
      fenceEl.textContent = `FENCE #${bay.fencingToken}`;
      meta.append(fenceEl);
    }

    const maintLabel = bay.status === "OutOfService" ? "Restore Free" : "Out of Service";
    actions.append(makeInspectorButton(maintLabel, "toggle", () => toggleBayMaintenance(bay).catch(console.error)));

    if (bay.status === "Occupied") {
      actions.append(makeInspectorButton("Vacate", "primary", () => setBayOccupancy(bay.bayId, false).catch(console.error)));
    } else if (bay.status !== "OutOfService") {
      const label = bay.status === "Leased" ? "Intrude (Reroute)" : "Simulate Park";
      actions.append(makeInspectorButton(label, "danger", () => setBayOccupancy(bay.bayId, true).catch(console.error)));
    }

    if (bay.status === "Leased" && bay.fencingToken > 0) {
      actions.append(makeInspectorButton("Release Lease", "accent", () => releaseBayLease(bay).catch(console.error)));
    }

    actions.append(makeInspectorButton("Close ×", "chip", () => {
      state.selectedBayId = null;
      renderInspectorBar();
    }));
  } else if (session) {
    const idEl = document.createElement("span");
    idEl.className = "insp-id";
    idEl.textContent = `${session.vehicleSessionId} (${session.vehicleType})`;

    const targetEl = document.createElement("span");
    targetEl.className = "insp-pill leased";
    targetEl.textContent = `TARGET ${session.assignedBayId}`;

    const fenceEl = document.createElement("span");
    fenceEl.className = "insp-pill";
    fenceEl.textContent = `FENCE #${session.fencingToken}`;

    // Build clickable floor progression legs along A* path
    const crumbs = document.createElement("span");
    crumbs.className = "crumb-list";
    const visitedFloors = [];
    for (const nid of session.route?.nodeIds || []) {
      const node = state.nodesById.get(nid);
      if (!node) continue;
      if (visitedFloors.length === 0 || visitedFloors[visitedFloors.length - 1] !== node.floor) {
        visitedFloors.push(node.floor);
      }
    }

    for (const fl of visitedFloors) {
      const cBtn = makeInspectorButton(`F${fl}`, fl === session.currentFloor ? "crumb current" : "crumb", () => selectFloor(fl));
      crumbs.append(cBtn);
    }

    meta.append(idEl, targetEl, fenceEl, crumbs);

    actions.append(makeInspectorButton("Step →", "primary", () => advanceSingleSession(session).catch(console.error)));
    actions.append(makeInspectorButton("Jump to Target", "accent", () => {
      const tb = state.baysById.get(session.assignedBayId);
      if (tb) selectFloor(tb.floor);
    }));
    actions.append(makeInspectorButton("Unpin ×", "chip", () => {
      state.selectedSessionId = null;
      refreshLiveUi();
    }));
  }

  inspectorContentEl.replaceChildren(meta, actions);
}

function selectFloor(floor) {
  state.selectedFloor = floor;
  state.hoveredItem = null;
  state.hoveredBayId = null;
  state.hoveredSessionId = null;
  state.hoveredRampId = null;
  state.hoveredVmsId = null;
  for (const el of document.querySelectorAll(".floor-tab")) {
    el.classList.toggle("active", Number(el.dataset.floor) === state.selectedFloor);
  }
  updateFloorHint();
}

function selectSession(sessionId, jumpToCurrentFloor) {
  const s = state.sessions.get(sessionId);
  if (!s) return;
  state.selectedBayId = null;
  state.selectedSessionId = sessionId;
  if (jumpToCurrentFloor) {
    selectFloor(s.currentFloor);
  }
  refreshLiveUi();
}

function updateFloorHint() {
  const titleEl = document.getElementById("canvasFloorTitle");
  const meta = FLOOR_META[state.selectedFloor] || FLOOR_META[0];
  const h = state.hoveredItem;
  if (h?.kind === "bay") {
    const nextAction = h.bay.status === "OutOfService" ? "RESTORE" : "INSPECT / SHIFT+CLICK MAINT";
    titleEl.textContent = `STALL ${h.bay.bayId} (${h.bay.status.toUpperCase()}) · ${nextAction}`;
  } else if (h?.kind === "vehicle") {
    titleEl.textContent = `VEHICLE ${h.session.vehicleSessionId} · CLICK TO FOLLOW ROUTE`;
  } else if (h?.kind === "ramp") {
    titleEl.textContent = `RAMP PORTAL · CLICK TO JUMP TO FLOOR F${h.ramp.targetFloor}`;
  } else {
    titleEl.textContent = `${meta.code} · ${meta.title}`;
  }
}

function resizeCanvas() {
  const rect = canvas.parentElement.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(600, Math.floor(rect.width * dpr));
  canvas.height = Math.max(460, Math.floor(rect.height * dpr));
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function drawFrame(now) {
  const layout = getCanvasLayout(canvas);
  const floor = state.selectedFloor;

  drawDeckBackground(ctx, layout, floor);
  drawDrivingCorridors(ctx, layout, floor, state, state.options);
  drawFloorRampsAndGates(ctx, layout, floor, state, now, state.hoveredRampId);
  drawParkingBays(ctx, layout, floor, state, now);
  drawNodesAndVmsSigns(ctx, layout, floor, state, now, state.hoveredVmsId);
  drawActiveRoutesAndVehicles(ctx, layout, floor, state, now, state.options);
  drawCadOverlay(ctx, layout, floor, state, state.hoveredItem);

  requestAnimationFrame(drawFrame);
}

async function spawnVehicle() {
  const gateId = document.getElementById("gateSelect").value;
  const vehicleType = document.getElementById("vehicleTypeSelect").value;
  const sessionId = `CAR-${String(state.demoCounter++).padStart(4, "0")}`;

  const res = await apiFetch("/api/parking/allocations", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gateId, vehicleSessionId: sessionId, vehicleType, leaseSeconds: 90 })
  });
  if (!res.ok) {
    logEvent(`Allocation rejected (${res.status}) for tenant ${state.tenantId}.`, "occupied");
  } else if (document.getElementById("auditDrawer").open) {
    await loadAuditEvents();
  }
}

async function injectIntruder() {
  const res = await apiFetch("/api/parking/chaos/intruder", { method: "POST" });
  if (!res.ok) {
    logEvent(`No active leased bay for tenant ${state.tenantId} to inject intruder into.`, "occupied");
  } else if (document.getElementById("auditDrawer").open) {
    await loadAuditEvents();
  }
}

async function triggerBurst() {
  const res = await apiFetch("/api/parking/chaos/burst?count=25", { method: "POST" });
  if (res.ok) {
    const data = await res.json();
    logEvent(`Burst [${state.tenantId}]: ${data.succeeded}/${data.requested} cars allocated concurrently.`, "assigned");
    if (document.getElementById("auditDrawer").open) {
      await loadAuditEvents();
    }
  } else {
    logEvent(`Burst rejected (${res.status}) for tenant ${state.tenantId}.`, "occupied");
  }
}

async function toggleBayMaintenance(bay) {
  const outOfService = bay.status !== "OutOfService";
  const res = await apiFetch(`/api/parking/bays/${encodeURIComponent(bay.bayId)}/maintenance`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ outOfService })
  });
  if (res.ok && document.getElementById("auditDrawer").open) {
    await loadAuditEvents();
  }
}

async function setBayOccupancy(bayId, occupied, occupantSessionId = null) {
  const body = occupied
    ? { occupied: true, occupantSessionId: occupantSessionId || `MANUAL-${String(state.demoCounter++).padStart(3, "0")}` }
    : { occupied: false };
  const res = await apiFetch(`/api/parking/bays/${encodeURIComponent(bayId)}/occupancy`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  if (res.ok && document.getElementById("auditDrawer").open) {
    await loadAuditEvents();
  }
}

async function releaseBayLease(bay) {
  const res = await apiFetch(
    `/api/parking/allocations/${encodeURIComponent(bay.bayId)}?fencingToken=${encodeURIComponent(bay.fencingToken)}`,
    { method: "DELETE" }
  );
  if (!res.ok) {
    logEvent(`Release rejected (${res.status}) for ${bay.bayId}.`, "occupied", { bayId: bay.bayId });
  } else {
    logEvent(`Released lease on ${bay.bayId} [fence:${bay.fencingToken}]`, "free", { bayId: bay.bayId });
    if (document.getElementById("auditDrawer").open) {
      await loadAuditEvents();
    }
  }
}

async function advanceSingleSession(session) {
  const nodes = session.route?.nodeIds || [];
  if (session.stepCursor >= nodes.length - 1) return;

  session.stepCursor++;
  const nodeId = nodes[session.stepCursor];
  await apiFetch(`/api/parking/sessions/${encodeURIComponent(session.vehicleSessionId)}/progress`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ currentNodeId: nodeId })
  });

  if (session.stepCursor >= nodes.length - 1) {
    await apiFetch(`/api/parking/bays/${encodeURIComponent(session.assignedBayId)}/occupancy`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ occupied: true, occupantSessionId: session.vehicleSessionId })
    });
    state.sessions.delete(session.vehicleSessionId);
    if (state.selectedSessionId === session.vehicleSessionId) {
      state.selectedSessionId = null;
    }
    refreshLiveUi();
  }
}

async function stepBrowserDemo() {
  if (!state.autoDemo) return;

  if (state.sessions.size < 10 && Math.random() < 0.6) {
    const gates = ["GATE-N1", "GATE-S1"];
    const types = ["Standard", "Standard", "EV", "SUV", "Moto", "Pcd"];
    const gateId = gates[Math.floor(Math.random() * gates.length)];
    const vehicleType = types[Math.floor(Math.random() * types.length)];
    const sessionId = `DEMO-${String(state.demoCounter++).padStart(4, "0")}`;
    await apiFetch("/api/parking/allocations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ gateId, vehicleSessionId: sessionId, vehicleType, leaseSeconds: 90 })
    });
  }

  await Promise.allSettled(
    Array.from(state.sessions.values()).map((session) => advanceSingleSession(session))
  );

  const occupiedBays = Array.from(state.baysById.values()).filter((b) => b.status === "Occupied");
  if (occupiedBays.length > 0 && Math.random() < 0.18) {
    const leaving = occupiedBays[Math.floor(Math.random() * occupiedBays.length)];
    await apiFetch(`/api/parking/bays/${encodeURIComponent(leaving.bayId)}/occupancy`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ occupied: false })
    });
  }

  if (state.sessions.size >= 2 && Math.random() < 0.12) {
    await apiFetch("/api/parking/chaos/intruder", { method: "POST" });
  }
}

async function loadAuditEvents() {
  document.getElementById("auditTenantLabel").textContent = state.tenantId;
  const res = await apiFetch("/api/parking/audit?limit=25");
  if (!res.ok) {
    logEvent(`Audit request rejected (${res.status}) for tenant ${state.tenantId}.`, "occupied");
    return;
  }

  const data = await res.json();
  document.getElementById("auditTenantLabel").textContent = data.tenantId ?? state.tenantId;
  document.getElementById("auditCountBadge").textContent = String(data.count ?? 0);
  document.getElementById("auditDroppedLabel").textContent = `dropped: ${data.droppedCount ?? 0}`;

  const list = document.getElementById("auditList");
  list.replaceChildren();
  for (const ev of data.events || []) {
    const li = document.createElement("li");
    const ts = new Date(ev.occurredAtUtc).toLocaleTimeString([], { hour12: false });
    const left = document.createElement("span");
    left.textContent = `${ev.eventType} · ${ev.bayId}${ev.sessionId ? ` (${ev.sessionId})` : ""}`;
    const right = document.createElement("span");
    right.className = "muted";
    right.textContent = ts;
    li.append(left, right);
    list.append(li);
  }
}

function toggleOption(key, btnId) {
  state.options[key] = !state.options[key];
  const btn = document.getElementById(btnId);
  if (btn) btn.classList.toggle("active", state.options[key]);
}

function toggleAutoDemo() {
  const demoBtn = document.getElementById("demoToggleBtn");
  state.autoDemo = !state.autoDemo;
  demoBtn.classList.toggle("active", state.autoDemo);
  demoBtn.textContent = `Auto-Demo: ${state.autoDemo ? "ON" : "OFF"}`;
  if (state.autoDemo) {
    state.demoTimer = setInterval(() => stepBrowserDemo().catch(console.error), 750);
  } else if (state.demoTimer) {
    clearInterval(state.demoTimer);
    state.demoTimer = null;
  }
}

// Canvas pointer interaction
canvas.addEventListener("mousemove", (e) => {
  const rect = canvas.getBoundingClientRect();
  const hit = hitTestCanvas(getCanvasLayout(canvas), state.selectedFloor, state, e.clientX - rect.left, e.clientY - rect.top);
  state.hoveredItem = hit;
  state.hoveredBayId = hit?.kind === "bay" ? hit.bay.bayId : null;
  state.hoveredSessionId = hit?.kind === "vehicle" ? hit.session.vehicleSessionId : null;
  state.hoveredRampId = hit?.kind === "ramp" ? hit.ramp.id : null;
  state.hoveredVmsId = hit?.kind === "vms" ? hit.panelId : null;

  canvas.style.cursor = hit ? "pointer" : "default";
  updateFloorHint();
});

canvas.addEventListener("mouseleave", () => {
  state.hoveredItem = null;
  state.hoveredBayId = null;
  state.hoveredSessionId = null;
  state.hoveredRampId = null;
  state.hoveredVmsId = null;
  canvas.style.cursor = "default";
  updateFloorHint();
});

canvas.addEventListener("click", (e) => {
  const rect = canvas.getBoundingClientRect();
  const hit = hitTestCanvas(getCanvasLayout(canvas), state.selectedFloor, state, e.clientX - rect.left, e.clientY - rect.top);
  if (!hit) {
    state.selectedBayId = null;
    state.selectedSessionId = null;
    refreshLiveUi();
    return;
  }

  if (hit.kind === "vehicle") {
    selectSession(hit.session.vehicleSessionId, false);
  } else if (hit.kind === "ramp") {
    selectFloor(hit.ramp.targetFloor);
  } else if (hit.kind === "bay") {
    if (e.shiftKey) {
      toggleBayMaintenance(hit.bay).catch(console.error);
    } else {
      state.selectedSessionId = null;
      state.selectedBayId = hit.bay.bayId;
      refreshLiveUi();
    }
  }
});

canvas.addEventListener("dblclick", (e) => {
  const rect = canvas.getBoundingClientRect();
  const hit = hitTestCanvas(getCanvasLayout(canvas), state.selectedFloor, state, e.clientX - rect.left, e.clientY - rect.top);
  if (hit?.kind === "bay") {
    toggleBayMaintenance(hit.bay).catch(console.error);
  } else if (hit?.kind === "vehicle") {
    selectSession(hit.session.vehicleSessionId, true);
    if (!state.options.autoFollowFloor) {
      toggleOption("autoFollowFloor", "followToggleBtn");
    }
  }
});

// UI controls & shortcuts
document.getElementById("floorTabs").addEventListener("click", (e) => {
  const btn = e.target.closest(".floor-tab");
  if (btn) selectFloor(Number(btn.dataset.floor));
});

document.getElementById("routesToggleBtn").addEventListener("click", () => toggleOption("showRoutes", "routesToggleBtn"));
document.getElementById("heatmapToggleBtn").addEventListener("click", () => toggleOption("showHeatmap", "heatmapToggleBtn"));
document.getElementById("followToggleBtn").addEventListener("click", () => toggleOption("autoFollowFloor", "followToggleBtn"));

const auditDrawer = document.getElementById("auditDrawer");
auditDrawer.addEventListener("toggle", () => {
  if (auditDrawer.open) loadAuditEvents().catch(console.error);
});
document.getElementById("auditRefreshBtn").addEventListener("click", () => loadAuditEvents().catch(console.error));

document.getElementById("spawnBtn").addEventListener("click", () => spawnVehicle().catch(console.error));
document.getElementById("intruderBtn").addEventListener("click", () => injectIntruder().catch(console.error));
document.getElementById("burstBtn").addEventListener("click", () => triggerBurst().catch(console.error));
document.getElementById("demoToggleBtn").addEventListener("click", toggleAutoDemo);

window.addEventListener("keydown", (e) => {
  const tag = e.target?.tagName;
  if (tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA") return;

  if (e.key === "1") selectFloor(0);
  else if (e.key === "2") selectFloor(1);
  else if (e.key === "3") selectFloor(2);
  else if (e.key === "r" || e.key === "R") toggleOption("showRoutes", "routesToggleBtn");
  else if (e.key === "h" || e.key === "H") toggleOption("showHeatmap", "heatmapToggleBtn");
  else if (e.key === "f" || e.key === "F") toggleOption("autoFollowFloor", "followToggleBtn");
  else if (e.key === "s" || e.key === "S") spawnVehicle().catch(console.error);
  else if (e.key === "i" || e.key === "I") injectIntruder().catch(console.error);
  else if (e.key === "m" || e.key === "M") {
    const targetBayId = state.hoveredBayId || state.selectedBayId;
    const bay = targetBayId ? state.baysById.get(targetBayId) : null;
    if (bay) toggleBayMaintenance(bay).catch(console.error);
  } else if (e.code === "Space") {
    e.preventDefault();
    toggleAutoDemo();
  } else if (e.key === "Escape") {
    state.selectedBayId = null;
    state.selectedSessionId = null;
    refreshLiveUi();
  }
});

const tenantSelect = document.getElementById("tenantSelect");
const customTenantInput = document.getElementById("customTenantInput");
const applyTenantBtn = document.getElementById("applyTenantBtn");

async function setActiveTenant(nextTenant) {
  if (!TENANT_REGEX.test(nextTenant)) {
    logEvent(`Invalid tenant ID '${nextTenant}' (must match ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$).`, "occupied");
    return;
  }

  state.tenantId = nextTenant;
  client.setTenantId(nextTenant);
  document.getElementById("auditTenantLabel").textContent = nextTenant;
  logEvent(`Active tenant switched -> ${nextTenant}`, "assigned");

  try {
    const hubSnapshot = await client.invoke("SubscribeTenant", nextTenant);
    if (hubSnapshot) applySnapshot(hubSnapshot);
  } catch {
    const snapRes = await apiFetch("/api/parking/snapshot");
    if (snapRes.ok) {
      applySnapshot(await snapRes.json());
    } else {
      logEvent(`Tenant '${nextTenant}' rejected (${snapRes.status}).`, "occupied");
    }
  }

  if (auditDrawer.open) {
    await loadAuditEvents();
  }
}

tenantSelect.addEventListener("change", () => {
  const isCustom = tenantSelect.value === "__custom__";
  customTenantInput.classList.toggle("hidden", !isCustom);
  applyTenantBtn.classList.toggle("hidden", !isCustom);
  if (!isCustom) {
    setActiveTenant(tenantSelect.value).catch(console.error);
  } else {
    customTenantInput.focus();
  }
});

applyTenantBtn.addEventListener("click", () => setActiveTenant(customTenantInput.value.trim()).catch(console.error));
customTenantInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") setActiveTenant(customTenantInput.value.trim()).catch(console.error);
});

async function bootstrap() {
  resizeCanvas();
  window.addEventListener("resize", resizeCanvas);
  updateFloorHint();

  const res = await apiFetch("/api/parking/snapshot");
  if (res.ok) {
    applySnapshot(await res.json());
  }

  try {
    client.setTenantId(state.tenantId);
    await client.connect();
    const snapshot = await client.invoke("GetParkingSnapshot");
    if (snapshot) applySnapshot(snapshot);
  } catch (err) {
    logEvent(`SignalR fallback active (${err.message})`, "occupied");
  }

  requestAnimationFrame(drawFrame);
}

bootstrap().catch(console.error);
