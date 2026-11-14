using Npgsql;
using ParkMesh.Application.Audit;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;
using Testcontainers.PostgreSql;

namespace ParkMesh.IntegrationTests;

public sealed class PostgresAuditStoreIntegrationTests : IAsyncLifetime
{
    private const string RestrictedRolePassword = "parkmesh_app_rls_test_pw";
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgresContainer.StartAsync();

    public Task DisposeAsync() => _postgresContainer.DisposeAsync().AsTask();

    private string BuildRestrictedAppConnectionString()
    {
        var csb = new NpgsqlConnectionStringBuilder(_postgresContainer.GetConnectionString())
        {
            Username = "parkmesh_app",
            Password = RestrictedRolePassword
        };
        return csb.ConnectionString;
    }

    private async Task ProvisionRestrictedRoleAsync()
    {
        await using var conn = new NpgsqlConnection(_postgresContainer.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'parkmesh_app') THEN
                    CREATE ROLE parkmesh_app WITH LOGIN PASSWORD '{RestrictedRolePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION;
                END IF;
            END
            $$;
            GRANT USAGE ON SCHEMA public TO parkmesh_app;
            GRANT SELECT, INSERT ON TABLE parking_audit_events TO parkmesh_app;
            GRANT USAGE, SELECT ON SEQUENCE parking_audit_events_id_seq TO parkmesh_app;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

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

    [Fact]
    public async Task RowLevelSecurity_EnforcesStrictTenantIsolationAndFailClosedBehaviorOnNonSuperuserRole()
    {
        await using var adminStore = new PostgresParkingAuditStore(_postgresContainer.GetConnectionString());
        await adminStore.InitializeAsync();
        await ProvisionRestrictedRoleAsync();

        await using (var adminConn = new NpgsqlConnection(_postgresContainer.GetConnectionString()))
        {
            await adminConn.OpenAsync();
            await using var rlsCheckCmd = adminConn.CreateCommand();
            rlsCheckCmd.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'parking_audit_events';";
            await using var reader = await rlsCheckCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }

        var restrictedConnStr = BuildRestrictedAppConnectionString();
        await using var appStore = new PostgresParkingAuditStore(restrictedConnStr);

        var now = DateTimeOffset.UtcNow;
        await appStore.RecordBatchAsync(
        [
            new ParkingAuditEvent(now, AuditEventType.BayAllocated, "F0-B01", "ALPHA-1", 1, VehicleType.Standard, "GATE-N1", "alpha", TenantId: "tenant-alpha"),
            new ParkingAuditEvent(now.AddSeconds(1), AuditEventType.PhysicalOccupied, "F0-B01", "ALPHA-1", null, null, null, null, TenantId: "tenant-alpha"),
            new ParkingAuditEvent(now.AddSeconds(2), AuditEventType.BayOutOfService, "F0-B01", null, null, null, null, "maint", TenantId: "tenant-beta"),
            new ParkingAuditEvent(now.AddSeconds(3), AuditEventType.BayAllocated, "F0-B09", "BETA-1", 1, VehicleType.EV, "GATE-S1", "beta", TenantId: "tenant-beta")
        ]);

        var alphaEvents = await appStore.GetRecentAsync("tenant-alpha", 50);
        var betaEvents = await appStore.GetRecentAsync("tenant-beta", 50);
        Assert.Equal(2, alphaEvents.Count);
        Assert.All(alphaEvents, e => Assert.Equal("tenant-alpha", e.TenantId));
        Assert.Equal(2, betaEvents.Count);
        Assert.All(betaEvents, e => Assert.Equal("tenant-beta", e.TenantId));

        Assert.Equal(AuditEventType.PhysicalOccupied, (await appStore.GetLatestBayPhysicalStatesAsync("tenant-alpha"))["F0-B01"]);
        Assert.Equal(AuditEventType.BayOutOfService, (await appStore.GetLatestBayPhysicalStatesAsync("tenant-beta"))["F0-B01"]);

        await using var appConn = new NpgsqlConnection(restrictedConnStr);
        await appConn.OpenAsync();

        await using (var selectUnscopedCmd = appConn.CreateCommand())
        {
            selectUnscopedCmd.CommandText = "SELECT COUNT(*) FROM parking_audit_events;";
            var visibleWithoutTenant = (long)(await selectUnscopedCmd.ExecuteScalarAsync())!;
            Assert.Equal(0L, visibleWithoutTenant);
        }

        await using (var insertUnscopedCmd = appConn.CreateCommand())
        {
            insertUnscopedCmd.CommandText = """
                INSERT INTO parking_audit_events (tenant_id, occurred_at_utc, event_type, bay_id)
                VALUES ('tenant-alpha', NOW(), 'BayAllocated', 'F0-B01');
                """;
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insertUnscopedCmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await using (var tx = await appConn.BeginTransactionAsync())
        {
            await using var setCmd = new NpgsqlCommand("SELECT set_config('app.current_tenant', 'tenant-alpha', true);", appConn, tx);
            await setCmd.ExecuteNonQueryAsync();

            await using var crossTenantInsert = new NpgsqlCommand("""
                INSERT INTO parking_audit_events (tenant_id, occurred_at_utc, event_type, bay_id)
                VALUES ('tenant-beta', NOW(), 'BayAllocated', 'F0-B01');
                """, appConn, tx);

            var crossEx = await Assert.ThrowsAsync<PostgresException>(() => crossTenantInsert.ExecuteNonQueryAsync());
            Assert.Equal("42501", crossEx.SqlState);
            await tx.RollbackAsync();
        }
    }
}
