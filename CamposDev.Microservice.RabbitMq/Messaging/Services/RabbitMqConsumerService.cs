namespace CamposDev.Microservice.RabbitMq.Messaging.Services;

using System.Text.Json;
using CamposDev.Microservice.RabbitMq.Contracts;
using CamposDev.Microservice.RabbitMq.Extensions;
using CamposDev.Microservice.RabbitMq.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public class RabbitMqConsumerService(ILogger<RabbitMqConsumerService> logger, IOptions<RabbitMqSettings> options, IServiceProvider serviceProvider) : BackgroundService
{
    private IConnection? _connection;
    private readonly RabbitMqSettings _rabbitMqSettings = options.Value;

    // Um canal por fila
    private readonly Dictionary<string, IChannel> _channelsByQueue = new();

    // Mapa: fila -> (pattern -> [handlers])
    private Dictionary<string, Dictionary<string, List<(Type HandlerType, Type PayloadType, Dictionary<string, object?>?)>>> _map = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BuildHandlerMap();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnected();
                var ch0 = await GetOrCreateChannelAsync("__bootstrap__");
                await ch0.ExchangeDeclareAsync(_rabbitMqSettings.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: stoppingToken);
                await ch0.ExchangeDeclareAsync(_rabbitMqSettings.ExchangeName, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
                await ch0.CloseAsync(stoppingToken);
                _channelsByQueue.Remove("__bootstrap__");

                // preparar todas as filas e iniciar consumo
                foreach (var queueMap in _map)
                {
                    var ch = await GetOrCreateChannelAsync(queueMap.Key);

                    if (_rabbitMqSettings.RecreateQueues)
                    {
                        await ch.QueueDeleteAsync(queueMap.Key, ifUnused: false, ifEmpty: false, cancellationToken: stoppingToken);
                    }

                    // garante existência da fila sem quebrar se já tiver args diferentes
                    var queueArgs = _map[queueMap.Key].SelectMany(kv => kv.Value).Select(x => x.Item3).FirstOrDefault(a => a != null);
                    await EnsureQueueExistsAsync(ch, queueMap.Key, _rabbitMqSettings, queueMap);

                    // bind somente os patterns desta fila
                    foreach (var pattern in _map[queueMap.Key].Keys.Distinct())
                        await ch.QueueBindAsync(queueMap.Key, _rabbitMqSettings.ExchangeName, pattern, cancellationToken: stoppingToken);

                    // (opcional) Prefetch por fila
                    await ch.BasicQosAsync(0, 10, false, stoppingToken);

                    var consumer = new AsyncEventingBasicConsumer(ch);

                    // capture queue name no handler
                    consumer.ReceivedAsync += (s, ea) => OnReceivedAsync(queueMap.Key, ch, ea);

                    await ch.BasicConsumeAsync(
                        queue: queueMap.Key,
                        autoAck: false, // ACK manual
                        consumer: consumer,
                        cancellationToken: stoppingToken);

                    logger.LogInformation("Consumindo fila '{queueMap}' (vhost: {VHost})", queueMap.Key, _rabbitMqSettings.VirtualHost);
                }

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // encerrando
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no loop do consumidor. Reconnect em 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await DisposeAmqp();
            }
        }
    }

    private void BuildHandlerMap()
    {
        using var scope = serviceProvider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IMessageHandler>().ToList();

        // group by queue then pattern
        _map = handlers
            .SelectMany(h => h.Patterns.Select(p => (Queue: h.QueueName, Pattern: p, HandlerType: h.GetType(), h.PayloadType, QueueArgs: h.QueueArgs)))
            .GroupBy(x => x.Queue)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.Pattern)
                    .ToDictionary(
                        gg => gg.Key,
                        gg => gg.Select(x => (x.HandlerType, x.PayloadType, x.QueueArgs)).Distinct().ToList()
                    )
            );
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

    private async Task<IChannel> GetOrCreateChannelAsync(string queueName)
    {
        if (_channelsByQueue.TryGetValue(queueName, out var ch) && ch.IsOpen) return ch;
        ch = await _connection!.CreateChannelAsync();
        _channelsByQueue[queueName] = ch;
        return ch;
    }

    // Evita PRECONDITION: tenta passive, se não existir, cria com args (DLX se configurado)
    private static async Task EnsureQueueExistsAsync(IChannel ch, string queueName, RabbitMqSettings rabbitMqSettings, KeyValuePair<string, Dictionary<string, List<(Type HandlerType, Type PayloadType, Dictionary<string, object?>?)>>> queueMap = default)
    {
        IDictionary<string, object?>? args = null;
        var dlq = !string.IsNullOrWhiteSpace(queueName) ? queueName.Replace(".queue", ".dlq") : rabbitMqSettings.DefaultDeadLetterQueueName;
        await ch.ExchangeDeclareAsync(rabbitMqSettings.ExchangeName, ExchangeType.Topic, durable: true);
        await ch.QueueDeclareAsync(dlq, true, false, false);
        await ch.QueueBindAsync(dlq, rabbitMqSettings.DeadLetterExchange, dlq);

        args = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = rabbitMqSettings.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = dlq
        };

        var queueArgs = queueMap.Value.SelectMany(kv => kv.Value).Select(x => x.Item3).FirstOrDefault(a => a != null);

        if (queueArgs != null)
        {
            foreach (var kv in queueArgs)
            {
                args[kv.Key] = kv.Value;
            }
        }
        foreach (var queueOptions in queueMap.Value)
        {
            await ch.QueueDeclareAsync(queue: queueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
            await ch.QueueBindAsync(queueName, rabbitMqSettings.ExchangeName, queueOptions.Key);
        }

    }

    private async Task OnReceivedAsync(string queueName, IChannel ch, BasicDeliverEventArgs ea)
    {
        var ctx = new RmqContext(ch, ea);

        try
        {
            var body = ctx.GetBodyString();
            logger.LogInformation("Mensagem recebida. queue={Queue} rk={RoutingKey} size={Size}B",
                queueName, ea.RoutingKey, ea.Body.Length);

            // handlers desta fila cujo padrão casa com a routing key
            if (!_map.TryGetValue(queueName, out var byPattern))
            {
                await ctx.AckAsync();
                return;
            }

            var targets = byPattern
                .Where(kv => AmqpExtensions.Matches(kv.Key, ea.RoutingKey))
                .SelectMany(kv => kv.Value)
                .Distinct()
                .ToList();

            if (targets.Count == 0)
            {
                logger.LogWarning("Sem handler para Fila= '{Queue}' RoutingKey= '{Key}'", queueName, ea.RoutingKey);
                await ctx.AckAsync();
                return;
            }

            foreach (var (handlerType, payloadType, queueArgs) in targets)
            {
                using var scope = serviceProvider.CreateScope();
                var handler = scope.ServiceProvider.GetServices<IMessageHandler>()
                    .First(h => h.GetType() == handlerType);

                var payload = JsonSerializer.Deserialize(body, payloadType,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                await handler.HandleAsync(ctx, payload!, CancellationToken.None);
            }

            await ctx.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha processando queue= '{Queue}' RoutingKey= '{Key}'. NACK.", queueName, ea.RoutingKey);
            await ctx.NackAsync(requeue: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Encerrando consumidor...");
        await DisposeAmqp();
        await base.StopAsync(cancellationToken);
    }

    private async Task DisposeAmqp()
    {
        foreach (var ch in _channelsByQueue.Values)
        {
            try
            {
                await ch.CloseAsync();
            }
            catch
            {
                // ignored
            }

            try
            {
                await ch.DisposeAsync();
            }
            catch
            {
                // ignored
            }
        }

        _channelsByQueue.Clear();

        try
        {
            if (_connection is not null) await _connection.CloseAsync();
        }
        catch
        {
            // ignored
        }

        try
        {
            _connection?.DisposeAsync();
        }
        catch
        {
            // ignored
        }

        _connection = null;
    }
}