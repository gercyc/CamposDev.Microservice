namespace CamposDEV.Mqtt.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using CamposDEV.Mqtt.Settings;
using HiveMQtt.Client;
using HiveMQtt.Client.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class MqttBrokerService : IMqttBrokerService
{
    public MqttBrokerSettings Settings { get; }

    private HiveMQClient? _client;
    private readonly ConcurrentQueue<(string Message, string Topic)> _messageQueue = new();
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
                Port = Settings.BrokerPort
            };
            _client = new HiveMQClient(clientOptions);
            _initialized = true;
        }

        _logger.LogInformation("Inicializando conexão MQTT...");
        await TryConnectAndSendPendingAsync();
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        await PublishWithRetryAsync(payload, topic);
    }

    private async Task<bool> PublishWithRetryAsync(string message, string? topic = null)
    {
        topic ??= Settings.Topic;
        const int timeoutSeconds = 5;

        if (_client == null)
        {
            _logger.LogWarning("Cliente MQTT ainda não inicializado.");
            EnqueueAndPersist(message, topic);
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
                    EnqueueAndPersist(message, topic);
                    return false;
                }
            }

            var result = await _client.PublishAsync(topic, message);
            if (result.ReasonCode() == (int)HiveMQtt.MQTT5.ReasonCodes.PubAckReasonCode.Success)
            {
                _lastSuccessfulCommunication = DateTime.UtcNow;
                lastSuccessTime = DateTime.UtcNow;
                _logger.LogInformation($"Mensagem publicada em {topic}: {message}");
                return true;
            }

            _logger.LogWarning($"Falha no envio: {result.ReasonCode()}");
            EnqueueAndPersist(message, topic);
            SetDisconnected("Desconectado após falha no envio.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Erro ao publicar no MQTT: {ex.Message}");
            EnqueueAndPersist(message, topic);
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

    private void EnqueueAndPersist(string message, string topic)
    {
        _messageQueue.Enqueue((message, topic));
        PersistPendingMessagesAsync().ConfigureAwait(false);
    }

    private async Task PersistPendingMessagesAsync()
    {
        try
        {
            var list = _messageQueue.ToArray()
                .Select(x => new PendingMessage { Message = x.Message, Topic = x.Topic })
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
                _messageQueue.Enqueue((item.Message, item.Topic));

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
        while (_messageQueue.TryDequeue(out var tuple))
        {
            await PublishWithRetryAsync(tuple.Message, tuple.Topic);
        }
    }

    private record PendingMessage
    {
        public string Message { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
    }
}