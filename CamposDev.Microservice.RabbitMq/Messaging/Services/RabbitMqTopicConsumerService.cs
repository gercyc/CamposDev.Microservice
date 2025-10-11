namespace CamposDev.Microservice.RabbitMq.Messaging.Services;

using System.Text.Json;
using CamposDev.Microservice.RabbitMq.Contracts;
using CamposDev.Microservice.RabbitMq.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public class RabbitMqTopicConsumerService(
    ILogger<RabbitMqTopicConsumerService> logger,
    IOptions<RabbitMqSettings> options,
    IServiceProvider serviceProvider) : BackgroundService
{
    private IConnection? _connection;
    private readonly RabbitMqSettings _rabbitMqSettings = options.Value;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    // use a single channel for the topic consumer queue
    private IChannel? _channel;

    // flattened handler list: (HandlerType, PayloadType, Patterns)
    private List<(Type HandlerType, Type PayloadType, IEnumerable<string> Patterns, string QueueName)> _handlers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BuildHandlerList();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnected();

                _channel ??= await _connection!.CreateChannelAsync();

                // ensure exchange exists (topic)
                await _channel.ExchangeDeclareAsync(_rabbitMqSettings.ExchangeName, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);

                // single queue that will receive all bindings from handlers
                var topicQueue = _rabbitMqSettings.TopicConsumerQueueName ?? "__topic_consumer__";

                // create queue (durable, shared)
                await _channel.QueueDeclareAsync(topicQueue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: stoppingToken);

                // bind each pattern from every handler to this queue
                var distinctPatternsWithQueue = _handlers
                    .SelectMany(h => h.Patterns.Select(p => (Pattern: p, QueueName: h.QueueName)))
                    .DistinctBy(x => (x.Pattern, x.QueueName))
                    .ToList();

                foreach (var (pattern, queueName) in distinctPatternsWithQueue)
                {
                    // Binding to the single topicQueue; we also surface the handler queue name for logging or future use.
                    await _channel.QueueBindAsync(queueName, _rabbitMqSettings.ExchangeName, pattern, cancellationToken: stoppingToken);
                    logger.LogDebug("Bound pattern '{Pattern}' (handler queue: '{HandlerQueue}') to topic queue '{TopicQueue}'",
                        pattern, queueName, topicQueue);
                }

                // optional prefetch for the queue
                await _channel.BasicQosAsync(0, 10, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;

                await _channel.BasicConsumeAsync(
                    queue: topicQueue,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                logger.LogInformation("Listening to exchange '{ExchangeName}' on queue '{Queue}' (vhost: {VHost})", _rabbitMqSettings.ExchangeName, topicQueue, _rabbitMqSettings.VirtualHost);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no loop do topic-consumer. Reconnect em 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await DisposeAmqp();
                _channel = null;
            }
        }
    }

    private void BuildHandlerList()
    {
        using var scope = _serviceProvider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IMessageHandler>().ToList();

        _handlers = handlers
            .Select(h => (HandlerType: h.GetType(), PayloadType: h.PayloadType, Patterns: h.Patterns, h.QueueName))
            .ToList();
    }

    private async Task EnsureConnected()
    {
        if (_connection is { IsOpen: true }) return;

        var factory = new ConnectionFactory
        {
            HostName = _rabbitMqSettings.Host,
            Port = _rabbitMqSettings.Port,
            VirtualHost = _rabbitMqSettings.VirtualHost,
            UserName = _rabbitMqSettings.Username,
            Password = _rabbitMqSettings.Password
        };

        _connection = await factory.CreateConnectionAsync();
    }

    private async Task OnReceivedAsync(object? sender, BasicDeliverEventArgs ea)
    {
        // Note: consumer channel is available on ea. Use it to build RmqContext.
        var ch = _channel!;
        var ctx = new RmqContext(ch, ea);

        try
        {
            var body = ctx.GetBodyString();
            logger.LogInformation("Topic message received. rk={RoutingKey} size={Size}B", ea.RoutingKey, ea.Body.Length);

            // find handlers whose patterns match the routing key
            var targets = _handlers
                .Where(h => h.Patterns.Any(p => AmqpExtensions.Matches(p, ea.RoutingKey)))
                .DistinctBy(h => h.HandlerType) // avoid duplicate handler invocation
                .ToList();

            if (targets.Count == 0)
            {
                logger.LogWarning("No handler for rk={RoutingKey}", ea.RoutingKey);
                await ctx.AckAsync();
                return;
            }

            foreach (var (handlerType, payloadType, patterns, queueName) in targets)
            {
                using var scope = _serviceProvider.CreateScope();
                var handler = scope.ServiceProvider.GetServices<IMessageHandler>()
                    .FirstOrDefault(h => h.GetType() == handlerType);

                if (handler is null)
                {
                    logger.LogWarning("Handler {HandlerType} not resolved from DI", handlerType);
                    continue;
                }

                var payload = JsonSerializer.Deserialize(body, payloadType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                await handler.HandleAsync(ctx, payload!, CancellationToken.None);
            }

            await ctx.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing topic message rk={RoutingKey}. NACK.", ea.RoutingKey);
            await ctx.NackAsync(requeue: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping topic consumer...");
        await DisposeAmqp();
        await base.StopAsync(cancellationToken);
    }

    private async Task DisposeAmqp()
    {
        if (_channel is not null)
        {
            try { await _channel.CloseAsync(); } catch { }
            try { await _channel.DisposeAsync(); } catch { }
            _channel = null;
        }

        try
        {
            if (_connection is not null) await _connection.CloseAsync();
        }
        catch { }

        try
        {
            _connection?.DisposeAsync();
        }
        catch { }

        _connection = null;
    }
}