# ParkMesh — Real-Time Spatial Routing & Atomic Bay Allocation Engine

**ParkMesh** is a **.NET 10 / C# 14** real-time spatial routing and distributed bay allocation engine designed for smart multi-floor parking garages. It treats the physical garage as a live weighted directed graph, computes conflict-free dynamic routes using **multi-floor A\***, enforces atomic bay reservations via **Redis Lua scripts with monotonic fencing tokens**, ingests high-frequency ultrasonic/IoT bay telemetry over **MQTT** with bounded backpressure and anti-flapping hysteresis, and streams real-time state deltas to a **2D interactive HTML5 Canvas simulator** over **SignalR**.

---

## Key Capabilities

- **Multi-Floor Weighted Directed Graph (`ParkMesh.Domain`)**:
  - Standard 3-floor (`0`, `1`, `2`), 120-bay topology with directional driving lanes, inter-floor helical ramps, LED directional display panels, and `VehicleType` support (`Standard`, `EV`, `SUV`, `Pcd`, `Moto`).
- **Dynamic Multi-Floor A\* Pathfinding (`ParkMesh.Application`)**:
  - Computes lowest-cost paths using an admissible Euclidean + floor-transition heuristic with live edge congestion penalties, plus a cached dense-index topology (`ConditionalWeakTable`) and `ArrayPool<T>` single-pass multi-target Dijkstra sweep with sliding k-th best early-exit cutoff for candidate bay discovery.
- **Atomic Redis Lua Leases & Fencing Tokens (`ParkMesh.Infrastructure`)**:
  - Prevents split-brain double-allocation under concurrent vehicle arrivals using atomic Redis Lua lease acquisition (`LuaScripts.AcquireBayLease`) paired with monotonically increasing fencing tokens, plus automatic in-memory fallback when Redis is unavailable locally.
- **Physical Intrusion Detection & Live Rerouting**:
  - When a vehicle physically occupies a `Leased` bay without holding the matching lease, `ParkingAllocationService.ReconcilePhysicalOccupancyAsync` immediately transitions the bay to `Occupied`, revokes the stale session lease, recalculates an alternative A\* route from the affected vehicle's `CurrentNodeId`, updates corridor LED panels, and emits `RouteReassigned`.
- **Bounded Telemetry Ingestion & Anti-Flapping (`ParkMesh.Application`, `ParkMesh.TelemetryWorker`)**:
  - Ingests raw sensor telemetry through a `BoundedChannel<T>` (`DropOldest` backpressure policy) and stabilizes noisy ultrasonic readings with debounce hysteresis (minimum consecutive samples + stability window) before applying domain transitions.
- **SignalR Delta Streaming, Chaos Testing & OpenTelemetry (`ParkMesh.Api`, `web/`)**:
  - Streams incremental state updates (`RouteAssigned`, `RouteReassigned`, `BayStateChanged`, `VehicleMoved`, `LedPanelUpdated`, `MetricsTick`) over `/hubs/parking`, exposes chaos endpoints (`/api/parking/chaos/intruder`, `/api/parking/chaos/burst`), Prometheus scraping (`/metrics`), and OpenTelemetry (`Meter("ParkMesh.Engine")`, `ActivitySource("ParkMesh.Engine")`), and serves a zero-build 2D multi-floor dashboard at `/`.
- **Durable Audit Outbox, Startup Hydration, Maintenance & Edge Hardening (`ParkMesh.Application`, `ParkMesh.Infrastructure`, `ParkMesh.Api`)**:
  - Records lifecycle audit events non-blockingly via bounded `AuditChannelSink` flushed in batches to PostgreSQL (`PostgresParkingAuditStore` / `Npgsql`) with automatic in-memory fallback (`InMemoryParkingAuditStore`).
  - Hydrates active Redis TTL leases (via batched `StringGetWithExpiryAsync`) and latest physical/maintenance bay states from audit storage on boot (`GarageStateHydrationHostedService`) before gating `/health/ready` (`hydrated: true`).
  - Supports operational bay maintenance (`PATCH /api/parking/bays/{bayId}/maintenance`) directly via API or 2D canvas clicks, automatically rerouting any displaced `Leased` vehicle mid-trip.
  - Sweeps expired no-show leases via `LeaseExpirationSweeperHostedService`, restores edge weights, serves an OpenAPI 3 specification at `/openapi/v1.json`, and enforces configurable `X-Api-Key` authentication + chaos rate limiting.

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
  ParkMesh.UnitTests/             # Domain topology, A*, allocation/rerouting, hydration, maintenance, metrics
  ParkMesh.IntegrationTests/      # API, OpenAPI, SignalR, audit, security & Testcontainers Redis/Postgres tests
