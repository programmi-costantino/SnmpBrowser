using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace SnmpBrowser.Plugins;

public sealed record MqttCredentials(string Username, string Password);

/// <summary>
/// Client MQTT 3.1.1 minimo, solo pubblicazione (QoS 0 e 1, retain), con TLS opzionale.
/// E' usato dall'app: lo script del plugin non apre mai connessioni di rete.
/// </summary>
public sealed class MqttSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private ushort _packetId;

    private MqttSession(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static string? ValidateTopic(string topic)
    {
        if (topic.Length == 0) return "topic vuoto";
        if (Encoding.UTF8.GetByteCount(topic) > 256) return "topic troppo lungo (max 256 byte)";
        if (topic.Contains('+') || topic.Contains('#') || topic.Contains('\0')) return "il topic non puo' contenere + # o caratteri nulli";
        if (topic.StartsWith('$')) return "i topic che iniziano con $ sono riservati al broker";
        return null;
    }

    /// <summary>Apre la connessione e fa il login. Lancia IOException con un messaggio leggibile in caso di errore.</summary>
    public static MqttSession Connect(PluginMqtt cfg, MqttCredentials? credentials, int timeoutMs = 5000)
    {
        var tcp = new TcpClient();
        try
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    tcp.ConnectAsync(cfg.Host, cfg.Port, cts.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw new IOException($"broker {cfg.Host}:{cfg.Port} non raggiungibile (timeout)");
                }
            }

            var network = tcp.GetStream();
            network.ReadTimeout = timeoutMs;
            network.WriteTimeout = timeoutMs;

            Stream stream = network;
            if (cfg.Tls)
            {
                var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                ssl.AuthenticateAsClient(cfg.Host);
                stream = ssl;
            }

            var session = new MqttSession(tcp, stream);
            session.Handshake(credentials);
            return session;
        }
        catch (IOException)
        {
            tcp.Dispose();
            throw;
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new IOException($"broker {cfg.Host}:{cfg.Port}: {ex.Message}");
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            throw new IOException($"connessione al broker non riuscita: {ex.Message}");
        }
    }

    private void Handshake(MqttCredentials? credentials)
    {
        var hasUser = credentials is { Username.Length: > 0 };
        var hasPass = hasUser && credentials!.Password.Length > 0;

        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(4);                                              // livello protocollo 3.1.1
        body.Add((byte)(0x02 | (hasUser ? 0x80 : 0) | (hasPass ? 0x40 : 0))); // clean session + login
        body.Add(0); body.Add(60);                                // keep alive 60 s
        AddString(body, "snmpbrowser-" + Guid.NewGuid().ToString("N")[..8]);
        if (hasUser) AddString(body, credentials!.Username);
        if (hasPass) AddString(body, credentials!.Password);

        Send(0x10, body);

        var (type, data) = ReadPacket();
        if (type != 0x20 || data.Length < 2) throw new IOException("risposta inattesa dal broker");
        if (data[1] != 0)
            throw new IOException(data[1] switch
            {
                1 => "il broker non accetta MQTT 3.1.1",
                2 => "identificativo client rifiutato dal broker",
                3 => "broker non disponibile",
                4 => "utente o password errati",
                5 => "non autorizzato dal broker",
                _ => $"connessione rifiutata (codice {data[1]})",
            });
    }

    public void Publish(string topic, byte[] payload, bool retain, int qos)
    {
        qos = qos == 1 ? 1 : 0;
        var body = new List<byte>();
        AddString(body, topic);

        ushort id = 0;
        if (qos == 1)
        {
            id = ++_packetId == 0 ? (_packetId = 1) : _packetId;
            body.Add((byte)(id >> 8));
            body.Add((byte)(id & 0xFF));
        }
        body.AddRange(payload);

        Send((byte)(0x30 | (qos << 1) | (retain ? 1 : 0)), body);

        if (qos == 0) return;

        for (var i = 0; i < 5; i++)
        {
            var (type, data) = ReadPacket();
            if (type == 0x40 && data.Length >= 2 && ((data[0] << 8) | data[1]) == id) return;
        }
        throw new IOException("il broker non ha confermato il messaggio");
    }

    public void Dispose()
    {
        try
        {
            _stream.Write(new byte[] { 0xE0, 0x00 });
            _stream.Flush();
        }
        catch
        {
        }
        _stream.Dispose();
        _tcp.Dispose();
    }

    // ---- Formato dei pacchetti ---------------------------------------------

    private static void AddString(List<byte> target, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        target.Add((byte)(bytes.Length >> 8));
        target.Add((byte)(bytes.Length & 0xFF));
        target.AddRange(bytes);
    }

    private void Send(byte header, List<byte> body)
    {
        var packet = new List<byte> { header };
        var length = body.Count;
        do
        {
            var digit = length % 128;
            length /= 128;
            if (length > 0) digit |= 0x80;
            packet.Add((byte)digit);
        } while (length > 0);
        packet.AddRange(body);

        _stream.Write(packet.ToArray());
        _stream.Flush();
    }

    private (int Type, byte[] Data) ReadPacket()
    {
        var first = ReadByte();
        var multiplier = 1;
        var length = 0;
        int digit;
        do
        {
            digit = ReadByte();
            length += (digit & 0x7F) * multiplier;
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128 * 128) throw new IOException("pacchetto non valido dal broker");
        } while ((digit & 0x80) != 0);

        if (length > 1_000_000) throw new IOException("pacchetto troppo grande dal broker");

        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = _stream.Read(data, read, length - read);
            if (n <= 0) throw new IOException("connessione chiusa dal broker");
            read += n;
        }
        return (first & 0xF0, data);
    }

    private int ReadByte()
    {
        var b = _stream.ReadByte();
        if (b < 0) throw new IOException("connessione chiusa dal broker");
        return b;
    }
}
