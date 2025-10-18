namespace CamposDEV.Mqtt.Settings;

public class MqttBrokerSettings
{
    public string BrokerHost { get; set; }
    public int BrokerPort { get; set; }
    public string Topic { get; set; }
    public int RetryIntervalMinutes { get; set; }
    public int NormalIntervalSeconds { get; set; }
}