benchmarks/
  ParkMesh.Benchmarks/            # BenchmarkDotNet latency and allocation harness
docs/
  ARCHITECTURE.md                 # End-to-end architecture blueprint
  adr/                            # Architecture Decision Records (ADRs 0001-0007)
.github/
  workflows/ci.yml                # GitHub Actions CI build and full test pipeline
```

---

## Quick Start

### 1. Start Infrastructure (Optional for Standalone Mode)

Start Redis, PostgreSQL, and Mosquitto via Docker Compose when testing distributed leases, PostgreSQL audit persistence, and MQTT messaging:

```bash
docker compose -f infra/docker-compose.yml up -d

# Optional: full containerized app + Prometheus + Grafana stack
docker compose -f infra/docker-compose.yml --profile full up -d --build
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

The simulator publishes noisy ultrasonic bay readings, physical intrusions, and simulated vehicle movements onto MQTT (`parkmesh/sensors/{floor}/{bayId}/state`), while `ParkMesh.TelemetryWorker` debounces chatter and forwards confirmed transitions to the API engine.

---

## Running Tests

```bash
# Run unit tests
dotnet test tests/ParkMesh.UnitTests

# Run API, SignalR hub, and chaos integration tests (no Docker required)
dotnet test tests/ParkMesh.IntegrationTests --filter FullyQualifiedName~ParkingApiIntegrationTests

# Run full integration suite including Testcontainers Redis & PostgreSQL tests (requires Docker)
dotnet test tests/ParkMesh.IntegrationTests

# Run BenchmarkDotNet microbenchmarks
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release -- --filter *
```

### Microbenchmark Baseline (`BenchmarkDotNet v0.15.8`, `.NET 10.0.11`, In-Process Short Run)

| Benchmark | Phase 4 Baseline | Current Mean (Phase 5) | Current Allocated |
| :--- | ---: | ---: | ---: |
| `AStar_Find15Candidates_Standard` | ~689.2 us (~847 KB) | `5.24 us` | `13,880 B` |
| `AStar_Find15Candidates_EV` | ~320.6 us (~409 KB) | `8.46 us` | `19,496 B` |
| `AllocateAndRelease_InMemory` | ~636.1 us (~892 KB) | `6.82 us` | `15,384 B` |
| `TelemetryFilter_EvaluateReading` | ~58.7 ns (72 B) | `59.28 ns` | `72 B` |
| `SlidingWindow_Compute1024Percentiles` | ~9.21 us (~8.1 KB) | `6.91 us` | `8,264 B` |

---

## Documentation

- [Architecture Blueprint](docs/ARCHITECTURE.md)
- [ADR 0001 — Modular Monolith with Dedicated Edge Workers](docs/adr/0001-modular-monolith-over-microservices.md)
- [ADR 0002 — Atomic Redis Lua Leases with Monotonic Fencing Tokens](docs/adr/0002-redis-lua-leases-with-fencing-tokens.md)
- [ADR 0003 — Multi-Floor A\* Pathfinding with Admissible Euclidean Heuristic](docs/adr/0003-multi-floor-astar-with-admissible-heuristic.md)
- [ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce](docs/adr/0004-bounded-channel-backpressure-and-anti-flapping.md)
- [ADR 0005 — SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator](docs/adr/0005-signalr-delta-protocol-and-zero-build-canvas.md)
- [ADR 0006 — Asynchronous Audit Outbox, Proactive Lease Reconciliation, and Edge Hardening](docs/adr/0006-async-audit-outbox-and-lease-reconciliation.md)
- [ADR 0007 — Single-Pass Indexed Multi-Target Routing, Startup State Hydration, and Bay Maintenance Lifecycle](docs/adr/0007-indexed-multi-target-routing-and-state-hydration.md)
