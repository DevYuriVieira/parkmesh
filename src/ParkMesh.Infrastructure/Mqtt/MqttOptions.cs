namespace ParkMesh.Infrastructure.Mqtt;

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientIdPrefix { get; set; } = "parkmesh";
    public int ReconnectDelaySeconds { get; set; } = 3;
}
