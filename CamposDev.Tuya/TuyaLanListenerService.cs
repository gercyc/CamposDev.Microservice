using CamposDEV.Mqtt.Services;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamposDev.Tuya;

/// <summary>
/// Conecta via TCP LAN (porta 6668) a cada sensor T&amp;H Tuya configurado quando ele acorda
/// (detectado pelo broadcast UDP do <see cref="TuyaLanDiscoveryService"/>), realiza a
/// negociação de sessão protocolo 3.4 e lê os DPs diretamente sem nuvem.
/// Suporta múltiplos dispositivos configurados em <see cref="TuyaApiOptions.Devices"/>.
/// Publica o resultado via MQTT no tópico <c>sensor/tuya/th/{deviceId}</c>.
/// </summary>
public class TuyaLanListenerService(
    TuyaLanDiscoveryService discovery,
    IMqttBrokerService mqttService,
    TuyaApiOptions options,
    ILogger<TuyaLanListenerService> logger)
    : BackgroundService
{
    private const int TcpPort    = 6668;
    private const int TcpTimeout = 5_000; // ms

    // Protocolo 55AA
    private const uint Prefix55AA = 0x000055AA;
    private const uint Suffix55AA = 0x0000AA55;

    // CMDs
    private const uint CmdSessKeyStart  = 3;
    private const uint CmdSessKeyResp   = 4;
    private const uint CmdSessKeyFinish = 5;
    private const uint CmdDpQueryNew    = 16;
    private const uint CmdStatus        = 8;

    // Comandos que NÃO levam prefixo de versão no payload
    private static readonly HashSet<uint> NoPrefixCmds = [3, 4, 5, 9, 10, 16, 18];

    // Tamanho do sufixo de integridade com HMAC-SHA256 (32 bytes HMAC + 4 suffix)
    private const int HmacEndSize = 36;
    private const int HeaderSize  = 16;

    // Throttle por device: gwId → última leitura bem-sucedida
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastQuery = new();
    private static readonly TimeSpan MinQueryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Devices.FindAll(d => !string.IsNullOrWhiteSpace(d.LocalKey));

        if (configured.Count == 0)
        {
            logger.LogWarning(
                "TuyaLanListenerService: nenhum device com LocalKey configurada em TuyaApi.Devices — serviço desativado.");
            return;
        }

        logger.LogInformation(
            "TuyaLanListenerService iniciado — monitorando {Count} device(s): {Ids}",
            configured.Count,
            string.Join(", ", configured.Select(d => d.DeviceId)));

        discovery.OnDeviceSeen += OnDeviceWakeUp;

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        discovery.OnDeviceSeen -= OnDeviceWakeUp;
        logger.LogInformation("TuyaLanListenerService encerrado.");
    }

    private void OnDeviceWakeUp(TuyaDevice device)
    {
        var config = options.FindDevice(device.GwId);
        if (config is null || string.IsNullOrWhiteSpace(config.LocalKey)) return;

        var last = _lastQuery.GetOrAdd(device.GwId, DateTimeOffset.MinValue);
        if (DateTimeOffset.Now - last < MinQueryInterval) return;

        _lastQuery[device.GwId] = DateTimeOffset.Now;
        _ = Task.Run(() => ConnectAndQueryAsync(device, config));
    }

    // -------------------------------------------------------------------------
    // TCP flow: connect → negotiate session key → query DPs → publish
    // -------------------------------------------------------------------------

    private async Task ConnectAndQueryAsync(TuyaDevice device, TuyaDeviceConfig config)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            logger.LogDebug("TCP conectando a {Ip}:{Port} [{Name}]", device.Ip, TcpPort, config.DeviceName);

            using var tcp = new TcpClient();
            tcp.ReceiveTimeout = TcpTimeout;
            tcp.SendTimeout    = TcpTimeout;
            await tcp.ConnectAsync(device.Ip, TcpPort, cts.Token);

            var stream   = tcp.GetStream();
            var localKey = Encoding.UTF8.GetBytes(config.LocalKey);
            var seq      = new SeqNo();

            var sessionKey = await NegotiateSessionKeyAsync(stream, localKey, seq, cts.Token);
            var dps        = await QueryDpsAsync(stream, sessionKey, seq, config.DeviceId, cts.Token);

            var reading = MapDpsToReading(dps);
            if (reading is null) return;

            var payload = JsonSerializer.Serialize(reading, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await mqttService.PublishAsync($"sensor/tuya/th/{config.DeviceId}", payload, retain: true);

            logger.LogInformation(
                "Tuya LAN [{Name}] → Temp: {Temp}°C | Humidade: {Humidity}% | Bateria: {Battery}",
                config.DeviceName, reading.Temperature, reading.Humidity, reading.BatteryState);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Timeout TCP para [{Name}] ({Id}) — device provavelmente voltou a dormir",
                config.DeviceName, config.DeviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na leitura TCP LAN de [{Name}] ({Id})", config.DeviceName, config.DeviceId);
        }
    }

    // -------------------------------------------------------------------------
    // Protocolo 3.4 — negociação de sessão (3 passos)
    // -------------------------------------------------------------------------

    private async Task<byte[]> NegotiateSessionKeyAsync(
        NetworkStream stream, byte[] localKey, SeqNo seq, CancellationToken ct)
    {
        var localNonce = Encoding.UTF8.GetBytes("0123456789abcdef"); // 16 bytes

        // Passo 1 — SESS_KEY_NEG_START
        await SendPacketAsync(stream, BuildPacket(CmdSessKeyStart, localNonce, localKey, seq), ct);
        logger.LogDebug("SessKey: START enviado");

        // Passo 2 — SESS_KEY_NEG_RESP
        var resp = await ReceivePacketAsync(stream, ct);
        if (resp.Cmd != CmdSessKeyResp)
            throw new InvalidOperationException($"Esperado CMD {CmdSessKeyResp}, recebido {resp.Cmd}");

        var decResp      = AesEcbDecrypt(resp.Payload, localKey);
        var remoteNonce  = decResp[..16];
        var theirHmac    = decResp[16..48];
        var expectedHmac = HMACSHA256.HashData(localKey, localNonce);

        if (!theirHmac.SequenceEqual(expectedHmac))
            throw new InvalidOperationException("HMAC do SESS_KEY_NEG_RESP inválido — local_key incorreta?");

        logger.LogDebug("SessKey: RESP recebido, HMAC OK");

        // Passo 3 — SESS_KEY_NEG_FINISH
        var finishPayload = HMACSHA256.HashData(localKey, remoteNonce);
        await SendPacketAsync(stream, BuildPacket(CmdSessKeyFinish, finishPayload, localKey, seq), ct);
        logger.LogDebug("SessKey: FINISH enviado");

        // Derivar session_key: AES-ECB(local_nonce XOR remote_nonce, local_key) sem padding
        var xored = new byte[16];
        for (var i = 0; i < 16; i++) xored[i] = (byte)(localNonce[i] ^ remoteNonce[i]);

        var sessionKey = AesEcbEncryptRaw(xored, localKey);
        logger.LogDebug("SessKey: chave de sessão derivada ({Len} bytes)", sessionKey.Length);
        return sessionKey;
    }

    // -------------------------------------------------------------------------
    // DP_QUERY_NEW (CMD=16) — leitura dos DPs
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, object>> QueryDpsAsync(
        NetworkStream stream, byte[] sessionKey, SeqNo seq, string deviceId, CancellationToken ct)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var jsonObj = new { gwId = deviceId, devId = deviceId, uid = deviceId, t };

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jsonObj));
        await SendPacketAsync(stream, BuildPacket(CmdDpQueryNew, jsonBytes, sessionKey, seq), ct);
        logger.LogDebug("DP_QUERY_NEW enviado para {DeviceId}", deviceId);

        const int maxAttempts = 4;
        JsonElement dpsEl = default;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var resp = await ReceivePacketAsync(stream, ct);

            if (resp.Cmd != CmdStatus && resp.Cmd != CmdDpQueryNew)
            {
                logger.LogDebug("Pacote intermediário ignorado CMD={Cmd}", resp.Cmd);
                continue;
            }

            byte[] plaintext;
            try
            {
                plaintext = AesEcbDecrypt(resp.Payload, sessionKey);
            }
            catch
            {
                logger.LogDebug("Falha ao decifrar pacote CMD={Cmd} len={Len}, ignorando", resp.Cmd, resp.Payload.Length);
                continue;
            }

            var strippedJson = StripVersionHeader(plaintext);
            var json         = Encoding.UTF8.GetString(strippedJson).TrimEnd('\0');

            if (!json.StartsWith('{'))
            {
                logger.LogDebug("Pacote CMD={Cmd} não é JSON válido, ignorando: {Preview}", resp.Cmd, json[..Math.Min(json.Length, 20)]);
                continue;
            }

            logger.LogDebug("DPs recebidos: {Json}", json);

            var root = JsonDocument.Parse(json).RootElement;

            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("dps", out var d1))
                { dpsEl = d1; break; }
            if (root.TryGetProperty("dps", out var d2))
                { dpsEl = d2; break; }

            logger.LogDebug("Resposta sem campo 'dps', ignorando: {Json}", json);
        }

        if (dpsEl.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Nenhuma resposta com DPs válidos após {maxAttempts} tentativas");

        return dpsEl.EnumerateObject().ToDictionary(
            p => p.Name,
            p => (object)(p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                _                   => p.Value.GetRawText()
            }));
    }

    // -------------------------------------------------------------------------
    // Empacotamento 55AA protocolo 3.4 (HMAC-SHA256)
    // -------------------------------------------------------------------------

    private byte[] BuildPacket(uint cmd, byte[] payload, byte[] key, SeqNo seq)
    {
        var toEncrypt = NoPrefixCmds.Contains(cmd)
            ? payload
            : [.. VersionHeader34, .. payload];

        var encrypted = AesEcbEncrypt(toEncrypt, key);
        var length    = (uint)(encrypted.Length + HmacEndSize);

        var header = new byte[HeaderSize];
        WriteUInt32BE(header, 0, Prefix55AA);
        WriteUInt32BE(header, 4, (uint)seq.Next());
        WriteUInt32BE(header, 8, cmd);
        WriteUInt32BE(header, 12, length);

        var dataForHmac = new byte[header.Length + encrypted.Length];
        header.CopyTo(dataForHmac, 0);
        encrypted.CopyTo(dataForHmac, header.Length);

        var hmac        = HMACSHA256.HashData(key, dataForHmac);
        var suffixBytes = new byte[4];
        WriteUInt32BE(suffixBytes, 0, Suffix55AA);

        return [.. dataForHmac, .. hmac, .. suffixBytes];
    }

    // -------------------------------------------------------------------------
    // Recepção de pacote TCP (framing por length field)
    // -------------------------------------------------------------------------

    private async Task<(uint Cmd, byte[] Payload)> ReceivePacketAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, HeaderSize, ct);
        var prefix = ReadUInt32BE(header, 0);
        var cmd    = ReadUInt32BE(header, 8);
        var length = ReadUInt32BE(header, 12);

        if (prefix != Prefix55AA)
            throw new InvalidOperationException($"Prefix inválido: 0x{prefix:X8}");

        var rest = await ReadExactAsync(stream, (int)length, ct);
        // rest = retcode(4) + encrypted_payload + HMAC(32) + suffix(4)
        const int retcodeSize = 4;
        const int suffixSize  = 4;
        const int hmacSize    = 32;
        var payloadEnd = rest.Length - hmacSize - suffixSize;
        var encrypted  = rest[retcodeSize..payloadEnd];

        logger.LogDebug("Recebido CMD={Cmd} len={Len}", cmd, encrypted.Length);
        return (cmd, encrypted);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf    = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new EndOfStreamException("Conexão encerrada pelo device.");
            offset += read;
        }
        return buf;
    }

    private static Task SendPacketAsync(NetworkStream stream, byte[] packet, CancellationToken ct)
        => stream.WriteAsync(packet, ct).AsTask();

    // -------------------------------------------------------------------------
    // Criptografia AES-128-ECB
    // -------------------------------------------------------------------------

    private static byte[] AesEcbEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbEncryptRaw(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = key;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    // -------------------------------------------------------------------------
    // DP → SensorReading
    // -------------------------------------------------------------------------

    private static SensorReading? MapDpsToReading(Dictionary<string, object> dps)
    {
        double? temp = null, humidity = null;
        string? battery = null, unit = null;

        foreach (var (k, v) in dps)
            switch (k)
            {
                case "20": unit    = v.ToString(); break;
                case "27" when v is double t: temp     = t / 10.0; break;
                case "46" when v is double h: humidity = h; break;
                case "101": battery = v.ToString(); break;
            }

        if (temp is null || humidity is null) return null;

        return new SensorReading(
            Temperature:    temp.Value,
            Humidity:       humidity.Value,
            BatteryState:   battery ?? "unknown",
            BatteryPercent: battery switch { "high" => 100, "middle" => 50, "low" => 10, _ => 0 },
            TempUnit:       unit ?? "c",
            HeatIndex:      HeatIndex(temp.Value, humidity.Value),
            ComfortLevel:   ComfortLevel(temp.Value, humidity.Value),
            MeasuredAt:     DateTimeOffset.UtcNow);
    }

    private static double HeatIndex(double t, double h)
    {
        if (t < 27 || h < 40) return Math.Round(t, 1);
        return Math.Round(-8.78469475556 + 1.61139411 * t + 2.33854883889 * h
            - 0.14611605 * t * h - 0.012308094 * t * t - 0.016424828 * h * h
            + 0.002211732 * t * t * h + 0.00072546 * t * h * h
            - 0.000003582 * t * t * h * h, 1);
    }

    private static string ComfortLevel(double t, double h) =>
        (t > 28 && h > 70) ? "Muito desconfortavel" :
        (t > 26 && h > 60) ? "Desconfortavel" :
        (t is >= 20 and <= 26 && h is >= 40 and <= 60) ? "Confortavel" :
        t < 18 ? "Frio" : "Razoavel";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly byte[] VersionHeader34 =
        [.. Encoding.UTF8.GetBytes("3.4"), .. new byte[12]];

    private static byte[] StripVersionHeader(byte[] data)
    {
        if (data.Length <= 15) return data;
        if (data[0] == '3' && data[1] == '.') return data[15..];
        return data;
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 |
                  data[offset + 2] << 8  | data[offset + 3]);

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset]     = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}

sealed class SeqNo
{
    private int _value = 1;
    public int Next() => _value++;
}
