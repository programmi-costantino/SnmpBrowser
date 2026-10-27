using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using SnmpBrowser.Models;
using SnmpBrowser.Services;

namespace SnmpBrowser.Plugins;

/// <summary>
/// Un plugin installato: legge plugin.json, calcola l'impronta SHA-256 dei suoi file e, solo se approvato,
/// esegue lo script in un interprete JavaScript isolato (niente file, rete o programmi del PC:
/// solo le funzioni app.*, snmp.* e mib.* esposte qui sotto).
/// </summary>
public sealed class PluginInstance : IDisposable
{
    private static readonly Regex IdRx = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    private static readonly string[] KnownPermissions = { "snmp.read", "snmp.write" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _decoderErrors = new();
    private Engine? _engine;
    private SnmpSettings? _settings;
    private int _budget;
    private string _mainSource = "";

    private PluginInstance(string key, string dir)
    {
        Key = key;
        Dir = dir;
    }

    public string Key { get; }
    public string Dir { get; }
    public PluginManifest Manifest { get; private set; } = new();
    public string Hash { get; private set; } = "";
    public string MainSource => _mainSource;
    public PluginState State { get; private set; } = PluginState.NeedsApproval;
    public string? ErrorText { get; private set; }

    public IReadOnlyList<PluginAction> Actions { get; private set; } = Array.Empty<PluginAction>();
    public IReadOnlyList<PluginPanel> Panels { get; private set; } = Array.Empty<PluginPanel>();
    public IReadOnlyList<PluginExporter> Exporters { get; private set; } = Array.Empty<PluginExporter>();
    public IReadOnlyList<PluginDecoder> Decoders { get; private set; } = Array.Empty<PluginDecoder>();

    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Path.GetFileName(Dir) : Manifest.Name;

    // ---- Lettura e verifica -------------------------------------------------

    /// <summary>Legge cartella e manifest, senza eseguire nulla.</summary>
    public static PluginInstance Read(string key, string dir)
    {
        var plugin = new PluginInstance(key, dir);
        try
        {
            var files = Snapshot(dir, out var hash);
            plugin.Hash = hash;

            if (!files.TryGetValue("plugin.json", out var manifestBytes))
                throw new InvalidDataException("manca il file plugin.json");

            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestBytes, Json)
                           ?? throw new InvalidDataException("plugin.json vuoto");
            Validate(manifest);
            plugin.Manifest = manifest;

            var main = manifest.Main.Replace('\\', '/');
            if (!files.TryGetValue(main, out var mainBytes))
                throw new InvalidDataException($"manca il file {manifest.Main}");

            // il codice eseguito e' esattamente quello di cui e' stata calcolata l'impronta
            plugin._mainSource = Encoding.UTF8.GetString(mainBytes);
        }
        catch (Exception ex)
        {
            plugin.State = PluginState.Error;
            plugin.ErrorText = ex.Message;
        }
        return plugin;
    }

