# ParkMesh Architecture Blueprint

## 1. Architectural Vision & Style

**ParkMesh** follows a **Modular Monolith + Dedicated Edge Workers** architecture built on **.NET 10 / C# 14**. Rather than introducing premature microservice serialization boundaries inside the latency-sensitive pathfinding and lease-acquisition critical path, core domain logic runs inside a single cohesive engine (`ParkMesh.Api`), while I/O-bound edge workloads (`ParkMesh.TelemetryWorker`, `ParkMesh.Simulator`) run as isolated `.NET Worker` processes communicating over MQTT and HTTP/JSON.

```mermaid
flowchart LR
    Sim["ParkMesh.Simulator\n(Vehicles, Noise, Intruders)"] -->|MQTT Raw Telemetry| Mosq["Eclipse Mosquitto\n(MQTT Broker)"]
    Mosq -->|parkmesh/v1/.../telemetry| Worker["ParkMesh.TelemetryWorker\n(BoundedChannel + Anti-Flapping)"]
    Worker -->|Confirmed Transitions HTTP| Api["ParkMesh.Api\n(Allocation Engine + SignalR + OTel)"]
    Api <-->|Atomic Lua SET NX + Fencing Token| Redis["Redis\n(Distributed Leases & Ephemeral State)"]
    Api -->|MQTT LED Commands| Mosq
    Api <-->|WebSockets /hubs/parking Deltas| Web["web/\n(2D Multi-Floor Canvas Simulator)"]
```

---

## 2. Solution Modules & Responsibilities

| Project / Directory | Layer | Primary Responsibilities |
| :--- | :--- | :--- |
| `src/ParkMesh.Domain` | Domain | Multi-floor weighted directed graph (`ParkingGraph`, `GraphNode`, `GraphEdge`, `ParkingBay`, `ParkingRoute`, `StandardTopologyFactory`), domain enums (`BayStatus`, `VehicleType`, `NodeType`, `DirectionHint`). Zero external dependencies. |
| `src/ParkMesh.Application` | Application | `AStarPathfinder` (`IPathfinder`), `ParkingAllocationService`, `IEdgeCongestionTracker`, `TelemetryIngestionChannel`, `SensorReadingFilter`, `SensorTelemetryProcessor`, `EngineMetricsRecorder` (`SlidingLatencyWindow`, OTel `Meter` & `ActivitySource`), `IParkingEventSink`. |
| `src/ParkMesh.Infrastructure` | Infrastructure | `RedisBayLeaseManager` & `LuaScripts` (atomic Redis Lua leases + monotonic fencing tokens), `InMemoryBayLeaseManager`, `MqttConnectionManager`, `MqttTelemetryHostedService`, `MqttLedPanelPublisher`, DI wiring. |
| `src/ParkMesh.Api` | Presentation / Host | Minimal APIs (`/api/parking/...`), `ParkingHub` (`/hubs/parking`), `SignalRParkingEventSink`, `MetricsBroadcastHostedService`, static serving of `web/` at `/`, OpenTelemetry tracing & metrics (`OTLP` export when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured). |
| `src/ParkMesh.TelemetryWorker` | Edge Worker | Subscribes to raw MQTT sensor frames, filters chatter via `SensorTelemetryProcessor`, and forwards confirmed bay occupancy transitions via `RemoteEngineAllocationProxy`. |
| `src/ParkMesh.Simulator` | Simulator Worker | Generates concurrent vehicle arrivals, sensor flapping noise, simulated vehicle progress steps, and physical bay intrusions against MQTT + REST. |
| `web/` | Visual UI | Zero-build HTML5 Canvas 2D multi-floor renderer (`app.js`), custom SignalR JSON WebSocket client (`signalr-lite.js`), Auto-Demo loop, chaos triggers, and P50/P95/P99 telemetry HUD. |

---

## 3. Core Subsystems

### 3.1 Multi-Floor Weighted Directed Graph & A\* Pathfinding

The parking facility is modeled in memory as a weighted directed graph $G = (V, E)$ via `StandardTopologyFactory.CreateStandardGarage()` (`3` floors: `0`, `1`, `2`; `120` parking bays):

- **Vertices ($V$)**: Entry gates (`NodeType.Gate`), lane intersections (`NodeType.Intersection`), inter-floor helical ramps (`NodeType.Ramp`), and individual bays (`NodeType.Bay`).
- **Edges ($E$)**: Directed `GraphEdge` segments carrying base distance (`BaseDistance`), ramp indicator (`IsRamp`), ramp penalty (`RampPenalty`), `EffectiveBaseCost`, and dynamic congestion cost adjustments via `IEdgeCongestionTracker`.
- **A\* & Multi-Target Search (`AStarPathfinder`)**: Compiles dense integer indices per `ParkingGraph` (`ConditionalWeakTable`), rents `ArrayPool<T>` scratch buffers, and runs a single-pass multi-target Dijkstra sweep with sliding k-th best early-exit cutoff for candidate bay discovery alongside admissible single-target A\* routing.

### 3.2 Atomic Bay Allocation, Fencing Tokens & Live Rerouting

