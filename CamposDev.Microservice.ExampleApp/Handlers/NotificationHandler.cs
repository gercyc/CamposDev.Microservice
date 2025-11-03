using System.Text.Json;
using CamposDev.Microservice.RabbitMq.Messaging;

namespace CamposDev.Microservice.ExampleApp.Handlers;

public sealed class NotificationHandler(ILogger<NotificationHandler> logger) : MessageHandler<AmqpNotificationMessage>
{
    public override string QueueName => "notifications.queue";
    //public override Dictionary<string, object?>? QueueArgs => new() { { "x-dead-letter-exchange", "notifications.dlq" } };
    public override Dictionary<string, object?>? QueueArgs { get; }

    public override IEnumerable<string> Patterns =>
    [
        "notifications.confirmation-email",
        "notifications.email"
    ];

    public override async Task HandleAsync(RmqContext ctx, AmqpNotificationMessage payload, CancellationToken ct)
    {
        var notificationData = payload.data;

        if (ctx.RoutingKey.Equals("notifications.confirmation-email"))
            logger.LogInformation("Notification confirmation mail: {data}", JsonSerializer.Serialize(notificationData));
        else if (ctx.RoutingKey.Contains("notifications.email"))
            logger.LogInformation("Notification mail: {data}", JsonSerializer.Serialize(notificationData));
    }
}

public sealed record AmqpNotificationMessage(string pattern, NotificationMessage data);

public sealed record NotificationMessage
{
    public string to { get; set; }
    public string subject { get; set; }
    public string body { get; set; }
    public string type { get; set; }
    public string referenceId { get; set; }
    public string userId { get; set; }
}
