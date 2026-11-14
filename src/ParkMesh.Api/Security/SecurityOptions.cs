using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ParkMesh.Api.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public const string ApiKeyHeaderName = "X-Api-Key";

    public string ApiKey { get; set; } = string.Empty;
    public bool EnableRateLimiting { get; set; } = true;
    public int ChaosPermitLimit { get; set; } = 30;
    public int ChaosWindowSeconds { get; set; } = 10;
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
        if (string.IsNullOrEmpty(configuredKey))
            return await next(context);

        if (!context.HttpContext.Request.Headers.TryGetValue(SecurityOptions.ApiKeyHeaderName, out var providedKey))
            return Results.Unauthorized();

        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        var actualBytes = Encoding.UTF8.GetBytes(providedKey.ToString());
        if (expectedBytes.Length != actualBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