Under high-concurrency bursts (for example 25+ vehicles arriving simultaneously at entry gates), naive read-then-write reservation logic creates race conditions and double-booked bays.

`ParkingAllocationService` + `RedisBayLeaseManager` (`LuaScripts`) eliminate split-brain allocations:

1. **Candidate Ranking**: Queries A\* cost from the vehicle's entry gate to compatible `BayStatus.Free` bays matching `VehicleType` (`Standard`, `EV`, `SUV`, `Pcd`, `Moto`).
2. **Atomic Lua Acquisition**: Executes `LuaScripts.AcquireBayLease` against `{prefix}:lease:bay:{bayId}` (`SET ... PX`) and `INCR` `{prefix}:fence:bay:{bayId}`, returning both acquisition status and a monotonic fencing token.
3. **Congestion Lock-In**: Increments live traffic load on edges along the chosen `ParkingRoute` and updates upstream LED directional panels (`DirectionHint`).
4. **Intrusion Detection & Self-Healing Reroute**: If a physical sensor reports occupancy on a bay in `BayStatus.Leased` state (an unbooked driver stole the spot), `ParkingAllocationService.ReconcilePhysicalOccupancyAsync`:
   - Marks the bay `Occupied`, revokes its active lease via fencing token check (`LuaScripts.ReleaseBayLease`), and removes stale edge congestion.
   - Locates the affected active session, computes a fresh A\* route starting from the vehicle's `LastKnownNodeId`, atomically acquires a new bay lease with a higher fencing token, and emits `RouteReassigned` so both LED panels and live SignalR clients immediately redirect the driver.

### 3.3 Telemetry Ingestion Pipeline & Anti-Flapping

Physical bay sensors suffer from reflection noise, pedestrians crossing bays, and network micro-bursts.

- **Bounded Backpressure (`TelemetryIngestionChannel`)**: Uses `System.Threading.Channels.Channel.CreateBounded<BaySensorReading>` with `BoundedChannelFullMode.DropOldest`, bounding memory usage and preserving freshest physical state while tracking `StaleDroppedCount` and `EnqueuedCount`.
- **Anti-Flapping Debounce (`SensorReadingFilter`)**: Requires consecutive matching samples (`MinConsecutiveReadings`) spanning at least `StabilityWindowMs` before confirming a physical transition (`ConfirmedTransitionsCount`), preventing spurious reroute storms while counting suppressed noise (`DebouncedCount`).
- **Confirmed Edge Forwarding (`SensorTelemetryProcessor`)**: Invokes downstream allocation reconciliation only on confirmed transitions rather than steady-state heartbeats.

### 3.4 Real-Time Delta Streaming, Chaos Engineering & OpenTelemetry

- **SignalR Hub (`/hubs/parking`)**:
  - Client call: `GetParkingSnapshot()` returns full initial graph geometry, floor list, gates, active sessions, bay states, and metrics snapshot.
  - Server-to-client delta events emitted by `SignalRParkingEventSink` and `MetricsBroadcastHostedService`:
    - `RouteAssigned`
    - `RouteReassigned` (including `displacedBayId`, `displacedSessionId`, and `newSession`)
    - `BayStateChanged`
    - `VehicleMoved`
    - `LedPanelUpdated`
    - `MetricsTick` (1-second heartbeat carrying P50/P95/P99 latencies, bay counts, allocations, contention retries, reroutes, and telemetry channel counters)
- **OpenTelemetry Instrumentation (`EngineMetricsRecorder`)**:
  - `Meter("ParkMesh.Engine")`: Histograms (`parkmesh.routing.astar.duration`, `parkmesh.lease.redis.duration`) and Counters (`parkmesh.allocations.total`, `parkmesh.allocations.contention`, `parkmesh.reroutes.total`, `parkmesh.leases.expired.total`).
  - `ActivitySource("ParkMesh.Engine")`: Distributed spans around allocation (`parkmesh.allocate`), physical occupancy reconciliation, and `Npgsql` database spans.
  - Sliding-window estimator (`SlidingLatencyWindow`, capacity `1024`) exposing real-time interpolated `P50`, `P95`, and `P99` latencies, alongside Prometheus scraping at `/metrics` and pre-provisioned Grafana dashboards (`infra/grafana/`).

### 3.5 Durable Audit Outbox, Lease Expiration Sweeper & Edge Hardening

