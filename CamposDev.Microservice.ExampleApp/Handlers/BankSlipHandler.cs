using CamposDev.Microservice.RabbitMq.Messaging;
using System.Text.Json;

namespace Demo.MicroserviceAspnet.Handlers;

public sealed class BankSlipHandler(ILogger<BankSlipHandler> logger) : MessageHandler<BankSlipDataMessage>
{
    public override string QueueName => "ticket.queue";
    public override Dictionary<string, object?>? QueueArgs { get; }

    public override IEnumerable<string> Patterns =>
    [
        "ticket.cmd.create",
        "ticket.evt.update"
    ];

    public override async Task HandleAsync(RmqContext ctx, BankSlipDataMessage payload, CancellationToken ct)
    {
        var bankSlipData = payload.data;
                
        if (ctx.RoutingKey.EndsWith(".create"))
            logger.LogInformation("Creating ticket: {data}", JsonSerializer.Serialize(bankSlipData));
        else if (ctx.RoutingKey.EndsWith(".update"))
            logger.LogInformation("Updating ticket: {data}", JsonSerializer.Serialize(bankSlipData));
    }
}

public sealed record BankSlipDataMessage(string pattern, BankSlipData data);
public sealed record BankSlipData(long CodigoBoleto, string Chave, DateTime DataDocumento, DateTime DataVencimento);