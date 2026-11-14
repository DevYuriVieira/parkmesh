using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.Infrastructure.Mqtt;

public sealed class MqttLedPanelPublisher : ILedPanelPublisher
{
    private readonly MqttConnectionManager _connectionManager;
    private readonly ILogger<MqttLedPanelPublisher> _logger;

    public MqttLedPanelPublisher(
        MqttConnectionManager connectionManager,
        ILogger<MqttLedPanelPublisher> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task PublishDirectivesAsync(IReadOnlyList<LedDirective> directives, CancellationToken ct = default)
    {
        if (directives.Count == 0 || !_connectionManager.IsConnected)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var directive in directives)
        {
            try
            {
                var command = new LedPanelCommand(directive.PanelId, directive.Floor, directive.Direction, now);
                await _connectionManager.PublishJsonAsync(
                    MqttTopics.PanelDirection(directive.PanelId),
                    command,
                    MqttQualityOfServiceLevel.AtMostOnce,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to publish LED directive for panel {PanelId}.", directive.PanelId);
            }
        }
    }
}