- **Asynchronous Audit Outbox & PostgreSQL RLS (`AuditChannelSink`, `PostgresParkingAuditStore`, `ParkingAuditHostedService`)**: Allocation, release, expiration, occupancy, and maintenance events enqueue non-blockingly into a bounded channel (`DropOldest`, capacity `8192`) stamped with the active `ITenantContext` (`X-Tenant-Id`, default `"default"`) and flush in per-tenant transactions using `SELECT set_config('app.current_tenant', @tenant_id, true)` into PostgreSQL `parking_audit_events`. The table enforces `ENABLE ROW LEVEL SECURITY` and `FORCE ROW LEVEL SECURITY` with a fail-closed `USING` / `WITH CHECK` policy (`NULLIF(current_setting('app.current_tenant', true), '')`), mirrored in `InMemoryParkingAuditStore` when PostgreSQL is unconfigured.
- **Proactive Lease Sweeper (`LeaseExpirationSweeperHostedService`)**: Polls `PurgeExpiredSessionsAsync` every 2 seconds using `TimeProvider`, reclaiming abandoned leases, clearing edge congestion, and broadcasting `BayStateChanged` (`Free`) to SignalR clients.
- **Edge Security, Tenant Isolation, Startup Hydration & Probes**: Constant-time SHA-256 `X-Api-Key` verification (`ApiKeyValidator` / `ApiKeyEndpointFilter`, rejecting duplicate headers) across mutation and audit routes when configured, strict `X-Tenant-Id` validation and optional `Security:AllowedTenants` allowlisting (`TenantResolutionMiddleware`), HTTP security headers + CSP + request body size caps (`SecurityHeadersMiddleware`), fixed-window rate limiting on chaos endpoints (`429 Too Many Requests`), dependency-and-hydration-aware `/health/ready`, `/openapi/v1.json`, and `/health/live`.

---

## 4. API & Protocol Surface

| Surface | Endpoint / Topic | Purpose |
| :--- | :--- | :--- |
| Static UI | `GET /` | Serves the 2D multi-floor HTML5 Canvas simulator (`web/index.html`) |
| Topology | `GET /api/parking/topology` | Returns graph nodes, edges, bays, and gates |
| Live Snapshot | `GET /api/parking/snapshot` | Full state + graph + metrics bootstrap payload |
| Telemetry Metrics | `GET /api/parking/metrics` | Current bay counts, active sessions, channel stats, and P50/P95/P99 |
| Allocate Route | `POST /api/parking/allocations` | Allocates optimal conflict-free route + bay lease |
| Vehicle Progress | `POST /api/parking/sessions/{sessionId}/progress` | Advances vehicle position along its assigned graph path |
| Release Session | `DELETE /api/parking/sessions/{sessionId}` | Releases lease and clears edge congestion |
| Physical Occupancy | `POST /api/parking/bays/{bayId}/occupancy` | Confirms physical occupied/vacated transition (triggers intrusion reroute if stolen) |
| Bay Maintenance | `PATCH /api/parking/bays/{bayId}/maintenance` | Marks a bay `OutOfService` (rerouting any leased car) or restores it `Free` |
| Sensor Reading | `POST /api/parking/telemetry/readings` | Enqueues a raw sensor reading into `TelemetryIngestionChannel` |
| Chaos Intruder | `POST /api/parking/chaos/intruder` | Injects a physical intrusion into an active leased bay to demonstrate live rerouting |
| Chaos Burst | `POST /api/parking/chaos/burst?count=25` | Fires concurrent allocation burst across entry gates (`1..60`) |
| Audit Log | `GET /api/parking/audit?limit=50` | Recent persisted parking lifecycle audit events |
| Health (Live) | `GET /health/live` | Process liveness probe |
| Health (Ready) | `GET /health/ready` | Dependency & startup state-hydration readiness probe (`Redis` + `PostgreSQL` + `IsHydrated`) |
| OpenAPI Spec | `GET /openapi/v1.json` | Machine-readable OpenAPI 3 specification generated via `.NET 10` OpenAPI |
| Prometheus | `GET /metrics` | OpenTelemetry Prometheus scraping endpoint |
| SignalR Hub | `WS /hubs/parking` | Bidirectional SignalR JSON protocol hub for real-time deltas |
| MQTT Sensors | `parkmesh/sensors/{floor}/{bayId}/state` | Raw ultrasonic sensor payload stream |
| MQTT Gates | `parkmesh/gates/{gateId}/admitted` | Gate vehicle admission events |
| MQTT LED Panels | `parkmesh/panels/{panelId}/direction` | Retained LED directional guidance payload |

---

## 5. Architecture Decision Records

Key technical trade-offs are documented in `docs/adr/`:

1. [ADR 0001 — Modular Monolith with Dedicated Edge Workers](adr/0001-modular-monolith-over-microservices.md)
2. [ADR 0002 — Atomic Redis Lua Leases with Monotonic Fencing Tokens](adr/0002-redis-lua-leases-with-fencing-tokens.md)
3. [ADR 0003 — Multi-Floor A\* Pathfinding with Admissible Euclidean Heuristic](adr/0003-multi-floor-astar-with-admissible-heuristic.md)
4. [ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce](adr/0004-bounded-channel-backpressure-and-anti-flapping.md)
5. [ADR 0005 — SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator](adr/0005-signalr-delta-protocol-and-zero-build-canvas.md)
6. [ADR 0006 — Asynchronous Audit Outbox, Proactive Lease Reconciliation, and Edge Hardening](adr/0006-async-audit-outbox-and-lease-reconciliation.md)
7. [ADR 0007 — Single-Pass Indexed Multi-Target Routing, Startup State Hydration, and Bay Maintenance Lifecycle](adr/0007-indexed-multi-target-routing-and-state-hydration.md)
