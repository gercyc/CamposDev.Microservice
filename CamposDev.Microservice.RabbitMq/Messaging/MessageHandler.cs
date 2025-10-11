namespace CamposDev.Microservice.RabbitMq.Messaging;

using CamposDev.Microservice.RabbitMq.Contracts;


public abstract class MessageHandler<TPayload> : IMessageHandler
{
    public abstract string QueueName { get; }
    public abstract Dictionary<string, object?>? QueueArgs { get; }
    public abstract IEnumerable<string> Patterns { get; }
    public Type PayloadType => typeof(TPayload);

    public async Task HandleAsync(RmqContext ctx, object payload, CancellationToken ct)
        => await HandleAsync(ctx, (TPayload)payload, ct);

    public abstract Task HandleAsync(RmqContext ctx, TPayload payload, CancellationToken ct);
}