using System.Text.RegularExpressions;
using SnmpBrowser.Models;

namespace SnmpBrowser.Services;

/// <summary>
/// Repository MIB centrale: un repo git pubblico che contiene un submodule per ogni libreria MIB;
/// le versioni di ogni libreria sono i tag git del submodule.
/// </summary>
public sealed class RepoCatalog
{
    public static RepoCatalog Mib { get; } = new(CatalogProfile.Mib);
    public static RepoCatalog Plugins { get; } = new(CatalogProfile.Plugins);

    private readonly CatalogProfile _profile;

    private RepoCatalog(CatalogProfile profile) => _profile = profile;

    public CatalogProfile Profile => _profile;

    private static readonly Regex TagRx = new(@"^[A-Za-z0-9][A-Za-z0-9._/+-]*$", RegexOptions.Compiled);

    private static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser");

    public string RepoDir => Path.Combine(AppDir, _profile.RepoDirName);
    private string UrlFile => Path.Combine(AppDir, _profile.UrlFileName);

    public bool IsCloned => Directory.Exists(Path.Combine(RepoDir, ".git"));

    // ---- URL --------------------------------------------------------------

    public string LoadUrl()
    {
        try
        {
            if (File.Exists(UrlFile))
            {
                var text = File.ReadAllText(UrlFile).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch
        {
        }
        return _profile.DefaultUrl;
    }

    public void SaveUrl(string url)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(UrlFile, url.Trim());
        }
        catch
        {
        }
    }

