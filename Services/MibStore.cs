using System.Text;
using System.Text.RegularExpressions;
using SnmpBrowser.Models;

namespace SnmpBrowser.Services;

public sealed record MibImportResult(int Loaded, List<string> Warnings)
{
    public string Summary()
    {
        var text = Loaded == 1 ? "1 MIB caricata" : $"{Loaded} MIB caricate";
        if (Warnings.Count > 0)
            text += $" - {Warnings.Count} avvis{(Warnings.Count == 1 ? "o" : "i")}: {Warnings[0]}";
        return text;
    }
}

/// <summary>
/// Archivio delle MIB caricate: risolve nomi &lt;-&gt; OID e fornisce descrizioni.
/// I file importati vengono copiati in %AppData%\SnmpBrowser\mibs e ricaricati a ogni avvio.
/// </summary>
public sealed class MibStore
{
    private static readonly HashSet<string> BuiltinModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "SNMPv2-SMI", "SNMPv2-TC", "SNMPv2-CONF", "SNMPv2-SMI-v1", "RFC1155-SMI", "RFC1065-SMI", "RFC-1212", "RFC-1215",
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mib", ".txt", ".my", ".smi", ".asn", ".asn1", ".mi2", "",
    };

    /// <summary>Radici standard dell'albero, sempre note anche senza caricare SNMPv2-SMI.</summary>
    private static readonly Dictionary<string, string> Roots = new()
    {
        ["ccitt"] = "0", ["iso"] = "1", ["joint-iso-ccitt"] = "2", ["zeroDotZero"] = "0.0",
        ["org"] = "1.3", ["dod"] = "1.3.6", ["internet"] = "1.3.6.1", ["directory"] = "1.3.6.1.1",
        ["mgmt"] = "1.3.6.1.2", ["mib-2"] = "1.3.6.1.2.1", ["transmission"] = "1.3.6.1.2.1.10",
        ["experimental"] = "1.3.6.1.3", ["private"] = "1.3.6.1.4", ["enterprises"] = "1.3.6.1.4.1",
        ["security"] = "1.3.6.1.5", ["snmpV2"] = "1.3.6.1.6", ["snmpDomains"] = "1.3.6.1.6.1",
        ["snmpProxys"] = "1.3.6.1.6.2", ["snmpModules"] = "1.3.6.1.6.3",
    };

    private static readonly Regex NameNumRx = new(@"^[A-Za-z][A-Za-z0-9_-]*\((?<v>\d+)\)$", RegexOptions.Compiled);

    public static MibStore Instance { get; } = new();

    public static string UserDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "mibs");

    private readonly List<ParsedModule> _modules = new();
    private readonly object _gate = new();

    private Dictionary<string, MibSymbol> _byOid = new();
    private Dictionary<string, MibSymbol> _byName = new();
    private Dictionary<string, MibSymbol> _byQualified = new();

    public IReadOnlyList<MibModuleInfo> Modules { get; private set; } = Array.Empty<MibModuleInfo>();
    public int SymbolCount => _byOid.Count;

    private MibStore()
    {
    }

    // ---- Caricamento ------------------------------------------------------

    /// <summary>Carica le MIB gia' importate in precedenza.</summary>
    public void LoadStartup()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(UserDir);
                foreach (var file in EnumerateMibFiles(UserDir))
                {
                    try
                    {
                        foreach (var module in MibParser.Parse(File.ReadAllText(file), file))
                        {
                            if (!_modules.Any(m => string.Equals(m.Name, module.Name, StringComparison.OrdinalIgnoreCase)))
                                _modules.Add(module);
                        }
                    }
                    catch
                    {
                        // file illeggibile: lo salto, l'utente puo' rimuoverlo dal gestore MIB
                    }
                }
            }
            catch
            {
            }
            Rebuild();
        }
    }

    public static IEnumerable<string> EnumerateMibFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".git"));

    /// <summary>Importa uno o piu' file MIB (li copia nella cartella dell'utente).</summary>
    public MibImportResult Import(IEnumerable<string> paths)
    {
        var warnings = new List<string>();
        var names = new List<string>();
        var loaded = 0;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(UserDir);
            }
            catch (Exception ex)
            {
                warnings.Add($"Cartella MIB non accessibile: {ex.Message}");
            }

            foreach (var path in paths)
            {
                var fileName = Path.GetFileName(path);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) { warnings.Add($"{fileName}: file non trovato"); continue; }
                    if (info.Length > 20_000_000) { warnings.Add($"{fileName}: file troppo grande"); continue; }

                    var parsed = MibParser.Parse(File.ReadAllText(path), path);
                    if (parsed.Count == 0 || parsed.All(m => m.Defs.Count == 0))
                    {
                        warnings.Add($"{fileName}: non sembra una MIB valida (nessuna definizione trovata)");
                        continue;
                    }

                    var dest = Path.Combine(UserDir, info.Name);
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        File.Copy(path, dest, overwrite: true);

                    foreach (var module in parsed)
                    {
                        module.FilePath = dest;

                        // stesso modulo gia' caricato: lo sostituisco con la nuova versione
                        foreach (var old in _modules.Where(m => string.Equals(m.Name, module.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                        {
                            _modules.Remove(old);
                            if (!string.Equals(old.FilePath, dest, StringComparison.OrdinalIgnoreCase)
                                && !_modules.Any(m => m.FilePath == old.FilePath))
                                TryDeleteUserFile(old.FilePath);
                        }

                        _modules.Add(module);
                        names.Add(module.Name);
                        loaded++;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{fileName}: {ex.Message}");
                }
            }

            Rebuild();

            foreach (var name in names)
            {
                var info = Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (info is { IsOk: false }) warnings.Add($"{info.Name}: {info.StatusText}");
            }
        }

        Changed?.Invoke();
        return new MibImportResult(loaded, warnings);
    }

    public bool Remove(string moduleName)
    {
        lock (_gate)
        {
            var module = _modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            if (module is null) return false;

            _modules.Remove(module);
            if (!_modules.Any(m => m.FilePath == module.FilePath))
                TryDeleteUserFile(module.FilePath);

            Rebuild();
        }

        Changed?.Invoke();
        return true;
    }

    public event Action? Changed;

    /// <summary>
    /// Carica le MIB di una sorgente esterna (es. un modulo del repository MIB) senza copiarle:
    /// sostituisce quanto caricato in precedenza dalla stessa sorgente.
    /// </summary>
    public void ReplaceSource(string source, IEnumerable<string> files)
    {
        lock (_gate)
        {
            _modules.RemoveAll(m => m.Source == source);

            foreach (var file in files)
            {
                try
                {
                    foreach (var module in MibParser.Parse(File.ReadAllText(file), file))
                    {
                        module.Source = source;
                        _modules.RemoveAll(m => string.Equals(m.Name, module.Name, StringComparison.OrdinalIgnoreCase));
                        _modules.Add(module);
                    }
                }
                catch
                {
                    // file illeggibile: saltato
                }
            }
            Rebuild();
        }
        Changed?.Invoke();
    }

    public void RemoveSource(string source)
    {
        lock (_gate)
        {
            _modules.RemoveAll(m => m.Source == source);
            Rebuild();
        }
        Changed?.Invoke();
    }

    private static void TryDeleteUserFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(UserDir) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                File.Delete(full);
        }
        catch
        {
        }
    }

    // ---- Risoluzione OID --------------------------------------------------

    private void Rebuild()
    {
        var names = new Dictionary<string, string>(Roots);
        var byOid = new Dictionary<string, MibSymbol>();
        var byName = new Dictionary<string, MibSymbol>();
        var byQualified = new Dictionary<string, MibSymbol>();
        var counts = new Dictionary<ParsedModule, int>();

        var pending = _modules.SelectMany(m => m.Defs.Select(d => (Module: m, Def: d))).ToList();
        foreach (var m in _modules) counts[m] = 0;

        // Passate ripetute: una definizione si risolve quando il suo "padre" e' gia' noto (anche in un altro modulo)
        bool progress;
        do
        {
            progress = false;
            var next = new List<(ParsedModule Module, RawDef Def)>();

            foreach (var item in pending)
            {
                if (!TryResolve(item.Def.Tokens, names, out var oid))
                {
                    next.Add(item);
                    continue;
                }

                var def = item.Def;
                var symbol = new MibSymbol
                {
                    Name = def.Name,
                    Module = item.Module.Name,
                    Oid = oid,
                    Kind = def.Kind,
                    Syntax = def.Syntax,
                    Access = def.Access,
                    Status = def.Status,
                    Units = def.Units,
                    Description = def.Description,
                    Enums = def.Enums,
                };

                names.TryAdd(def.Name, oid);
                byOid.TryAdd(oid, symbol);
                byName.TryAdd(def.Name, symbol);
                byQualified.TryAdd($"{item.Module.Name}::{def.Name}", symbol);
                counts[item.Module]++;
                progress = true;
            }
            pending = next;
        }
        while (progress && pending.Count > 0);

        var loadedNames = new HashSet<string>(_modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var infos = new List<MibModuleInfo>();

        foreach (var module in _modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var unresolved = module.Defs.Count - counts[module];
            var status = "OK";

            if (unresolved > 0)
            {
                var missing = module.Imports
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(i => !loadedNames.Contains(i) && !BuiltinModules.Contains(i))
                    .ToList();

                status = $"{unresolved} simboli non risolti";
                if (missing.Count > 0) status += $" - carica anche: {string.Join(", ", missing)}";
            }

            infos.Add(new MibModuleInfo
            {
                Name = module.Name,
                FilePath = module.FilePath,
                Origin = module.Source ?? "",
                SymbolCount = counts[module],
                StatusText = status,
                IsOk = unresolved == 0,
            });
        }

        _byOid = byOid;
        _byName = byName;
        _byQualified = byQualified;
        Modules = infos;
    }

    private static bool TryResolve(List<string> tokens, Dictionary<string, string> names, out string oid)
    {
        oid = "";
        var parts = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var named = NameNumRx.Match(token);

            if (named.Success)
            {
                parts.Add(named.Groups["v"].Value);
            }
            else if (token.Length > 0 && token.All(char.IsAsciiDigit))
            {
                parts.Add(token);
            }
            else if (i == 0)
            {
                var key = token[(token.LastIndexOf('.') + 1)..]; // "SNMPv2-SMI.enterprises" -> "enterprises"
                if (!names.TryGetValue(key, out var baseOid)) return false;
                parts.Add(baseOid);
            }
            else
            {
                return false;
            }
        }

        if (parts.Count == 0) return false;
        oid = string.Join('.', parts);
        return true;
    }

    /// <summary>Simbolo con il prefisso piu' lungo dell'OID (es. ifDescr per 1.3.6.1.2.1.2.2.1.2.5). suffix = ".5".</summary>
    public MibSymbol? Lookup(string oid, out string suffix)
    {
        var map = _byOid;
        var current = oid;

        while (true)
        {
            if (map.TryGetValue(current, out var symbol))
            {
                suffix = oid[current.Length..];
                return symbol;
            }

            var dot = current.LastIndexOf('.');
            if (dot < 0) break;
            current = current[..dot];
        }

        suffix = "";
        return null;
    }

    public string? Exact(string oid) => _byOid.TryGetValue(oid, out var symbol) ? symbol.Name : null;

    /// <summary>Accetta "ifDescr.5", "IF-MIB::ifDescr.5" o un OID numerico gia' pronto.</summary>
    public bool TryResolveText(string text, out string oid)
    {
        oid = "";
        var t = text.Trim().TrimStart('.');
        if (t.Length == 0 || !t.Any(char.IsLetter)) return false;

        string? module = null;
        var sep = t.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
        {
            module = t[..sep];
            t = t[(sep + 2)..];
        }

        var dot = t.IndexOf('.');
        var name = dot < 0 ? t : t[..dot];
        var rest = dot < 0 ? "" : t[dot..];
        if (rest.Length > 0 && !Regex.IsMatch(rest, @"^(\.\d+)+$")) return false;

        string? baseOid = null;
        if (module is not null)
        {
            if (_byQualified.TryGetValue($"{module}::{name}", out var q)) baseOid = q.Oid;
        }
        else if (_byName.TryGetValue(name, out var symbol))
        {
            baseOid = symbol.Oid;
        }
        else if (Roots.TryGetValue(name, out var root))
        {
            baseOid = root;
        }
        else if (MibNames.TryByName(name, out var known))
        {
            baseOid = known;
        }

        if (baseOid is null) return false;
        oid = baseOid + rest;
        return true;
    }

    /// <summary>Converte un testo (nome o numerico) in un OID numerico senza punto iniziale.</summary>
    public string NormalizeOid(string text) =>
        TryResolveText(text, out var oid) ? oid : text.Trim().TrimStart('.');

    // ---- Decodifica e descrizione ----------------------------------------

    /// <summary>Per gli Integer32 con enumerazione mostra "up(1)" invece di "1".</summary>
    public string DecorateValue(string oid, string type, string raw)
    {
        if (type != "Integer32") return raw;

        var symbol = Lookup(oid, out _);
        if (symbol is null || symbol.Enums.Count == 0) return raw;

        return int.TryParse(raw, out var n) && symbol.Enums.TryGetValue(n, out var label) ? $"{label}({n})" : raw;
    }

    /// <summary>Testo descrittivo (nome, modulo, tipo, accesso, descrizione) per il pannello di dettaglio.</summary>
    public string? Describe(string oid)
    {
        var symbol = Lookup(oid, out var suffix);
        if (symbol is null) return null;

        var sb = new StringBuilder();
        sb.Append(symbol.Name).Append(suffix).Append("  -  ").Append(symbol.Module).Append("  -  ").Append(symbol.Kind);

        var meta = new List<string>();
        if (!string.IsNullOrEmpty(symbol.Syntax)) meta.Add($"Tipo: {symbol.Syntax}");
        if (!string.IsNullOrEmpty(symbol.Access)) meta.Add($"Accesso: {symbol.Access}");
        if (!string.IsNullOrEmpty(symbol.Status)) meta.Add($"Stato: {symbol.Status}");
        if (!string.IsNullOrEmpty(symbol.Units)) meta.Add($"Unita': {symbol.Units}");
        if (meta.Count > 0) sb.AppendLine().Append(string.Join("  |  ", meta));

        if (!string.IsNullOrEmpty(symbol.Description)) sb.AppendLine().Append(symbol.Description);
        return sb.ToString();
    }
}
