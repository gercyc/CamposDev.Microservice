# MassTransit Raw JSON Producer

This example demonstrates how to publish raw JSON messages to RabbitMQ using MassTransit while avoiding MassTransit's envelope. The goal is interoperability: downstream consumers are Node/NestJS services that do not use MassTransit, so the produced messages must follow a specific JSON payload format.

Key points:
- MassTransit is used only as a transport/publisher.
- The producer uses a raw JSON serializer so the message body is the application payload (no MassTransit envelope).
- Messages are published to a topic exchange with routing key `ticket.cmd.download`.
- The payload uses camelCase JSON property names to match JavaScript/TypeScript conventions.

Message JSON example (consumer will receive this body):

```json
{
    "pattern": "ticket.cmd.download",
    "data": {
        "boletoId": "3410",
        "boletoChave": "ISHYM9SGE",
        "boletoStatus": "R",
        "codigoOrganizacao": "1",
        "codigoUsuarioIntegracao": "4"
    }
}
```

Files changed / annotated:
- `Program.cs` — comments explaining RabbitMQ host, use of raw JSON serializer, serializer options (camelCase), and exchange/queue binding.
- `Worker.cs` — comments explaining publishing behavior, setting routing key, and payload shape.

How it works (summary):
1. `Program.cs` configures MassTransit with `UseRawJsonSerializer(...)`. That produces plain JSON bodies for messages so non-MassTransit consumers can read them directly.
2. `Program.cs` configures an exchange for `AmqpMessageCreateBankSlip` and binds a queue `ticket.queue` with routing key `ticket.cmd.download`.
3. `Worker` periodically constructs an `AmqpMessageCreateBankSlip` object and publishes it. The publish call sets the RabbitMQ routing key directly on the send context (`SetRoutingKey`), so the message is routed as expected.
4. Consumers (Node/NestJS) should bind to the same topic exchange and routing key, and parse the JSON body. The payload uses camelCase.

Running locally:
1. Ensure RabbitMQ is running and accessible at the host configured in `Program.cs`:
   - Host/IP: `192.168.101.3`
   - Virtual host: `vhost-masstransit`
   - Username/password: `guest` / `guest`
   Update these values in `Program.cs` for your environment.
2. Build and run the worker project:
   - dotnet run (from project directory) or run from Visual Studio.
3. Observe messages arriving in RabbitMQ with the JSON payload shown above.

Consumer guidance (Node/NestJS):
- Use a RabbitMQ client (amqplib or NestJS microservices) to consume from a topic exchange.
- Ensure the queue is bound to the exchange with routing key `ticket.cmd.download`.
- The message body will already be valid JSON; parse it directly (no MassTransit envelope).

Security and production notes:
- Do not use default credentials in production. Use secure credentials and TLS.
- Consider idempotency, message validation, and schema verification on the consumer side.
- If you require headers or metadata, coordinate a simple header contract with consumers — avoid MassTransit-specific headers if interoperability is required.
