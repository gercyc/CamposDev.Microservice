namespace CamposDev.Microservice.RabbitMq.Contracts;

public interface IRabbitMqPublisherService : IScopedService
{
    Task PublishToExchange<T>(string exchange, string routingKey, T payload);
    Task PublishToQueue<T>(string queue, T payload);
}