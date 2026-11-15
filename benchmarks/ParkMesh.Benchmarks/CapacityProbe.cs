using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.Benchmarks;

public static class CapacityProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var tenantsCount = ParseIntArg(args, "--tenants", 100, 10, 2000);

        var tenantContext = new AsyncLocalTenantContext();
        var service = new ParkingAllocationService(
            StandardTopologyFactory.CreateStandardGarage(),
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(tenantContext),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            tenantContext: tenantContext);

        // Warm up default tenant, JIT, ThreadStatic pools, and A* compiled topology before sampling.
        using (tenantContext.BeginScope(TenantDefaults.DefaultTenantId))
        {
            var warm = await service.AllocateAsync(new AllocateSpotRequest("GATE-N1", "CAP-WARM", VehicleType.Standard));
            if (warm.Succeeded && warm.Session is not null)
                await service.ReleaseLeaseAsync(warm.Session.AssignedBayId, warm.Session.FencingToken);
        }

        ForceFullGc();
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < tenantsCount; i++)
        {
            var tenantId = $"tenant-cap-{i:D4}";
            using (tenantContext.BeginScope(tenantId))
            {
                var res = await service.AllocateAsync(new AllocateSpotRequest("GATE-N1", $"CAP-{i:D4}", VehicleType.Standard));
                if (res.Succeeded && res.Session is not null)
                    await service.ReleaseLeaseAsync(res.Session.AssignedBayId, res.Session.FencingToken);
            }
        }

        ForceFullGc();
        var afterActiveBytes = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(service);

        var deltaBytes = Math.Max(0, afterActiveBytes - baselineBytes);
        var perTenantBytes = deltaBytes / (double)tenantsCount;
        var perTenantKb = perTenantBytes / 1024.0;
        var inv = CultureInfo.InvariantCulture;

        // Theoretical heap-only ceiling (excludes runtime overhead, session history, Redis/Postgres buffers, and GC fragmentation).
        var estTenantsPer512MiB = perTenantBytes > 0 ? (long)((512d * 1024 * 1024) / perTenantBytes) : 0;

        Console.WriteLine("=== ParkMesh Tenant Memory Capacity Probe ===");
        Console.WriteLine($"Tenants Provisioned   : {tenantsCount} (each warmed via Allocate + A* + Release)");
        Console.WriteLine(string.Create(inv, $"Managed Baseline      : {baselineBytes / 1024.0:F1} KiB"));
        Console.WriteLine(string.Create(inv, $"Managed After Tenants : {afterActiveBytes / 1024.0:F1} KiB"));
        Console.WriteLine(string.Create(inv, $"Managed Delta Total   : {deltaBytes / 1024.0:F1} KiB ({deltaBytes / (1024.0 * 1024.0):F2} MiB)"));
        Console.WriteLine(string.Create(inv, $"Per Active Tenant Avg : {perTenantKb:F2} KiB ({perTenantBytes:F0} B)"));
        Console.WriteLine($"Extrapolated 512 MiB  : ~{estTenantsPer512MiB} tenants (managed heap bound only; apply safety factor <= 0.5x in production)");

        return 0;
    }

    private static void ForceFullGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static int ParseIntArg(string[] args, string flag, int fallback, int min, int max)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var parsed))
            {
                return Math.Clamp(parsed, min, max);
            }
        }

        return fallback;
    }
}
