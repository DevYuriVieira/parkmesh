# ParkMesh

Smart parking routing and bay allocation engine written in .NET 10 (C# 14). It models a multi-floor garage as a weighted directed graph, routes incoming vehicles with multi-floor A* (incorporating live corridor congestion), coordinates bay reservations using Redis Lua scripts with monotonic fencing tokens, ingests ultrasonic sensor telemetry over MQTT with debounce filtering, and broadcasts live state deltas over SignalR to an HTML5 Canvas simulator.

## Overview

- `ParkMesh.Domain`: 3-floor (`0..2`), 120-bay graph model with directional lanes, helical ramps, LED directional panels, and vehicle type constraints (`Standard`, `EV`, `SUV`, `Pcd`, `Moto`).
- `ParkMesh.Application`: Cached dense-index topology (`ConditionalWeakTable`), `ArrayPool<T>` single-pass multi-target search with early-exit cutoff, multi-round contention replanning under parallel arrivals, intrusion rerouting, maintenance toggles (`OutOfService`), bounded telemetry debounce (`SensorReadingFilter`), and async tenant context (`ITenantContext`).
- `ParkMesh.Infrastructure`: Atomic Redis Lua bay leases (`LuaScripts.AcquireBayLease`) with automatic in-memory fallback when `ConnectionStrings:Redis` is unset, MQTTnet ingestion worker, and bounded channel audit outbox (`AuditChannelSink`) flushing batches to PostgreSQL (`PostgresParkingAuditStore`) with `ENABLE`/`FORCE ROW LEVEL SECURITY` fail-closed policies (`set_config('app.current_tenant', ..., true)`) or `InMemoryParkingAuditStore` locally.
- `ParkMesh.Api`: Minimal APIs, `/hubs/parking` SignalR stream, Prometheus `/metrics`, OpenAPI spec (`/openapi/v1.json`), startup state hydration gating `/health/ready`, SHA-256 constant-time `X-Api-Key` verification, `X-Tenant-Id` validation and optional allowlisting (`Security:AllowedTenants`), security response headers, chaos rate limiting, lease expiration sweeper, and static dashboard hosting (`web/`).
- `ParkMesh.TelemetryWorker` & `ParkMesh.Simulator`: Standalone MQTT simulator (noisy sensors, intrusions, fleet arrivals) and worker forwarding confirmed physical state transitions to the API.

## Repository Layout

```text
ParkMesh.sln
Directory.Build.props
infra/
  docker-compose.yml              # Redis, PostgreSQL, Mosquitto (+ app/prometheus/grafana profiles)
  mosquitto/mosquitto.conf
src/
  ParkMesh.Domain/
  ParkMesh.Application/
  ParkMesh.Infrastructure/
  ParkMesh.Api/
  ParkMesh.TelemetryWorker/
  ParkMesh.Simulator/
web/
  index.html                      # 2D multi-floor canvas simulator and audit drawer
  styles.css
  app.js
  signalr-lite.js
tests/
  ParkMesh.UnitTests/
  ParkMesh.IntegrationTests/
benchmarks/
  ParkMesh.Benchmarks/
docs/
  ARCHITECTURE.md
  adr/                            # ADRs 0001-0007
.github/
  workflows/ci.yml
```

## Quick Start

### Standalone mode (no Docker required)

When `ConnectionStrings:Redis` and `ConnectionStrings:Postgres` are omitted, the API uses `InMemoryBayLeaseManager` and `InMemoryParkingAuditStore` so you can run the dashboard locally right away:

```bash
dotnet run --project src/ParkMesh.Api
```

Open the URL printed in the console (for example `http://localhost:5150`) to use the 2D canvas dashboard (`/`), SignalR updates (`/hubs/parking`), snapshot API (`/api/parking/snapshot`), and chaos triggers.

### Full stack with Docker Compose

Start Redis, PostgreSQL, and Mosquitto:

```bash
docker compose -f infra/docker-compose.yml up -d
```

Or run the containerized API, telemetry worker, simulator, Prometheus, and Grafana together:

```bash
docker compose -f infra/docker-compose.yml --profile full up -d --build
```

To run the standalone MQTT pipeline manually against a local `ParkMesh.Api` instance:

```bash
dotnet run --project src/ParkMesh.TelemetryWorker
dotnet run --project src/ParkMesh.Simulator
```

## Tests and Benchmarks

```bash
# Unit tests
dotnet test tests/ParkMesh.UnitTests

# Integration tests without Docker (API, OpenAPI, SignalR, security headers, tenant isolation)
dotnet test tests/ParkMesh.IntegrationTests --filter "FullyQualifiedName!~PostgresAuditStoreIntegrationTests&FullyQualifiedName!~RedisBayLeaseManagerIntegrationTests"

# Full integration suite including Testcontainers Redis and PostgreSQL RLS tests (requires Docker)
dotnet test tests/ParkMesh.IntegrationTests

# BenchmarkDotNet suite
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release -- --filter *
```

Microbenchmark numbers (`BenchmarkDotNet v0.15.8`, `.NET 10.0.11`, in-process short run):

| Benchmark | Phase 4 Baseline | Current Mean | Current Allocated |
| :--- | ---: | ---: | ---: |
| `AStar_Find15Candidates_Standard` | ~689.2 us (~847 KB) | `5.43 us` | `13,880 B` |
| `AStar_Find15Candidates_EV` | ~320.6 us (~409 KB) | `13.48 us` | `19,496 B` |
| `AllocateAndRelease_InMemory` | ~636.1 us (~892 KB) | `10.14 us` | `15,440 B` |
| `TelemetryFilter_EvaluateReading` | ~58.7 ns (72 B) | `59.21 ns` | `72 B` |
| `SlidingWindow_Compute1024Percentiles` | ~9.21 us (~8.1 KB) | `7.20 us` | `8,264 B` |

## Documentation

- [Architecture Blueprint](docs/ARCHITECTURE.md)
- [ADR 0001: Modular Monolith with Dedicated Edge Workers](docs/adr/0001-modular-monolith-over-microservices.md)
- [ADR 0002: Atomic Redis Lua Leases with Monotonic Fencing Tokens](docs/adr/0002-redis-lua-leases-with-fencing-tokens.md)
- [ADR 0003: Multi-Floor A* Pathfinding with Admissible Euclidean Heuristic](docs/adr/0003-multi-floor-astar-with-admissible-heuristic.md)
- [ADR 0004: Bounded Channel Backpressure and Anti-Flapping Debounce](docs/adr/0004-bounded-channel-backpressure-and-anti-flapping.md)
- [ADR 0005: SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator](docs/adr/0005-signalr-delta-protocol-and-zero-build-canvas.md)
- [ADR 0006: Asynchronous Audit Outbox, Proactive Lease Reconciliation, and Edge Hardening](docs/adr/0006-async-audit-outbox-and-lease-reconciliation.md)
- [ADR 0007: Single-Pass Indexed Multi-Target Routing, Startup State Hydration, and Bay Maintenance Lifecycle](docs/adr/0007-indexed-multi-target-routing-and-state-hydration.md)
