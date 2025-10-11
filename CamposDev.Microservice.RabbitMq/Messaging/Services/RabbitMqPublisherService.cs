namespace CamposDev.Microservice.RabbitMq.Messaging.Services;

using System.Text;
using System.Text.Json;
using CamposDev.Microservice.RabbitMq.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

public class RabbitMqPublisherService : IRabbitMqPublisherService
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    public RabbitMqPublisherService(IOptions<RabbitMqSettings> options)
    {
        var settings = options.Value;

        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            UserName = settings.Username,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost
        };

        _connection = factory.CreateConnectionAsync().Result;
        _channel = _connection.CreateChannelAsync().Result;
    }

    /// <summary>
    /// Publica em uma exchange com routing key definida.
    /// </summary>
    public async Task PublishToExchange<T>(string exchange, string routingKey, T payload)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        await _channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            body: body
        );
    }

    /// <summary>
    /// Publica diretamente em uma fila usando a default exchange.
    /// A routingKey deve ser o nome da fila.
    /// </summary>
    public async Task PublishToQueue<T>(string queue, T payload)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        await _channel.BasicPublishAsync(
            exchange: "",
            routingKey: queue,
            mandatory: false,
            body: body
        );
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}