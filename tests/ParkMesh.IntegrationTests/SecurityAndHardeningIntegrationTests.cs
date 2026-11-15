using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using ParkMesh.Api.Security;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.IntegrationTests;

public sealed class SecurityAndHardeningIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _factory;

    public SecurityAndHardeningIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SecurityHeaders_ArePresentOnDashboardApiHealthAndOpenApiResponses()
    {
        using var client = _factory.CreateClient();

        foreach (var path in new[] { "/", "/api/parking/snapshot", "/health/ready", "/openapi/v1.json" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"Expected 2xx for {path}, got {response.StatusCode}");
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("none", response.Headers.GetValues("X-Permitted-Cross-Domain-Policies").Single());
            Assert.True(response.Headers.Contains("Content-Security-Policy"));
            Assert.True(response.Headers.Contains("Permissions-Policy"));
        }
    }

    [Theory]
    [InlineData("' OR 1=1--")]
    [InlineData("../etc/passwd")]
    [InlineData("tenant with spaces")]
    [InlineData("-bad-prefix")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task TenantHeaderValidation_RejectsInvalidIdentifiersWith400(string badTenant)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/parking/audit");
        request.Headers.Add("X-Tenant-Id", badTenant);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderValidation_RejectsDuplicateHeadersAndEnforcesAllowedTenantsList()
    {
        using var restrictedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:AllowedTenants:0"] = "garage-sp-01",
                    ["Security:AllowedTenants:1"] = "garage-rj-02"
                });
            });
        });

        using var client = restrictedFactory.CreateClient();

        using var duplicateHeaderReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/bays");
        duplicateHeaderReq.Headers.Add("X-Tenant-Id", ["garage-sp-01", "garage-sp-01"]);
        var dupResp = await client.SendAsync(duplicateHeaderReq);
        Assert.Equal(HttpStatusCode.BadRequest, dupResp.StatusCode);

        var defaultDeniedResp = await client.GetAsync("/api/parking/bays");
        Assert.Equal(HttpStatusCode.Forbidden, defaultDeniedResp.StatusCode);

        using var unauthorizedTenantReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/bays");
        unauthorizedTenantReq.Headers.Add("X-Tenant-Id", "garage-rogue");
        var rogueResp = await client.SendAsync(unauthorizedTenantReq);
        Assert.Equal(HttpStatusCode.Forbidden, rogueResp.StatusCode);

        using var authorizedTenantReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/bays");
        authorizedTenantReq.Headers.Add("X-Tenant-Id", "garage-sp-01");
        var okResp = await client.SendAsync(authorizedTenantReq);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
    }

    [Fact]
    public async Task AuditEndpoint_EnforcesTenantIsolationAcrossConcurrentRequests()
    {
        using var isolatedFactory = _factory.WithWebHostBuilder(_ => { });
        using var client = isolatedFactory.CreateClient();

        using var allocNorth = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
        {
            Content = JsonContent.Create(new { GateId = "GATE-N1", VehicleSessionId = "NORTH-CAR-1", VehicleType = VehicleType.Standard }, options: JsonOptions)
        };
        allocNorth.Headers.Add("X-Tenant-Id", "tenant-north");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(allocNorth)).StatusCode);

        using var allocSouth = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
        {
            Content = JsonContent.Create(new { GateId = "GATE-S1", VehicleSessionId = "SOUTH-CAR-1", VehicleType = VehicleType.EV }, options: JsonOptions)
        };
        allocSouth.Headers.Add("X-Tenant-Id", "tenant-south");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(allocSouth)).StatusCode);

        AuditPayload? northAudit = null;
        for (var i = 0; i < 20; i++)
        {
            using var auditReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/audit?limit=50");
            auditReq.Headers.Add("X-Tenant-Id", "tenant-north");
            var resp = await client.SendAsync(auditReq);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            northAudit = await resp.Content.ReadFromJsonAsync<AuditPayload>(JsonOptions);
            if (northAudit?.Events.Any(e => e.SessionId == "NORTH-CAR-1") == true)
                break;

            await Task.Delay(50);
        }

        Assert.NotNull(northAudit);
        Assert.Equal("tenant-north", northAudit!.TenantId);
        Assert.Contains(northAudit.Events, e => e.SessionId == "NORTH-CAR-1");
        Assert.DoesNotContain(northAudit.Events, e => e.SessionId == "SOUTH-CAR-1");

        using var southReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/audit?limit=50");
        southReq.Headers.Add("X-Tenant-Id", "tenant-south");
        var southAudit = await (await client.SendAsync(southReq)).Content.ReadFromJsonAsync<AuditPayload>(JsonOptions);
        Assert.NotNull(southAudit);
        Assert.Contains(southAudit!.Events, e => e.SessionId == "SOUTH-CAR-1");
        Assert.DoesNotContain(southAudit.Events, e => e.SessionId == "NORTH-CAR-1");

        var defaultAudit = await client.GetFromJsonAsync<AuditPayload>("/api/parking/audit?limit=50", JsonOptions);
        Assert.DoesNotContain(defaultAudit!.Events, e => e.SessionId is "NORTH-CAR-1" or "SOUTH-CAR-1");
    }

    [Fact]
    public async Task ApiKeyFilter_ProtectsAllMutationsAndAuditEndpoint_WhileAllowingProbes()
    {
        using var securedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:ApiKey"] = "enterprise-api-key-98765",
                    ["Security:EnableRateLimiting"] = "false"
                });
            });
        });

        using var client = securedFactory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        var protectedRequests = new Func<HttpRequestMessage>[]
        {
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
            {
                Content = JsonContent.Create(new { GateId = "GATE-N1", VehicleSessionId = "SEC-1", VehicleType = VehicleType.Standard }, options: JsonOptions)
            },
            () => new HttpRequestMessage(HttpMethod.Delete, "/api/parking/allocations/F0-B01?fencingToken=1"),
            () => new HttpRequestMessage(HttpMethod.Patch, "/api/parking/bays/F0-B01/maintenance")
            {
                Content = JsonContent.Create(new { OutOfService = true }, options: JsonOptions)
            },
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/bays/F0-B01/occupancy")
            {
                Content = JsonContent.Create(new { Occupied = false }, options: JsonOptions)
            },
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/sessions/SEC-1/progress")
            {
                Content = JsonContent.Create(new { CurrentNodeId = "GATE-N1" }, options: JsonOptions)
            },
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/telemetry/readings")
            {
                Content = JsonContent.Create(new BaySensorReading("F0-B01", false, 200, 1, DateTimeOffset.UtcNow), options: JsonOptions)
            },
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/intruder"),
            () => new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/burst?count=1"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/audit"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/snapshot"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/bays"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/metrics"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/sessions"),
            () => new HttpRequestMessage(HttpMethod.Get, "/api/parking/telemetry/metrics")
        };

        foreach (var factory in protectedRequests)
        {
            using var missingKey = factory();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(missingKey)).StatusCode);

            using var wrongKey = factory();
            wrongKey.Headers.Add("X-Api-Key", "wrong-key");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongKey)).StatusCode);

            using var duplicateKey = factory();
            duplicateKey.Headers.Add("X-Api-Key", ["enterprise-api-key-98765", "enterprise-api-key-98765"]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(duplicateKey)).StatusCode);
        }

        using var authorizedAudit = new HttpRequestMessage(HttpMethod.Get, "/api/parking/audit");
        authorizedAudit.Headers.Add("X-Api-Key", "enterprise-api-key-98765");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorizedAudit)).StatusCode);
    }

    [Fact]
    public async Task RequestLimitsAndMalformedJson_Return413And400WithoutStackTraceExposure()
    {
        using var strictFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:MaxRequestBodyBytes"] = "512"
                });
            });
        });

        using var client = strictFactory.CreateClient();

        var oversizedPayload = new string('A', 1024);
        using var bigContent = new StringContent($"{{\"gateId\":\"{oversizedPayload}\"}}", Encoding.UTF8, "application/json");
        var tooLargeResponse = await client.PostAsync("/api/parking/allocations", bigContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLargeResponse.StatusCode);

        using var malformedContent = new StringContent("{ invalid-json", Encoding.UTF8, "application/json");
        var badJsonResponse = await client.PostAsync("/api/parking/allocations", malformedContent);
        Assert.Equal(HttpStatusCode.BadRequest, badJsonResponse.StatusCode);
        var body = await badJsonResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.Text.Json", body);
        Assert.DoesNotContain("Exception", body);
    }

    [Fact]
    public void ApiKeyValidator_EnforcesExactSingleHeaderAndHandlesDisabledMode()
    {
        var headers = new HeaderDictionary();
        Assert.True(ApiKeyValidator.IsAuthorized(headers, string.Empty));
        Assert.False(ApiKeyValidator.IsAuthorized(headers, "super-secret-token"));

        headers[SecurityOptions.ApiKeyHeaderName] = "wrong-token";
        Assert.False(ApiKeyValidator.IsAuthorized(headers, "super-secret-token"));

        headers[SecurityOptions.ApiKeyHeaderName] = "super-secret-tokeX";
        Assert.False(ApiKeyValidator.IsAuthorized(headers, "super-secret-token"));

        headers[SecurityOptions.ApiKeyHeaderName] = new StringValues(["super-secret-token", "super-secret-token"]);
        Assert.False(ApiKeyValidator.IsAuthorized(headers, "super-secret-token"));

        headers[SecurityOptions.ApiKeyHeaderName] = "super-secret-token";
        Assert.True(ApiKeyValidator.IsAuthorized(headers, "super-secret-token"));
    }

    [Fact]
    public async Task AllocateEndpoint_IdempotentRetryReturnsSameBay_AndMismatchedRetryReturns409Conflict()
    {
        using var client = _factory.CreateClient();
        var sessionId = $"IDEM-CAR-{Guid.NewGuid():N}";

        var firstResp = await client.PostAsJsonAsync(
            "/api/parking/allocations",
            new { GateId = "GATE-N1", VehicleSessionId = sessionId, VehicleType = VehicleType.Standard },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var firstBody = await firstResp.Content.ReadFromJsonAsync<AllocationSummary>(JsonOptions);
        Assert.NotNull(firstBody);

        var retryResp = await client.PostAsJsonAsync(
            "/api/parking/allocations",
            new { GateId = "GATE-N1", VehicleSessionId = sessionId, VehicleType = VehicleType.Standard },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, retryResp.StatusCode);
        var retryBody = await retryResp.Content.ReadFromJsonAsync<AllocationSummary>(JsonOptions);
        Assert.NotNull(retryBody);
        Assert.Equal(firstBody.BayId, retryBody.BayId);
        Assert.Equal(firstBody.FencingToken, retryBody.FencingToken);

        var conflictResp = await client.PostAsJsonAsync(
            "/api/parking/allocations",
            new { GateId = "GATE-S1", VehicleSessionId = sessionId, VehicleType = VehicleType.EV },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
    }

    private sealed record AllocationSummary(string BayId, long FencingToken);
    private sealed record AuditPayload(string TenantId, int Count, long DroppedCount, List<AuditEventItem> Events);
    private sealed record AuditEventItem(string BayId, string? SessionId, string TenantId);
}
