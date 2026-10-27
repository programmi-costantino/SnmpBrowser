using System.Net.Http;
using System.Reflection;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SnmpBrowser.Services;

/// <summary>
/// Aggiornamenti dell'app tramite Velopack e le GitHub Releases del repository pubblico.
/// Funzionano solo nella versione installata (non quando si avvia da Visual Studio).
/// </summary>
public static class UpdateService
{
    /// <summary>Repository GitHub che pubblica le release (le crea il workflow .github/workflows/release.yml).</summary>
    public const string RepoUrl = "https://github.com/programmi-costantino/SnmpBrowser";

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "update-auto-check.txt");

    private static IUpdateSource? _source;
    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;
    private static List<VelopackAsset> _fullAssets = new();
    private static bool _downloaded;

    /// <summary>Cambia quando si trova o si scarica un aggiornamento. Puo' arrivare da qualsiasi thread.</summary>
    public static event Action? Changed;

    public static string? AvailableVersion { get; private set; }
    public static bool Downloaded => _downloaded;

    private static UpdateManager? Manager()
    {
        if (RepoUrl.Contains("CHANGE-ME", StringComparison.Ordinal)) return null;
        _source ??= new GithubSource(RepoUrl, null, prerelease: false);
        return _manager ??= new UpdateManager(_source);
    }

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return Manager()?.IsInstalled == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (Manager() is { IsInstalled: true } m && m.CurrentVersion is { } v) return v.ToString();
            }
            catch
            {
            }

            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? "sviluppo";
        }
    }

    /// <summary>Per default l'app controlla gli aggiornamenti all'avvio; l'utente puo' disattivarlo.</summary>
    public static bool AutoCheck
    {
        get
        {
            try
            {
                return !File.Exists(SettingsFile) || File.ReadAllText(SettingsFile).Trim() != "0";
            }
            catch
            {
                return true;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, value ? "1" : "0");
            }
            catch
            {
            }
        }
    }

    /// <summary>Cerca una versione piu' recente. Restituisce l'errore, o null se la ricerca e' andata a buon fine.</summary>
    public static async Task<string?> CheckAsync()
    {
        var manager = Manager();
        if (manager is null) return "Repository degli aggiornamenti non configurato.";
        if (!manager.IsInstalled) return "Gli aggiornamenti funzionano solo nella versione installata, non da Visual Studio.";

        try
        {
            var info = await manager.CheckForUpdatesAsync();
            _pending = info;
            _downloaded = false;
            AvailableVersion = info?.TargetFullRelease.Version.ToString();
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>Scarica l'aggiornamento trovato. Restituisce l'errore, o null se ok.</summary>
    public static async Task<string?> DownloadAsync(IProgress<int>? progress)
    {
        var manager = Manager();
        if (manager is null || _pending is null) return "Nessun aggiornamento da scaricare: controlla prima la disponibilita'.";

        try
        {
            await manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p));
            _downloaded = true;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    /// <summary>Una versione dell'app pubblicata su GitHub.</summary>
    public sealed record VersionChoice(string Version, string Text, bool IsCurrent);

    /// <summary>Elenco di tutte le versioni pubblicate (dalla piu' recente), anche quelle precedenti a quella installata.</summary>
    public static async Task<(List<VersionChoice> Versions, string? Error)> ListVersionsAsync()
    {
        var manager = Manager();
        if (manager is null || _source is null) return (new List<VersionChoice>(), "Repository degli aggiornamenti non configurato.");
        if (!manager.IsInstalled)
            return (new List<VersionChoice>(), "Il cambio di versione funziona solo nella versione installata, non da Visual Studio.");

        try
        {
            var feed = await _source.GetReleaseFeed(NullVelopackLogger.Instance, manager.AppId, channel: null);
            var full = feed.Assets.Where(a => a.Type == VelopackAssetType.Full).ToList();
            full.Sort((a, b) => RepoCatalog.CompareVersions(b.Version.ToString(), a.Version.ToString()));
            _fullAssets = full;

            var current = CurrentVersion;
            var list = full.Select((a, i) =>
            {
                var v = a.Version.ToString();
                var isCurrent = v == current;
                var suffix = isCurrent ? "  (attuale)" : i == 0 ? "  (ultima)" : "";
                return new VersionChoice(v, v + suffix, isCurrent);
            }).ToList();

            return (list, list.Count == 0 ? "Nessuna versione trovata nelle release." : null);
        }
        catch (Exception ex)
        {
            return (new List<VersionChoice>(), Describe(ex));
        }
    }

    public static bool IsOlderThanCurrent(string version) => RepoCatalog.CompareVersions(version, CurrentVersion) < 0;

    /// <summary>Sceglie la versione da installare (anche precedente); poi DownloadAsync e ApplyAndRestart. Restituisce l'errore, o null.</summary>
    public static string? SelectVersion(string version)
    {
        var target = _fullAssets.FirstOrDefault(a => a.Version.ToString() == version);
        if (Manager() is null || target is null) return "Versione non trovata: riapri la finestra per ricaricare l'elenco.";

        _pending = new UpdateInfo(target, IsOlderThanCurrent(version));
        _downloaded = false;
        return null;
    }

    /// <summary>Chiude l'app, installa l'aggiornamento scaricato e la riapre.</summary>
    public static void ApplyAndRestart()
    {
        if (Manager() is { } manager && _pending is not null && _downloaded)
            manager.ApplyUpdatesAndRestart(_pending);
    }

    private static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) ex = inner;

        return ex switch
        {
            HttpRequestException => "Impossibile contattare GitHub: controlla la connessione a internet e che il repository sia pubblico.",
            TaskCanceledException => "Operazione scaduta: la connessione a GitHub e' troppo lenta.",
            _ => ex.Message,
        };
    }
}
