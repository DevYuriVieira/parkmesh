using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace ParkMesh.Infrastructure.Mqtt;

public sealed class MqttConnectionManager : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    private readonly ILogger<MqttConnectionManager> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public MqttConnectionManager(
        IOptions<MqttOptions> options,
        ILogger<MqttConnectionManager> logger,
        string? clientRoleSuffix = null)
    {
        var opts = options.Value;
        _logger = logger;

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var suffix = string.IsNullOrWhiteSpace(clientRoleSuffix)
            ? Guid.NewGuid().ToString("N")[..8]
            : $"{clientRoleSuffix}-{Guid.NewGuid().ToString("N")[..6]}";

        _clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(opts.Host, opts.Port)
            .WithClientId($"{opts.ClientIdPrefix}-{suffix}")
            .WithCleanSession()
            .Build();
    }

    public IMqttClient Client => _client;
    public bool IsConnected => _client.IsConnected;

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
            return true;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client.IsConnected)
                return true;

            await _client.ConnectAsync(_clientOptions, ct);
            _logger.LogInformation("Connected to MQTT broker.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to connect to MQTT broker.");
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishJsonAsync<T>(
        string topic,
        T payload,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce,
        CancellationToken ct = default)
    {
        if (!_client.IsConnected)
            return;

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(qos)
            .Build();

        await _client.PublishAsync(message, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disconnecting MQTT client.");
            }
        }

        _client.Dispose();
        _connectLock.Dispose();
    }
}