    public static string? ValidateUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "Inserisci l'URL del repository MIB.";
        if (url.Any(char.IsWhiteSpace)) return "L'URL non puo' contenere spazi.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return "L'URL deve essere un indirizzo https:// (es. https://github.com/utente/repo.git).";
        if (url.Contains("CHANGE-ME", StringComparison.Ordinal))
            return "Imposta l'URL reale del tuo repository MIB.";
        return null;
    }

    // ---- .gitmodules -------------------------------------------------------

    private sealed record Entry(string Name, string Path, string Url);

    private List<Entry> ReadGitmodules()
    {
        var file = Path.Combine(RepoDir, ".gitmodules");
        var result = new List<Entry>();
        if (!File.Exists(file)) return result;

        string? name = null, path = null, url = null;

        void Flush()
        {
            if (name is not null && path is not null && url is not null && IsSafePath(path) && IsHttps(url))
                result.Add(new Entry(name, path, url));
            name = path = url = null;
        }

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var header = Regex.Match(line, @"^\[submodule\s+""(?<n>.+)""\]$");
            if (header.Success)
            {
                Flush();
                name = header.Groups["n"].Value;
                continue;
            }

            var kv = Regex.Match(line, @"^(?<k>[A-Za-z]+)\s*=\s*(?<v>.+)$");
            if (!kv.Success) continue;

            if (kv.Groups["k"].Value == "path") path = kv.Groups["v"].Value.Trim();
            else if (kv.Groups["k"].Value == "url") url = kv.Groups["v"].Value.Trim();
        }
        Flush();
        return result;
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !url.Any(char.IsWhiteSpace);

    private static bool IsSafePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return !path.Split('/', '\\').Any(p => p is ".." or "." or "" || p.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private string ModuleDir(string path) => Path.GetFullPath(Path.Combine(RepoDir, path));

    private bool IsInstalledDir(string path)
    {
        var dir = ModuleDir(path);
        return File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"));
    }

    // ---- Avvio: carica le MIB gia' scaricate -------------------------------

    public void LoadInstalled()
    {
        try
        {
            foreach (var entry in ReadGitmodules().Where(e => IsInstalledDir(e.Path)))
                _profile.Activate(entry.Name, ModuleDir(entry.Path));
        }
        catch
        {
            // il repository e' opzionale: un errore qui non deve impedire l'avvio
        }
    }

    // ---- Elenco e sincronizzazione ----------------------------------------

    /// <summary>Elenco dal clone locale, senza rete.</summary>
    public Task<List<MibPackageItem>> LoadLocalAsync() => BuildItemsAsync(allowRemote: false);

    /// <summary>Scarica (o aggiorna) il repository centrale e rilegge elenco e versioni.</summary>
    public async Task<(List<MibPackageItem> Items, string? Error)> SyncAsync(string url)
    {
        url = url.Trim();
        var invalid = ValidateUrl(url);
        if (invalid is not null) return (new List<MibPackageItem>(), invalid);
        if (GitService.GitPath is null)
            return (new List<MibPackageItem>(), (await GitService.RunAsync(new[] { "--version" })).Error);

        try
        {
            Directory.CreateDirectory(AppDir);

            if (!IsCloned)
            {
                if (Directory.Exists(RepoDir)) Directory.Delete(RepoDir, recursive: true);

                var clone = await GitService.RunAsync(
                    new[] { "clone", "--depth", "1", "--no-recurse-submodules", "--", url, RepoDir }, AppDir, 300);
                if (!clone.Ok) return (new List<MibPackageItem>(), $"Download del repository fallito: {clone.ErrorSummary()}");
            }
            else
            {
                var setUrl = await GitService.RunAsync(new[] { "remote", "set-url", "origin", url }, RepoDir);
                if (!setUrl.Ok) return (new List<MibPackageItem>(), setUrl.ErrorSummary());

                var fetch = await GitService.RunAsync(new[] { "fetch", "--depth", "1", "origin", "HEAD" }, RepoDir, 180);
                if (!fetch.Ok) return (new List<MibPackageItem>(), $"Aggiornamento fallito: {fetch.ErrorSummary()}");

                var reset = await GitService.RunAsync(new[] { "reset", "--hard", "FETCH_HEAD" }, RepoDir);
                if (!reset.Ok) return (new List<MibPackageItem>(), reset.ErrorSummary());
            }

            await GitService.RunAsync(new[] { "submodule", "sync", "--quiet" }, RepoDir);
            SaveUrl(url);
        }
        catch (Exception ex)
        {
            return (new List<MibPackageItem>(), ex.Message);
        }

        var items = await BuildItemsAsync(allowRemote: true);
        return (items, items.Count == 0 ? "Il repository non contiene nessun modulo MIB (.gitmodules vuoto o mancante)." : null);
    }

    private async Task<List<MibPackageItem>> BuildItemsAsync(bool allowRemote)
    {
        var entries = ReadGitmodules();
        var items = new List<MibPackageItem>();
        using var gate = new SemaphoreSlim(4);

        async Task<MibPackageItem> Build(Entry entry)
        {
            await gate.WaitAsync();
            try
            {
                var item = new MibPackageItem(entry.Name, entry.Path, entry.Url) { ActivationKey = entry.Name };
                var dir = ModuleDir(entry.Path);
                List<string> tags;

                if (IsInstalledDir(entry.Path))
                {
                    if (allowRemote)
                        await GitService.RunAsync(new[] { "fetch", "--tags", "--force", "--quiet", "origin" }, dir, 120);

                    var list = await GitService.RunAsync(new[] { "tag", "-l" }, dir);
                    tags = list.Ok ? ParseTags(list.Output.Split('\n')) : new List<string>();
                    item.InstalledVersion = await CurrentVersionAsync(dir);
                }
                else if (allowRemote)
                {
                    var remote = await GitService.RunAsync(new[] { "ls-remote", "--tags", "--refs", "--", entry.Url }, null, 60);
                    tags = remote.Ok
                        ? ParseTags(remote.Output.Split('\n').Select(l => l.Contains("refs/tags/") ? l[(l.IndexOf("refs/tags/") + 10)..] : ""))
                        : new List<string>();
                }
                else
                {
                    tags = new List<string>();
                }

                item.SetVersions(tags);
                return item;
            }
            finally
            {
                gate.Release();
            }
        }

        foreach (var built in await Task.WhenAll(entries.Select(Build)))
            items.Add(built);

        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ParseTags(IEnumerable<string> lines)
    {
        var tags = lines.Select(l => l.Trim()).Where(l => TagRx.IsMatch(l)).Distinct().ToList();
        tags.Sort((a, b) => CompareVersions(b, a)); // dalla piu' recente
        return tags;
    }

    internal static async Task<string> CurrentVersionAsync(string dir)
    {
        var exact = await GitService.RunAsync(new[] { "describe", "--tags", "--exact-match", "HEAD" }, dir);
        if (exact.Ok && exact.Output.Length > 0) return exact.Output;

        var head = await GitService.RunAsync(new[] { "rev-parse", "--short", "HEAD" }, dir);
        return head.Ok && head.Output.Length > 0 ? head.Output : "sconosciuta";
    }

    // ---- Installa / cambia versione / rimuovi ------------------------------

    /// <summary>Equivale a: git submodule update --init &lt;path&gt;  +  git checkout &lt;tag&gt;. Restituisce l'errore, o null se ok.</summary>
    public async Task<string?> InstallAsync(MibPackageItem item, string? tag)
    {
        if (tag is not null && (!TagRx.IsMatch(tag) || !item.Versions.Contains(tag)))
            return "Versione non valida.";
        if (!IsSafePath(item.Path)) return "Percorso del modulo non valido.";

        try
        {
            var dir = ModuleDir(item.Path);

            if (!IsInstalledDir(item.Path))
            {
                var init = await GitService.RunAsync(new[] { "submodule", "update", "--init", "--", item.Path }, RepoDir, 600);
                if (!init.Ok) return $"Download di {item.Name} fallito: {init.ErrorSummary()}";
            }

            if (tag is not null)
            {
                var fetch = await GitService.RunAsync(new[] { "fetch", "--tags", "--force", "--quiet", "origin" }, dir, 180);
                if (!fetch.Ok) return $"Recupero delle versioni fallito: {fetch.ErrorSummary()}";

                var checkout = await GitService.RunAsync(new[] { "checkout", "--quiet", "--detach", $"refs/tags/{tag}" }, dir);
                if (!checkout.Ok) return $"Cambio versione fallito: {checkout.ErrorSummary()}";
            }

            item.InstalledVersion = await CurrentVersionAsync(dir);
            item.SelectedVersion = item.InstalledVersion;
            _profile.Activate(item.Name, dir);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> RemoveAsync(MibPackageItem item)
    {
        if (!IsSafePath(item.Path)) return "Percorso del modulo non valido.";

        var result = await GitService.RunAsync(new[] { "submodule", "deinit", "-f", "--", item.Path }, RepoDir);
        if (!result.Ok) return $"Rimozione fallita: {result.ErrorSummary()}";

        _profile.Deactivate(item.Name);
        item.InstalledVersion = null;
        return null;
    }

    // ---- Ordinamento versioni ----------------------------------------------

    /// <summary>Confronto "naturale" (v1.10 &gt; v1.9); una pre-release (-rc1) vale meno della versione finale.</summary>
    public static int CompareVersions(string a, string b)
    {
        var ta = Regex.Matches(a, @"\d+|\D+").Select(m => m.Value).ToList();
        var tb = Regex.Matches(b, @"\d+|\D+").Select(m => m.Value).ToList();

        for (var i = 0; i < Math.Min(ta.Count, tb.Count); i++)
        {
            var x = ta[i];
            var y = tb[i];
            int c;

            if (char.IsAsciiDigit(x[0]) && char.IsAsciiDigit(y[0]))
            {
                x = x.TrimStart('0');
                y = y.TrimStart('0');
                c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else
            {
                c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }

            if (c != 0) return c;
        }

        if (ta.Count == tb.Count) return 0;

        var longer = ta.Count > tb.Count ? ta : tb;
        var extra = longer[Math.Min(ta.Count, tb.Count)];
        var longerIsPre = !char.IsAsciiDigit(extra[0]) && extra[0] != '.';
        var cmp = longerIsPre ? -1 : 1; // pre-release: la versione piu' lunga e' piu' vecchia
        return ta.Count > tb.Count ? cmp : -cmp;
    }
}
