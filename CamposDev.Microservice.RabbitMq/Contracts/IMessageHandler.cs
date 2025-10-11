namespace CamposDev.Microservice.RabbitMq.Contracts;

using CamposDev.Microservice.RabbitMq.Messaging;

/// <summary>
/// Interface que define um manipulador de mensagens para processamento de eventos via RabbitMQ.
/// Implementações desta interface são automaticamente registradas como handlers de eventos.
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    /// Nome da fila onde o handler irá escutar as mensagens.
    /// Este nome deve ser único para cada tipo de processamento.
    /// </summary>
    /// <example>"ticket.queue", "customer.queue", "payment.queue"</example>
    string QueueName { get; } 
    
    /// <summary>
    /// Argumentos opcionais para configuração da fila RabbitMQ.
    /// Pode ser usado para definir TTL, dead letter exchanges, etc.
    /// Retorne null se não precisar de argumentos especiais.
    /// </summary>
    /// <example>
    /// new Dictionary&lt;string, object?&gt; 
    /// {
    ///     ["x-message-ttl"] = 60000, // TTL de 60 segundos
    ///     ["x-dead-letter-exchange"] = "dlx.exchange"
    /// }
    /// </example>
    Dictionary<string, object?>? QueueArgs { get; }
    
    /// <summary>
    /// Padrões de routing keys que este handler deve processar.
    /// O handler será acionado quando uma mensagem com routing key correspondente for recebida.
    /// Suporta múltiplos padrões para um mesmo handler.
    /// </summary>
    /// <example>["customers.evt.create", "customers.evt.update", "customers.cmd.delete"]</example>
    IEnumerable<string> Patterns { get; }
    
    /// <summary>
    /// Tipo do objeto que representa o payload da mensagem.
    /// Este tipo será usado para deserialização automática da mensagem.
    /// </summary>
    /// <example>typeof(CustomerCreatedEvent), typeof(PaymentProcessedMessage)</example>
    Type PayloadType { get; }
    
    /// <summary>
    /// Método principal que processa a mensagem recebida.
    /// É chamado automaticamente quando uma mensagem correspondente aos padrões é recebida.
    /// </summary>
    /// <param name="ctx">Contexto da mensagem RabbitMQ com informações de delivery e métodos para ACK/NACK</param>
    /// <param name="payload">Objeto deserializado da mensagem, do tipo especificado em PayloadType</param>
    /// <param name="ct">Token de cancelamento para operações assíncronas</param>
    /// <returns>Task que representa a operação assíncrona de processamento</returns>
    /// <remarks>
    /// - Use ctx.AckAsync() para confirmar o processamento bem-sucedido
    /// - Use ctx.NackAsync() para rejeitar a mensagem (com ou sem requeue)
    /// - Acesse ctx.RoutingKey para obter a routing key específica da mensagem
    /// - Implemente tratamento de exceções adequado
    /// </remarks>
    Task HandleAsync(RmqContext ctx, object payload, CancellationToken ct);
}