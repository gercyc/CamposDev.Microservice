using CamposDev.Microservice.RabbitMq.Extensions;
using CamposDev.Microservice.RabbitMq.Messaging;
using CamposDev.Microservice.RabbitMq.Messaging.Services;
using CamposDev.Microservice.RabbitMq.Persistence;
using Demo.MicroserviceAspnet.EfContext;
using Serilog;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets(typeof(Program).Assembly);
}

// Configuração do Serilog para logging estruturado
builder.Services.AddSerilog(builder.Configuration);

// Configuração das opções do RabbitMQ a partir do appsettings.json
builder.Services.AddOptions<RabbitMqSettings>().Bind(builder.Configuration.GetSection("RabbitMqSettings"));

// Registro automático de todos os handlers de evento do assembly atual
// Procura por classes que implementam IMessageHandler e as registra automaticamente
builder.Services.AddEventHandlersFrom(Assembly.GetExecutingAssembly());

// Registro do serviço consumidor de tópicos RabbitMQ como hosted service
// Este serviço irá escutar as filas e acionar os handlers apropriados
builder.Services.AddHostedService<RabbitMqTopicConsumerService>();

// Registro do serviço consumidor de filas RabbitMQ como hosted service
// Este serviço irá escutar as filas e acionar os handlers apropriados
builder.Services.AddHostedService<RabbitMqConsumerService>();

// Configuração da persistência com Entity Framework
builder.Services.AddPersistence<Context>(builder.Configuration);

// Registro automático de serviços marcados com IScopedService/ITransientService
// Procura por classes marcadas e registra suas interfaces automaticamente
builder.Services.AddServicesFrom(Assembly.GetExecutingAssembly());

var app = builder.Build();

// Inicia a aplicação
app.Run();