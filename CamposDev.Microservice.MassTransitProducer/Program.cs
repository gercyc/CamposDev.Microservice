using CamposDev.Microservice.MassTransitProducer;
using MassTransit;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMassTransit(x =>
{
    // Enable MassTransit's delayed message scheduler (if you need delayed messages)
    x.AddDelayedMessageScheduler();

    var entryAssembly = Assembly.GetEntryAssembly();

    x.UsingRabbitMq((context, cfg) =>
    {
        // RabbitMQ host + vhost configuration
        cfg.Host("192.168.101.3", "vhost-masstransit", h =>
        {
            // Credentials for the vhost (update for production)
            h.Username("guest");
            h.Password("guest");
        });

        // Use raw JSON serializer to publish the message body without MassTransit's envelope.
        // Raw serializer sends the message payload as plain JSON, allowing non-MassTransit consumers
        // (e.g., Node/NestJS services) to read the payload directly.
        cfg.UseRawJsonSerializer(RawSerializerOptions.AnyMessageType, true);

        // Configure System.Text.Json options used by the raw serializer.
        // Setting PropertyNamingPolicy = CamelCase ensures payload properties are camelCase,
        // matching typical JavaScript/TypeScript conventions.
        cfg.ConfigureJsonSerializerOptions(cfg =>
        {
            cfg.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            // Custom JSON serializer options can be configured here
            return cfg;
        });

        // Configure Exchange/Queue binding details for the specific message type.
        // This defines an exchange for 'AmqpMessageCreateBankSlip' and binds a queue with a specific routing key.
        cfg.Publish<AmqpMessageCreateBankSlip>(p =>
        {
            // Use topic exchange so routing keys can be used flexibly by consumers.
            p.ExchangeType = "topic";
            p.Durable = true;      // Keep exchange across broker restarts
            p.AutoDelete = false;  // Do not remove exchange when unused

            // Bind a queue named "ticket.queue" to this exchange with the routing key "ticket.cmd.download".
            // This is useful for local testing or when the producer should ensure the queue binding exists.
            p.BindQueue(p.Exchange.ExchangeName, "ticket.queue", s =>
            {
                s.RoutingKey = "ticket.cmd.download";
                s.ExchangeType = "topic";
            });
        });
    });
});

// Register the background worker that publishes messages periodically.
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();