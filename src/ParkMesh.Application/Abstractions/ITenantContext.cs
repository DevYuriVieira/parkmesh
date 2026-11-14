using System.Text.RegularExpressions;

namespace ParkMesh.Application.Abstractions;

public static class TenantDefaults
{
    public const string DefaultTenantId = "default";
    public const string HeaderName = "X-Tenant-Id";
}

public static partial class TenantIdValidator
{
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidTenantRegex();

    public static bool IsValid(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) && ValidTenantRegex().IsMatch(tenantId);

    public static string NormalizeOrThrow(string? tenantId)
    {
        if (tenantId is null)
            return TenantDefaults.DefaultTenantId;

        if (!IsValid(tenantId))
            throw new ArgumentException("Tenant identifier must match ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$.", nameof(tenantId));

        return tenantId;
    }
}

public interface ITenantContext
{
    string CurrentTenantId { get; }
    IDisposable BeginScope(string tenantId);
}

public sealed class AsyncLocalTenantContext : ITenantContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string CurrentTenantId => _current.Value ?? TenantDefaults.DefaultTenantId;

    public IDisposable BeginScope(string tenantId)
    {
        var normalized = TenantIdValidator.NormalizeOrThrow(tenantId);
        var previous = _current.Value;
        _current.Value = normalized;
        return new ScopeGuard(_current, previous);
    }

    private sealed class ScopeGuard : IDisposable
    {
        private readonly AsyncLocal<string?> _slot;
        private readonly string? _previous;
        private bool _disposed;

        public ScopeGuard(AsyncLocal<string?> slot, string? previous)
        {
            _slot = slot;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _slot.Value = _previous;
            _disposed = true;
        }
    }
}

public sealed class DefaultTenantContext : ITenantContext
{
    public static readonly DefaultTenantContext Instance = new();

    public string CurrentTenantId => TenantDefaults.DefaultTenantId;

    public IDisposable BeginScope(string tenantId) => NoopScope.Instance;

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
