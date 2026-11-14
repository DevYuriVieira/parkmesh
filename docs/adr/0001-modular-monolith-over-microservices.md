# ADR 0001 — Modular Monolith with Dedicated Edge Workers

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Sub-millisecond pathfinding latency, strong consistency across spatial graph state and edge congestion, operational simplicity for IoT telemetry ingestion.

## Context

ParkMesh evaluates multi-floor A\* graph traversals, updates live edge congestion weights, acquires distributed Redis leases, recalculates routes on physical intrusion, and pushes real-time deltas over SignalR. Splitting graph state, pathfinding, congestion tracking, and allocation into separate network-connected microservices would introduce serialization latency, distributed transaction complexity, and stale local graph caches on the hottest path of the engine.

At the same time, edge responsibilities—such as consuming noisy high-frequency MQTT frames from hundreds of ultrasonic bay sensors and simulating synthetic fleets—have distinct burst characteristics and restart cadences from the core routing API.

## Decision

Adopt a **Modular Monolith + Dedicated Edge Workers** topology:

1. Keep `ParkMesh.Domain`, `ParkMesh.Application`, `ParkMesh.Infrastructure`, and `ParkMesh.Api` strictly layered by dependency inversion inside a single deployable routing engine (`ParkMesh.Api`).
2. Isolate edge telemetry consumption (`ParkMesh.TelemetryWorker`) and synthetic load/noise generation (`ParkMesh.Simulator`) into standalone `.NET` worker hosts communicating with `ParkMesh.Api` over HTTP and Eclipse Mosquitto MQTT.
3. Enforce strict project boundaries so domain entities (`ParkingGraph`, `ParkingBay`, `ParkingRoute`) remain free of infrastructure and framework dependencies.

## Consequences

### Positive
- Zero inter-service serialization overhead between `AStarPathfinder`, `IEdgeCongestionTracker`, and `ParkingAllocationService`.
- Edge telemetry spikes or broker disconnects cannot starve or crash core HTTP/SignalR request threads inside `ParkMesh.Api`.
- Clean seam to extract specific subsystems later if independent scaling becomes necessary.

### Negative
- `ParkMesh.Api` owns the authoritative in-memory `ParkingGraph` instance, requiring sticky routing or shared distributed eventing if horizontally scaled beyond a single active routing engine instance.
