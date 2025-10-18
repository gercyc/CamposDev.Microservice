# CamposDEV.Mqtt - Cliente MQTT Resiliente para .NET

Uma biblioteca .NET 9 para comunicação MQTT robusta e confiável, com suporte a reconexão automática, persistência de mensagens offline e retry inteligente.

## 📋 Características

- ✅ **Reconexão Automática**: Reconecta automaticamente quando a conexão com o broker é perdida
- ✅ **Persistência Offline**: Armazena mensagens localmente quando o broker está indisponível
- ✅ **Retry Inteligente**: Sistema de retry automático para mensagens falhadas
- ✅ **Logging Estruturado**: Integração completa com `ILogger`
- ✅ **Configuração Flexível**: Configuração via `IOptions<T>` e appsettings.json
- ✅ **Thread-Safe**: Operações seguras para ambientes multi-thread
- ✅ **.NET 9**: Otimizado para a versão mais recente do .NET

## 🚀 Instalação
`dotnet add package CamposDEV.Mqtt`

## ⚙️ Configuração

### 1. Configure o appsettings.json
```json
{
    "MqttBrokerSettings": {
        "BrokerHost": "localhost",
        "BrokerPort": 1883,
        "Topic": "default/topic",
        "RetryIntervalMinutes": 5,
        "NormalIntervalSeconds": 30
    }
}
```


### 2. Registre o serviço no Program.cs

```csharp
using CamposDEV.Mqtt.Services;
using CamposDEV.Mqtt.Settings;
var builder = Host.CreateApplicationBuilder(args);
// Configura as opções do MQTT
builder.Services.Configure<MqttBrokerSettings>(builder.Configuration.GetSection("MqttBrokerSettings"));
// Registra o serviço MQTT
builder.Services.AddSingleton<IMqttBrokerService, MqttBrokerService>();
var app = builder.Build();
// Inicializa o cliente MQTT
var mqttService = app.Services.GetRequiredService<IMqttBrokerService>();
await mqttService.InitializeAsync();
app.Run();
```

## 💡 Como Usar

### Publicando Mensagens

