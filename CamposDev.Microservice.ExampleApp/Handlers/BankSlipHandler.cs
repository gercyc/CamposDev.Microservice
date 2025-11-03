using System.Text.Json;
using CamposDev.Microservice.RabbitMq.Messaging;

namespace CamposDev.Microservice.ExampleApp.Handlers;

public sealed class BankSlipHandler(ILogger<BankSlipHandler> logger) : MessageHandler<BankSlipDataMessage>
{
    public override string QueueName => "homelab.queue";
    //public override Dictionary<string, object?>? QueueArgs => new() { { "x-dead-letter-exchange", "homelab.dlq" } };
    public override Dictionary<string, object?>? QueueArgs { get; }


    public override IEnumerable<string> Patterns =>
    [
        "homelab.cmd.create-something",
        "homelab.evt.update-something"
    ];

    public override async Task HandleAsync(RmqContext ctx, BankSlipDataMessage payload, CancellationToken ct)
    {
        var bankSlipData = payload.data;

        if (ctx.RoutingKey.Contains(".create"))
            logger.LogInformation("Creating ticket: {data}", JsonSerializer.Serialize(bankSlipData));
        else if (ctx.RoutingKey.Contains(".update"))
            logger.LogInformation("Updating ticket: {data}", JsonSerializer.Serialize(bankSlipData));
    }
}

public sealed record BankSlipDataMessage(string pattern, BankSlipData data);
public sealed record BankSlipData(long CodigoBoleto, string Chave, DateTime DataDocumento, DateTime DataVencimento);