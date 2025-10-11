namespace CamposDev.Microservice.RabbitMq.Messaging;

using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public sealed class RmqContext
{
    public IChannel Channel { get; }
    public BasicDeliverEventArgs Delivery { get; }
    public string RoutingKey => Delivery.RoutingKey;
    public ulong DeliveryTag => Delivery.DeliveryTag;

    public RmqContext(IChannel channel, BasicDeliverEventArgs delivery)
    {
        Channel = channel;
        Delivery = delivery;
    }

    public async Task AckAsync() => await Channel.BasicAckAsync(DeliveryTag, false);
    public async Task NackAsync(bool requeue = false) => await Channel.BasicNackAsync(DeliveryTag, false, requeue);

    public string GetBodyString() => Encoding.UTF8.GetString(Delivery.Body.ToArray());
}