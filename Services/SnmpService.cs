using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using SnmpBrowser.Models;

namespace SnmpBrowser.Services;

public sealed class SnmpSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 161;
    public string Community { get; init; } = "public";
    public VersionCode Version { get; init; } = VersionCode.V2;
    public int TimeoutMs { get; init; } = 3000;
}

/// <summary>Esito di un'operazione SNMP: errori e timeout sono dati, non eccezioni.</summary>
public sealed record SnmpResult(List<SnmpEntry> Entries, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Wrapper di SharpSnmpLib. Le chiamate bloccanti girano su un thread del pool e
/// ogni eccezione viene intercettata qui, cosi' non risale mai al chiamante come "non gestita".
/// </summary>
public static class SnmpService
{
    public static Task<SnmpResult> GetAsync(SnmpSettings s, string oid) => Task.Run(() =>
    {
        try
        {
            var vars = new List<Variable> { new(new ObjectIdentifier(Normalize(oid))) };
            var result = Messenger.Get(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
            return new SnmpResult(result.Select(ToEntry).ToList());
        }
        catch (Exception ex)
        {
            return new SnmpResult(new List<SnmpEntry>(), Describe(ex, s));
        }
    });

    public static Task<SnmpResult> WalkAsync(SnmpSettings s, string rootOid) => Task.Run(() =>
    {
        var list = new List<Variable>();
        try
        {
            Messenger.Walk(s.Version, Resolve(s), new OctetString(s.Community),
                new ObjectIdentifier(Normalize(rootOid)), list, s.TimeoutMs, WalkMode.WithinSubtree);
            return new SnmpResult(list.Select(ToEntry).ToList());
        }
        catch (Exception ex)
        {
            // se il walk si interrompe a meta' (es. timeout) restituisco quanto raccolto finora
            return new SnmpResult(list.Select(ToEntry).ToList(), Describe(ex, s));
        }
    });

    public static Task<SnmpResult> SetAsync(SnmpSettings s, string oid, string type, string value) => Task.Run(() =>
    {
        try
        {
            var vars = new List<Variable> { new(new ObjectIdentifier(Normalize(oid)), BuildData(type, value.Trim())) };
            var result = Messenger.Set(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
            return new SnmpResult(result.Select(ToEntry).ToList());
        }
        catch (Exception ex)
        {
            return new SnmpResult(new List<SnmpEntry>(), Describe(ex, s));
        }
    });

    /// <summary>Traduce le eccezioni in messaggi comprensibili.</summary>
    private static string Describe(Exception ex, SnmpSettings s)
    {
        if (ex is AggregateException { InnerException: { } inner })
            ex = inner;

        if (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            return $"Timeout: {s.Host}:{s.Port} non ha risposto entro {s.TimeoutMs} ms. " +
                   $"Controlla IP, porta, community, versione SNMP e che il firewall lasci passare UDP {s.Port}.";
        }

        if (ex is SocketException se)
        {
            return se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    => $"Host '{s.Host}' non trovato (DNS).",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable
                    => $"Host '{s.Host}' non raggiungibile.",
                SocketError.ConnectionReset or SocketError.ConnectionRefused
                    => $"{s.Host}:{s.Port} ha rifiutato la connessione (nessun agent SNMP in ascolto?).",
                _ => $"Errore di rete: {se.Message}"
            };
        }

        if (ex.GetType().Name == "ErrorException")
            return $"L'agent ha risposto con un errore: {ex.Message}";

        if (ex is FormatException or OverflowException or ArgumentException)
            return $"Valore non valido: {ex.Message}";

        return ex.Message;
    }

    private static ISnmpData BuildData(string type, string value) => type switch
    {
        "Integer32" => new Integer32(int.Parse(value)),
        "OctetString" => new OctetString(value),
        "Gauge32" => new Gauge32(uint.Parse(value)),
        "TimeTicks" => new TimeTicks(uint.Parse(value)),
        "IpAddress" => new IP(IPAddress.Parse(value).GetAddressBytes()),
        "ObjectIdentifier" => new ObjectIdentifier(Normalize(value)),
        _ => throw new ArgumentException($"Tipo non supportato: {type}")
    };

    private static string Normalize(string oid) => MibStore.Instance.NormalizeOid(oid);

    private static IPEndPoint Resolve(SnmpSettings s)
    {
        if (!IPAddress.TryParse(s.Host, out var ip))
        {
            ip = Dns.GetHostAddresses(s.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? throw new ArgumentException($"Impossibile risolvere l'host '{s.Host}'");
        }
        return new IPEndPoint(ip, s.Port);
    }

    private static SnmpEntry ToEntry(Variable v)
    {
        var oid = v.Id.ToString().TrimStart('.');
        var type = v.Data.TypeCode.ToString();
        var raw = v.Data.ToString() ?? "";
        return new SnmpEntry(oid, MibNames.Resolve(oid), type, MibStore.Instance.DecorateValue(oid, type, raw), raw);
    }
}
