import { SignalRLiteClient } from "./signalr-lite.js";

const state = {
  selectedFloor: 0,
  nodesById: new Map(),
  edges: [],
  baysById: new Map(),
  sessions: new Map(),
  ledPanels: new Map(),
  metrics: null,
  autoDemo: false,
  demoTimer: null,
  demoSeq: 1,
  demoCounter: 1
};

const canvas = document.getElementById("garageCanvas");
const ctx = canvas.getContext("2d");
const connDot = document.getElementById("connDot");
const connLabel = document.getElementById("connLabel");
const eventFeed = document.getElementById("eventFeed");

const client = new SignalRLiteClient("/hubs/parking");

client.onStateChange((connState) => {
  connDot.className = `dot ${connState === "connected" ? "connected" : connState === "disconnected" ? "disconnected" : ""}`;
  connLabel.textContent = connState === "connected" ? "SignalR Live" : connState === "disconnected" ? "Reconnecting..." : "Error";
});

client.on("RouteAssigned", (session) => {
  upsertSession(session, false);
  logEvent(`Assigned ${session.vehicleSessionId} (${session.vehicleType}) -> ${session.assignedBayId} (fence=${session.fencingToken})`, "assigned");
});

client.on("RouteReassigned", (payload) => {
  if (payload.newSession) {
    upsertSession(payload.newSession, true);
    logEvent(`INTRUDER on ${payload.displacedBayId}! Rerouted ${payload.displacedSessionId} -> ${payload.newSession.assignedBayId}`, "reroute");
  } else {
    state.sessions.delete(payload.displacedSessionId);
    logEvent(`INTRUDER on ${payload.displacedBayId}! No alternative bay available for ${payload.displacedSessionId}`, "reroute");
  }
});

client.on("BayStateChanged", (delta) => {
  const bay = state.baysById.get(delta.bayId);
  if (!bay) return;

  bay.status = delta.status;
  bay.currentSessionId = delta.sessionId;
  bay.leaseExpiresAtUtc = delta.leaseExpiresAtUtc;

  if (delta.status === "Occupied" || delta.status === "Free") {
    if (delta.status === "Free") {
      for (const [sid, s] of state.sessions.entries()) {
        if (s.assignedBayId === delta.bayId) {
          state.sessions.delete(sid);
        }
      }
    }
    logEvent(`Bay ${delta.bayId} -> ${delta.status}`, delta.status.toLowerCase());
  }

  updateFloorBadges();
});

