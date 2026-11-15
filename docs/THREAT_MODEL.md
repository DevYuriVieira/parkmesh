# ParkMesh Threat Model

Single-page operational threat summary for the ParkMesh modular monolith (`ParkMesh.Api`, `ParkMesh.TelemetryWorker`, `ParkMesh.Simulator`, Redis, PostgreSQL, Mosquitto).

## 1. Critical Assets

| Asset | Impact if Compromised |
|---|---|
| **Bay Lease & Fencing Integrity (Redis + `TenantRuntime`)** | Double-booked parking bays, corrupted routes, bypassed maintenance/intrusion overrides |
| **Tenant Isolation Boundary (`X-Tenant-Id`, PostgreSQL RLS, SignalR groups)** | Cross-tenant read/write of audit logs, garage occupancy state, or live routes |
| **Process Memory & Availability (`ParkMesh.Api`)** | OOM via unbounded lazy `TenantRuntime` creation, channel saturation, chaos abuse |
| **Operational Credentials (`Security:ApiKey`, DB/Redis strings)** | Unauthorized bay allocation, lease release, maintenance manipulation, or audit inspection |

## 2. Trust Boundaries & Actors

1. **HTTP Edge Clients & Simulator -> `ParkMesh.Api` (`/api/parking/*`, `/hubs/parking`)**
   - Protected outside `Development` by `ApiKeyEndpointFilter` (`X-Api-Key` fixed-time comparison) across `/api/parking/topology`, `/snapshot`, `/metrics`, `/audit`, `/bays`, `/allocations`, `/allocations/{bayId}`, `/bays/{bayId}/occupancy`, `/bays/{bayId}/maintenance`, `/sessions`, `/sessions/{sessionId}/progress`, `/telemetry/metrics`, `/telemetry/readings`, and `/api/parking/chaos/*`.
   - `TenantResolutionMiddleware` validates `X-Tenant-Id` (or `?tenantId=` on `/hubs/parking`) via `TenantIdValidator` and rejects unrecognized tenants when `Security:AllowedTenants` is non-empty.
   - Outside `Development`, startup fails closed if `Security:ApiKey` is empty (unless `Security:AllowUnauthenticatedProduction=true`), if `Security:AllowedTenants` is empty, or if any configured entry fails `TenantIdValidator.IsValid`.
   - `/api/parking/chaos/*` routes are unmapped unless `Security:EnableChaosEndpoints=true`, and rate-limited by a fixed-window limiter.
   - `SecurityHeadersMiddleware` sets baseline security headers; SignalR `SubscribeTenant` validates both `TenantIdValidator` and `Security:AllowedTenants`.
2. **MQTT Broker (Mosquitto) -> `MqttTelemetryHostedService`**
   - Subscribes to `parkmesh/sensors/+/+/state` (`MqttTopics.SensorsWildcard`) and `parkmesh/gates/+/admitted` (`MqttTopics.GatesWildcard`) on the internal Docker/LAN network (`MqttConnectionManager`).
   - Any payload `TenantId` arriving over MQTT is stripped (`reading with { TenantId = null }`) and routed exclusively to `default`, preventing spoofing into other tenants via MQTT.
   - Malformed JSON payloads are caught and logged without crashing ingestion.
3. **API -> Redis & PostgreSQL**
   - Redis executes atomic Lua scripts (`LuaScripts.AcquireBayLease`, `LuaScripts.ReleaseBayLease`) keyed per tenant prefix so concurrent contention and stale-fencing-token releases fail atomically.
   - PostgreSQL uses `NpgsqlDataSource` + parameterized queries and enforces `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` on `parking_audit_events` using transaction-local `app.current_tenant`. Verified fail-closed (`SQLSTATE 42501`) on a non-superuser role (`PostgresAuditStoreIntegrationTests`).

## 3. Abuse Cases, Residual Risks & Accepted PoC Scope

| Threat / Gap | Current Mitigation | Accepted Limit (Do Not Over-Engineer Yet) |
|---|---|---|
| **Unbounded memory exhaustion via arbitrary `X-Tenant-Id`** | Production requires non-empty `Security:AllowedTenants`, validated at startup (`~97.5 KiB`/tenant). | In `Development` (or if explicitly open), arbitrary valid tenant IDs allocate in-memory `TenantRuntime` until process restart. |
| **Shared secret privilege escalation** | Constant-time `X-Api-Key` comparison; fail-closed production defaults. | Single shared `Security:ApiKey` trusts `X-Tenant-Id` headers from authenticated edge callers; deploy upstream JWT/OIDC termination if untrusted multi-tenant callers share ingress. |
| **Event flood / Channel exhaustion** | Bounded channels (`AuditChannelSink` cap `8192` `DropOldest`; `_gateChannel` cap `1000` `DropOldest`; `TelemetryIngestionChannel` bounded + `SensorReadingFilter` anti-flapping debounce per `(tenant, bay)`). | Sustained DB outage over `8192` events drops oldest buffered audit rows rather than blocking allocation latency. |
| **Unauthenticated local compose services** | Container runs non-root `USER app` (`UID != 0`); CI smoke test asserts `id -u != 0`. | `infra/docker-compose.yml` uses `ASPNETCORE_ENVIRONMENT=Development`, open Mosquitto, and local dev credentials strictly for local demos — harden secrets, TLS, and ACLs via environment overrides in real deployments. |
| **Superuser DB role bypassing RLS** | Supports `Postgres:AppRole` (`SET LOCAL ROLE` in `ApplyRlsContextAsync`) and `FORCE ROW LEVEL SECURITY`. | Connecting with a PostgreSQL superuser/owner without `Postgres:AppRole` bypasses RLS unless restricted credentials (`NOSUPERUSER NOBYPASSRLS`) are provisioned. |
