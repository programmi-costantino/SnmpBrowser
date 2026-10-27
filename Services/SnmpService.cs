using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using SnmpBrowser.Models;

namespace SnmpBrowser.Services;

public enum V3Level
{
    NoAuthNoPriv,
    AuthNoPriv,
    AuthPriv,
}

/// <summary>Errore SNMPv3 con messaggio gia' pronto per l'utente.</summary>
public sealed class SnmpV3Exception : Exception
{
    public SnmpV3Exception(string message) : base(message)
    {
    }
}

public sealed class SnmpSettings
{
    // SNMPv3 (con v1/v2c questi campi non sono usati)
    public string User { get; init; } = "";
    public V3Level SecurityLevel { get; init; } = V3Level.AuthPriv;
    public string AuthProtocol { get; init; } = "SHA-1";
    public string AuthPassword { get; init; } = "";
    public string PrivProtocol { get; init; } = "AES-128";
    public string PrivPassword { get; init; } = "";
    public string ContextName { get; init; } = "";

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
            var result = s.Version == VersionCode.V3
                ? V3Request(s, "get", vars)
                : Messenger.Get(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
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
            if (s.Version == VersionCode.V3)
                V3Walk(s, new ObjectIdentifier(Normalize(rootOid)), list);
            else
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
            var result = s.Version == VersionCode.V3
                ? V3Request(s, "set", vars)
                : Messenger.Set(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
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

        if (ex is SnmpV3Exception)
            return ex.Message;

        if (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            var credentials = s.Version == VersionCode.V3 ? "utente, livello di sicurezza" : "community";
            return $"Timeout: {s.Host}:{s.Port} non ha risposto entro {s.TimeoutMs} ms. " +
                   $"Controlla IP, porta, {credentials}, versione SNMP e che il firewall lasci passare UDP {s.Port}.";
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

    // ---- SNMPv3 -------------------------------------------------------------

    private static IPrivacyProvider BuildPrivacy(SnmpSettings s)
    {
        if (s.SecurityLevel == V3Level.NoAuthNoPriv)
            return new DefaultPrivacyProvider(DefaultAuthenticationProvider.Instance);

        var authPhrase = new OctetString(s.AuthPassword);
        IAuthenticationProvider auth = s.AuthProtocol switch
        {
            "MD5" => new MD5AuthenticationProvider(authPhrase),
            "SHA-1" => new SHA1AuthenticationProvider(authPhrase),
            "SHA-256" => new SHA256AuthenticationProvider(authPhrase),
            "SHA-384" => new SHA384AuthenticationProvider(authPhrase),
            "SHA-512" => new SHA512AuthenticationProvider(authPhrase),
            _ => throw new SnmpV3Exception($"Protocollo di autenticazione non supportato: {s.AuthProtocol}"),
        };

        if (s.SecurityLevel == V3Level.AuthNoPriv)
            return new DefaultPrivacyProvider(auth);

        var privPhrase = new OctetString(s.PrivPassword);
        return s.PrivProtocol switch
        {
            "DES" => new DESPrivacyProvider(privPhrase, auth),
            "AES-128" => new AESPrivacyProvider(privPhrase, auth),
            "AES-192" => new AES192PrivacyProvider(privPhrase, auth),
            "AES-256" => new AES256PrivacyProvider(privPhrase, auth),
            _ => throw new SnmpV3Exception($"Protocollo di cifratura non supportato: {s.PrivProtocol}"),
        };
    }

    /// <summary>Un singolo scambio v3: scoperta dell'agent (engine ID e orologio), poi la richiesta vera e propria.</summary>
    private sealed class V3Session
    {
        public required SnmpSettings Settings { get; init; }
        public required IPEndPoint Endpoint { get; init; }
        public required IPrivacyProvider Privacy { get; init; }
        public required ReportMessage Report { get; init; }
    }

    private static V3Session V3Open(SnmpSettings s)
    {
        var endpoint = Resolve(s);
        var privacy = BuildPrivacy(s);
        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = discovery.GetResponse(s.TimeoutMs, endpoint);
        return new V3Session { Settings = s, Endpoint = endpoint, Privacy = privacy, Report = report };
    }

    private static IList<Variable> V3Send(V3Session session, string kind, List<Variable> vars)
    {
        var s = session.Settings;
        var user = new OctetString(s.User);
        var context = new OctetString(s.ContextName);

        ISnmpMessage request = kind switch
        {
            "get" => new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
                user, context, vars, session.Privacy, Messenger.MaxMessageSize, session.Report),
            "next" => new GetNextRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
                user, context, vars, session.Privacy, Messenger.MaxMessageSize, session.Report),
            "set" => new SetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
                user, context, vars, session.Privacy, Messenger.MaxMessageSize, session.Report),
            _ => throw new ArgumentException(kind),
        };

        var reply = request.GetResponse(s.TimeoutMs, session.Endpoint);

        if (reply is ReportMessage report)
            throw new SnmpV3Exception(DescribeReport(report, s));

        var pdu = reply.Pdu();
        var status = pdu.ErrorStatus.ToInt32();
        if (status != 0)
            throw new SnmpV3Exception($"L'agent ha risposto con un errore: {ErrorName(status)} (indice {pdu.ErrorIndex.ToInt32()}).");

        return pdu.Variables;
    }

