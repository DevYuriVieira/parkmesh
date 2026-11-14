# ADR 0005 — SignalR Delta Protocol and Zero-Build HTML5 Canvas Simulator

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Sub-second visual observability of multi-floor A\* routing and intrusion recovery, minimal frontend toolchain friction, low wire overhead under burst simulations.

## Context

Observing distributed spatial routing behavior through logs or JSON endpoints alone makes concurrency conflicts, A\* path selection, ramp transitions, and physical-intrusion rerouting difficult to validate interactively. At the same time, repeatedly polling full 120-bay graph snapshots every few hundred milliseconds wastes bandwidth and introduces choppy visual jumps. Adding a heavy Node/Webpack frontend toolchain inside a `.NET 10` backend-focused repository would also complicate onboarding and CI builds.

## Decision

Pair a SignalR **snapshot + incremental delta protocol** in `ParkMesh.Api` with a **zero-build static frontend** in `web/`:

1. **Bootstrap + Delta Protocol (`ParkingHub`, `SignalRParkingEventSink`, `MetricsBroadcastHostedService`)**:
   - Expose `GetParkingSnapshot()` over `/hubs/parking` for one-time topology, gate, LED panel, active session, and metric hydration on connect/reconnect.
   - Push fine-grained delta events (`RouteAssigned`, `RouteReassigned`, `BayStateChanged`, `VehicleMoved`, `LedPanelUpdated`, and 1 Hz `MetricsTick`) on domain state changes.
2. **Zero-Build Static Canvas Client (`web/`)**:
   - Serve `web/index.html`, `web/styles.css`, `web/app.js`, and a lightweight native WebSocket SignalR JSON client (`web/signalr-lite.js`) directly from `ParkMesh.Api` at `/`.
   - Render multi-floor lanes, bays, LED arrows, animated vehicles, reroute flashes, chaos controls (`Intruder`, `Burst`), and Auto-Demo mode without requiring `npm` or external CDN scripts.

## Consequences

### Positive
- Running `dotnet run --project src/ParkMesh.Api` immediately launches both the engine and an interactive 2D live visual simulator with zero external frontend dependencies or internet connectivity required.
- Fine-grained SignalR delta messages keep frame updates responsive even during 25-to-80-vehicle chaos bursts.
- SignalR WebSocket contract (`\u001e` record-delimited JSON) is straightforward to test directly in ASP.NET Core integration tests.

### Negative
- Maintaining UI complexity beyond an operational/diagnostic simulator (such as multi-view complex state management) would eventually benefit from a dedicated typed frontend framework if expanded into a full SaaS operator portal.
