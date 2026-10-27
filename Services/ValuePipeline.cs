using SnmpBrowser.Plugins;

namespace SnmpBrowser.Services;

/// <summary>Come si arriva al valore mostrato: valore grezzo, poi enumerazioni delle MIB, poi decodificatori dei plugin.</summary>
public static class ValuePipeline
{
    public static string Format(string oid, string type, string raw)
    {
        var value = MibStore.Instance.DecorateValue(oid, type, raw);
        return PluginManager.Instance.DecodeValue(oid, type, raw, value);
    }
}
