using ParkMesh.Domain;
using ParkMesh.Infrastructure.Mqtt;
using ParkMesh.Simulator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.AddSingleton(_ => StandardTopologyFactory.CreateStandardGarage());
builder.Services.AddSingleton<MqttConnectionManager>();
builder.Services.AddHostedService<ParkingSimulatorWorker>();

var host = builder.Build();
host.Run();
