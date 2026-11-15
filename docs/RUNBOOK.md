# ParkMesh Operational Runbook

Concise incident response procedures for `ParkMesh.Api` and its backing dependencies. Each scenario separates **behavior verified by automated tests** from **code-derived behavior not yet fault-injected live**.

## Health & Observability Endpoints

| Signal | Endpoint / Component | Healthy Expectation |
|---|---|---|
| Liveness | `GET /health/live` | `200 OK` when Kestrel process is responsive |
| Readiness | `GET /health/ready` | `200 OK` only when `StateHydrationTracker` is hydrated, Redis `PingAsync()` succeeds (if configured), and Postgres `CheckConnectivityAsync()` succeeds (if configured) |
| Metrics | `GET /metrics` | Prometheus scrape endpoint (`OpenTelemetry.Exporter.Prometheus.AspNetCore`) |
| Live Telemetry | SignalR `/hubs/parking` (`MetricsTick`) | Per-tenant bay/session counts + process-global latency percentiles (`AStarLatency`, `RedisLeaseLatency`) and `Telemetry` |

> **Container privilege note:** `ParkMesh.Api`, `ParkMesh.TelemetryWorker`, and `ParkMesh.Simulator` run as non-root `USER app` (asserted in CI via `id -u != 0`).

---

## Scenario 1 — Redis Outage or Partition

### Expected System Behavior
- `RedisBayLeaseManager.PingAsync` returns `false`, causing `GET /health/ready` to fail readiness probes so orchestrators stop routing new traffic to the affected instance.
- `ParkingAllocationService.AllocateAsync` calls `_leaseManager.TryAcquireLeaseAsync(...)` **before** committing `bay.TrySetLeased(...)` in memory. Redis timeouts/connection errors abort allocation before local `ParkingGraph` state mutates, preventing phantom leases.
- `PurgeExpiredSessionsAsync` calls `_leaseManager.TryReleaseLeaseAsync(...)`; expired local sessions wait until Redis recovers if errors occur.
- `GarageStateHydrationHostedService` blocks `/health/ready` on boot and retries `RehydrateStateAsync` every `1s` if Redis is unreachable at startup.

### Verification Evidence
- **Verified automatically:** Atomic Lua lease lock/release, monotonic fencing tokens, 50-way contention, and multi-tenant key isolation in `RedisBayLeaseManagerIntegrationTests` (Testcontainers Redis `7.4-alpine`). Fault-injected outage rollback (`AllocateAsync_WhenLeaseStoreThrowsOutageException_DoesNotLeavePhantomLocalLeaseOrSession`) and `/health/ready` `503 degraded` response on failed lease-store ping (`HealthReady_Returns503Degraded_WhenLeaseStoreOrAuditStoreFailsConnectivity`) are verified in unit/API tests.
- **Code-derived (not chaos-tested live):** Live multi-container Redis network partition and automatic `StackExchange.Redis` socket recovery.

### Operator Actions
1. Check `GET /health/ready` and `ParkMesh.Api` logs for `StackExchange.Redis` timeouts or `"Redis connectivity ping failed."`.
2. Restore Redis reachability/memory headroom (`ConnectionStrings__Redis`). No manual key cleanup is normally needed because leases expire by TTL and `LeaseExpirationSweeperHostedService` sweeps expired in-memory sessions every `2s`.
3. Once `/health/ready` returns `200 OK`, verify new allocations increment `fencingToken` cleanly.

---

## Scenario 2 — PostgreSQL Outage

### Expected System Behavior
- `PostgresParkingAuditStore.CheckConnectivityAsync` catches connectivity failures and returns `false`, degrading `GET /health/ready`.
- **Hot path unaffected:** `ParkingAllocationService` enqueues `ParkingAuditEvent` records to `AuditChannelSink` (`BoundedChannel`, capacity `8192`, `DropOldest`). Allocations and physical occupancy transitions remain sub-millisecond in memory + Redis.
- **Audit durability caveat:** In `ParkingAuditHostedService.FlushAsync`, if `_store.RecordBatchAsync(batch, ct)` throws, the worker logs `"Failed to persist {BatchCount} parking audit events."` and clears `batch` (`batch.Clear()`). Events flushed while Postgres is down are **dropped** rather than retried indefinitely.
- If Postgres is unavailable when `ParkMesh.Api` boots, `GarageStateHydrationHostedService` logs `"Startup state hydration failed; retrying in 1s before marking /health/ready."` and retries `InitializeAsync` + `RehydrateStateAsync` every `1s`.

### Verification Evidence
- **Verified automatically:** Schema init, `NpgsqlBatch` persistence, latest physical/maintenance query, and fail-closed `FORCE ROW LEVEL SECURITY` in `PostgresAuditStoreIntegrationTests` (Testcontainers Postgres `17-alpine`). Fault-injected transient failure survival with batch drop (`ParkingAuditHostedService_SurvivesTransientStoreFailureAndPersistsSubsequentBatch`) and `/health/ready` `503 degraded` on failed store connectivity check (`HealthReady_Returns503Degraded_WhenLeaseStoreOrAuditStoreFailsConnectivity`) are verified in unit/API tests.
- **Code-derived (not chaos-tested live):** Live multi-container Postgres restart and Npgsql pool recovery.

