using System.Text.Json;

namespace SnmpBrowser.Services;

/// <summary>
/// Ricorda su disco le versioni (tag) dei pacchetti non ancora installati, cosi' l'elenco e' completo
/// anche all'apertura della finestra o senza rete, senza dover premere "Aggiorna elenco".
/// </summary>
internal sealed class TagCache
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, List<string>>? _data;

    public TagCache(string file) => _file = file;

    public List<string> Get(string key)
    {
        lock (_gate)
        {
            Load();
            return _data!.TryGetValue(key, out var list) ? list.ToList() : new List<string>();
        }
    }

    public void Set(string key, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            Load();
            var list = tags.ToList();
            if (_data!.TryGetValue(key, out var old) && old.SequenceEqual(list)) return;

            _data[key] = list;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, JsonSerializer.Serialize(_data));
            }
            catch
            {
                // la cache e' solo un aiuto: se non si salva, l'elenco si riscarica
            }
        }
    }

    private void Load()
    {
        if (_data is not null) return;

        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_file));
        }
        catch
        {
        }
        _data ??= new Dictionary<string, List<string>>();
    }
}