```csharp
public class MeuWorkerService : BackgroundService
{
    private readonly IMqttBrokerService _mqttService;
    private readonly ILogger<MeuWorkerService> _logger;

    public MeuWorkerService(IMqttBrokerService mqttService, ILogger<MeuWorkerService> logger)
    {
        _mqttService = mqttService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dados = new
            {
                Timestamp = DateTime.UtcNow,
                Temperatura = Random.Shared.Next(15, 35),
                Umidade = Random.Shared.Next(40, 80)
            };

            var json = JsonSerializer.Serialize(dados);

            // Publica a mensagem - se o broker estiver offline, 
            // a mensagem será armazenada e enviada quando reconectar
            await _mqttService.PublishAsync("sensores/temperatura", json);

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

### Exemplo com Diferentes Tópicos
#### Mensagem simples 
````csharp
await _mqttService.PublishAsync("logs/info", "Sistema inicializado");
````
#### Mensagem com retain (última mensagem será mantida pelo broker) 
````csharp
await _mqttService.PublishAsync("status/sistema", "online", retain: true);
````
#### Mensagem JSON estruturada 
````csharp
var evento = new
{
    EventoId = Guid.NewGuid(),
    Tipo = "PedidoCriado",
    Dados = new { PedidoId = 12345, ClienteId = "ABC123" }
};

await _mqttService.PublishAsync("eventos/pedidos", JsonSerializer.Serialize(evento));
````

## 🔧 Configurações Avançadas

### Propriedades do MqttBrokerSettings

| Propriedade | Tipo | Descrição | Padrão |
|-------------|------|-----------|---------|
| `BrokerHost` | `string` | Endereço do broker MQTT | `localhost` |
| `BrokerPort` | `int` | Porta do broker | `1883` |
| `Topic` | `string` | Tópico padrão para publicação | - |
| `RetryIntervalMinutes` | `int` | Intervalo entre tentativas de reconexão (minutos) | `5` |
| `NormalIntervalSeconds` | `int` | Intervalo normal entre operações (segundos) | `30` |

### Exemplo de Configuração Avançada

```json
{
    "MqttBrokerSettings": {
        "BrokerHost": "mqtt.meuservidor.com",
        "BrokerPort": 8883,
        "Topic": "empresa/dispositivos/sensores",
        "RetryIntervalMinutes": 2,
        "NormalIntervalSeconds": 15
    }
}
```

## 🛡️ Recursos de Resiliência

### 1. **Persistência Offline**
Quando o broker está indisponível, as mensagens são automaticamente:
- Armazenadas em uma fila na memória
- Persistidas em arquivo JSON (`pending_messages.json`)
- Reenviadas automaticamente quando a conexão é restaurada

### 2. **Reconexão Automática**
- Detecta automaticamente perda de conexão
- Tenta reconectar em intervalos configuráveis
- Continua funcionando mesmo com instabilidade de rede

### 3. **Monitoramento de Status**

````csharp
// Verifique o status da conexão 
if (_mqttService is MqttBrokerService service)
{
    if (service.IsConnected) { _logger.LogInformation("MQTT conectado"); }
    else { _logger.LogWarning("MQTT desconectado - mensagens sendo enfileiradas"); }
}
````

## 📊 Logs e Monitoramento

A biblioteca gera logs estruturados que ajudam no monitoramento:
```
[INFO] Inicializando conexão MQTT... 
[INFO] Conectado ao broker MQTT com sucesso! 
[INFO] Mensagem publicada em sensores/temperatura: {"Timestamp":"2024-01-01T10:00:00Z"...} 
[WARN] Falha no envio: ConnectionLost [INFO] Tentando reconectar MQTT (1/2)... 
[INFO] Restauradas 3 mensagens pendentes do disco.
```

## 🔗 Compatibilidade

- **.NET 9.0** ou superior
- Baseado na biblioteca **HiveMQtt**
- Compatível com qualquer broker MQTT (Mosquitto, AWS IoT, Azure IoT Hub, etc.)

## 📝 Exemplo Completo

````csharp
//Program.cs
using CamposDEV.Mqtt.Services;
using CamposDEV.Mqtt.Settings;
using System.Text.Json;
var builder = Host.CreateApplicationBuilder(args);
// Configuração do MQTT
builder.Services.Configure<MqttBrokerSettings>(builder.Configuration.GetSection("MqttBrokerSettings"));
builder.Services.AddSingleton<IMqttBrokerService, MqttBrokerService>();
builder.Services.AddHostedService<SensorWorker>();
var app = builder.Build();
// Inicializa MQTT na startup var mqttService = app.Services.GetRequiredService<IMqttBrokerService>(); await mqttService.InitializeAsync();
app.Run();


// Worker Service para envio de dados
public class SensorWorker : BackgroundService
{
    private readonly IMqttBrokerService _mqttService;
    private readonly ILogger<SensorWorker> _logger;
    public SensorWorker(IMqttBrokerService mqttService, ILogger<SensorWorker> logger)
    {
        _mqttService = mqttService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dados = new
            {
                DeviceId = Environment.MachineName,
                Timestamp = DateTime.UtcNow,
                Temperature = Random.Shared.Next(15, 35),
                Humidity = Random.Shared.Next(40, 80),
                Status = "online"
            };

            await _mqttService.PublishAsync(
                $"devices/{dados.DeviceId}/telemetry",
                JsonSerializer.Serialize(dados)
            );

            _logger.LogInformation("Dados do sensor enviados: {Temperature}°C", dados.Temperature);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
````


## 🆘 Troubleshooting

### Problema: Mensagens não estão sendo enviadas
**Soluções:**
1. Verifique se `InitializeAsync()` foi chamado na startup
2. Confirme as configurações do broker no appsettings.json
3. Verifique os logs para erros de conexão
4. Teste a conectividade de rede com o broker

### Problema: Reconexão não está funcionando
**Soluções:**
1. Verifique se `RetryIntervalMinutes` não está muito alto
2. Confirme se o broker está aceitando conexões
3. Verifique se não há firewall bloqueando a conexão

### Problema: Muitas mensagens pendentes
**Soluções:**
1. Verifique a estabilidade da conexão de rede
2. Considere reduzir `NormalIntervalSeconds`
3. Monitore o arquivo `pending_messages.json`

## 📞 Suporte

- **GitHub**: [CamposDev.Microservice](https://github.com/gercyc/CamposDev.Microservice)
- **Website**: [camposdev.ind.br](https://camposdev.ind.br)

## 📄 Licença

Este projeto está licenciado sob a [Licença MIT](LICENSE).

---

**Desenvolvido com ❤️ usando .NET 9 e HiveMQtt**