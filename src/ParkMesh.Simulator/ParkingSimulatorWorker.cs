using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Mqtt;

namespace ParkMesh.Simulator;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = string.Empty;
    public int TickIntervalMs { get; set; } = 600;
    public double ArrivalProbabilityPerTick { get; set; } = 0.35;
    public int MaxConcurrentCars { get; set; } = 18;
    public int DwellTicksAfterParking { get; set; } = 12;
    public double IntrusionProbability { get; set; } = 0.12;
    public double SensorNoiseProbability { get; set; } = 0.08;
}

public sealed class ParkingSimulatorWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class SimulatedCar
    {
        public required string SessionId { get; init; }
        public required VehicleType VehicleType { get; init; }
        public required string TargetBayId { get; set; }
        public required IReadOnlyList<string> NodeIds { get; set; }
        public int NodeIndex { get; set; }
        public bool IsParked { get; set; }
        public int ParkedTicksRemaining { get; set; }
    }

    private readonly SimulatorOptions _options;
    private readonly MqttOptions _mqttOptions;
    private readonly MqttConnectionManager _mqtt;
    private readonly ParkingGraph _graph;
    private readonly ILogger<ParkingSimulatorWorker> _logger;
    private readonly HttpClient _http;
    private readonly Random _rng = new();
    private readonly Dictionary<string, long> _baySequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SimulatedCar> _cars = [];
    private int _carCounter;

    public ParkingSimulatorWorker(
        IOptions<SimulatorOptions> options,
        IOptions<MqttOptions> mqttOptions,
        MqttConnectionManager mqtt,
        ParkingGraph graph,
        ILogger<ParkingSimulatorWorker> logger)
    {
        _options = options.Value;
        _mqttOptions = mqttOptions.Value;
        _mqtt = mqtt;
        _graph = graph;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/")
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickDelay = TimeSpan.FromMilliseconds(Math.Max(150, _options.TickIntervalMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_mqttOptions.Enabled && !_mqtt.IsConnected)
                {
                    await _mqtt.EnsureConnectedAsync(stoppingToken);
                }

                await StepSimulationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Simulator tick failed; retrying after delay.");
            }

            await Task.Delay(tickDelay, stoppingToken);
        }
    }

    private async Task StepSimulationAsync(CancellationToken ct)
    {
        if (_cars.Count < _options.MaxConcurrentCars && _rng.NextDouble() < _options.ArrivalProbabilityPerTick)
        {
            await SpawnArrivalAsync(ct);
        }

        await AdvanceMovingCarsAsync(ct);
        await HandleParkedCarsAsync(ct);

        if (_rng.NextDouble() < _options.IntrusionProbability)
        {
            await InjectPhysicalIntruderAsync(ct);
        }

        if (_rng.NextDouble() < _options.SensorNoiseProbability)
        {
            await EmitTransientNoiseAsync(ct);
        }
    }

    private async Task SpawnArrivalAsync(CancellationToken ct)
    {
        var gateId = _rng.Next(2) == 0 ? "GATE-N1" : "GATE-S1";
        var vehicleType = PickRandomVehicleType();
        var sessionId = $"SIM-{++_carCounter:D4}";

        var response = await _http.PostAsJsonAsync(
            "api/parking/allocations",
            new { GateId = gateId, VehicleSessionId = sessionId, VehicleType = vehicleType, LeaseSeconds = 90 },
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
            return;

        var envelope = await response.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions, ct);
        if (envelope?.Session is null)
            return;

        _cars.Add(new SimulatedCar
        {
            SessionId = sessionId,
            VehicleType = vehicleType,
            TargetBayId = envelope.Session.AssignedBayId,
            NodeIds = envelope.Session.Route.NodeIds,
            NodeIndex = 0
        });

        _logger.LogInformation(
            "Spawned {SessionId} ({VehicleType}) at {GateId} -> bay {BayId}.",
            sessionId,
            vehicleType,
            gateId,
            envelope.Session.AssignedBayId);
    }

    private async Task AdvanceMovingCarsAsync(CancellationToken ct)
    {
        var activeSessions = await FetchActiveSessionsAsync(ct);

        foreach (var car in _cars.Where(c => !c.IsParked).ToList())
        {
            if (activeSessions.TryGetValue(car.SessionId, out var serverSession) &&
                !string.Equals(serverSession.AssignedBayId, car.TargetBayId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Detected live reroute for {SessionId}: {OldBay} -> {NewBay}.",
                    car.SessionId,
                    car.TargetBayId,
                    serverSession.AssignedBayId);
                car.TargetBayId = serverSession.AssignedBayId;
                car.NodeIds = serverSession.Route.NodeIds;
                car.NodeIndex = 0;
            }

            if (car.NodeIndex < car.NodeIds.Count - 1)
            {
                car.NodeIndex++;
                var currentNodeId = car.NodeIds[car.NodeIndex];
                await _http.PostAsJsonAsync(
                    $"api/parking/sessions/{car.SessionId}/progress",
                    new { CurrentNodeId = currentNodeId },
                    JsonOptions,
                    ct);
            }

            if (car.NodeIndex >= car.NodeIds.Count - 1)
            {
                await PublishConfirmedSensorTransitionAsync(car.TargetBayId, true, car.SessionId, ct);
                car.IsParked = true;
                car.ParkedTicksRemaining = _options.DwellTicksAfterParking;
                _logger.LogInformation("{SessionId} parked in {BayId}.", car.SessionId, car.TargetBayId);
            }
        }
    }

    private async Task HandleParkedCarsAsync(CancellationToken ct)
    {
        foreach (var car in _cars.Where(c => c.IsParked).ToList())
        {
            car.ParkedTicksRemaining--;
            if (car.ParkedTicksRemaining > 0)
                continue;

            await PublishConfirmedSensorTransitionAsync(car.TargetBayId, false, null, ct);
            _cars.Remove(car);
            _logger.LogInformation("{SessionId} vacated {BayId}.", car.SessionId, car.TargetBayId);
        }
    }

    private async Task InjectPhysicalIntruderAsync(CancellationToken ct)
    {
        var movingCar = _cars.FirstOrDefault(c => !c.IsParked && c.NodeIndex > 0 && c.NodeIndex < c.NodeIds.Count - 2);
        if (movingCar is null)
            return;

        var intruderId = $"INTRUDER-{_rng.Next(100, 999)}";
        await PublishConfirmedSensorTransitionAsync(movingCar.TargetBayId, true, intruderId, ct);
        _logger.LogWarning(
            "Injected intruder {IntruderId} into leased bay {BayId} (displacing {SessionId}).",
            intruderId,
            movingCar.TargetBayId,
            movingCar.SessionId);
    }

    private async Task EmitTransientNoiseAsync(CancellationToken ct)
    {
        var bayIds = _graph.BaysById.Keys.ToList();
        var candidateBay = bayIds[_rng.Next(bayIds.Count)];
        if (_cars.Any(c => string.Equals(c.TargetBayId, candidateBay, StringComparison.OrdinalIgnoreCase)))
            return;

        await PublishSensorReadingAsync(candidateBay, true, 120.0, DateTimeOffset.UtcNow, null, ct);
    }

    private async Task PublishConfirmedSensorTransitionAsync(
        string bayId,
        bool occupied,
        string? sessionId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var distance = occupied ? 42.0 : 210.0;
        await PublishSensorReadingAsync(bayId, occupied, distance, now, sessionId, ct);
        await PublishSensorReadingAsync(bayId, occupied, distance, now.AddSeconds(2), sessionId, ct);
    }

    private async Task PublishSensorReadingAsync(
        string bayId,
        bool occupied,
        double distanceCm,
        DateTimeOffset timestampUtc,
        string? sessionId,
        CancellationToken ct)
    {
        var seq = _baySequences.TryGetValue(bayId, out var current) ? current + 1 : 1;
        _baySequences[bayId] = seq;

        var reading = new BaySensorReading(bayId, occupied, distanceCm, seq, timestampUtc, sessionId);
        if (_mqttOptions.Enabled && _mqtt.IsConnected && _graph.TryGetBay(bayId, out var bay))
        {
            await _mqtt.PublishJsonAsync(
                MqttTopics.SensorState(bay.Floor, bay.BayId),
                reading,
                MqttQualityOfServiceLevel.AtLeastOnce,
                ct);
            return;
        }

        await _http.PostAsJsonAsync("api/parking/telemetry/readings", reading, JsonOptions, ct);
    }

    private async Task<Dictionary<string, ActiveVehicleSession>> FetchActiveSessionsAsync(CancellationToken ct)
    {
        var sessions = await _http.GetFromJsonAsync<List<ActiveVehicleSession>>("api/parking/sessions", JsonOptions, ct);
        return (sessions ?? []).ToDictionary(s => s.VehicleSessionId, StringComparer.OrdinalIgnoreCase);
    }

    private VehicleType PickRandomVehicleType() => _rng.Next(100) switch
    {
        < 55 => VehicleType.Standard,
        < 73 => VehicleType.EV,
        < 86 => VehicleType.SUV,
        < 94 => VehicleType.Moto,
        _ => VehicleType.Pcd
    };

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private sealed record AllocationEnvelope(ActiveVehicleSession Session, double ComputationTimeMs);
}
