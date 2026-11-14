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
| `src/ParkMesh.Domain` | Domain | Multi-floor weighted directed graph (`ParkingGraph`, `GraphNode`, `GraphEdge`, `ParkingBay`, `ParkingRoute`, `StandardTopologyFactory`), state enums (`BayState`, `BayCategory`, `NodeType`). Zero external dependencies. |
| `src/ParkMesh.Application` | Application | `AStarPathfinder` (`IPathfinder`), `ParkingAllocationService`, `EdgeCongestionTracker`, `TelemetryIngestionChannel`, `SensorReadingFilter`, `SensorTelemetryProcessor`, `EngineMetricsRecorder` (`SlidingLatencyWindow`, OTel `Meter` & `ActivitySource`), `IParkingEventSink`. |
| `src/ParkMesh.Infrastructure` | Infrastructure | `RedisBayLeaseManager` & `LuaScripts` (atomic Redis Lua leases + monotonic fencing tokens), `InMemoryBayLeaseManager`, `MqttConnectionManager`, `MqttTelemetryHostedService`, `MqttLedPanelPublisher`, DI wiring. |
| `src/ParkMesh.Api` | Presentation / Host | Minimal APIs (`/api/parking/...`), `ParkingHub` (`/hubs/parking`), `SignalRParkingEventSink`, `MetricsBroadcastHostedService`, static serving of `web/` at `/`, OpenTelemetry tracing & metrics (`OTLP` export when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured). |
| `src/ParkMesh.TelemetryWorker` | Edge Worker | Subscribes to raw MQTT sensor frames, filters chatter via `SensorTelemetryProcessor`, and forwards confirmed bay occupancy transitions via `RemoteEngineAllocationProxy`. |
| `src/ParkMesh.Simulator` | Simulator Worker | Generates concurrent vehicle arrivals, sensor flapping noise, simulated vehicle progress steps, and physical bay intrusions against MQTT + REST. |
| `web/` | Visual UI | Zero-build HTML5 Canvas 2D multi-floor renderer (`app.js`), custom SignalR JSON WebSocket client (`signalr-lite.js`), Auto-Demo loop, chaos triggers, and P50/P95/P99 telemetry HUD. |

---

## 3. Core Subsystems

### 3.1 Multi-Floor Weighted Directed Graph & A\* Pathfinding

The parking facility is modeled in memory as a weighted directed graph $G = (V, E)$ via `StandardTopologyFactory.CreateStandardMultiFloorGarage()` (`3` floors: `-1`, `0`, `1`; `120` parking bays):

- **Vertices ($V$)**: Entry/exit gates (`EntryGate`, `ExitGate`), lane intersections (`Intersection`), inter-floor helical ramps (`RampUp`, `RampDown`), and individual bays (`Bay`).
- **Edges ($E$)**: Unidirectional driving segments (`EdgeType.Lane`, `Ramp`, `BayAccess`) carrying base distance/travel weight plus live congestion multiplier (`IEdgeCongestionTracker`).
- **A\* Search (`AStarPathfinder`)**: Uses a `PriorityQueue<string, double>` with an admissible Euclidean heuristic incorporating vertical floor elevation cost (`floorHeightCost`), ensuring optimal shortest-cost routes while dynamically steering around congested aisles.

### 3.2 Atomic Bay Allocation, Fencing Tokens & Live Rerouting

Under high-concurrency bursts (for example 25+ vehicles arriving simultaneously at entry gates), naive read-then-write reservation logic creates race conditions and double-booked bays.

`ParkingAllocationService` + `RedisBayLeaseManager` (`LuaScripts`) eliminate split-brain allocations:

1. **Candidate Ranking**: Queries A\* cost from the vehicle's entry gate to compatible `Free` bays matching `VehicleClass` (`Standard`, `Compact`, `EV`, `PRM`, `VIP`).
2. **Atomic Lua Acquisition**: Executes `LuaScripts.TryAcquireLease` against `parkmesh:lease:{bayId}` (`SET NX EX`), returning both acquisition status and an atomic `INCR` fencing token (`parkmesh:fencing:{bayId}`).
3. **Congestion Lock-In**: Increments live traffic load on all edges along the chosen `ParkingRoute` and updates upstream LED directional panels (`LedPanel`).
4. **Intrusion Detection & Self-Healing Reroute**: If a hardware ultrasonic sensor reports `PhysicalOccupied` on a bay in `Reserved` state (an unbooked driver stole the spot), `ParkingAllocationService.HandlePhysicalOccupancyAsync`:
   - Marks the bay `Occupied`, releases its active lease, and removes edge congestion from the obsolete tail segment.
   - Locates the affected active session, computes a fresh A\* route starting from the vehicle's `LastKnownNodeId`, atomically acquires a new bay lease with a higher fencing token, and emits `RouteReassigned` so both LED panels and live SignalR clients immediately redirect the driver.

