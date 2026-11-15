# ADR 0008 — Tenant as Isolated Logical Garage

- **Status:** Accepted
- **Date:** 2026-11-14
- **Context Drivers:** Multi-tenant isolation inside the modular monolith, bounded production attack surface, honest observability semantics, and avoiding premature distributed state complexity.

## Context

ParkMesh evolved from a single virtual garage (`default`) to serving multiple logical tenants over a shared API process, Redis cluster, PostgreSQL database, and SignalR hub. Three architectural tensions needed explicit resolution:

1. **Isolation vs. Operational Simplicity:** Should each tenant get an independent cluster/schema deployment, or act as an isolated logical garage inside the modular monolith?
2. **Process-Wide Telemetry vs. Per-Tenant Views:** `EngineMetricsSnapshot` feeds both `/metrics` (Prometheus) and per-tenant SignalR `MetricsTick` frames; operators need clarity on which fields are per-tenant versus process-global.
3. **Unbounded Tenant Allocation Risk & Telemetry Channel Boundaries:** If any caller can invent arbitrary `X-Tenant-Id` values in production, lazy garage materialization becomes an unbounded memory sink; meanwhile, MQTT sensor topics carry no tenant metadata.

## Decision

Model each tenant as an **isolated logical garage (`TenantRuntime`) within the single API host**, sharing compiled stateless infrastructure while isolating mutable garage state across all tiers:

1. **Application Isolation (`ParkingAllocationService`)**:
   - `_tenants` maps each tenant identifier (`StringComparer.Ordinal`) to a `TenantRuntime` holding a cloned `ParkingGraph`, an independent `InMemoryEdgeCongestionTracker`, and its own `Lock` (`StateLock`), keeping active sessions keyed by `(TenantId, VehicleSessionId)`.
   - `ParkingAllocationService` stays as a cohesive application orchestrator rather than splitting into speculative sub-services without an active correctness driver.
2. **Storage & Messaging Isolation**:
   - **Redis (`RedisBayLeaseManager`)**: Keys are prefixed per tenant (`{KeyPrefix}:tenant:{tenantId}:lease:bay:{bayId}` / `fence:bay:{bayId}`, preserving `{KeyPrefix}:...` for `default`).
   - **PostgreSQL (`PostgresParkingAuditStore`)**: Rows carry `tenant_id`, and `ENABLE`/`FORCE ROW LEVEL SECURITY` bind queries and inserts to `current_setting('app.current_tenant', true)` when running under a non-superuser role (`Postgres:AppRole` / `SET LOCAL ROLE`).
   - **SignalR (`ParkingHub`, `SignalRParkingEventSink`)**: Connections join `tenant:{tenantId}` groups validated by `TenantIdValidator` and `Security:AllowedTenants`.
3. **Fail-Closed Edge Boundaries**:
   - Outside `Development`, startup rejects empty `Security:ApiKey` (unless `Security:AllowUnauthenticatedProduction=true`), rejects empty `Security:AllowedTenants`, and validates every entry in `Security:AllowedTenants` with `TenantIdValidator.IsValid`.
   - `GET /api/parking/topology` requires `ApiKeyEndpointFilter` alongside mutation/state endpoints.
   - HTTP `POST /api/parking/telemetry/readings` stamps `TenantId` from validated `X-Tenant-Id`, `SensorReadingFilter` and `SensorTelemetryProcessor` normalize blank `TenantId` values to `TenantDefaults.DefaultTenantId`, and MQTT ingestion on `parkmesh/sensors/+/+/state` (`MqttTopics.SensorsWildcard`) forces `default`.
4. **Accepted Engineering Trade-Offs**:
   - **Mixed `EngineMetricsSnapshot` scope:** `TotalBays`, `FreeBays`, `LeasedBays`, `OccupiedBays`, `OutOfServiceBays`, and `ActiveSessions` reflect the current tenant's garage; `AllocationsTotal`, `ContentionRetriesTotal`, `ReroutesTotal`, `LeasesExpiredTotal`, `AStarLatency`, `RedisLeaseLatency`, and `Telemetry` remain process-global counters/histograms rather than duplicating full Prometheus time-series per tenant.
   - **Idle-tenant `MetricsTick` scope:** `MetricsBroadcastHostedService` broadcasts `MetricsTick` every `1s` only across `_allocationService.GetKnownTenants()` (tenants with a materialized `TenantRuntime`), rather than tracking unmaterialized SignalR group connections. Connecting to `/hubs/parking?tenantId={id}` without a prior request, `GetParkingSnapshot()`, or `SubscribeTenant()` invocation does not emit `MetricsTick` frames.
   - **Clock injection:** Deterministic domain paths (`ParkingAllocationService`, `SignalRParkingEventSink`, lease expiration) use `TimeProvider`, while non-test-sensitive timestamping (e.g., `RedisBayLeaseManager` response metadata and simulator workers) retains `DateTimeOffset.UtcNow`.

## Consequences

### Positive
- Cross-tenant collisions across bay IDs (`F0-B01`), active sessions, routing congestion, Redis Lua fencing tokens, PostgreSQL audit history, and SignalR frames are eliminated without running per-tenant processes.
- Measured heap footprint stays linear and predictable (`~97.5 KiB` per materialized tenant; see [Capacity Baseline](../CAPACITY.md)).
- Fail-closed startup checks prevent anonymous tenant proliferation in production while keeping standalone `Development` mode zero-config.

### Negative / Constraints
- A client connected to `/hubs/parking?tenantId={id}` for an idle, unmaterialized tenant receives no `MetricsTick` until an HTTP request, `GetParkingSnapshot()`, or `SubscribeTenant()` initializes its `TenantRuntime`; once materialized, it remains in `GetKnownTenants()` until process restart.
- Shared process-global latency histograms mean one noisy tenant can skew `AStarLatency` / `RedisLeaseLatency` percentiles visible in `MetricsTick`.
- Horizontal scale-out of multiple `ParkMesh.Api` replicas would require shared edge congestion and a SignalR backplane (deferred per ADR 0001).

## Revisit Criteria

Revisit this decision only when one of the following concrete triggers occurs:
- Active materialized tenant count exceeds `~2,500` in a single `512 MiB` API container (adding LRU eviction/idle unloading on `TenantRuntime` backed by Redis + PostgreSQL rehydration).
- Untrusted public clients require cryptographic tenant binding (replacing shared `Security:ApiKey` + trusted `X-Tenant-Id` with JWT/OIDC tenant claim validation at ingress or API gateway).
- Multiple physical sites share one MQTT broker (introducing versioned per-tenant MQTT topic namespaces per [Contract Versioning Policy](../CONTRACT_VERSIONING.md)).
- Tenant-level SLA billing requires per-tenant latency histograms or dedicated SignalR subscriber tracking.
