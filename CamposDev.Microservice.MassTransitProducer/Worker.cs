namespace CamposDev.Microservice.MassTransitProducer;

using MassTransit;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

// Background service that publishes an AMQP message every interval.
// The constructor-style Primary Constructor (C# 12/13) injects the IBus instance.
public class Worker(IBus bus) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Loop until cancellation requested
        while (!stoppingToken.IsCancellationRequested)
        {
            // Build the payload using the defined record types.
            // Because Program.cs configures the raw JSON serializer with camelCase,
            // the outgoing JSON will be camelCase, matching the Node/NestJS consumers' expectations.
            var amqpMessage = new AmqpMessageCreateBankSlip
            {
                data = new DownloadBankSlipPayload { BoletoId = "3410", BoletoChave = "ISHYM9SGE", BoletoStatus = "R", CodigoOrganizacao = "1", CodigoUsuarioIntegracao = "4" }
            };

            // Publish the message using MassTransit.
            // Because the transport is configured to use the raw JSON serializer, the body
            // will be the raw JSON representation of AmqpMessageCreateBankSlip (no MassTransit envelope).
            // We set the routing key explicitly on the RabbitMQ send context so consumers listening
            // to "ticket.cmd.download" (topic) will receive the message.
            await bus.Publish(amqpMessage,
                context =>
                {
                    if (context is RabbitMqSendContext rabbitContext)
                    {
                        // Set the routing key used by topic exchanges
                        rabbitContext.SetRoutingKey("ticket.cmd.download");
                    }
                },
                stoppingToken);

            // Wait before sending the next message. Use CancellationToken to stop quickly.
            await Task.Delay(50000, stoppingToken);
        }
    }
}

// Outgoing message contract: Producer sends this JSON structure as the raw message body.
// Note: property names will be emitted in camelCase due to serializer settings.
public sealed record AmqpMessageCreateBankSlip
{
    public string pattern { get; set; } = "ticket.cmd.download";
    public DownloadBankSlipPayload data { get; set; }
}

public sealed record DownloadBankSlipPayload
{
    public required string BoletoId { get; set; }
    public required string BoletoChave { get; set; }
    public required string BoletoStatus { get; set; }
    public required string CodigoOrganizacao { get; set; }
    public required string CodigoUsuarioIntegracao { get; set; }
}