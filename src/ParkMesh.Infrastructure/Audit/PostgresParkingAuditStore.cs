using Npgsql;
using ParkMesh.Application.Audit;
using ParkMesh.Domain;

namespace ParkMesh.Infrastructure.Audit;

public sealed class PostgresParkingAuditStore : IParkingAuditStore, IAsyncDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS parking_audit_events (
            id BIGSERIAL PRIMARY KEY,
            occurred_at_utc TIMESTAMPTZ NOT NULL,
            event_type TEXT NOT NULL,
            bay_id TEXT NOT NULL,
            session_id TEXT NULL,
            fencing_token BIGINT NULL,
            vehicle_type TEXT NULL,
            node_id TEXT NULL,
            details TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_parking_audit_events_occurred_at
            ON parking_audit_events (occurred_at_utc DESC, id DESC);
        """;

    private const string InsertSql = """
        INSERT INTO parking_audit_events
            (occurred_at_utc, event_type, bay_id, session_id, fencing_token, vehicle_type, node_id, details)
        VALUES
            (@occurred_at_utc, @event_type, @bay_id, @session_id, @fencing_token, @vehicle_type, @node_id, @details);
        """;

    private const string SelectRecentSql = """
        SELECT id, occurred_at_utc, event_type, bay_id, session_id, fencing_token, vehicle_type, node_id, details
        FROM parking_audit_events
        ORDER BY id DESC
        LIMIT @limit;
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresParkingAuditStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(SchemaSql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return;

        await using var batch = _dataSource.CreateBatch();
        foreach (var ev in events)
        {
            var batchCommand = new NpgsqlBatchCommand(InsertSql);
            batchCommand.Parameters.AddWithValue("occurred_at_utc", ev.OccurredAtUtc);
            batchCommand.Parameters.AddWithValue("event_type", ev.EventType.ToString());
            batchCommand.Parameters.AddWithValue("bay_id", ev.BayId);
            batchCommand.Parameters.AddWithValue("session_id", (object?)ev.SessionId ?? DBNull.Value);
            batchCommand.Parameters.AddWithValue("fencing_token", (object?)ev.FencingToken ?? DBNull.Value);
            batchCommand.Parameters.AddWithValue("vehicle_type", (object?)ev.VehicleType?.ToString() ?? DBNull.Value);
            batchCommand.Parameters.AddWithValue("node_id", (object?)ev.NodeId ?? DBNull.Value);
            batchCommand.Parameters.AddWithValue("details", (object?)ev.Details ?? DBNull.Value);
            batch.BatchCommands.Add(batchCommand);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var results = new List<ParkingAuditEvent>(take);

        await using var cmd = _dataSource.CreateCommand(SelectRecentSql);
        cmd.Parameters.AddWithValue("limit", take);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var eventType = Enum.Parse<AuditEventType>(reader.GetString(2), ignoreCase: true);
            VehicleType? vehicleType = reader.IsDBNull(6)
                ? null
                : Enum.Parse<VehicleType>(reader.GetString(6), ignoreCase: true);

            results.Add(new ParkingAuditEvent(
                reader.GetFieldValue<DateTimeOffset>(1),
                eventType,
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                vehicleType,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt64(0)));
        }

        return results;
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

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
