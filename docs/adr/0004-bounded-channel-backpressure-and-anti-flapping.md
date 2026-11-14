# ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Protection against sensor telemetry storms, elimination of false intrusion reroutes from ultrasonic chatter, predictable memory bounds.

## Context

Ultrasonic and radar bay sensors frequently exhibit electromagnetic chatter, partial echoes when pedestrians walk through empty bays, and transient disconnect/reconnect bursts over MQTT. Forwarding every raw frame synchronously into `ParkingAllocationService` would cause spurious bay state flips, unnecessary session cancellations, and cascading reroute storms across live SignalR dashboards. Simultaneously, unbounded in-memory queues risk `OutOfMemoryException` during network partition recovery bursts.

## Decision

Implement a two-stage ingestion pipeline in `ParkMesh.Application.Telemetry` and `ParkMesh.TelemetryWorker`:

1. **Bounded Backpressure (`TelemetryIngestionChannel`)**: Buffer incoming `BaySensorReading` frames in a `Channel.CreateBounded<BaySensorReading>` configured with `BoundedChannelFullMode.DropOldest`, ensuring bounded memory consumption and prioritizing freshest hardware state while tracking `StaleDroppedCount` and `EnqueuedCount`.
2. **Temporal + Sample-Count Anti-Flapping (`SensorReadingFilter`)**: Maintain per-bay state and require both a minimum consecutive count of matching samples (`MinConsecutiveReadings`) and elapsed dwell time (`StabilityWindowMs`) before confirming a physical state transition (`ConfirmedTransitionsCount`).
3. **Confirmed Edge Forwarding (`SensorTelemetryProcessor`, `MqttTelemetryHostedService`)**: Invoke `ReconcilePhysicalOccupancyAsync` only on confirmed transitions, rather than on steady-state heartbeats.

## Consequences

### Positive
- Completely isolates `ParkingAllocationService` from sensor chatter and pedestrian-crossing false positives.
- Deterministic bounded memory footprint under arbitrary MQTT frame floods.
- Measurable `StaleDroppedCount`, `ConfirmedTransitionsCount`, and `DebouncedCount` exposed via `/api/parking/metrics` and SignalR `MetricsTick`.

### Negative
- Adds intentional detection latency equal to `StabilityWindowMs` for legitimate physical parking events, requiring tuning based on physical vehicle speed and sensor sampling frequency.
