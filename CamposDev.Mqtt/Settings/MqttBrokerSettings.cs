namespace CamposDEV.Mqtt.Settings;

public class MqttBrokerSettings
{
    public string BrokerHost { get; set; }
    public int BrokerPort { get; set; }
    public string Topic { get; set; }
    public int RetryIntervalMinutes { get; set; }
    public int NormalIntervalSeconds { get; set; }

    /// <summary>
    /// Tópico de disponibilidade do cliente (opcional). Quando definido, o broker publica
    /// <c>offline</c> (retido) como Last Will se a conexão cair, e o cliente publica
    /// <c>online</c> (retido) a cada conexão — o formato do <c>availability_topic</c> do Home Assistant.
    /// </summary>
    public string? AvailabilityTopic { get; set; }
}