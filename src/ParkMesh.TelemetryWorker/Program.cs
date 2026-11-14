using ParkMesh.Application.Allocation;
using ParkMesh.Domain;
using ParkMesh.Infrastructure;
using ParkMesh.TelemetryWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ParkMeshApiOptions>(builder.Configuration.GetSection(ParkMeshApiOptions.SectionName));
builder.Services.AddSingleton(_ => StandardTopologyFactory.CreateStandardGarage());
builder.Services.AddSingleton<IParkingAllocationService, RemoteEngineAllocationProxy>();
builder.Services.AddParkMeshTelemetry(builder.Configuration);

var host = builder.Build();
host.Run();