    private static IList<Variable> V3Request(SnmpSettings s, string kind, List<Variable> vars) =>
        V3Send(V3Open(s), kind, vars);

    /// <summary>Walk con GetNext: una richiesta per OID, piu' lento del bulk ma identico per tutti gli agent.</summary>
    private static void V3Walk(SnmpSettings s, ObjectIdentifier root, List<Variable> result)
    {
        var session = V3Open(s);
        var current = root;

        for (var i = 0; i < 20000; i++)
        {
            var reply = V3Send(session, "next", new List<Variable> { new(current) });
            if (reply.Count == 0) return;

            var v = reply[0];
            if (v.Data.TypeCode is SnmpType.EndOfMibView or SnmpType.NoSuchObject or SnmpType.NoSuchInstance) return;

            var id = v.Id.ToString().TrimStart('.');
            var prefix = root.ToString().TrimStart('.');
            if (id != prefix && !id.StartsWith(prefix + ".", StringComparison.Ordinal)) return; // fuori dal sottoalbero
            if (v.Id.CompareTo(current) <= 0) return; // l'agent non avanza: evito un ciclo infinito

            result.Add(v);
            current = v.Id;
        }
    }

    private static string DescribeReport(ReportMessage report, SnmpSettings s)
    {
        var oid = report.Pdu().Variables.FirstOrDefault()?.Id.ToString().TrimStart('.') ?? "";
        return oid switch
        {
            "1.3.6.1.6.3.15.1.1.1.0" => "SNMPv3: livello di sicurezza non supportato dall'agent per questo utente.",
            "1.3.6.1.6.3.15.1.1.2.0" => "SNMPv3: messaggio fuori dalla finestra di tempo. Riprova.",
            "1.3.6.1.6.3.15.1.1.3.0" => $"SNMPv3: l'utente '{s.User}' non esiste sull'agent.",
            "1.3.6.1.6.3.15.1.1.4.0" => "SNMPv3: engine ID sconosciuto. Riprova.",
            "1.3.6.1.6.3.15.1.1.5.0" => "SNMPv3: autenticazione fallita. Password o protocollo di autenticazione errati.",
            "1.3.6.1.6.3.15.1.1.6.0" => "SNMPv3: decifrazione fallita. Password o protocollo di cifratura errati.",
            _ => $"SNMPv3: l'agent ha risposto con un report ({oid}).",
        };
    }

    private static string ErrorName(int status) => status switch
    {
        1 => "tooBig", 2 => "noSuchName", 3 => "badValue", 4 => "readOnly", 5 => "genErr", 6 => "noAccess",
        7 => "wrongType", 8 => "wrongLength", 9 => "wrongEncoding", 10 => "wrongValue", 11 => "noCreation",
        12 => "inconsistentValue", 13 => "resourceUnavailable", 14 => "commitFailed", 15 => "undoFailed",
        16 => "authorizationError", 17 => "notWritable", 18 => "inconsistentName",
        _ => $"codice {status}",
    };

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
        return new SnmpEntry(oid, MibNames.Resolve(oid), type, ValuePipeline.Format(oid, type, raw), raw);
    }
}
