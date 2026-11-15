using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;

namespace ParkMesh.Infrastructure.Mqtt;

public sealed class MqttTelemetryHostedService : BackgroundService
{
    private readonly MqttConnectionManager _connectionManager;
    private readonly ITelemetryIngestionChannel _sensorChannel;
    private readonly ISensorTelemetryProcessor _sensorProcessor;
    private readonly IParkingAllocationService _allocationService;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttTelemetryHostedService> _logger;
    private readonly Channel<VehicleAdmittedEvent> _gateChannel = Channel.CreateBounded<VehicleAdmittedEvent>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public MqttTelemetryHostedService(
        MqttConnectionManager connectionManager,
        ITelemetryIngestionChannel sensorChannel,
        ISensorTelemetryProcessor sensorProcessor,
        IParkingAllocationService allocationService,
        IOptions<MqttOptions> options,
        ILogger<MqttTelemetryHostedService> logger)
    {
        _connectionManager = connectionManager;
        _sensorChannel = sensorChannel;
        _sensorProcessor = sensorProcessor;
        _allocationService = allocationService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connectionManager.Client.ApplicationMessageReceivedAsync += HandleIncomingMessageAsync;

        var sensorWorker = _sensorProcessor.RunAsync(stoppingToken);
        var gateWorker = ProcessGateAdmissionsAsync(stoppingToken);
        var mqttLoop = MaintainMqttSubscriptionAsync(stoppingToken);

        await Task.WhenAll(sensorWorker, gateWorker, mqttLoop);
    }

    private async Task MaintainMqttSubscriptionAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("MQTT ingestion disabled via configuration; running local channel processor only.");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        var subscribed = false;
        while (!ct.IsCancellationRequested)
        {
            if (!_connectionManager.IsConnected)
            {
                subscribed = false;
                if (await _connectionManager.EnsureConnectedAsync(ct))
                {
                    await SubscribeAsync(ct);
                    subscribed = true;
                }
            }
            else if (!subscribed)
            {
                await SubscribeAsync(ct);
                subscribed = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectDelaySeconds)), ct);
        }
    }

    private async Task SubscribeAsync(CancellationToken ct)
    {
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(MqttTopics.SensorsWildcard).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.GatesWildcard).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        await _connectionManager.Client.SubscribeAsync(subscribeOptions, ct);
        _logger.LogInformation("Subscribed to MQTT topics {Sensors} and {Gates}.", MqttTopics.SensorsWildcard, MqttTopics.GatesWildcard);
    }

    private Task HandleIncomingMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

        try
        {
            if (topic.StartsWith("parkmesh/sensors/", StringComparison.OrdinalIgnoreCase))
            {
                var reading = JsonSerializer.Deserialize<BaySensorReading>(payload, MqttConnectionManager.JsonOptions);
                if (reading is not null)
                    _sensorChannel.TryEnqueue(reading with { TenantId = null }); // MQTT topics are single-tenant ("default").
            }
            else if (topic.StartsWith("parkmesh/gates/", StringComparison.OrdinalIgnoreCase))
            {
                var admitted = JsonSerializer.Deserialize<VehicleAdmittedEvent>(payload, MqttConnectionManager.JsonOptions);
                if (admitted is not null)
                    _gateChannel.Writer.TryWrite(admitted);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding malformed MQTT JSON payload on topic {Topic}.", topic);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessGateAdmissionsAsync(CancellationToken ct)
    {
        await foreach (var admitted in _gateChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                var sessionId = string.IsNullOrWhiteSpace(admitted.VehicleSessionId)
                    ? $"SES-{admitted.EventId}"
                    : admitted.VehicleSessionId;

                var result = await _allocationService.AllocateAsync(
                    new AllocateSpotRequest(admitted.GateId, sessionId, admitted.VehicleType),
                    ct);

                if (!result.Succeeded)
                {
                    _logger.LogWarning(
                        "Gate admission allocation failed for {SessionId} at {GateId}: {Reason}",
                        sessionId,
                        admitted.GateId,
                        result.FailureReason);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling gate admission event {EventId}.", admitted.EventId);
            }
        }
    }
}
