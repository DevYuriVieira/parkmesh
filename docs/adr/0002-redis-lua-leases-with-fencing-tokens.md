# ADR 0002 — Atomic Redis Lua Leases with Monotonic Fencing Tokens

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Burst concurrency at entry gates, prevention of double-booked bays, stale writer protection under GC pauses or delayed sensor events.

## Context

When 25 or more vehicles arrive concurrently across multiple entry gates, multiple threads compute candidate routes simultaneously against the same set of nearest compatible `Free` bays. Using separate Redis `GET` followed by `SET` commands creates a time-of-check to time-of-use (TOCTOU) race condition where two drivers can be routed to the same physical bay. Furthermore, delayed release or expiry notifications from an earlier stalled process must never overwrite or revoke a newer legitimate reservation.

## Decision

Implement `RedisBayLeaseManager` backed by atomic server-side Redis Lua scripts (`LuaScripts`):

1. Execute `LuaScripts.AcquireBayLease` atomically inside Redis by checking `{prefix}:lease:bay:{bayId}` (`EXISTS`), incrementing `{prefix}:fence:bay:{bayId}` (`INCR`), and storing `{sessionId}:{token}` with `SET ... PX {ttlMilliseconds}` to issue a strictly increasing `long FencingToken` per bay acquisition.
2. Require matching fencing token verification inside `LuaScripts.ReleaseBayLease` before deleting `{prefix}:lease:bay:{bayId}` on release or expiration.
3. Retry candidate bays deterministically inside `ParkingAllocationService` when contention occurs and record contention retries via `IEngineMetricsRecorder.RecordContentionRetry()`.
4. Provide `InMemoryBayLeaseManager` implementing the identical `IBayLeaseManager` contract (including atomic transitions and monotonic fencing tokens) when `ConnectionStrings:Redis` is unconfigured for zero-dependency local runs and fast unit/API tests.

## Consequences

### Positive
- Eliminates double-booking under burst concurrency (verified under Testcontainers Redis contention tests).
- Monotonic fencing tokens provide causal ordering across lease handoffs and intrusion revocations.
- Identical interface semantics allow both Redis-backed distributed deployments and fast in-memory local/CI runs.

### Negative
- Requires Redis persistence/availability tuning (`AOF` or sentinel/cluster quorum) in multi-node production environments to avoid fencing counter resets after full Redis restarts.
