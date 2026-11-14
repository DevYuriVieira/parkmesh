using System.Text.RegularExpressions;
using Npgsql;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Audit;
using ParkMesh.Domain;

namespace ParkMesh.Infrastructure.Audit;

public sealed partial class PostgresParkingAuditStore : IParkingAuditStore, IAsyncDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS parking_audit_events (
            id BIGSERIAL PRIMARY KEY,
            tenant_id TEXT NOT NULL DEFAULT 'default',
            occurred_at_utc TIMESTAMPTZ NOT NULL,
            event_type TEXT NOT NULL,
            bay_id TEXT NOT NULL,
            session_id TEXT NULL,
            fencing_token BIGINT NULL,
            vehicle_type TEXT NULL,
            node_id TEXT NULL,
            details TEXT NULL
        );

        ALTER TABLE parking_audit_events
            ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT 'default';

        CREATE INDEX IF NOT EXISTS ix_parking_audit_events_occurred_at
            ON parking_audit_events (occurred_at_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_parking_audit_events_bay_id
            ON parking_audit_events (bay_id, id DESC);

        CREATE INDEX IF NOT EXISTS ix_parking_audit_events_tenant_occurred
            ON parking_audit_events (tenant_id, occurred_at_utc DESC, id DESC);

        CREATE INDEX IF NOT EXISTS ix_parking_audit_events_tenant_bay
            ON parking_audit_events (tenant_id, bay_id, id DESC);

        ALTER TABLE parking_audit_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE parking_audit_events FORCE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS parking_audit_tenant_isolation ON parking_audit_events;
        CREATE POLICY parking_audit_tenant_isolation ON parking_audit_events
            FOR ALL
            USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), ''))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant', true), ''));
        """;

    private const string SetLocalTenantSql = "SELECT set_config('app.current_tenant', @tenant_id, true);";

    private const string InsertSql = """
        INSERT INTO parking_audit_events
            (tenant_id, occurred_at_utc, event_type, bay_id, session_id, fencing_token, vehicle_type, node_id, details)
        VALUES
            (@tenant_id, @occurred_at_utc, @event_type, @bay_id, @session_id, @fencing_token, @vehicle_type, @node_id, @details);
        """;

    private const string SelectRecentSql = """
        SELECT id, tenant_id, occurred_at_utc, event_type, bay_id, session_id, fencing_token, vehicle_type, node_id, details
        FROM parking_audit_events
        ORDER BY id DESC
        LIMIT @limit;
        """;

    private const string SelectLatestPhysicalStatesSql = """
        SELECT DISTINCT ON (bay_id) bay_id, event_type
        FROM parking_audit_events
        WHERE event_type IN ('PhysicalOccupied', 'IntrusionRerouted', 'PhysicalVacated', 'BayOutOfService', 'BayRestored')
        ORDER BY bay_id, id DESC;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _databaseRole;

    public PostgresParkingAuditStore(string connectionString, string? databaseRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (databaseRole is not null && !ValidRoleIdentifierRegex().IsMatch(databaseRole))
            throw new ArgumentException("Invalid PostgreSQL role identifier.", nameof(databaseRole));

        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        _databaseRole = databaseRole;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidRoleIdentifierRegex();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(SchemaSql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return;

        foreach (var group in events.GroupBy(e => TenantIdValidator.NormalizeOrThrow(e.TenantId)))
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await ApplyRlsContextAsync(conn, tx, group.Key, ct);

            await using var batch = new NpgsqlBatch(conn, tx);
            foreach (var ev in group)
            {
                var cmd = new NpgsqlBatchCommand(InsertSql);
                cmd.Parameters.AddWithValue("tenant_id", group.Key);
                cmd.Parameters.AddWithValue("occurred_at_utc", ev.OccurredAtUtc);
                cmd.Parameters.AddWithValue("event_type", ev.EventType.ToString());
                cmd.Parameters.AddWithValue("bay_id", ev.BayId);
                cmd.Parameters.AddWithValue("session_id", (object?)ev.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("fencing_token", (object?)ev.FencingToken ?? DBNull.Value);
                cmd.Parameters.AddWithValue("vehicle_type", (object?)ev.VehicleType?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("node_id", (object?)ev.NodeId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("details", (object?)ev.Details ?? DBNull.Value);
                batch.BatchCommands.Add(cmd);
            }

            await batch.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    public Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
        GetRecentAsync(TenantDefaults.DefaultTenantId, limit, ct);

    public async Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(
        string tenantId,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (!TenantIdValidator.IsValid(tenantId))
            return Array.Empty<ParkingAuditEvent>();

        var take = Math.Clamp(limit, 1, 200);
        var results = new List<ParkingAuditEvent>(take);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ApplyRlsContextAsync(conn, tx, tenantId, ct);

        await using var cmd = new NpgsqlCommand(SelectRecentSql, conn, tx);
        cmd.Parameters.AddWithValue("limit", take);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var eventType = Enum.Parse<AuditEventType>(reader.GetString(3), ignoreCase: true);
            VehicleType? vehicleType = reader.IsDBNull(7)
                ? null
                : Enum.Parse<VehicleType>(reader.GetString(7), ignoreCase: true);

            results.Add(new ParkingAuditEvent(
                reader.GetFieldValue<DateTimeOffset>(2),
                eventType,
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                vehicleType,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(0),
                reader.GetString(1)));
        }

        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return results;
    }

    public Task<IReadOnlyDictionary<string, AuditEventType>> GetLatestBayPhysicalStatesAsync(
        CancellationToken ct = default) =>
        GetLatestBayPhysicalStatesAsync(TenantDefaults.DefaultTenantId, ct);

    public async Task<IReadOnlyDictionary<string, AuditEventType>> GetLatestBayPhysicalStatesAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        var states = new Dictionary<string, AuditEventType>(StringComparer.OrdinalIgnoreCase);
        if (!TenantIdValidator.IsValid(tenantId))
            return states;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ApplyRlsContextAsync(conn, tx, tenantId, ct);

        await using var cmd = new NpgsqlCommand(SelectLatestPhysicalStatesSql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var bayId = reader.GetString(0);
            var eventType = Enum.Parse<AuditEventType>(reader.GetString(1), ignoreCase: true);
            states[bayId] = eventType;
        }

        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return states;
    }

    public async Task<bool> CheckConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cmd = _dataSource.CreateCommand("SELECT 1;");
            var scalar = await cmd.ExecuteScalarAsync(ct);
            return scalar is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private async Task ApplyRlsContextAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string tenantId,
        CancellationToken ct)
    {
        if (_databaseRole is not null)
        {
            await using var roleCmd = new NpgsqlCommand($"SET LOCAL ROLE {_databaseRole};", conn, tx);
            await roleCmd.ExecuteNonQueryAsync(ct);
        }

        await using var tenantCmd = new NpgsqlCommand(SetLocalTenantSql, conn, tx);
        tenantCmd.Parameters.AddWithValue("tenant_id", tenantId);
        await tenantCmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