### Operator Actions
1. Check `GET /health/ready` and logs from `ParkingAuditHostedService` / `GarageStateHydrationHostedService`.
2. Restore Postgres availability (`ConnectionStrings__Postgres`) and confirm non-superuser role permissions if `Postgres:AppRole` (`SET LOCAL ROLE`) / RLS is enabled.
3. Confirm `GET /health/ready` returns `200 OK` and verify `GET /api/parking/audit?limit=10` resumes returning fresh events (noting historical rows dropped during the outage window are not backfilled).

---

## Scenario 3 — MQTT Broker (Mosquitto) Outage

### Expected System Behavior
- `MqttConnectionManager.EnsureConnectedAsync` catches exceptions, logs `"Unable to connect to MQTT broker."`, and returns `false`.
- `MqttTelemetryHostedService` disconnects gracefully from subscription state and retries connection + resubscription to `parkmesh/sensors/+/+/state` (`MqttTopics.SensorsWildcard`) and `parkmesh/gates/+/admitted` (`MqttTopics.GatesWildcard`) every `max(1, Mqtt:ReconnectDelaySeconds)` seconds.
- **HTTP fallback stays operational:** `POST /api/parking/telemetry/readings` continues feeding `_sensorChannel` and `_sensorProcessor` directly, independent of MQTT connectivity. When `Mqtt:Enabled=false`, the API logs `"MQTT ingestion disabled via configuration; running local channel processor only."`

### Verification Evidence
- **Verified automatically:** Non-root container startup with `Mqtt__Enabled=false` passes `/health/ready` in CI; HTTP telemetry debounce pipeline is verified in `TelemetryPipelineTests`; unreachable broker handling without throwing (`MqttConnectionManager_EnsureConnectedAsync_ReturnsFalseWhenBrokerUnreachable`) is verified in unit tests.
- **Code-derived (not chaos-tested live):** Live Mosquitto broker restart and automatic topic resubscription (`MqttTelemetryHostedService`).

### Operator Actions
1. Inspect `ParkMesh.Api` and `mosquitto` container logs for broker connection errors (`Mqtt:Host`, `Mqtt:Port`).
2. Restart or restore Mosquitto; watch logs for `"Connected to MQTT broker."` followed by `"Subscribed to MQTT topics..."`.
3. Expect `default`-tenant edge telemetry to resume within `Mqtt:ReconnectDelaySeconds`; reconcile physical bay state via `POST /api/parking/bays/{bayId}/occupancy` if sensors missed updates while offline.

---

## Scenario 4 — `ParkMesh.Api` Process Restart Mid-Operation

### Expected System Behavior
- On startup, `GarageStateHydrationHostedService` initializes Postgres schema and runs `RehydrateStateAsync` across `default` plus every tenant in `Security:AllowedTenants`.
- Rehydration replays `GetLatestBayPhysicalStatesAsync` (marking `Occupied` and `OutOfService` bays) and `GetActiveLeasesAsync` from Redis (restoring `Leased` bays with remaining TTL, fencing tokens, active sessions, and recomputed `A*` route edge congestion) **before** `StateHydrationTracker.MarkHydrated` flips `/health/ready` to healthy.
- Active vehicle sessions (`_sessions`) whose leases already expired during downtime will time out in Redis; physical occupancy already logged in Postgres (`PhysicalOccupied` / `BayOutOfService`) survives restart.
- Single-instance boundary: because `TenantRuntime` and `InMemoryEdgeCongestionTracker` live in memory, running multiple concurrent API pods causes state divergence (ADR 0001).

### Verification Evidence
- **Verified automatically:** `StateHydrationTests.RehydrateStateAsync_RestoresActiveLeasesOccupiedBaysOutOfServiceAndCongestionAcrossRestart` (unit tests with shared stores) and `RedisBayLeaseManagerIntegrationTests.MultiTenantContext_IsolatesRedisLeaseKeysAndSupportsMultiTenantRehydration` (Testcontainers Redis).
- **Verified automatically:** `StateHydrationTracker` gating `/health/ready` verified in `StateHydrationTests` and `ParkingApiIntegrationTests` / CI container smoke test.

### Operator Actions
1. Wait for `GET /health/ready` to return `200 OK` before routing edge traffic (check log `"Rehydrated garage state on startup..."`).
2. If dynamic tenants were created in `Development` mode (`Security:AllowedTenants` empty), note that startup only pre-hydrates `default` when `AllowedTenants` is empty; configure explicit `Security:AllowedTenants` in non-dev environments so all tenants rehydrate at boot.
