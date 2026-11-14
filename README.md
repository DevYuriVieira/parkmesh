# ParkMesh — Real-Time Spatial Routing & Atomic Bay Allocation Engine

**ParkMesh** is a **.NET 10 / C# 14** real-time spatial routing and distributed bay allocation engine designed for smart multi-floor parking garages. It treats the physical garage as a live weighted directed graph, computes conflict-free dynamic routes using **multi-floor A\***, enforces atomic bay reservations via **Redis Lua scripts with monotonic fencing tokens**, ingests high-frequency ultrasonic/IoT bay telemetry over **MQTT** with bounded backpressure and anti-flapping hysteresis, and streams real-time state deltas to a **2D interactive HTML5 Canvas simulator** over **SignalR**.

---

## Key Capabilities

- **Multi-Floor Weighted Directed Graph (`ParkMesh.Domain`)**:
  - Standard 3-floor (`-1`, `0`, `1`), 120-bay topology with directional driving lanes, inter-floor helical ramps, LED directional display panels, and bay classifications (`Standard`, `Compact`, `EV`, `PRM`, `VIP`).
- **Dynamic Multi-Floor A\* Pathfinding (`ParkMesh.Application`)**:
  - Computes lowest-cost paths from any entry gate to an optimal candidate bay using an admissible Euclidean + floor-transition heuristic combined with live edge congestion penalties.
- **Atomic Redis Lua Leases & Fencing Tokens (`ParkMesh.Infrastructure`)**:
  - Prevents split-brain double-allocation under concurrent vehicle arrivals using atomic Redis `SET ... NX EX` Lua transitions paired with monotonically increasing fencing tokens, plus automatic in-memory fallback when Redis is unavailable locally.
- **Physical Intrusion Detection & Live Rerouting**:
  - When a vehicle physically occupies a reserved bay without holding the matching lease, the engine immediately transitions the bay to `Occupied`, revokes the stale session lease, recalculates an alternative A\* route from the affected vehicle's last known graph node, updates corridor LED panels, and broadcasts `RouteReassigned`.
- **Bounded Telemetry Ingestion & Anti-Flapping (`ParkMesh.Application`, `ParkMesh.TelemetryWorker`)**:
  - Ingests raw sensor telemetry through a `BoundedChannel<T>` (`DropOldest` backpressure policy) and stabilizes noisy ultrasonic readings with debounce hysteresis (minimum consecutive samples + stability window) before applying domain transitions.
- **SignalR Delta Streaming, Chaos Testing & OpenTelemetry (`ParkMesh.Api`, `web/`)**:
  - Streams incremental state updates (`RouteAssigned`, `RouteReassigned`, `BayStateChanged`, `VehicleMoved`, `LedPanelUpdated`, `MetricsTick`) over `/hubs/parking`, exposes chaos endpoints (`/api/parking/chaos/intruder`, `/api/parking/chaos/burst`) and OpenTelemetry (`Meter("ParkMesh.Engine")`, `ActivitySource("ParkMesh.Engine")`), and serves a zero-build 2D multi-floor dashboard at `/`.

---

## Repository Structure

```text
ParkMesh.sln
Directory.Build.props
infra/
  docker-compose.yml              # Redis, PostgreSQL, and Eclipse Mosquitto local stack
  mosquitto/mosquitto.conf        # Local MQTT broker configuration
src/
  ParkMesh.Domain/                # Graph model, multi-floor topology, bays, routes, enums
  ParkMesh.Application/           # A* pathfinder, allocation orchestrator, telemetry pipeline, metrics
  ParkMesh.Infrastructure/        # Redis Lua lease manager, in-memory fallback, MQTTnet pub/sub
  ParkMesh.Api/                   # Minimal APIs, SignalR hub (/hubs/parking), OTel, static UI hosting
  ParkMesh.TelemetryWorker/       # Standalone MQTT subscriber forwarding confirmed sensor transitions
  ParkMesh.Simulator/             # Fleet arrival, sensor noise/flapping, and intrusion simulator
web/
  index.html                      # Interactive 2D multi-floor visual dashboard & simulator
  styles.css                      # Dark-slate telemetry UI theme
  app.js                          # Canvas topology renderer, animation loop, chaos & demo controls
  signalr-lite.js                 # Zero-dependency SignalR WebSocket JSON protocol client
tests/
  ParkMesh.UnitTests/             # Domain topology, A*, allocation/rerouting, telemetry, P50/P95/P99
  ParkMesh.IntegrationTests/      # API, SignalR WebSocket, chaos endpoints & Testcontainers Redis tests
docs/
  ARCHITECTURE.md                 # End-to-end architecture blueprint
  adr/                            # Architecture Decision Records (ADRs 0001-0005)
```

---

## Quick Start

### 1. Start Infrastructure (Optional for Standalone Mode)

Start Redis, PostgreSQL, and Mosquitto via Docker Compose when testing distributed leases and MQTT messaging:

```bash
docker compose -f infra/docker-compose.yml up -d
```

> **Note:** `ParkMesh.Api` defaults to `InMemoryBayLeaseManager` when `ConnectionStrings:Redis` is omitted, allowing immediate local execution and exploration of the `/` 2D simulator without external dependencies.

### 2. Run the API & 2D Visual Dashboard

```bash
dotnet run --project src/ParkMesh.Api
```

Open the URL reported in the console (for example `http://localhost:5150`) in a browser to access the interactive 2D multi-floor dashboard (`/`), SignalR stream (`/hubs/parking`), snapshot API (`/api/parking/snapshot`), and live chaos triggers.

### 3. Run the Standalone MQTT Pipeline (Phase 2 Mode)

With `infra/docker-compose.yml` running and `ParkMesh.Api` online:

```bash
dotnet run --project src/ParkMesh.TelemetryWorker
dotnet run --project src/ParkMesh.Simulator
```

The simulator publishes noisy ultrasonic bay readings, physical intrusions, and simulated vehicle movements onto MQTT (`parkmesh/v1/garages/{garageId}/bays/{bayId}/telemetry`), while `ParkMesh.TelemetryWorker` debounces chatter and forwards confirmed transitions to the API engine.

---

## Running Tests

```bash
# Run unit tests
dotnet test tests/ParkMesh.UnitTests

# Run API, SignalR hub, and chaos integration tests (no Docker required)
dotnet test tests/ParkMesh.IntegrationTests --filter FullyQualifiedName~ParkingApiIntegrationTests

# Run full integration suite including Testcontainers Redis Lua tests (requires Docker)
dotnet test tests/ParkMesh.IntegrationTests
```

---

## Documentation

- [Architecture Blueprint](docs/ARCHITECTURE.md)
- [ADR 0001 — Modular Monolith with Dedicated Edge Workers](docs/adr/0001-modular-monolith-over-microservices.md)
- [ADR 0002 — Atomic Redis Lua Leases with Monotonic Fencing Tokens](docs/adr/0002-redis-lua-leases-with-fencing-tokens.md)
- [ADR 0003 — Multi-Floor A* Pathfinding with Admissible Euclidean Heuristic](docs/adr/0003-multi-floor-astar-with-admissible-heuristic.md)
- [ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce](docs/adr/0004-bounded-channel-backpressure-and-anti-flapping.md)
- [ADR 0005 — SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator](docs/adr/0005-signalr-delta-protocol-and-zero-build-canvas.md)
