using SnmpBrowser.Plugins;

namespace SnmpBrowser.Services;

/// <summary>
/// Descrive un tipo di catalogo (MIB o plugin): dove vivono i suoi file e cosa fare quando un pacchetto
/// viene installato, cambiato di versione o rimosso. Il meccanismo git e' lo stesso per tutti.
/// </summary>
public sealed class CatalogProfile
{
    public required string Key { get; init; }
    public required string DefaultUrl { get; init; }
    public required string RepoDirName { get; init; }
    public required string UrlFileName { get; init; }
    public required string PersonalFileName { get; init; }
    public required string PersonalDirName { get; init; }
    public required string PersonalKeyPrefix { get; init; }

    /// <summary>Pacchetto installato o cambiato: (chiave sorgente, cartella).</summary>
    public required Action<string, string> Activate { get; init; }

    /// <summary>Pacchetto rimosso: (chiave sorgente).</summary>
    public required Action<string> Deactivate { get; init; }

    public static CatalogProfile Mib { get; } = new()
    {
        Key = "mib",
        DefaultUrl = "https://github.com/programmi-costantino/snmpbrowser-mibs.git",
        RepoDirName = "mib-repo",
        UrlFileName = "mib-repo-url.txt",
        PersonalFileName = "personal-sources.json",
        PersonalDirName = "personal",
        PersonalKeyPrefix = "personal:",
        Activate = (key, dir) => MibStore.Instance.ReplaceSource(key, MibStore.EnumerateMibFiles(dir)),
        Deactivate = key => MibStore.Instance.RemoveSource(key),
    };

    public static CatalogProfile Plugins { get; } = new()
    {
        Key = "plugin",
        DefaultUrl = "https://github.com/programmi-costantino/snmpbrowser-plugins.git",
        RepoDirName = "plugin-repo",
        UrlFileName = "plugin-repo-url.txt",
        PersonalFileName = "personal-plugin-sources.json",
        PersonalDirName = "personal-plugins",
        PersonalKeyPrefix = "plugin-personal:",
        Activate = (key, dir) => PluginManager.Instance.Load(key, dir),
        Deactivate = key => PluginManager.Instance.Unload(key),
    };
}
