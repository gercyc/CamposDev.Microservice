# CamposDev.Tuya

Biblioteca .NET 9 para descoberta e leitura de dispositivos Tuya via **protocolo LAN 55AA** — sem dependência da nuvem Tuya.

## Funcionalidades

| Serviço | Descrição |
|---------|-----------|
| `TuyaLanDiscoveryService` | Escuta broadcasts UDP na porta 6667 e mantém um inventário de dispositivos Tuya ativos na rede |
| `TuyaLanListenerService` | Ao detectar o wake-up de um device alvo, conecta via TCP (porta 6668), negocia sessão protocolo 3.4 e lê os DPs diretamente |
| `TuyaTokenService` | Gerencia tokens de acesso à Tuya OpenAPI (obtenção + renovação via refresh token) |

## Registro no DI

A forma recomendada de registrar todos os serviços é através da extensão `AddTuyaLan`:

```csharp
// Requer que IMqttBrokerService já esteja registrado
builder.Services.AddTuyaLan(builder.Configuration);
```

O método lê a seção `TuyaApi` do `appsettings.json` por padrão. Para usar outra seção:

```csharp
builder.Services.AddTuyaLan(builder.Configuration, configSection: "MeuSensorTuya");
```

> **Pré-requisito:** `IMqttBrokerService` (de `CamposDev.Mqtt`) deve estar registrado antes de chamar `AddTuyaLan`, pois `TuyaLanListenerService` depende dele para publicar as leituras via MQTT.

### Registro manual (granular)

```csharp
// Registrar apenas o token service + discovery (sem listener MQTT)
builder.Services.AddSingleton<TuyaApiOptions>(
    builder.Configuration.GetSection("TuyaApi").Get<TuyaApiOptions>()!);
builder.Services.AddSingleton<TuyaTokenService>();
builder.Services.AddSingleton<TuyaLanDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TuyaLanDiscoveryService>());
```

## Configuração (`appsettings.json`)

```json
"TuyaApi": {
  "Endpoint":     "https://openapi.tuyaus.com",
  "AccessId":     "<seu_access_id>",
  "AccessSecret": "<seu_access_secret>",
  "DeviceId":     "eb169bae589e44c6b3fieg",
  "DeviceName":   "T&H Sensor Mobile",
  "DeviceModel":  "RMW002",
  "LocalKey":     "5k{2C++If5XRin.}",
  "DeviceIp":     "192.168.101.109",
  "LocalBindIp":  "192.168.101.149"
}
```

| Campo | Obrigatório | Descrição |
|-------|-------------|-----------|
| `DeviceId` | Sim | ID do dispositivo na Tuya (gwId) |
| `LocalKey` | Para LAN | Chave AES-128 local do device. Sem ela, `TuyaLanListenerService` desativa-se |
| `DeviceIp` | Para LAN | IP do device — filtra qual wake-up UDP dispara a leitura TCP |
| `LocalBindIp` | Recomendado | Interface de rede para receber broadcasts UDP. Em hosts com múltiplas NICs, `0.0.0.0` pode não receber os broadcasts |
| `Endpoint` | Para API | URL base da Tuya OpenAPI (usado apenas pelo `TuyaTokenService`) |
| `AccessId` / `AccessSecret` | Para API | Credenciais da Tuya OpenAPI |

## Modelos

```csharp
record TuyaDevice(string GwId, string Ip, string ProductKey, string Version, bool Encrypt, DateTimeOffset SeenAt);

record SensorReading(
    double Temperature, double Humidity,
    string BatteryState, int BatteryPercent,
    string TempUnit, double HeatIndex,
    string ComfortLevel, DateTimeOffset MeasuredAt);

record TuyaToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
```

## Protocolo LAN 55AA (resumo)

```
UDP :6667  →  TuyaLanDiscoveryService  →  evento OnDeviceSeen
                       │ (a cada wake-up do device alvo)
                       ▼
              TuyaLanListenerService
                       │
                       ▼  TCP :6668 (timeout 15s)
              Negociação protocolo 3.4
              CMD 3 → CMD 4 → CMD 5  (session key)
                       │
                       ▼
              CMD 16 DP_QUERY_NEW
                       │ até 4 tentativas (pacotes intermediários descartados)
                       ▼
              DPs JSON → SensorReading → MQTT retain
```

- **UDP broadcast**: payload cifrado com `MD5("yGAdlopoPVldABfn")` (nonce hardcoded Tuya)
- **TCP protocolo 3.4**: payload cifrado com AES-128-ECB; integridade via HMAC-SHA256
- **Session key**: `AES-ECB(local_nonce XOR remote_nonce, local_key)` sem padding
- **Tópico MQTT publicado**: `sensor/tuya/th/{deviceId}`

## Dependências

- `CamposDev.Mqtt` — `IMqttBrokerService` para publicação MQTT
- `Microsoft.Extensions.Hosting` — `BackgroundService`
- `Microsoft.Extensions.Options` — injeção de configuração
