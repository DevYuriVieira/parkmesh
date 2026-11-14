using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;
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
        services.AddSingleton<IParkingAllocationService, ParkingAllocationService>();
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

        return services;
    }
}
