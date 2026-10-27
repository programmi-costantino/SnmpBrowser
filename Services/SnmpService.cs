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

/// <summary>Wrapper di SharpSnmpLib: le chiamate bloccanti girano su un thread del pool.</summary>
public static class SnmpService
{
    public static Task<List<SnmpEntry>> GetAsync(SnmpSettings s, string oid) => Task.Run(() =>
    {
        var vars = new List<Variable> { new(new ObjectIdentifier(Normalize(oid))) };
        var result = Messenger.Get(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
        return result.Select(ToEntry).ToList();
    });

    public static Task<List<SnmpEntry>> WalkAsync(SnmpSettings s, string rootOid) => Task.Run(() =>
    {
        var list = new List<Variable>();
        Messenger.Walk(s.Version, Resolve(s), new OctetString(s.Community),
            new ObjectIdentifier(Normalize(rootOid)), list, s.TimeoutMs, WalkMode.WithinSubtree);
        return list.Select(ToEntry).ToList();
    });

    public static Task<List<SnmpEntry>> SetAsync(SnmpSettings s, string oid, string type, string value) => Task.Run(() =>
    {
        var vars = new List<Variable> { new(new ObjectIdentifier(Normalize(oid)), BuildData(type, value)) };
        var result = Messenger.Set(s.Version, Resolve(s), new OctetString(s.Community), vars, s.TimeoutMs);
        return result.Select(ToEntry).ToList();
    });

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

    private static string Normalize(string oid) => oid.Trim().TrimStart('.');

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
        return new SnmpEntry(oid, MibNames.Resolve(oid), v.Data.TypeCode.ToString(), v.Data.ToString() ?? "");
    }
}
