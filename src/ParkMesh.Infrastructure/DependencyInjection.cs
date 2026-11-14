using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;
using ParkMesh.Infrastructure.Mqtt;
using ParkMesh.Infrastructure.Redis;
using StackExchange.Redis;

namespace ParkMesh.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddParkMeshCore(this IServiceCollection services)
    {
        services.AddSingleton(_ => StandardTopologyFactory.CreateStandardGarage());
        services.AddSingleton<IPathfinder, AStarPathfinder>();
        services.AddSingleton<IEdgeCongestionTracker, InMemoryEdgeCongestionTracker>();
        services.AddSingleton<IEngineMetricsRecorder, EngineMetricsRecorder>();
        services.AddSingleton<ITenantContext, AsyncLocalTenantContext>();
        services.AddSingleton<IParkingAuditSink>(_ => new AuditChannelSink());
        services.AddSingleton<IParkingAllocationService, ParkingAllocationService>();
        return services;
    }

    public static IServiceCollection AddParkMeshTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SensorDebounceOptions>(configuration.GetSection(SensorDebounceOptions.SectionName));
        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.SectionName));

        services.AddSingleton<ISensorReadingFilter>(sp =>
            new SensorReadingFilter(sp.GetRequiredService<IOptions<SensorDebounceOptions>>().Value));
        services.AddSingleton<ITelemetryIngestionChannel>(sp =>
            new TelemetryIngestionChannel(sp.GetRequiredService<IOptions<SensorDebounceOptions>>().Value));
        services.AddSingleton<ISensorTelemetryProcessor, SensorTelemetryProcessor>();

        services.AddSingleton<MqttConnectionManager>();
        services.AddSingleton<ILedPanelPublisher, MqttLedPanelPublisher>();
        services.AddHostedService<MqttTelemetryHostedService>();

        return services;
    }

    public static IServiceCollection AddParkMeshInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisLeaseOptions>(configuration.GetSection(RedisLeaseOptions.SectionName));

        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(redisConnectionString));
            services.AddSingleton<IBayLeaseManager, RedisBayLeaseManager>();
        }
        else
        {
            services.AddSingleton<IBayLeaseManager, InMemoryBayLeaseManager>();
        }

        var postgresConnectionString = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            var appRole = configuration["Postgres:AppRole"];
            services.AddSingleton<IParkingAuditStore>(_ =>
                new PostgresParkingAuditStore(postgresConnectionString, appRole));
        }
        else
        {
            services.AddSingleton<IParkingAuditStore, InMemoryParkingAuditStore>();
        }

        services.AddHostedService<ParkingAuditHostedService>();

        return services;
    }
}
