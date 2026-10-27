using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnmpBrowser.Models;

namespace SnmpBrowser.Services;

public sealed class PersonalSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>Token di accesso cifrato con DPAPI (utente Windows corrente), in base64. Mai salvato in chiaro.</summary>
    public string? ProtectedToken { get; set; }
}

/// <summary>
/// Sorgenti personali: repository git (anche privati) dell'utente, ognuno clonato per conto suo.
/// Restano sul PC dell'utente e non sono parte del repository centrale.
/// </summary>
public static class PersonalSourceService
{
    private const string BranchSuffix = MibPackageItem.BranchSuffix;

    private static readonly Regex RefRx = new(@"^[A-Za-z0-9][A-Za-z0-9._/+-]*$", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly object Gate = new();
    private static List<PersonalSource>? _sources;

    private static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser");

    private static string PersonalRoot => Path.Combine(AppDir, "personal");
    private static string FilePath => Path.Combine(AppDir, "personal-sources.json");

    public static string CloneDir(PersonalSource s) => Path.Combine(PersonalRoot, s.Id);
    public static string SourceKey(PersonalSource s) => "personal:" + s.Id;
    private static bool IsCloned(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    // ---- Archivio delle sorgenti -------------------------------------------

    public static IReadOnlyList<PersonalSource> All
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _sources!.ToList();
            }
        }
    }

    public static PersonalSource? Find(string? id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _sources!.FirstOrDefault(s => s.Id == id);
        }
    }

    private static void EnsureLoaded()
    {
        if (_sources is not null) return;

        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<PersonalSource>>(File.ReadAllText(FilePath)) ?? new();
                _sources = list.Where(s => IdRx.IsMatch(s.Id) && s.Name.Length > 0).ToList(); // l'Id diventa un nome di cartella
                return;
            }
        }
        catch
        {
        }
        _sources = new List<PersonalSource>();
    }

    private static string? Save()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_sources, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex)
        {
            return $"Impossibile salvare le sorgenti personali: {ex.Message}";
        }
    }

    // ---- Token (DPAPI) ------------------------------------------------------

    private static string? GetToken(PersonalSource s)
    {
        if (string.IsNullOrEmpty(s.ProtectedToken)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(s.ProtectedToken), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // token cifrato da un altro utente/PC: va reinserito
        }
    }

    /// <summary>Token salvato, in chiaro solo in memoria (serve per "Verifica connessione").</summary>
    public static string? SavedToken(PersonalSource s) => GetToken(s);

    public static bool HasToken(PersonalSource s) => !string.IsNullOrEmpty(s.ProtectedToken);

    private static string ProtectToken(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));

    // ---- Validazione e modifica --------------------------------------------

    public static string? ValidateUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "Inserisci l'URL del repository.";
        if (url.Any(char.IsWhiteSpace)) return "L'URL non puo' contenere spazi.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return "L'URL deve essere un indirizzo https:// (es. https://github.com/utente/repo.git).";
        if (uri.UserInfo.Length > 0)
            return "Non scrivere utente o token nell'URL: inseriscilo nel campo Token.";
        return null;
    }

    private static string? ValidateFields(string name, string url, PersonalSource? editing)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) return "Il nome deve avere da 1 a 40 caratteri.";
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
            return "Il nome non puo' contenere / \\ : o caratteri di controllo.";
        if (All.Any(s => s.Id != editing?.Id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "Esiste gia' una sorgente con questo nome.";
        return ValidateUrl(url);
    }

    public static string? Add(string name, string url, string? token)
    {
        var error = ValidateFields(name, url, null);
        if (error is not null) return error;

        lock (Gate)
        {
            EnsureLoaded();
            _sources!.Add(new PersonalSource
            {
                Name = name.Trim(),
                Url = url.Trim(),
                ProtectedToken = string.IsNullOrWhiteSpace(token) ? null : ProtectToken(token),
            });
            return Save();
        }
    }

    public static string? Update(PersonalSource source, string name, string url, string? newToken, bool clearToken)
    {
        var error = ValidateFields(name, url, source);
        if (error is not null) return error;

        lock (Gate)
        {
            EnsureLoaded();
            var target = _sources!.FirstOrDefault(s => s.Id == source.Id);
            if (target is null) return "Sorgente non trovata.";

            // URL diverso: il clone esistente non e' piu' valido
            if (!string.Equals(target.Url, url.Trim(), StringComparison.Ordinal))
            {
                SafeDelete(CloneDir(target));
                MibStore.Instance.RemoveSource(SourceKey(target));
            }

            target.Name = name.Trim();
            target.Url = url.Trim();
            if (clearToken) target.ProtectedToken = null;
            else if (!string.IsNullOrWhiteSpace(newToken)) target.ProtectedToken = ProtectToken(newToken);
            return Save();
        }
    }

    public static string? Delete(PersonalSource source)
    {
        lock (Gate)
        {
            EnsureLoaded();
            SafeDelete(CloneDir(source));
            MibStore.Instance.RemoveSource(SourceKey(source));
            _sources!.RemoveAll(s => s.Id == source.Id);
            return Save();
        }
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var root = Path.GetFullPath(PersonalRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { } // i file di .git sono di sola lettura
            }
            Directory.Delete(full, recursive: true);
        }
        catch
        {
        }
    }

    // ---- Git con autenticazione --------------------------------------------

    /// <summary>
    /// Il token passa a git solo come variabile d'ambiente del processo (non nella riga di comando, non nell'URL,
    /// non nei file di configurazione) ed e' limitato all'host del repository.
    /// </summary>
    private static async Task<GitResult> RunGitAsync(IEnumerable<string> args, string? dir, string url, string? token, int timeoutSeconds = 120)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_CONFIG_KEY_0"] = "credential.helper", // niente finestre del credential manager di sistema
            ["GIT_CONFIG_VALUE_0"] = "",
        };

        if (!string.IsNullOrEmpty(token) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            env["GIT_CONFIG_COUNT"] = "2";
            env["GIT_CONFIG_KEY_1"] = $"http.{uri.GetLeftPart(UriPartial.Authority)}/.extraHeader";
            env["GIT_CONFIG_VALUE_1"] =
                "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        }
        else
        {
            env["GIT_CONFIG_COUNT"] = "1";
        }

        var result = await GitService.RunAsync(args, dir, timeoutSeconds, env);
        return result with { Output = Scrub(result.Output, token), Error = Scrub(result.Error, token) };
    }

    private static string Scrub(string text, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(text)) return text;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        return text.Replace(token, "***").Replace(basic, "***");
    }

    private static string Explain(GitResult r)
    {
        var detail = r.ErrorSummary();
        var lower = detail.ToLowerInvariant();

        string[] authHints =
        {
            "authentication failed", "terminal prompts disabled", "could not read username",
            "invalid username or password", "repository not found", "returned error: 403", "returned error: 401",
        };

        return authHints.Any(lower.Contains)
            ? $"Accesso negato o repository non trovato: controlla l'URL e che il token abbia accesso in sola lettura a questo repository e non sia scaduto. ({detail})"
            : detail;
    }

    /// <summary>Prova la connessione (git ls-remote) con l'URL e il token indicati.</summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(string url, string? token)
    {
        var invalid = ValidateUrl(url);
        if (invalid is not null) return (false, invalid);

        var r = await RunGitAsync(new[] { "ls-remote", "--tags", "--heads", "--refs", "--", url.Trim() }, null, url.Trim(), token, 60);
        if (!r.Ok) return (false, Explain(r));

        var count = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return (true, $"Connessione riuscita: {count} riferimenti (tag e branch) trovati.");
    }

    // ---- Elenco, installazione, rimozione ----------------------------------

    /// <summary>All'avvio carica le MIB delle sorgenti gia' scaricate (senza rete).</summary>
    public static void LoadInstalled()
    {
        try
        {
            foreach (var source in All.Where(s => IsCloned(CloneDir(s))))
                MibStore.Instance.ReplaceSource(SourceKey(source), MibStore.EnumerateMibFiles(CloneDir(source)));
        }
        catch
        {
        }
    }

    public static async Task<List<MibPackageItem>> LoadItemsAsync(bool allowRemote)
    {
        var sources = All;
        var items = await Task.WhenAll(sources.Select(s => BuildItemAsync(s, allowRemote)));
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<MibPackageItem> BuildItemAsync(PersonalSource source, bool allowRemote)
    {
        var item = new MibPackageItem(source.Name, source.Id, source.Url) { SourceId = source.Id };
        var dir = CloneDir(source);
        var token = GetToken(source);
        List<string> versions = new();

        if (!string.IsNullOrEmpty(source.ProtectedToken) && token is null)
            item.Error = "Token non leggibile su questo PC/utente: modifica la sorgente e reinseriscilo.";

        if (IsCloned(dir))
        {
            if (allowRemote)
            {
                var fetch = await RunGitAsync(new[] { "fetch", "--tags", "--force", "--prune", "--quiet", "origin" }, dir, source.Url, token, 120);
                if (!fetch.Ok) item.Error = Explain(fetch);
            }

            versions = await LocalRefsAsync(dir);
            item.InstalledVersion = await CurrentRefAsync(dir);
        }
        else if (allowRemote && item.Error is null)
        {
            var remote = await RunGitAsync(new[] { "ls-remote", "--tags", "--heads", "--refs", "--", source.Url }, null, source.Url, token, 60);
            if (remote.Ok) versions = ParseRemoteRefs(remote.Output);
            else item.Error = Explain(remote);
        }

        item.SetVersions(versions);
        return item;
    }

    private static List<string> FormatRefs(IEnumerable<string> tags, IEnumerable<string> branches)
    {
        var sortedTags = tags.Where(t => RefRx.IsMatch(t)).Distinct().ToList();
        sortedTags.Sort((a, b) => MibRepoService.CompareVersions(b, a)); // dalla piu' recente

        var sortedBranches = branches.Where(b => RefRx.IsMatch(b)).Distinct()
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .Select(b => b + BranchSuffix);

        return sortedTags.Concat(sortedBranches).ToList();
    }

    private static List<string> ParseRemoteRefs(string output)
    {
        var tags = new List<string>();
        var branches = new List<string>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var refName = line.Split('\t').LastOrDefault()?.Trim() ?? "";
            if (refName.StartsWith("refs/tags/")) tags.Add(refName["refs/tags/".Length..]);
            else if (refName.StartsWith("refs/heads/")) branches.Add(refName["refs/heads/".Length..]);
        }
        return FormatRefs(tags, branches);
    }

    private static async Task<List<string>> LocalRefsAsync(string dir)
    {
        var tagResult = await GitService.RunAsync(new[] { "tag", "-l" }, dir);
        var tags = tagResult.Ok ? tagResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();

        var branchResult = await GitService.RunAsync(new[] { "for-each-ref", "--format=%(refname)", "refs/remotes/origin" }, dir);
        var branches = branchResult.Ok
            ? branchResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r["refs/remotes/origin/".Length..])
                .Where(b => b != "HEAD")
            : Enumerable.Empty<string>();

        return FormatRefs(tags, branches);
    }

    private static string RefMarker(string dir) => Path.Combine(dir, ".git", "snmpbrowser-ref");

    private static async Task<string> CurrentRefAsync(string dir)
    {
        try
        {
            var marker = RefMarker(dir);
            if (File.Exists(marker))
            {
                var text = File.ReadAllText(marker).Trim();
                if (text.EndsWith(BranchSuffix) && RefRx.IsMatch(text[..^BranchSuffix.Length])) return text;
            }
        }
        catch
        {
        }
        return await MibRepoService.CurrentVersionAsync(dir);
    }

    /// <summary>Scarica (clone o fetch) e passa alla versione/branch scelto. Restituisce l'errore, o null se ok.</summary>
    public static async Task<string?> InstallAsync(MibPackageItem item, string? selected)
    {
        var source = Find(item.SourceId);
        if (source is null) return "Sorgente non trovata.";
        if (selected is null || !item.Versions.Contains(selected)) return "Versione non valida.";

        var isBranch = MibPackageItem.IsBranchRef(selected);
        var refName = isBranch ? selected[..^BranchSuffix.Length] : selected;
        if (!RefRx.IsMatch(refName)) return "Nome di versione non valido.";

        try
        {
            var dir = CloneDir(source);
            var token = GetToken(source);

            if (!IsCloned(dir))
            {
                SafeDelete(dir);
                Directory.CreateDirectory(PersonalRoot);

                var clone = await RunGitAsync(new[] { "clone", "--quiet", "--", source.Url, dir }, PersonalRoot, source.Url, token, 600);
                if (!clone.Ok)
                {
                    SafeDelete(dir);
                    return $"Download di {source.Name} fallito: {Explain(clone)}";
                }
            }
            else
            {
                var fetch = await RunGitAsync(new[] { "fetch", "--tags", "--force", "--prune", "--quiet", "origin" }, dir, source.Url, token, 180);
                if (!fetch.Ok) return $"Aggiornamento di {source.Name} fallito: {Explain(fetch)}";
            }

            var target = isBranch ? $"refs/remotes/origin/{refName}" : $"refs/tags/{refName}";
            var checkout = await GitService.RunAsync(new[] { "checkout", "--quiet", "--detach", target }, dir);
            if (!checkout.Ok) return $"Cambio versione fallito: {checkout.ErrorSummary()}";

            try
            {
                if (isBranch) File.WriteAllText(RefMarker(dir), selected);
                else if (File.Exists(RefMarker(dir))) File.Delete(RefMarker(dir));
            }
            catch
            {
            }

            item.SetVersions(await LocalRefsAsync(dir));
            item.InstalledVersion = await CurrentRefAsync(dir);
            item.SelectedVersion = item.InstalledVersion;
            item.Error = null;

            MibStore.Instance.ReplaceSource(SourceKey(source), MibStore.EnumerateMibFiles(dir));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Scarica dal programma le MIB della sorgente e cancella il clone locale (la sorgente resta nell'elenco).</summary>
    public static Task<string?> RemoveAsync(MibPackageItem item)
    {
        var source = Find(item.SourceId);
        if (source is null) return Task.FromResult<string?>("Sorgente non trovata.");

        SafeDelete(CloneDir(source));
        MibStore.Instance.RemoveSource(SourceKey(source));
        item.InstalledVersion = null;
        return Task.FromResult<string?>(null);
    }
}