client.on("VehicleMoved", (delta) => {
  const s = state.sessions.get(delta.vehicleSessionId);
  if (!s) return;

  s.lastKnownNodeId = delta.currentNodeId;
  s.targetX = delta.x;
  s.targetY = delta.y;
  s.currentFloor = delta.floor;
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

function upsertSession(session, isRerouted) {
  const startNode = state.nodesById.get(session.lastKnownNodeId || session.route.nodeIds[0]);
  const existing = state.sessions.get(session.vehicleSessionId);

  state.sessions.set(session.vehicleSessionId, {
    ...session,
    renderX: existing ? existing.renderX : startNode ? startNode.x : 0,
    renderY: existing ? existing.renderY : startNode ? startNode.y : 10,
    targetX: startNode ? startNode.x : 0,
    targetY: startNode ? startNode.y : 10,
    currentFloor: startNode ? startNode.floor : 0,
    rerouteFlashUntil: isRerouted ? performance.now() + 3000 : 0,
    stepCursor: 0,
    parkedAtMs: 0
  });
}

function logEvent(text, kind) {
  const li = document.createElement("li");
  li.className = kind;
  const ts = new Date().toLocaleTimeString([], { hour12: false });
  li.textContent = `[${ts}] ${text}`;
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

  state.sessions.clear();
  for (const session of snapshot.activeSessions) {
    upsertSession(session, false);
  }

  for (const panel of snapshot.ledPanels || []) {
    state.ledPanels.set(panel.panelId, { ...panel, flashUntil: 0 });
  }

  if (snapshot.metrics) {
    state.metrics = snapshot.metrics;
    renderMetrics(snapshot.metrics);
  }

  updateFloorBadges();
}

function updateFloorBadges() {
  for (let floor = 0; floor < 3; floor++) {
    let freeCount = 0;
    for (const bay of state.baysById.values()) {
      if (bay.floor === floor && bay.status === "Free") {
        freeCount++;
      }
    }
    const el = document.getElementById(`floorBadge${floor}`);
    if (el) el.textContent = `${freeCount} free`;
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
    `${m.freeBays} / ${m.leasedBays} / ${m.occupiedBays}`;
}

function resizeCanvas() {
  const rect = canvas.parentElement.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(600, Math.floor(rect.width * dpr));
  canvas.height = Math.max(460, Math.floor(rect.height * dpr));
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function drawFrame(now) {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  ctx.clearRect(0, 0, width, height);

  const padX = 48;
  const padY = 42;
  const scaleX = (width - padX * 2) / 100;
  const scaleY = (height - padY * 2) / 60;
  const toX = (x) => padX + x * scaleX;
  const toY = (y) => padY + y * scaleY;

  const floor = state.selectedFloor;

  for (const edge of state.edges) {
    const from = state.nodesById.get(edge.fromNodeId);
    const to = state.nodesById.get(edge.toNodeId);
    if (!from || !to) continue;
    if (from.floor !== floor || to.floor !== floor) continue;

    ctx.beginPath();
    ctx.moveTo(toX(from.x), toY(from.y));
    ctx.lineTo(toX(to.x), toY(to.y));
    ctx.strokeStyle = to.type === "Bay" ? "#1e293b" : "#2b3e5c";
    ctx.lineWidth = to.type === "Bay" ? 1.5 : 4;
    ctx.stroke();
  }

  for (const session of state.sessions.values()) {
    const nodeIds = session.route?.nodeIds || [];
    const isFlash = session.rerouteFlashUntil > now;
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
    ctx.setLineDash([5, 5]);
    ctx.strokeStyle = isFlash ? "#f43f5e" : "rgba(6, 182, 212, 0.55)";
    ctx.lineWidth = isFlash ? 2.8 : 1.8;
    ctx.stroke();
    ctx.setLineDash([]);
  }

  for (const bay of state.baysById.values()) {
    if (bay.floor !== floor) continue;

    const bx = toX(bay.x);
    const by = toY(bay.y);
    const boxW = Math.max(24, scaleX * 3.8);
    const boxH = Math.max(18, scaleY * 3.8);

    const fill = bay.status === "Free"
      ? "rgba(16, 185, 129, 0.22)"
      : bay.status === "Leased"
        ? "rgba(245, 158, 11, 0.30)"
        : "rgba(239, 68, 68, 0.34)";

    const stroke = bay.status === "Free"
      ? "#10b981"
      : bay.status === "Leased"
        ? "#f59e0b"
        : "#ef4444";

    ctx.fillStyle = fill;
    ctx.strokeStyle = stroke;
    ctx.lineWidth = 1.6;
    ctx.fillRect(bx - boxW / 2, by - boxH / 2, boxW, boxH);
    ctx.strokeRect(bx - boxW / 2, by - boxH / 2, boxW, boxH);

    ctx.fillStyle = "#e2e8f0";
    ctx.font = "bold 9px Inter, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(bay.bayId.replace(`F${floor}-`, ""), bx, by - 1);

    ctx.fillStyle = "#94a3b8";
    ctx.font = "8px Inter, sans-serif";
    const tag = bay.status === "Leased" && bay.leaseExpiresAtUtc
      ? `${Math.max(0, Math.ceil((Date.parse(bay.leaseExpiresAtUtc) - Date.now()) / 1000))}s`
      : bay.primaryType.toUpperCase();
    ctx.fillText(tag, bx, by + 9);
  }

  for (const node of state.nodesById.values()) {
    if (node.floor !== floor) continue;

    const nx = toX(node.x);
    const ny = toY(node.y);

    if (node.type === "Gate") {
      ctx.fillStyle = "#3b82f6";
      ctx.fillRect(nx - 14, ny - 10, 28, 20);
      ctx.fillStyle = "#fff";
      ctx.font = "bold 9px Inter, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(node.id.replace("GATE-", ""), nx, ny + 3);
    } else if (node.type === "Ramp") {
      ctx.fillStyle = "#a855f7";
      ctx.beginPath();
      ctx.arc(nx, ny, 9, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = "#fff";
      ctx.font = "bold 9px Inter, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(node.id.includes("UP") ? "▲" : "▼", nx, ny + 3);
    }

    if (node.ledPanelId) {
      const panel = state.ledPanels.get(node.ledPanelId);
      const flashing = panel && panel.flashUntil > now;
      ctx.fillStyle = flashing ? "#a855f7" : "#1e293b";
      ctx.strokeStyle = "#a855f7";
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.arc(nx, ny - 12, 7, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = "#fff";
      ctx.font = "bold 9px Inter, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(directionArrow(panel?.direction), nx, ny - 9);
    }
  }

  for (const session of state.sessions.values()) {
    session.renderX += (session.targetX - session.renderX) * 0.16;
    session.renderY += (session.targetY - session.renderY) * 0.16;

    if (session.currentFloor !== floor) continue;

    const cx = toX(session.renderX);
    const cy = toY(session.renderY);
    const rerouted = session.rerouteFlashUntil > now;

    ctx.beginPath();
    ctx.arc(cx, cy, 8, 0, Math.PI * 2);
    ctx.fillStyle = rerouted ? "#f43f5e" : "#06b6d4";
    ctx.fill();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 1.6;
    ctx.stroke();

    ctx.fillStyle = "#fff";
    ctx.font = "bold 8px Inter, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(session.vehicleSessionId.slice(-4), cx, cy - 11);
  }

  requestAnimationFrame(drawFrame);
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

async function spawnVehicle() {
  const gateId = document.getElementById("gateSelect").value;
  const vehicleType = document.getElementById("vehicleTypeSelect").value;
  const sessionId = `CAR-${String(state.demoCounter++).padStart(4, "0")}`;

  await fetch("/api/parking/allocations", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gateId, vehicleSessionId: sessionId, vehicleType, leaseSeconds: 90 })
  });
}

async function injectIntruder() {
  const res = await fetch("/api/parking/chaos/intruder", { method: "POST" });
  if (!res.ok) {
    logEvent("No active leased bay to inject intruder into.", "occupied");
  }
}

async function triggerBurst() {
  const res = await fetch("/api/parking/chaos/burst?count=25", { method: "POST" });
  if (res.ok) {
    const data = await res.json();
    logEvent(`Burst completed: ${data.succeeded}/${data.requested} cars allocated concurrently.`, "assigned");
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
    await fetch("/api/parking/allocations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ gateId, vehicleSessionId: sessionId, vehicleType, leaseSeconds: 90 })
    });
  }

  for (const session of Array.from(state.sessions.values())) {
    const nodes = session.route?.nodeIds || [];
    if (session.stepCursor < nodes.length - 1) {
      session.stepCursor++;
      const nodeId = nodes[session.stepCursor];
      await fetch(`/api/parking/sessions/${encodeURIComponent(session.vehicleSessionId)}/progress`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ currentNodeId: nodeId })
      });

      if (session.stepCursor >= nodes.length - 1) {
        await fetch(`/api/parking/bays/${encodeURIComponent(session.assignedBayId)}/occupancy`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ occupied: true, occupantSessionId: session.vehicleSessionId })
        });
        state.sessions.delete(session.vehicleSessionId);
      }
    }
  }

  const occupiedBays = Array.from(state.baysById.values()).filter((b) => b.status === "Occupied");
  if (occupiedBays.length > 0 && Math.random() < 0.18) {
    const leaving = occupiedBays[Math.floor(Math.random() * occupiedBays.length)];
    await fetch(`/api/parking/bays/${encodeURIComponent(leaving.bayId)}/occupancy`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ occupied: false })
    });
  }

  if (state.sessions.size >= 2 && Math.random() < 0.12) {
    await fetch("/api/parking/chaos/intruder", { method: "POST" });
  }
}

