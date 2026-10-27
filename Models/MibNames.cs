using SnmpBrowser.Services;

namespace SnmpBrowser.Models;

/// <summary>Piccolo dizionario di nomi MIB-II piu' comuni, per mostrare nomi leggibili.</summary>
public static class MibNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["1.3.6.1"] = "internet",
        ["1.3.6.1.2.1"] = "mib-2",
        ["1.3.6.1.2.1.1"] = "system",
        ["1.3.6.1.2.1.1.1"] = "sysDescr",
        ["1.3.6.1.2.1.1.2"] = "sysObjectID",
        ["1.3.6.1.2.1.1.3"] = "sysUpTime",
        ["1.3.6.1.2.1.1.4"] = "sysContact",
        ["1.3.6.1.2.1.1.5"] = "sysName",
        ["1.3.6.1.2.1.1.6"] = "sysLocation",
        ["1.3.6.1.2.1.1.7"] = "sysServices",
        ["1.3.6.1.2.1.2"] = "interfaces",
        ["1.3.6.1.2.1.2.1"] = "ifNumber",
        ["1.3.6.1.2.1.2.2"] = "ifTable",
        ["1.3.6.1.2.1.2.2.1"] = "ifEntry",
        ["1.3.6.1.2.1.2.2.1.1"] = "ifIndex",
        ["1.3.6.1.2.1.2.2.1.2"] = "ifDescr",
        ["1.3.6.1.2.1.2.2.1.3"] = "ifType",
        ["1.3.6.1.2.1.2.2.1.4"] = "ifMtu",
        ["1.3.6.1.2.1.2.2.1.5"] = "ifSpeed",
        ["1.3.6.1.2.1.2.2.1.6"] = "ifPhysAddress",
        ["1.3.6.1.2.1.2.2.1.7"] = "ifAdminStatus",
        ["1.3.6.1.2.1.2.2.1.8"] = "ifOperStatus",
        ["1.3.6.1.2.1.2.2.1.10"] = "ifInOctets",
        ["1.3.6.1.2.1.2.2.1.14"] = "ifInErrors",
        ["1.3.6.1.2.1.2.2.1.16"] = "ifOutOctets",
        ["1.3.6.1.2.1.2.2.1.20"] = "ifOutErrors",
        ["1.3.6.1.2.1.4"] = "ip",
        ["1.3.6.1.2.1.5"] = "icmp",
        ["1.3.6.1.2.1.6"] = "tcp",
        ["1.3.6.1.2.1.7"] = "udp",
        ["1.3.6.1.2.1.11"] = "snmp",
        ["1.3.6.1.2.1.25"] = "host",
        ["1.3.6.1.2.1.31"] = "ifMIB",
        ["1.3.6.1.2.1.31.1.1.1.1"] = "ifName",
        ["1.3.6.1.2.1.31.1.1.1.6"] = "ifHCInOctets",
        ["1.3.6.1.2.1.31.1.1.1.10"] = "ifHCOutOctets",
        ["1.3.6.1.2.1.31.1.1.1.15"] = "ifHighSpeed",
        ["1.3.6.1.2.1.31.1.1.1.18"] = "ifAlias",
        ["1.3.6.1.4.1"] = "enterprises",
    };

    private static readonly Dictionary<string, string> ByName =
        Names.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>OID di un nome MIB-II noto (es. sysDescr).</summary>
    public static bool TryByName(string name, out string oid) => ByName.TryGetValue(name, out oid!);

    /// <summary>Nome esatto dell'OID, se noto (prima le MIB caricate, poi quelle incluse).</summary>
    public static string? Exact(string oid) =>
        MibStore.Instance.Exact(oid) ?? (Names.TryGetValue(oid, out var n) ? n : null);

    /// <summary>Risolve l'OID con il prefisso noto piu' lungo (es. sysDescr.0).</summary>
    public static string Resolve(string oid)
    {
        var symbol = MibStore.Instance.Lookup(oid, out var suffix);
        if (symbol is not null) return symbol.Name + suffix;

        var parts = oid.Split('.');
        for (var len = parts.Length; len >= 1; len--)
        {
            var prefix = string.Join('.', parts, 0, len);
            if (Names.TryGetValue(prefix, out var name))
            {
                return len == parts.Length
                    ? name
                    : name + "." + string.Join('.', parts, len, parts.Length - len);
            }
        }
        return oid;
    }
}
