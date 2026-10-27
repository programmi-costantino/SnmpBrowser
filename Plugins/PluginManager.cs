using System.Text.Json;
using System.Windows;
using SnmpBrowser.Models;
using SnmpBrowser.Services;

namespace SnmpBrowser.Plugins;

/// <summary>
/// Elenco dei plugin installati e registro delle approvazioni. Un plugin gira solo se l'utente l'ha approvato
/// e il suo codice (impronta SHA-256) e' identico a quello approvato.
/// </summary>
public sealed class PluginManager
{
    public static PluginManager Instance { get; } = new();

    private sealed class TrustRecord
    {
        public string Hash { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    private static string TrustFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "plugins-trust.json");

    private readonly object _gate = new();
    private readonly Dictionary<string, PluginInstance> _byKey = new();
    private Dictionary<string, TrustRecord>? _trust;

    private PluginManager()
    {
    }

    /// <summary>Segnala qualsiasi cambiamento (installazione, rimozione, attivazione). Puo' arrivare da qualsiasi thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<PluginInstance> All
    {
        get
        {
            lock (_gate) return _byKey.Values.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IEnumerable<PluginInstance> Active => All.Where(p => p.State == PluginState.Active);

    public PluginInstance? Find(string key)
    {
        lock (_gate) return _byKey.GetValueOrDefault(key);
    }

    // ---- Registro approvazioni ---------------------------------------------

    private Dictionary<string, TrustRecord> Trust()
    {
        if (_trust is not null) return _trust;

        try
        {
            if (File.Exists(TrustFile))
            {
                _trust = JsonSerializer.Deserialize<Dictionary<string, TrustRecord>>(File.ReadAllText(TrustFile));
                if (_trust is not null) return _trust;
            }
        }
        catch
        {
        }
        return _trust = new Dictionary<string, TrustRecord>();
    }

    private void SaveTrust()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TrustFile)!);
            File.WriteAllText(TrustFile, JsonSerializer.Serialize(Trust(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // l'approvazione resta valida finche' l'app e' aperta; alla prossima apertura verra' richiesta di nuovo
        }
    }

    // ---- Installazione / rimozione -----------------------------------------

    /// <summary>Chiamato dal catalogo quando un pacchetto viene installato o cambia versione.</summary>
    public void Load(string key, string dir)
    {
        PluginInstance instance;
        var start = false;

        lock (_gate)
        {
            if (_byKey.Remove(key, out var old)) old.Dispose();

            instance = PluginInstance.Read(key, dir);
            if (instance.State != PluginState.Error)
            {
                var duplicate = _byKey.Values.FirstOrDefault(p =>
                    p.State != PluginState.Error && p.Manifest.Id == instance.Manifest.Id);

                if (duplicate is not null)
                {
                    instance.Fail($"l'id '{instance.Manifest.Id}' e' gia' usato da un altro plugin installato");
                }
                else if (Trust().TryGetValue(instance.Manifest.Id, out var record) && record.Hash == instance.Hash)
                {
                    if (record.Enabled) start = true;
                    else instance.Disable();
                }
                else
                {
                    instance.MarkNeedsApproval();
                }
            }
            _byKey[key] = instance;
        }

        if (start) instance.Start(); // fuori dal lock: lo script potrebbe essere lento
        Changed?.Invoke();
    }

    public void Unload(string key)
    {
        PluginInstance? removed;
        lock (_gate) _byKey.Remove(key, out removed);

        removed?.Dispose();
        Changed?.Invoke();
    }

    // ---- Approvazione e attivazione ----------------------------------------

    /// <summary>L'utente ha visto autore, permessi e impronta e conferma: il plugin viene attivato.</summary>
    public async Task ApproveAsync(PluginInstance plugin)
    {
        if (plugin.State == PluginState.Error) return;

        lock (_gate)
        {
            Trust()[plugin.Manifest.Id] = new TrustRecord { Hash = plugin.Hash, Enabled = true };
            SaveTrust();
        }

        await Task.Run(plugin.Start);
        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(PluginInstance plugin, bool enabled)
    {
        if (plugin.State is PluginState.Error or PluginState.NeedsApproval) return;

        lock (_gate)
        {
            if (Trust().TryGetValue(plugin.Manifest.Id, out var record))
            {
                record.Enabled = enabled;
                SaveTrust();
            }
        }

        if (enabled) await Task.Run(plugin.Start);
        else plugin.Disable();
        Changed?.Invoke();
    }

    // ---- Servizi per gli script --------------------------------------------

    /// <summary>Ogni scrittura SNMP richiesta da un plugin passa da una conferma dell'utente.</summary>
    public bool ConfirmWrite(string pluginName, string oid, string type, string value)
    {
        var app = Application.Current;
        if (app is null) return false;

        return app.Dispatcher.Invoke(() =>
            MessageBox.Show(
                $"Il plugin '{pluginName}' vuole scrivere sul dispositivo:\n\n{oid} = {value} ({type})\n\nPermettere la scrittura?",
                "Conferma scrittura del plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    }

    /// <summary>Applica i decodificatori dei plugin attivi al valore di una riga.</summary>
    public string DecodeValue(string oid, string type, string raw, string current)
    {
        foreach (var plugin in Active)
        {
            foreach (var decoder in plugin.Decoders)
            {
                var prefix = decoder.OidPrefix;
                if (prefix.Length > 0 && oid != prefix && !oid.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                if (plugin.IsDecoderBroken(decoder.Id)) continue;

                var args = JsonSerializer.Serialize(
                    new { oid, name = MibNames.Resolve(oid), type, value = current, raw },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                var result = plugin.CallSync("decoder", decoder.Id, args, null);
                if (!result.Ok)
                {
                    plugin.NoteDecoderError(decoder.Id);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(result.Json ?? "null");
                    switch (doc.RootElement.ValueKind)
                    {
                        case JsonValueKind.String: current = doc.RootElement.GetString() ?? current; break;
                        case JsonValueKind.Number: current = doc.RootElement.GetRawText(); break;
                    }
                }
                catch
                {
                    plugin.NoteDecoderError(decoder.Id);
                }
            }
        }
        return current;
    }
}
