# Demo Microservice - Sistema de Processamento de Eventos

Este projeto implementa um sistema de microserviços baseado em eventos usando RabbitMQ para comunicação assíncrona entre serviços.

## 📋 Índice

- [Visão Geral](#visão-geral)
- [Configuração](#configuração)
- [Como Criar um Handler](#como-criar-um-handler)
- [Exemplos Práticos](#exemplos-práticos)
- [Configuração do RabbitMQ](#configuração-do-rabbitmq)
- [Troubleshooting](#troubleshooting)

## 🔍 Visão Geral

O sistema utiliza o padrão **Message Handler** para processar eventos de forma assíncrona. Cada handler é responsável por processar um tipo específico de evento ou comando.

### Componentes Principais

- **IMessageHandler**: Interface que define a estrutura de um handler
- **MessageHandler<T>**: Classe base abstrata que facilita a implementação
- **RabbitMqTopicConsumerService**: Serviço que consome mensagens e aciona os handlers

## ⚙️ Configuração

### 1. Configuração do appsettings.json
````
{
    "RabbitMqSettings": {
        "HostName": "localhost",
        "Port": 5672,
        "UserName": "guest",
        "Password": "guest",
        "VirtualHost": "/",
        "ExchangeName": "events.exchange"
    }
}
````

### Passo 2: Implementar o Handler
````csharp
using Demo.Microservice.Common.Messaging;
namespace SeuProjeto.Handlers;

public sealed class CustomerHandler(ILogger<CustomerHandler> logger) : MessageHandler<CustomerMessage>
{ 
    // Nome único da fila para este handler
    public override string QueueName => "customer.queue";

    // Argumentos opcionais da fila (pode ser null)
    public override Dictionary<string, object?>? QueueArgs => null;

    // Padrões de routing keys que este handler processa
    public override IEnumerable<string> Patterns =>
        [
            "customer.evt.created",
            "customer.evt.updated",
            "customer.cmd.delete"
        ];

    // Método que processa a mensagem
    public override async Task HandleAsync(RmqContext ctx, CustomerMessage payload, CancellationToken ct)
    {
        try
        {
            var customerData = payload.data;

            // Processar baseado na routing key
            switch (ctx.RoutingKey)
            {
                case "customer.evt.created":
                    logger.LogInformation("Criando cliente: {CustomerName}", customerData.Name);
                    await ProcessCustomerCreation(customerData, ct);
                    break;

                case "customer.evt.updated":
                    logger.LogInformation("Atualizando cliente: {CustomerId}", customerData.Id);
                    await ProcessCustomerUpdate(customerData, ct);
                    break;

                case "customer.cmd.delete":
                    logger.LogInformation("Deletando cliente: {CustomerId}", customerData.Id);
                    await ProcessCustomerDeletion(customerData.Id, ct);
                    break;
            }

            // Confirma o processamento bem-sucedido
            await ctx.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao processar mensagem do cliente {CustomerId}", payload.data.Id);

            // Rejeita a mensagem sem requeue (evita loop infinito)
            await ctx.NackAsync(requeue: false);
        }
    }

    private async Task ProcessCustomerCreation(CustomerData customer, CancellationToken ct)
    {
        // Sua lógica de criação aqui
        await Task.Delay(100, ct); // Simula processamento
    }

    private async Task ProcessCustomerUpdate(CustomerData customer, CancellationToken ct)
    {
        // Sua lógica de atualização aqui
        await Task.Delay(100, ct); // Simula processamento
    }

    private async Task ProcessCustomerDeletion(long customerId, CancellationToken ct)
    {
        // Sua lógica de deleção aqui
        await Task.Delay(100, ct); // Simula processamento
    }
}
````

## 📚 Exemplos Práticos

### Handler Simples (Apenas Log)
````csharp
public sealed class LogHandler(ILogger<LogHandler> logger) : MessageHandler<LogMessage>
{
    public override string QueueName => "logs.queue"; 
    public override Dictionary<string, object?>? QueueArgs => null; 
    public override IEnumerable<string> Patterns => [".log."];
    public override async Task HandleAsync(RmqContext ctx, LogMessage payload, CancellationToken ct)
    {
        logger.LogInformation("Log recebido: {Message}", payload.message);
        await ctx.AckAsync();
    }
}
````

### Handler com Configuração de Fila Avançada
````csharp
public sealed class PaymentHandler : MessageHandler<PaymentMessage>
{
    public override string QueueName => "payment.queue";
    // Configuração avançada da fila
    public override Dictionary<string, object?>? QueueArgs => new()
    {
        ["x-message-ttl"] = 300000, // TTL de 5 minutos
        ["x-dead-letter-exchange"] = "payment.dlx",
        ["x-max-retries"] = 3
    };

    public override IEnumerable<string> Patterns =>
    [
        "payment.cmd.process",
        "payment.evt.completed",
        "payment.evt.failed"
    ];

    public override async Task HandleAsync(RmqContext ctx, PaymentMessage payload, CancellationToken ct)
    {
        // Sua lógica de processamento aqui
        await ctx.AckAsync();
    }
}
````

## 🐰 Configuração do RabbitMQ

### Docker Compose (Recomendado para desenvolvimento)
````yaml
version: "3.8"
services:
    rabbitmq:
        image: rabbitmq:3-management
        container_name: rabbitmq
        ports:
            - "5672:5672" # AMQP port
            - "15672:15672" # Management UI
        environment:
            RABBITMQ_DEFAULT_USER: guest
            RABBITMQ_DEFAULT_PASS: guest
        volumes:
            - rabbitmq_data:/var/lib/rabbitmq
volumes:
    rabbitmq_data:
````

### Acessar o Management UI
Após iniciar o RabbitMQ, acesse: http://localhost:15672
- **Usuário**: guest
- **Senha**: guest

## 🎯 Boas Práticas

### 1. Nomenclatura de Routing Keys

#### Eventos (algo que aconteceu)

`customer.evt.created`<br>
`order.evt.shipped`<br>
`payment.evt.completed`

#### Comandos (ação a ser executada)

`customer.cmd.create`<br>
`order.cmd.ship`<br>
`payment.cmd.process`

### 2. Tratamento de Erros
````csharp
public override async Task HandleAsync(RmqContext ctx, MinhaMessage payload, CancellationToken ct)
{
    try
    { 
        // Sua lógica aqui
        await ProcessMessage(payload, ct); 
        await ctx.AckAsync();
        // Sucesso
    }
    catch (BusinessException ex)
    {
        logger.LogWarning(ex, "Erro de negócio - mensagem rejeitada");
        await ctx.NackAsync(requeue: false);
        // Não tenta novamente
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro inesperado - mensagem será tentada novamente");
        await ctx.NackAsync(requeue: true);
        // Tenta novamente
    }
}
````

### 3. Logging Estruturado
````csharp
logger.LogInformation("Processando pedido {OrderId} do cliente {CustomerId}", order.Id, order.CustomerId);
````

## 🔧 Troubleshooting

### Problema: Handler não está sendo chamado
**Soluções:**
1. Verifique se o handler está no assembly correto
2. Confirme se a routing key está correta
3. Verifique os logs para erros de conexão
4. Confirme se o RabbitMQ está rodando

### Problema: Mensagens ficam em loop
**Soluções:**
1. Sempre chame `ctx.AckAsync()` ou `ctx.NackAsync()`
2. Use `requeue: false` em erros não recuperáveis
3. Implemente dead letter queues para mensagens problemáticas

### Problema: Erro de deserialização
**Soluções:**
1. Verifique se o DTO corresponde ao formato da mensagem
2. Confirme se os nomes das propriedades estão corretos
3. Use JsonPropertyName se necessário

## 📞 Suporte

Para dúvidas ou problemas:
1. Verifique os logs da aplicação
2. Consulte o Management UI do RabbitMQ
3. Verifique a documentação do RabbitMQ

---

**Desenvolvido com ❤️ usando .NET 9 e RabbitMQ**