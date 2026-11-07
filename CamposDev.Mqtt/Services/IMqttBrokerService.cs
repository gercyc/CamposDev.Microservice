namespace CamposDEV.Mqtt.Services
{
    /// <summary>
    /// Provides an abstraction for publishing messages to an MQTT broker.
    /// </summary>
    public interface IMqttBrokerService
    {
        /// <summary>
        /// Initializes the MQTT client and attempts to connect to the broker.
        /// This method should be called once during application startup.
        /// </summary>
        /// <param name="cancellationToken">A token that can be used to cancel the initialization.</param>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes a message to the specified MQTT topic.
        /// If the broker is offline, the message is persisted locally and retried later.
        /// </summary>
        /// <param name="topic">The MQTT topic to which the message will be published.</param>
        /// <param name="payload">The message payload as a string.</param>
        /// <param name="retain">
        /// Optional. If set to <c>true</c>, the broker will retain the message for future subscribers.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the publish operation.</param>
        Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribes to a topic filter (MQTT wildcards <c>+</c> and <c>#</c> are supported).
        /// The subscription is restored automatically after every reconnection.
        /// </summary>
        /// <param name="topicFilter">The MQTT topic filter, e.g. <c>devices/+/set</c>.</param>
        /// <param name="handler">
        /// Invoked with the concrete topic and the payload as string for each matching message.
        /// Exceptions thrown by the handler are logged and do not affect the connection.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the subscribe operation.</param>
        /// <remarks>
        /// Added in 1.1.0. The default implementation keeps existing implementers of this
        /// interface (mocks, decorators) compiling; they must override it to support subscriptions.
        /// </remarks>
        Task SubscribeAsync(string topicFilter, Func<string, string, Task> handler, CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"{GetType().Name} does not support subscriptions.");
    }
}
