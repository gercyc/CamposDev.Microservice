namespace CamposDev.Microservice.RabbitMq.Messaging;

public class RabbitMqSettings
{
    public required string Host { get; set; }
    public int Port { get; set; }
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string VirtualHost { get; set; }
    public required string ExchangeName { get; set; }
    public required bool RecreateQueues{ get; set; }
    public string? TopicConsumerQueueName { get; set; }

    public string DeadLetterExchange => string.Concat(ExchangeName, ".dlx");
    public string DefaultDeadLetterQueueName { get; set; } = "default.dlq";
}