# ParkMesh Contract & Schema Versioning Policy

Lightweight compatibility rules for evolving ParkMesh's four runtime boundaries without breaking edge simulators, web clients, or warm-restart audit replay.

## 1. HTTP / OpenAPI (`/api/parking/*`, `/openapi/v1.json`)

- **Additive changes (safe in-place):** Adding new endpoints, optional query parameters, optional JSON request fields with server defaults, or new properties on JSON responses (`AllocateSpotApiRequest`, `PhysicalOccupancyApiRequest`, `MaintenanceApiRequest`, `VehicleProgressApiRequest`, `EngineMetricsSnapshot`, etc.).
- **Breaking changes (require `/api/v2/...` or deprecation overlap):** Removing/renaming existing routes (`/api/parking/allocations`, `/bays/{bayId}/occupancy`, `/bays/{bayId}/maintenance`, `/sessions/{sessionId}/progress`, `/telemetry/readings`, etc.), renaming or re-typing JSON properties, changing HTTP status codes relied upon by edge callers (`201`, `204`, `409`), or enforcing tighter headers without a `Development`/opt-out toggle.
- **OpenAPI verification:** Any change to public HTTP routes or payload records must keep `/openapi/v1.json` generation valid (verified in CI integration tests).

## 2. SignalR Real-Time Protocol (`/hubs/parking`)

- **Client-invoked hub methods (`ParkingHub`):** `GetParkingSnapshot()`, `SubscribeTenant(string tenantId)`.
- **Server-pushed events (`SignalRParkingEventSink`, `MetricsBroadcastHostedService`):** `RouteAssigned`, `RouteReassigned`, `BayStateChanged`, `VehicleMoved`, `LedPanelUpdated`, `MetricsTick`.
- **Group contract:** Tenant streams are segmented via `tenant:{tenantId}` (and `?tenantId=` / `X-Tenant-Id` on connect).
- **Compatibility rule:** Never rename, remove, or alter the semantics of existing hub method names or push event names in place. Append new optional properties (e.g., defaulted trailing fields such as `LeasesExpiredTotal = 0`, `OutOfServiceBays = 0` on `EngineMetricsSnapshot`) so existing browser clients (`web/app.js`) ignore unknown fields safely.

## 3. MQTT Topics & Telemetry Payloads

- **Subscribed topics (`MqttTelemetryHostedService`):** `parkmesh/sensors/+/+/state` (`MqttTopics.SensorsWildcard`, `BaySensorReading`) and `parkmesh/gates/+/admitted` (`MqttTopics.GatesWildcard`, `VehicleAdmittedEvent`); published panel directives (`LedPanelCommand`) target `MqttTopics.PanelDirection(panelId)` (`parkmesh/panels/{panelId}/direction`).
- **Payload evolution:** JSON deserialization uses `JsonSerializerDefaults.Web` with `JsonStringEnumConverter` (`MqttConnectionManager.JsonOptions`). New fields must be nullable or defaulted (`BaySensorReading.TenantId` is optional and ignored over MQTT, where ingestion forces `default`).
- **Topic tenant migration rule:** Because current edge sensor/gate topics do not carry tenant identifiers, introducing multi-tenant MQTT broker routing later requires introducing a new versioned topic namespace (e.g., `parkmesh/v2/tenants/{tenantId}/sensors/+/+/state`) while keeping `parkmesh/sensors/+/+/state` mapped to `default` during rollout.

## 4. PostgreSQL Audit Schema & RLS (`parking_audit_events`)

- **Idempotent additive migrations:** Schema initialization in `PostgresParkingAuditStore.InitializeAsync` uses `CREATE TABLE IF NOT EXISTS`, `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` (e.g., backfilling `tenant_id` with `'default'`), and idempotent RLS/policy setup.
- **RLS safety invariants:** Every tenant-scoped table must preserve `ENABLE ROW LEVEL SECURITY` and `FORCE ROW LEVEL SECURITY` bound to `current_setting('app.current_tenant', true)` so restricted non-superuser roles fail closed (`42501`) when tenant context is unset.
- **Replay compatibility:** `GarageStateHydrationHostedService` replays `GetLatestBayPhysicalStatesAsync` and `GetRecentAsync` on startup using existing `AuditEventType` string values (`BayAllocated`, `LeaseReleased`, `LeaseExpired`, `PhysicalOccupied`, `PhysicalVacated`, `IntrusionRerouted`, `BayOutOfService`, `BayRestored`). Add new enum values strictly by adding new variants — never rename persisted `event_type` strings without a backfill migration.
