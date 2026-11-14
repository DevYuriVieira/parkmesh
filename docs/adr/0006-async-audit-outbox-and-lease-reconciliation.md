# ADR 0006 — Asynchronous Audit Outbox, Proactive Lease Reconciliation, and Edge Hardening

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Sub-millisecond hot-path preservation with durable auditability, automatic cleanup of abandoned reservations, container observability, and machine-to-machine edge protection.

## Context

Phase 1–3 established an in-memory multi-floor A\* router, atomic Redis Lua leases, MQTT anti-flapping telemetry, and live SignalR visualization. Moving toward production readiness exposes four operational gaps:

1. Writing synchronously to PostgreSQL during `AllocateAsync` or `ReconcilePhysicalOccupancyAsync` would add network/disk latency to the critical routing path.
2. When a driver claims a lease and never arrives (*no-show*), Redis TTL expiration removes the Redis key automatically, but in-memory graph bay state (`BayStatus.Leased`) and edge congestion remain stale unless swept proactively.
3. Operators need dependency-aware readiness checks (`/health/ready`), machine-to-machine endpoint authentication (`X-Api-Key`), rate limiting on chaos endpoints, and Prometheus/Grafana telemetry scraping.
4. Claimed sub-millisecond performance requires reproducible microbenchmarks (`BenchmarkDotNet`).

## Decision

Introduce a non-blocking production-hardening layer without altering the core modular-monolith boundary:

1. **Bounded Async Audit Outbox (`AuditChannelSink`, `PostgresParkingAuditStore`, `ParkingAuditHostedService`)**:
   - `ParkingAllocationService` enqueues immutable `ParkingAuditEvent` records into a bounded `Channel<ParkingAuditEvent>` (`DropOldest`, capacity `8192`) in sub-microsecond non-blocking time.
   - `ParkingAuditHostedService` drains queued events in batches (`NpgsqlBatch`) into PostgreSQL (`parking_audit_events`) via `NpgsqlDataSource`, falling back automatically to `InMemoryParkingAuditStore` when `ConnectionStrings:Postgres` is omitted.
2. **Proactive Lease Expiration Sweeper (`PurgeExpiredSessionsAsync`, `LeaseExpirationSweeperHostedService`)**:
   - Use injectable `TimeProvider` across allocation and expiration checks so unit tests run deterministically without real-time waits.
   - Sweep expired sessions every 2 seconds, releasing `ParkingBay` back to `BayStatus.Free`, removing stale edge congestion load, emitting `BayStateChanged` (`Free`) to SignalR clients, incrementing `parkmesh.leases.expired.total`, and logging `AuditEventType.LeaseExpired`.
3. **Edge Security & Health Probes (`ApiKeyEndpointFilter`, Rate Limiter, `/health/ready`, `/metrics`)**:
   - Protect edge ingestion/progress/occupancy routes with fixed-time `X-Api-Key` verification when `Security:ApiKey` is configured, and enforce dynamic fixed-window rate limiting on `/api/parking/chaos/*`.
   - Separate `/health/live` from dependency-backed `/health/ready` (`IBayLeaseManager.PingAsync` + `IParkingAuditStore.CheckConnectivityAsync`).
   - Expose `/metrics` via `OpenTelemetry.Exporter.Prometheus.AspNetCore` wired to Grafana dashboards.

## Consequences

### Positive
- Allocation and rerouting remain sub-millisecond in benchmarks while audit durability runs out-of-band.
- Abandoned leases no longer cause phantom congestion or stuck bays in the live garage map.
- Zero external configuration is still required for local dev/testing due to automatic in-memory store fallbacks and disabled-by-default empty API keys.

### Negative
- Under sustained extreme database outage exceeding `8192` buffered audit events, `DropOldest` discards oldest audit rows rather than blocking vehicle routing.
- Proactive lease sweeping relies on up to a 2-second polling cadence plus single-instance in-memory session tracking (`ParkingAllocationService`).