    private static void Validate(PluginManifest m)
    {
        if (!IdRx.IsMatch(m.Id)) throw new InvalidDataException("id non valido (usa minuscole, numeri e trattini, max 40)");
        if (m.Name.Length > 60) throw new InvalidDataException("nome troppo lungo (max 60)");
        if (m.ApiVersion != 1) throw new InvalidDataException($"API versione {m.ApiVersion} non supportata (serve 1)");

        var main = m.Main.Replace('\\', '/');
        if (main.Length == 0 || !main.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || main.StartsWith('/') ||
            main.Contains(':') || main.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("'main' deve essere un file .js dentro il plugin");

        m.Permissions = m.Permissions.Select(p => p.Trim()).Distinct().ToList();
        var unknown = m.Permissions.FirstOrDefault(p => !KnownPermissions.Contains(p));
        if (unknown is not null) throw new InvalidDataException($"permesso sconosciuto: {unknown}");
    }

    private static Dictionary<string, byte[]> Snapshot(string dir, out string hash)
    {
        var root = Path.GetFullPath(dir);
        var map = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;

            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // niente collegamenti simbolici

            total += info.Length;
            if (total > 5_000_000 || map.Count >= 300) throw new InvalidDataException("plugin troppo grande");
            map[rel] = File.ReadAllBytes(file);
        }

        var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, bytes) in map)
        {
            h.AppendData(Encoding.UTF8.GetBytes(rel));
            h.AppendData(new byte[] { 0 });
            h.AppendData(BitConverter.GetBytes((long)bytes.Length));
            h.AppendData(bytes);
        }
        hash = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        return new Dictionary<string, byte[]>(map);
    }

    // ---- Stato --------------------------------------------------------------

    public void Fail(string message)
    {
        lock (_gate)
        {
            StopInternal();
            State = PluginState.Error;
            ErrorText = message;
        }
    }

    public void MarkNeedsApproval()
    {
        lock (_gate)
        {
            StopInternal();
            State = PluginState.NeedsApproval;
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            StopInternal();
            State = PluginState.Disabled;
        }
    }

    private void StopInternal()
    {
        _engine?.Dispose();
        _engine = null;
        Actions = Array.Empty<PluginAction>();
        Panels = Array.Empty<PluginPanel>();
        Exporters = Array.Empty<PluginExporter>();
        Decoders = Array.Empty<PluginDecoder>();
        _decoderErrors.Clear();
    }

    public void Dispose()
    {
        lock (_gate) StopInternal();
    }

    // ---- Avvio dello script -------------------------------------------------

    private sealed class MetaItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Extension { get; set; } = "";
        public string OidPrefix { get; set; } = "";
    }

    private sealed class Meta
    {
        public List<MetaItem> Actions { get; set; } = new();
        public List<MetaItem> Panels { get; set; } = new();
        public List<MetaItem> Exporters { get; set; } = new();
        public List<MetaItem> Decoders { get; set; } = new();
    }

    /// <summary>Esegue lo script di registrazione. Va chiamato solo dopo l'approvazione dell'utente.</summary>
    public void Start()
    {
        lock (_gate)
        {
            StopInternal();
            try
            {
                var engine = new Engine(options =>
                {
                    options.Strict();
                    options.TimeoutInterval(TimeSpan.FromSeconds(20));
                    options.LimitMemory(64L * 1024 * 1024);
                    options.LimitRecursion(256);
                });

                engine.SetValue("__hostLog", new Action<string>(HostLog));
                engine.SetValue("__hostGet", new Func<string, string>(HostGet));
                engine.SetValue("__hostWalk", new Func<string, string>(HostWalk));
                engine.SetValue("__hostSet", new Func<string, string, string, string>(HostSet));
                engine.SetValue("__hostResolve", new Func<string, string>(HostResolve));
                engine.SetValue("__hostName", new Func<string, string>(HostName));

                engine.Execute(Prelude, "prelude");
                engine.Execute(_mainSource, Manifest.Main);

                var meta = JsonSerializer.Deserialize<Meta>(engine.Evaluate("__describe()").ToString(), Json) ?? new Meta();

                Actions = meta.Actions.Select(m => new PluginAction(m.Id, m.Title)).ToList();
                Panels = meta.Panels.Select(m => new PluginPanel(m.Id, m.Title)).ToList();
                Exporters = meta.Exporters.Select(m => new PluginExporter(m.Id, m.Title, SafeExtension(m.Extension))).ToList();
                Decoders = meta.Decoders.Select(m => new PluginDecoder(m.Id, m.Title, m.OidPrefix.Trim().TrimStart('.'))).ToList();

                _engine = engine;
                State = PluginState.Active;
                ErrorText = null;
            }
            catch (Exception ex)
            {
                StopInternal();
                State = PluginState.Error;
                ErrorText = Describe(ex);
            }
        }
    }

    private static string SafeExtension(string ext)
    {
        var clean = new string(ext.Trim().TrimStart('.').Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return clean.Length == 0 ? "txt" : clean.ToLowerInvariant();
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "tempo massimo di esecuzione superato (20 s)",
        _ => ex.Message,
    };

    // ---- Chiamate dal programma allo script ---------------------------------

    /// <summary>Esegue una funzione del plugin su un thread del pool. kind: action, panel, exporter, decoder.</summary>
    public Task<PluginCallResult> CallAsync(string kind, string id, string argsJson, SnmpSettings? settings) =>
        Task.Run(() => CallSync(kind, id, argsJson, settings));

    public PluginCallResult CallSync(string kind, string id, string argsJson, SnmpSettings? settings)
    {
        lock (_gate)
        {
            if (State != PluginState.Active || _engine is null)
                return new PluginCallResult(null, "il plugin non e' attivo");

            _settings = settings;
            _budget = 300;
            try
            {
                _engine.SetValue("__k", kind);
                _engine.SetValue("__i", id);
                _engine.SetValue("__j", argsJson);
                var result = _engine.Evaluate("__call(__k, __i, __j)");
                return new PluginCallResult(result.ToString(), null);
            }
            catch (Exception ex)
            {
                return new PluginCallResult(null, Describe(ex));
            }
            finally
            {
                _settings = null;
            }
        }
    }

    public void NoteDecoderError(string id)
    {
        lock (_gate)
        {
            _decoderErrors[id] = _decoderErrors.GetValueOrDefault(id) + 1;
        }
    }

    /// <summary>Dopo 3 errori un decodificatore viene escluso, cosi' uno script difettoso non rallenta ogni riga.</summary>
    public bool IsDecoderBroken(string id)
    {
        lock (_gate)
        {
            return _decoderErrors.GetValueOrDefault(id) >= 3;
        }
    }

    // ---- Funzioni esposte allo script (i permessi si controllano qui) ------

    private static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    private static object EntryDto(SnmpEntry e) =>
        new { oid = e.Oid, name = e.Name, type = e.Type, value = e.Value, raw = e.Raw ?? e.Value };

    private static string ErrJson(string error) => ToJson(new { ok = false, error, entries = Array.Empty<object>() });

    private static string ResultJson(SnmpResult r) =>
        ToJson(new { ok = r.Ok, error = r.Error, entries = r.Entries.Select(EntryDto).ToList() });

    private bool Has(string permission) => Manifest.Permissions.Contains(permission);

    private void HostLog(string message) =>
        System.Diagnostics.Debug.WriteLine($"[plugin {Manifest.Id}] {message}");

    private string HostGet(string oid) => Snmp("snmp.read", oid, s => SnmpService.GetAsync(s, oid));

    private string HostWalk(string oid) => Snmp("snmp.read", oid, s => SnmpService.WalkAsync(s, oid));

    private string HostSet(string oid, string type, string value)
    {
        if (!Has("snmp.write")) return ErrJson("permesso mancante: snmp.write");

        var invalid = InputValidator.SetValue(type, value);
        if (invalid is not null) return ErrJson(invalid);

        return Snmp("snmp.write", oid, s =>
            PluginManager.Instance.ConfirmWrite(DisplayName, oid, type, value)
                ? SnmpService.SetAsync(s, oid, type, value)
                : Task.FromResult(new SnmpResult(new List<SnmpEntry>(), "Scrittura annullata dall'utente.")));
    }

    private string Snmp(string permission, string oid, Func<SnmpSettings, Task<SnmpResult>> operation)
    {
        if (!Has(permission)) return ErrJson($"permesso mancante: {permission}");
        if (_settings is null) return ErrJson("nessuna connessione valida: controlla host, porta e timeout nella finestra principale");
        if (--_budget < 0) return ErrJson("troppe chiamate SNMP in una sola esecuzione (max 300)");

        var invalid = InputValidator.Oid(oid);
        if (invalid is not null) return ErrJson(invalid);

        // lo script gira gia' su un thread del pool: attendere qui non blocca l'interfaccia
        return ResultJson(operation(_settings).GetAwaiter().GetResult());
    }

    private static string HostResolve(string text)
    {
        if (MibStore.Instance.TryResolveText(text, out var oid)) return oid;
        return InputValidator.Oid(text) is null ? text.Trim().TrimStart('.') : "";
    }

    private static string HostName(string oid) => MibNames.Resolve(oid.Trim().TrimStart('.'));

    // ---- Script di base eseguito in ogni plugin -----------------------------

    private const string Prelude = """
var __handlers = { action: {}, panel: {}, exporter: {}, decoder: {} };
var __meta = { actions: [], panels: [], exporters: [], decoders: [] };
var __entry = { action: 'run', panel: 'render', exporter: 'export', decoder: 'decode' };
var __plural = { action: 'actions', panel: 'panels', exporter: 'exporters', decoder: 'decoders' };

function __reg(kind, def) {
  var fn = __entry[kind];
  if (!def || typeof def.id !== 'string' || typeof def[fn] !== 'function') {
    throw new Error('definizione non valida: servono id (testo) e ' + fn + '() (funzione)');
  }
  __handlers[kind][def.id] = def;
  var m = { id: def.id, title: String(def.title || def.id) };
  if (kind === 'exporter') m.extension = String(def.extension || 'txt');
  if (kind === 'decoder') m.oidPrefix = String(def.oidPrefix || '');
  __meta[__plural[kind]].push(m);
}

var app = {
  registerAction: function (d) { __reg('action', d); },
  registerPanel: function (d) { __reg('panel', d); },
  registerExporter: function (d) { __reg('exporter', d); },
  registerDecoder: function (d) { __reg('decoder', d); },
  log: function (m) { __hostLog(String(m)); }
};

var snmp = {
  get: function (oid) { return JSON.parse(__hostGet(String(oid))); },
  walk: function (oid) { return JSON.parse(__hostWalk(String(oid))); },
  set: function (oid, type, value) { return JSON.parse(__hostSet(String(oid), String(type), String(value))); }
};

var mib = {
  resolve: function (text) { return __hostResolve(String(text)); },
  name: function (oid) { return __hostName(String(oid)); }
};

function __call(kind, id, argsJson) {
  var d = __handlers[kind] && __handlers[kind][id];
  if (!d) throw new Error('funzione non trovata: ' + kind + ' ' + id);
  var r = d[__entry[kind]](JSON.parse(argsJson));
  return r === undefined ? 'null' : JSON.stringify(r);
}

function __describe() { return JSON.stringify(__meta); }
""";
}
