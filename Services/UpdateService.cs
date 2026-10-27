using System.Net.Http;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace SnmpBrowser.Services;

/// <summary>
/// Aggiornamenti dell'app tramite Velopack e le GitHub Releases del repository pubblico.
/// Funzionano solo nella versione installata (non quando si avvia da Visual Studio).
/// </summary>
public static class UpdateService
{
    /// <summary>Repository GitHub che pubblica le release (le crea il workflow .github/workflows/release.yml).</summary>
    public const string RepoUrl = "https://github.com/programmi-costantino/snmpbrowser";

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "update-auto-check.txt");

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;
    private static bool _downloaded;

    /// <summary>Cambia quando si trova o si scarica un aggiornamento. Puo' arrivare da qualsiasi thread.</summary>
    public static event Action? Changed;

    public static string? AvailableVersion { get; private set; }
    public static bool Downloaded => _downloaded;

    private static UpdateManager? Manager()
    {
        if (RepoUrl.Contains("CHANGE-ME", StringComparison.Ordinal)) return null;
        return _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
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
