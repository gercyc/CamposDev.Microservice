namespace CamposDEV.Mqtt.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using CamposDEV.Mqtt.Settings;
using HiveMQtt.Client;
using HiveMQtt.Client.Events;
using HiveMQtt.Client.Options;
using HiveMQtt.MQTT5.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class MqttBrokerService : IMqttBrokerService
{
    public MqttBrokerSettings Settings { get; }

    private HiveMQClient? _client;
    private const string AvailabilityOnline  = "online";
    private const string AvailabilityOffline = "offline";

    private readonly ConcurrentQueue<(string Message, string Topic, bool Retain)> _messageQueue = new();
    // Filtro de tópico → handler; restaurado a cada reconexão.
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _subscriptions = new();

    // Serializa conexão e pós-conexão: publicadores concorrentes não disputam o ConnectAsync.
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Última tarefa despachada por tópico: mensagens do mesmo tópico rodam na ordem de chegada
    // (ex.: ON seguido de OFF), tópicos diferentes em paralelo.
    private readonly Dictionary<string, Task> _dispatchTails = new();
    private readonly object _lock = new();
    private readonly ILogger<MqttBrokerService> _logger;
    private readonly string _persistFilePath;
    private bool _initialized = false;
    private bool _isConnected = false;
    private DateTime _lastSuccessfulCommunication = DateTime.MinValue;
    private DateTime lastSuccessTime = DateTime.UtcNow;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _isConnected;
            }
        }
    }

    public MqttBrokerService(IOptions<MqttBrokerSettings> options, ILogger<MqttBrokerService> logger)
    {
        Settings = options.Value;
        _logger = logger;
        _persistFilePath = Path.Combine(AppContext.BaseDirectory, "pending_messages.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        lock (_lock)
        {
            if (_initialized)
                return;

            var clientOptions = new HiveMQClientOptions
            {
                Host = Settings.BrokerHost,
                Port = Settings.BrokerPort,
                // Sem isso, um consumidor que só assina nunca se recupera de uma queda:
                // a reconexão manual só acontece dentro de um publish.
                AutomaticReconnect = true
            };

            if (!string.IsNullOrWhiteSpace(Settings.AvailabilityTopic))
                clientOptions.LastWillAndTestament = new LastWillAndTestament(
                    Settings.AvailabilityTopic, AvailabilityOffline, QualityOfService.AtLeastOnceDelivery, true);

            _client = new HiveMQClient(clientOptions);
            _client.OnMessageReceived += OnMessageReceived;
            _client.AfterConnect      += OnAfterConnect;
            _initialized = true;
        }

        _logger.LogInformation("Inicializando conexão MQTT...");
        await TryConnectAndSendPendingAsync();
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        await PublishWithRetryAsync(payload, topic, retain);
    }

    public async Task SubscribeAsync(string topicFilter, Func<string, string, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);
        ArgumentNullException.ThrowIfNull(handler);

        _subscriptions[topicFilter] = handler;

        // Desconectado: a assinatura fica registrada e é feita na próxima conexão.
        if (_client?.IsConnected() == true)
            await SubscribeOnBrokerAsync(topicFilter);
    }

    private async Task<bool> PublishWithRetryAsync(string message, string? topic = null, bool retain = false)
    {
        topic ??= Settings.Topic;
        const int timeoutSeconds = 5;

        if (_client == null)
        {
            _logger.LogWarning("Cliente MQTT ainda não inicializado.");
            EnqueueAndPersist(message, topic, retain);
            return false;
        }

        try
        {
            if (!_client.IsConnected() || (DateTime.UtcNow - _lastSuccessfulCommunication).TotalSeconds > timeoutSeconds)
            {
                _logger.LogInformation("Verificando conexão com o broker MQTT...");
                var connected = await CheckRealConnectionAsync();
                lock (_lock)
                {
                    _isConnected = connected;
                }

                if (!connected)
                {
                    EnqueueAndPersist(message, topic, retain);
                    return false;
                }
            }

            var publishMessage = new MQTT5PublishMessage(topic, QualityOfService.AtLeastOnceDelivery)
            {
                PayloadAsString = message,
                Retain          = retain
            };
            var result = await _client.PublishAsync(publishMessage);
            // Com QoS 1 o broker responde NoMatchingSubscribers quando ninguém assina o tópico:
            // a mensagem foi aceita (e retida, se for o caso), então não é falha.
            var reasonCode = result.ReasonCode();
            if (reasonCode == (int)HiveMQtt.MQTT5.ReasonCodes.PubAckReasonCode.Success ||
                reasonCode == (int)HiveMQtt.MQTT5.ReasonCodes.PubAckReasonCode.NoMatchingSubscribers)
            {
                _lastSuccessfulCommunication = DateTime.UtcNow;
                lastSuccessTime = DateTime.UtcNow;
                _logger.LogInformation($"Mensagem publicada em {topic}: {message}");
                return true;
            }

            _logger.LogWarning($"Falha no envio: {result.ReasonCode()}");
            EnqueueAndPersist(message, topic, retain);
            SetDisconnected("Desconectado após falha no envio.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Erro ao publicar no MQTT: {ex.Message}");
            EnqueueAndPersist(message, topic, retain);
            SetDisconnected("Erro de publicação.");
            return false;
        }
    }

    private async Task TryConnectAndSendPendingAsync()
    {
        if (_client == null)
            return;

        try
        {
            _logger.LogInformation($"Tentando conectar ao broker em {Settings.BrokerHost}:{Settings.BrokerPort}...");
            var connectResult = await _client.ConnectAsync();

            if (connectResult.ReasonCode == HiveMQtt.MQTT5.ReasonCodes.ConnAckReasonCode.Success)
            {
                lock (_lock)
                {
                    _isConnected = true;
                }

                _logger.LogInformation("Conectado ao broker MQTT com sucesso!");

                await RestorePendingMessagesAsync(); //Carrega mensagens do JSON
                await SendAllPendingAsync();          //Envia tudo
            }
            else
            {
                SetDisconnected($"Falha ao conectar: {connectResult.ReasonCode}");
            }
        }
        catch (Exception ex)
        {
            SetDisconnected($"Erro ao conectar: {ex.Message}");
        }
    }

    private void SetDisconnected(string reason)
    {
        lock (_lock)
        {
            _isConnected = false;
        }

        _logger.LogWarning(reason);
    }

    private async Task<bool> CheckRealConnectionAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            return await CheckRealConnectionLockedAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<bool> CheckRealConnectionLockedAsync()
    {
        const int maxAttempts = 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation($"Tentando reconectar MQTT ({attempt}/{maxAttempts})...");
                if (!_client!.IsConnected())
                {
                    var connectResult = await _client.ConnectAsync();
                    if (connectResult.ReasonCode != HiveMQtt.MQTT5.ReasonCodes.ConnAckReasonCode.Success)
                        continue;
                }

                if (_client.IsConnected())
                {
                    _lastSuccessfulCommunication = DateTime.UtcNow;
                    return true;
                }
            }
            catch
            {
                // Ignora e tenta novamente
            }
        }

        return false;
    }

    /// <summary>
    /// Após cada (re)conexão: anuncia disponibilidade e refaz as assinaturas,
    /// que o broker descarta junto com a sessão anterior.
    /// </summary>
    private async Task OnConnectedAsync()
    {
        if (!string.IsNullOrWhiteSpace(Settings.AvailabilityTopic))
        {
            try
            {
                var online = new MQTT5PublishMessage(Settings.AvailabilityTopic, QualityOfService.AtLeastOnceDelivery)
                {
                    PayloadAsString = AvailabilityOnline,
                    Retain          = true
                };
                await _client!.PublishAsync(online);
            }
            catch (Exception ex)
            {
                // Não impede a restauração das assinaturas logo abaixo.
                _logger.LogError(ex, "Falha ao publicar disponibilidade em {Topic}", Settings.AvailabilityTopic);
            }
        }

        foreach (var topicFilter in _subscriptions.Keys)
            await SubscribeOnBrokerAsync(topicFilter);
    }

    private void OnAfterConnect(object? sender, AfterConnectEventArgs args)
    {
        if (args.ConnectResult?.ReasonCode != HiveMQtt.MQTT5.ReasonCodes.ConnAckReasonCode.Success)
            return;

        // O evento roda dentro do fluxo de conexão do cliente: publicar ou assinar aqui,
        // de forma síncrona, competiria com o próprio ConnectAsync.
        _ = Task.Run(async () =>
        {
            try
            {
                await OnConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no pós-conexão MQTT");
            }
        });
    }

    private async Task SubscribeOnBrokerAsync(string topicFilter)
    {
        try
        {
            var result  = await _client!.SubscribeAsync(topicFilter, QualityOfService.AtLeastOnceDelivery);
            var granted = result.Subscriptions.All(sub =>
                (int)sub.SubscribeReasonCode <= (int)HiveMQtt.MQTT5.ReasonCodes.SubAckReasonCode.GrantedQoS2);

            if (granted)
                _logger.LogInformation("Assinado o tópico {TopicFilter}", topicFilter);
            else
                _logger.LogError("Broker recusou a assinatura de {TopicFilter}", topicFilter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao assinar o tópico {TopicFilter}", topicFilter);
        }
    }

    private void OnMessageReceived(object? sender, OnMessageReceivedEventArgs args)
    {
        var topic   = args.PublishMessage.Topic ?? "";
        var payload = args.PublishMessage.PayloadAsString ?? "";

        foreach (var (filter, handler) in _subscriptions)
        {
            if (!TopicMatches(filter, topic)) continue;

            Dispatch(topic, () => handler(topic, payload));
        }
    }

    /// <summary>
    /// Encadeia o handler atrás do anterior do mesmo tópico. O evento roda na thread de
    /// recepção do cliente, então o handler segue em segundo plano para não travar a leitura.
    /// </summary>
    private void Dispatch(string topic, Func<Task> work)
    {
        lock (_dispatchTails)
        {
            var previous = _dispatchTails.GetValueOrDefault(topic, Task.CompletedTask);
            var next = previous.ContinueWith(async _ =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro no handler do tópico {Topic}", topic);
                }
            }, TaskScheduler.Default).Unwrap();

            _dispatchTails[topic] = next;
        }
    }

    /// <summary>Casa um tópico concreto com um filtro MQTT (<c>+</c> = um nível, <c>#</c> = o resto).</summary>
    internal static bool TopicMatches(string filter, string topic)
    {
        var filterLevels = filter.Split('/');
        var topicLevels  = topic.Split('/');

        for (var i = 0; i < filterLevels.Length; i++)
        {
            if (filterLevels[i] == "#") return true;
            if (i >= topicLevels.Length) return false;
            if (filterLevels[i] != "+" && filterLevels[i] != topicLevels[i]) return false;
        }

        return filterLevels.Length == topicLevels.Length;
    }

    private void EnqueueAndPersist(string message, string topic, bool retain)
    {
        _messageQueue.Enqueue((message, topic, retain));
        PersistPendingMessagesAsync().ConfigureAwait(false);
    }

    private async Task PersistPendingMessagesAsync()
    {
        try
        {
            var list = _messageQueue.ToArray()
                .Select(x => new PendingMessage { Message = x.Message, Topic = x.Topic, Retain = x.Retain })
                .ToList();

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_persistFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Erro ao persistir mensagens pendentes: {ex.Message}");
        }
    }

    private async Task RestorePendingMessagesAsync()
    {
        if (!File.Exists(_persistFilePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_persistFilePath);
            var list = JsonSerializer.Deserialize<List<PendingMessage>>(json) ?? new();
            foreach (var item in list)
                _messageQueue.Enqueue((item.Message, item.Topic, item.Retain));

            File.Delete(_persistFilePath);
            _logger.LogInformation($"Restauradas {list.Count} mensagens pendentes do disco.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Erro ao restaurar mensagens pendentes: {ex.Message}");
        }
    }

    private async Task SendAllPendingAsync()
    {
        // Uma falha reenfileira a mensagem: drenar até esvaziar viraria laço infinito
        // com o broker fora. Envia só o que já estava na fila e para no primeiro erro.
        var pending = _messageQueue.Count;
        for (var i = 0; i < pending && _messageQueue.TryDequeue(out var tuple); i++)
        {
            if (!await PublishWithRetryAsync(tuple.Message, tuple.Topic, tuple.Retain))
                break;
        }
    }

    private record PendingMessage
    {
        public string Message { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public bool Retain { get; set; }
    }
}