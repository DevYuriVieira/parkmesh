# ADR 0007 — Single-Pass Indexed Multi-Target Routing, Startup State Hydration, and Bay Maintenance Lifecycle

- **Status:** Accepted
- **Date:** 2026-11-14
- **Context Drivers:** Eliminating per-allocation dictionary/pathfinding allocations, recovering live leases and physical occupancy after process restarts before serving traffic, and enabling safe operational bay maintenance with automated vehicle rerouting.

## Context

After Phase 4 hardening, three engineering bottlenecks remained before production-grade operation:

1. **Multi-Bay Allocation Overhead**: `AStarPathfinder.FindCandidateBayRoutes` previously executed one full A* graph traversal per compatible `Free` bay (up to 120 bays) using string-keyed `Dictionary<string, ...>` and `HashSet<string>` structures on every request, creating linear `O(B * (V log V + E))` latency and transient GC allocations under burst load.
2. **Process Restart State Loss**: While Redis held distributed TTL leases and PostgreSQL held durable audit history, an API process restart began with empty in-memory `BayStatus` flags and zero tracked active sessions in `ParkingAllocationService`, causing `/health/ready` to pass before local graph state matched persisted truth.
3. **Maintenance & API Discoverability**: The domain entity (`ParkingBay.MarkOutOfService()`) existed without an operational REST workflow, interactive UI control, live displaced-vehicle reroute path, or machine-readable OpenAPI schema.

## Decision

Implement Phase 5 improvements across routing, resilience, operations, and API surface:

1. **Single-Pass Indexed Multi-Target Search (`AStarPathfinder`)**:
   - Compile a thread-safe `CompiledTopology` per `ParkingGraph` instance via `ConditionalWeakTable<ParkingGraph, CompiledTopology>`, assigning dense integer indices to every node and edge.
   - Rent `ArrayPool<double>`, `ArrayPool<int>`, `ArrayPool<bool>`, and `ArrayPool<GraphEdge?>` scratch arrays in `FindCandidateBayRoutes` so candidate discovery executes in **one multi-target Dijkstra sweep** with an exact sliding k-th best score cutoff (`currentDist > kthWorstScore`), eliminating string hashing and retaining mathematical equivalence to global best-bay ranking.
2. **Startup State Hydration & Readiness Gating (`IStateHydrationTracker`, `GarageStateHydrationHostedService`)**:
   - Batch-query active Redis lease keys (`IBatch.StringGetAsync` + `IBatch.KeyTimeToLiveAsync`) via `IBayLeaseManager.GetActiveLeasesAsync` and recover the latest per-bay physical/maintenance events (`PhysicalOccupied`, `IntrusionRerouted`, `PhysicalVacated`, `BayOutOfService`, `BayRestored`) via `IParkingAuditStore.GetLatestBayPhysicalStatesAsync` (`DISTINCT ON (bay_id)` indexed query in PostgreSQL).
   - Run `GarageStateHydrationHostedService` on boot to initialize audit schema, restore physical bay states, recreate active vehicle sessions and corridor congestion, and gate `/health/ready` behind `IStateHydrationTracker.IsHydrated` while keeping `/health/live` non-blocking.
3. **Operational `OutOfService` Lifecycle, Interactive Canvas & OpenAPI 3**:
   - Expose `PATCH /api/parking/bays/{bayId}/maintenance`, reusing the displaced-session rerouting workflow to invalidate active leases, increment reroutes, compute an alternative path from `LastKnownNodeId`, emit SignalR `BayStateChanged` + `RouteReassigned`, and record `BayOutOfService` / `BayRestored` audit events.
   - Surface click-to-toggle maintenance controls and a collapsible `/api/parking/audit` drawer in `web/`, alongside `.NET 10` OpenAPI (`/openapi/v1.json`) and `Testcontainers.PostgreSql` integration coverage.

## Consequences

### Positive
- Multi-target candidate search scales independently of total bay count, dropping `AStar_Find15Candidates_Standard` from hundreds of microseconds and ~850 KB allocations down to low microseconds and minimal heap traffic.
- Container restarts recover occupied bays, maintenance state, active vehicle routes, fencing tokens, and corridor congestion automatically before readiness probes report healthy.
- Real PostgreSQL DDL/batch/query paths (`DISTINCT ON`) are verified against `postgres:17-alpine` via `Testcontainers.PostgreSql` in CI.

### Negative
- Topology compilation assumes `ParkingGraph` structure (nodes, edges, and bay definitions) is immutable for a given `ParkingGraph` instance; only dynamic edge weights and bay runtime statuses mutate at runtime.
- Multi-replica active-active API deployments would still require moving active session progress state out of process-local memory into shared storage.
