# ParkMesh Capacity & SLO Baseline

Measured capacity and throughput/latency numbers for `ParkMesh.Api` on a single host (`Release` configuration). All values are reproducible directly from `benchmarks/ParkMesh.Benchmarks`.

## 1. Per-Tenant Memory Footprint

Measured via `CapacityProbe` (`benchmarks/ParkMesh.Benchmarks/CapacityProbe.cs`), which materializes `N` isolated tenants inside `ParkingAllocationService` (each with a cloned 120-bay `ParkingGraph`, independent `InMemoryEdgeCongestionTracker`, `Lock`, and warmed via `Allocate + A* + Release`) and compares stabilized `GC.GetTotalMemory(forceFullCollection: true)`:

```bash
dotnet build ParkMesh.sln -c Release --no-restore
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release --no-build -- --capacity --tenants 100
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release --no-build -- --capacity --tenants 500
```

| Tenant Count (`N`) | Baseline Heap | After Heap | Heap Delta | Avg Per Active Tenant | Raw 512 MiB Heap Extrapolation |
|---:|---:|---:|---:|---:|---:|
| `100` | `487.4 KiB` | `10,239.4 KiB` | `9,752.0 KiB` (`9.52 MiB`) | **`97.52 KiB`** (`99,860 B`) | `~5,376` tenants |
| `500` | `487.4 KiB` | `49,253.5 KiB` | `48,766.0 KiB` (`47.62 MiB`) | **`97.53 KiB`** (`99,873 B`) | `~5,375` tenants |

### Operational Headroom Guidance
- `CapacityProbe` isolates managed-heap growth from `TenantRuntime` objects only; it does not include Kestrel socket buffers, SignalR connections, Npgsql/Redis connection pools, or native/GC fragmentation overhead.
- Reserve at least **50% memory headroom** for runtime/IO buffers and GC survival space. For a `512 MiB` container limit, treat **`~2,500` concurrent materialized tenants** as the practical single-instance operating budget before adding idle `TenantRuntime` eviction (ADR 0008).

---

## 2. Concurrent HTTP + SignalR Throughput & Latency Baseline

Measured via `LoadHarnessRunner` (`benchmarks/ParkMesh.Benchmarks/LoadHarnessRunner.cs`) against an in-process `WebApplicationFactory`: `16` concurrent workers executing `300` full allocate/exit/reroute cycles with `30` idempotent retries (`1,230` HTTP requests total) plus live SignalR delta delivery:

```bash
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release --no-build -- --load --concurrency 16 --requests 300 --slo benchmarks/ParkMesh.Benchmarks/slo-baseline.json
```

| Metric | Observed Local Baseline (4 runs) | CI Regression Gate (`slo-baseline.json`) |
|---|---|---|
| **Throughput** | `5,411.4 – 7,068.3 req/s` (`174.0 – 227.3 ms`) | `>= 400.0 req/s` |
| **P50 latency** | `0.40 – 0.50 ms` | Informational |
| **P95 latency** | `5.30 – 6.98 ms` | `<= 120.0 ms` |
| **P99 latency** | `70.92 – 92.92 ms` | `<= 450.0 ms` |
| **Max latency** | `96.26 – 124.51 ms` | Informational |
| **SignalR JSON frames** | `525 – 533` frames | `>= 100` frames |
| **Correctness** | `300 ok`, `30 idempotent retries`, `0 conflicts`, `0 errors` | `minAllocationsOk=300`, `minIdempotentVerified=30`, `maxConflicts=0`, `maxErrors=0` |

> **Why P99/max are higher than P50:** `--load` intentionally runs without pre-warmup, so the first batch of concurrent requests captures JIT compilation and lazy `TenantRuntime` initialization. Shared 2-vCPU CI runners (`ubuntu-latest`) apply headroom on latency/throughput thresholds while holding correctness gates at zero tolerance.

## 3. Core Hot-Path Microbenchmarks

For isolated algorithm and lock-free window timings without HTTP/SignalR harness variance, run `BenchmarkDotNet` (`RoutingAndTelemetryBenchmarks`):

```bash
dotnet run --project benchmarks/ParkMesh.Benchmarks -c Release --no-build
```