document.getElementById("floorTabs").addEventListener("click", (e) => {
  const btn = e.target.closest(".floor-tab");
  if (!btn) return;
  state.selectedFloor = Number(btn.dataset.floor);
  for (const el of document.querySelectorAll(".floor-tab")) {
    el.classList.toggle("active", Number(el.dataset.floor) === state.selectedFloor);
  }
  document.getElementById("canvasFloorTitle").textContent =
    `Viewing Floor ${state.selectedFloor} — 40 Bays${state.selectedFloor === 0 ? " · 2 Entry Gates" : " · Ramp Access"}`;
});

document.getElementById("spawnBtn").addEventListener("click", () => spawnVehicle().catch(console.error));
document.getElementById("intruderBtn").addEventListener("click", () => injectIntruder().catch(console.error));
document.getElementById("burstBtn").addEventListener("click", () => triggerBurst().catch(console.error));

const demoBtn = document.getElementById("demoToggleBtn");
demoBtn.addEventListener("click", () => {
  state.autoDemo = !state.autoDemo;
  demoBtn.classList.toggle("active", state.autoDemo);
  demoBtn.textContent = `Auto-Demo: ${state.autoDemo ? "ON" : "OFF"}`;
  if (state.autoDemo) {
    state.demoTimer = setInterval(() => stepBrowserDemo().catch(console.error), 750);
  } else if (state.demoTimer) {
    clearInterval(state.demoTimer);
    state.demoTimer = null;
  }
});

async function bootstrap() {
  resizeCanvas();
  window.addEventListener("resize", resizeCanvas);

  const res = await fetch("/api/parking/snapshot");
  if (res.ok) {
    applySnapshot(await res.json());
  }

  try {
    await client.connect();
    const snapshot = await client.invoke("GetParkingSnapshot");
    if (snapshot) applySnapshot(snapshot);
  } catch (err) {
    logEvent(`SignalR fallback active (${err.message})`, "occupied");
  }

  requestAnimationFrame(drawFrame);
}

bootstrap().catch(console.error);
