# ADR 0004 — Bounded Channel Backpressure and Anti-Flapping Debounce

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Protection against sensor telemetry storms, elimination of false intrusion reroutes from ultrasonic chatter, predictable memory bounds.

## Context

Ultrasonic and radar bay sensors frequently exhibit electromagnetic chatter, partial echoes when pedestrians walk through empty bays, and transient disconnect/reconnect bursts over MQTT. Forwarding every raw frame synchronously into `ParkingAllocationService` would cause spurious bay state flips, unnecessary session cancellations, and cascading reroute storms across live SignalR dashboards. Simultaneously, unbounded in-memory queues risk `OutOfMemoryException` during network partition recovery bursts.

## Decision

Implement a two-stage ingestion pipeline in `ParkMesh.Application.Telemetry` and `ParkMesh.TelemetryWorker`:

1. **Bounded Backpressure (`TelemetryIngestionChannel`)**: Buffer incoming `RawSensorReading` frames in a `Channel.CreateBounded<RawSensorReading>` configured with `BoundedChannelFullMode.DropOldest`, ensuring bounded memory consumption and prioritizing freshest hardware state while tracking `DroppedCount`.
2. **Temporal + Sample-Count Anti-Flapping (`SensorReadingFilter`)**: Maintain per-bay state and require both a minimum consecutive count of matching samples (`MinimumConsecutiveSamples`) and elapsed dwell time (`StabilityWindow`) before emitting a confirmed physical state transition.
3. **Confirmed Edge Forwarding (`SensorTelemetryProcessor`, `MqttTelemetryHostedService`)**: Invoke `HandlePhysicalOccupancyAsync` only on confirmed transitions, rather than on steady-state heartbeats.

## Consequences

### Positive
- Completely isolates `ParkingAllocationService` from sensor chatter and pedestrian-crossing false positives.
- Deterministic bounded memory footprint under arbitrary MQTT frame floods.
- Measurable `DroppedCount`, `ConfirmedTransitionCount`, and `FilteredNoiseCount` exposed via `/api/parking/metrics` and SignalR `MetricsTick`.

### Negative
- Adds intentional detection latency equal to `StabilityWindow` for legitimate physical parking events, requiring tuning based on physical vehicle speed and sensor sampling frequency.
