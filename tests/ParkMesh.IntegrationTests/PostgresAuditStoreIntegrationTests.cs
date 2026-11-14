using ParkMesh.Application.Audit;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;
using Testcontainers.PostgreSql;

namespace ParkMesh.IntegrationTests;

public sealed class PostgresAuditStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgresContainer.StartAsync();

    public Task DisposeAsync() => _postgresContainer.DisposeAsync().AsTask();

    [Fact]
    public async Task InitializeAndRecordBatch_PersistsEventsInPostgreSqlAndReadsLatestFirst()
    {
        await using var store = new PostgresParkingAuditStore(_postgresContainer.GetConnectionString());
        await store.InitializeAsync();
        Assert.True(await store.CheckConnectivityAsync());

        var baseTime = DateTimeOffset.UtcNow;
        await store.RecordBatchAsync(
        [
            new ParkingAuditEvent(baseTime, AuditEventType.BayAllocated, "F0-B01", "PG-1", 1, VehicleType.Standard, "GATE-N1", "cost=42.50"),
            new ParkingAuditEvent(baseTime.AddSeconds(1), AuditEventType.PhysicalOccupied, "F0-B01", "PG-1", null, null, null, null),
            new ParkingAuditEvent(baseTime.AddSeconds(2), AuditEventType.BayAllocated, "F0-B02", "PG-2", 2, VehicleType.EV, "GATE-S1", "cost=55.00"),
            new ParkingAuditEvent(baseTime.AddSeconds(3), AuditEventType.LeaseReleased, "F0-B02", "PG-2", 2, VehicleType.EV, "GATE-S1", null)
        ]);

        var recent = await store.GetRecentAsync(10);

        Assert.Equal(4, recent.Count);
        Assert.Equal(AuditEventType.LeaseReleased, recent[0].EventType);
        Assert.Equal("F0-B02", recent[0].BayId);
        Assert.Equal(AuditEventType.BayAllocated, recent[3].EventType);
        Assert.Equal("GATE-N1", recent[3].NodeId);
        Assert.True(recent[0].Id > recent[3].Id);
    }

    [Fact]
    public async Task GetLatestBayPhysicalStatesAsync_ReturnsLatestPhysicalAndMaintenanceStatePerBay()
    {
        await using var store = new PostgresParkingAuditStore(_postgresContainer.GetConnectionString());
        await store.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        await store.RecordBatchAsync(
        [
            new ParkingAuditEvent(now, AuditEventType.BayAllocated, "F0-B01", "S-1", 1, VehicleType.Standard, "GATE-N1", null),
            new ParkingAuditEvent(now.AddSeconds(1), AuditEventType.PhysicalOccupied, "F0-B01", "S-1", null, null, null, null),
            new ParkingAuditEvent(now.AddSeconds(2), AuditEventType.PhysicalVacated, "F0-B01", "S-1", null, null, null, null),
            new ParkingAuditEvent(now.AddSeconds(3), AuditEventType.BayAllocated, "F0-B02", "S-2", 1, VehicleType.EV, "GATE-S1", null),
            new ParkingAuditEvent(now.AddSeconds(4), AuditEventType.IntrusionRerouted, "F0-B02", "S-2", 2, VehicleType.EV, "F0-I-W-N", "rerouted"),
            new ParkingAuditEvent(now.AddSeconds(5), AuditEventType.BayOutOfService, "F1-B10", null, null, null, null, "maintenance"),
            new ParkingAuditEvent(now.AddSeconds(6), AuditEventType.BayOutOfService, "F1-B11", null, null, null, null, "maintenance"),
            new ParkingAuditEvent(now.AddSeconds(7), AuditEventType.BayRestored, "F1-B11", null, null, null, null, "restored"),
            new ParkingAuditEvent(now.AddSeconds(8), AuditEventType.BayAllocated, "F1-B11", "S-3", 1, VehicleType.SUV, "GATE-N1", null)
        ]);

        var latest = await store.GetLatestBayPhysicalStatesAsync();

        Assert.Equal(AuditEventType.PhysicalVacated, latest["F0-B01"]);
        Assert.Equal(AuditEventType.IntrusionRerouted, latest["F0-B02"]);
        Assert.Equal(AuditEventType.BayOutOfService, latest["F1-B10"]);
        Assert.Equal(AuditEventType.BayRestored, latest["F1-B11"]);
    }
}