### 3.3 Telemetry Ingestion Pipeline & Anti-Flapping

Physical bay sensors suffer from reflection noise, pedestrians crossing bays, and network micro-bursts.

- **Bounded Backpressure (`TelemetryIngestionChannel`)**: Uses `System.Threading.Channels.Channel.CreateBounded<RawSensorReading>` with `BoundedChannelFullMode.DropOldest`, bounding memory usage and preserving freshest physical state under traffic spikes.
- **Anti-Flapping Debounce (`SensorReadingFilter`)**: Requires consecutive matching samples (`MinimumConsecutiveSamples`) spanning at least `StabilityWindow` before confirming a `Free -> Occupied` or `Occupied -> Free` edge transition, preventing spurious reroute storms.

### 3.4 Real-Time Delta Streaming, Chaos Engineering & OpenTelemetry

- **SignalR Hub (`/hubs/parking`)**:
  - Client call: `GetParkingSnapshot()` returns full initial graph geometry, floor list, LED panels, active sessions, bay states, and metrics snapshot.
  - Server-to-client delta events emitted by `SignalRParkingEventSink` and `MetricsBroadcastHostedService`:
    - `RouteAssigned`
    - `RouteReassigned` (including `PreviousBayId` and `Reason`)
    - `BayStateChanged`
    - `VehicleMoved`
    - `LedPanelUpdated`
    - `MetricsTick` (1-second heartbeat carrying P50/P95/P99 latencies, throughput, contention retries, reroute count, and channel drop count)
- **OpenTelemetry Instrumentation (`EngineMetricsRecorder`)**:
  - `Meter("ParkMesh.Engine")`: Histograms (`parkmesh.astar.duration.ms`, `parkmesh.redis.lease.duration.ms`) and Counters (`parkmesh.allocations.total`, `parkmesh.contention.retries.total`, `parkmesh.reroutes.total`).
  - `ActivitySource("ParkMesh.Engine")`: Distributed spans around allocation (`AllocateRoute`) and physical intrusion recovery (`HandlePhysicalOccupancy`).
  - Sliding-window estimator (`SlidingLatencyWindow`, capacity `1024`) exposing real-time interpolated `P50`, `P95`, and `P99` latencies without GC-heavy heap churn.

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
| Sensor Reading | `POST /api/parking/telemetry/readings` | Enqueues a raw sensor reading into `TelemetryIngestionChannel` |
| Chaos Intruder | `POST /api/parking/chaos/intruder` | Injects a physical intrusion into an active reserved bay to demonstrate live rerouting |
| Chaos Burst | `POST /api/parking/chaos/burst?count=25` | Fires concurrent allocation burst across entry gates (`1..80`) |
| SignalR Hub | `WS /hubs/parking` | Bidirectional SignalR JSON protocol hub for real-time deltas |
| MQTT Telemetry | `parkmesh/v1/garages/{garageId}/bays/{bayId}/telemetry` | Raw ultrasonic sensor payload stream |
| MQTT LED Panels | `parkmesh/v1/garages/{garageId}/panels/{panelId}/command` | Retained LED directional guidance payload |

---

## 5. Architecture Decision Records

Key technical trade-offs are documented in `docs/adr/`:

1. [ADR 0001 — Modular Monolith with Dedicated Edge Workers](adr/0001-modular-monolith-over-microservices.md)
2. [ADR 0002 — Atomic Redis Lua Leases with Monotonic Fencing Tokens](adr/0002-redis-lua-leases-with-fencing-tokens.md)
3. [ADR 0003 — Multi-Floor A\* Pathfinding with Admissible Euclidean Heuristic](adr/0003-multi-floor-astar-with-admissible-heuristic.md)
4. [ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce](adr/0004-bounded-channel-backpressure-and-anti-flapping.md)
5. [ADR 0005 — SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator](adr/0005-signalr-delta-protocol-and-zero-build-canvas.md)
