using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Abstractions;

namespace ParkMesh.Api.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public const string ApiKeyHeaderName = "X-Api-Key";

    public string ApiKey { get; set; } = string.Empty;
    public bool EnableRateLimiting { get; set; } = true;
    public int ChaosPermitLimit { get; set; } = 30;
    public int ChaosWindowSeconds { get; set; } = 10;
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;
    public string[] AllowedTenants { get; set; } = [];
    public bool EnableChaosEndpoints { get; set; } = false;
    public bool AllowUnauthenticatedProduction { get; set; } = false;
}

public static class ApiKeyValidator
{
    public static bool IsAuthorized(IHeaderDictionary headers, string configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
            return true;

        if (!headers.TryGetValue(SecurityOptions.ApiKeyHeaderName, out var providedValues) || providedValues.Count != 1)
            return false;

        var provided = providedValues[0];
        if (string.IsNullOrEmpty(provided))
            return false;

        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey), expectedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), actualHash);

        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}

public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public ApiKeyEndpointFilter(IOptionsMonitor<SecurityOptions> options)
    {
        _options = options;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = _options.CurrentValue.ApiKey;
        if (!ApiKeyValidator.IsAuthorized(context.HttpContext.Request.Headers, configuredKey))
            return Results.Unauthorized();

        return await next(context);
    }
}

public sealed class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws: wss:; " +
        "img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptionsMonitor<SecurityOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("X-Permitted-Cross-Domain-Policies", "none");
            headers.TryAdd("Permissions-Policy", "geolocation=(), camera=(), microphone=()");
            headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
            return Task.CompletedTask;
        }, context);

        var maxBytes = _options.CurrentValue.MaxRequestBodyBytes;
        if (maxBytes > 0)
        {
            if (context.Request.ContentLength > maxBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new { error = "Request payload exceeds the configured size limit." });
                return;
            }

            var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature is { IsReadOnly: false })
            {
                bodySizeFeature.MaxRequestBodySize = maxBytes;
            }
        }

        await _next(context);
    }
}

public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenantContext _tenantContext;
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        ITenantContext tenantContext,
        IOptionsMonitor<SecurityOptions> options)
    {
        _next = next;
        _tenantContext = tenantContext;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isApiPath = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        var isHubPath = context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

        if (!isApiPath && !isHubPath)
        {
            await _next(context);
            return;
        }

        if (isHubPath)
        {
            var configuredKey = _options.CurrentValue.ApiKey;
            if (!ApiKeyValidator.IsAuthorized(context.Request.Headers, configuredKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
                return;
            }
        }

        var tenantId = TenantDefaults.DefaultTenantId;
        if (context.Request.Headers.TryGetValue(TenantDefaults.HeaderName, out var values))
        {
            if (values.Count != 1 || !TenantIdValidator.IsValid(values[0]))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid X-Tenant-Id header." });
                return;
            }

            tenantId = values[0]!;
        }
        else if (isHubPath && context.Request.Query.TryGetValue("tenantId", out var queryValues))
        {
            if (queryValues.Count != 1 || !TenantIdValidator.IsValid(queryValues[0]))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid tenantId query parameter." });
                return;
            }

            tenantId = queryValues[0]!;
        }

        var allowed = _options.CurrentValue.AllowedTenants;
        if (allowed is { Length: > 0 } && !allowed.Contains(tenantId, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant is not authorized." });
            return;
        }

        using var _ = _tenantContext.BeginScope(tenantId);
        await _next(context);
    }
}
