using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SnmpBrowser.Models;

/// <summary>Una riga della tabella "Repository MIB": un pacchetto (submodule) con le sue versioni.</summary>
public sealed class MibPackageItem : INotifyPropertyChanged
{
    private string? _selectedVersion;
    private string? _installedVersion;
    private bool _isBusy;
    private string _busyText = "";

    public MibPackageItem(string name, string path, string url)
    {
        Name = name;
        Path = path;
        Url = url;
    }

    public string Name { get; }
    public string Path { get; }
    public string Url { get; }
    public ObservableCollection<string> Versions { get; } = new();

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set { _selectedVersion = value; Notify(); }
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        set { _installedVersion = value; Notify(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; Notify(); }
    }

    public string? Latest => Versions.FirstOrDefault();
    public bool IsInstalled => _installedVersion is not null;
    public string InstalledText => _installedVersion ?? "-";
    public string LatestText => Latest ?? "-";

    public string ActionText => !IsInstalled ? "Installa" : _selectedVersion == _installedVersion ? "Installata" : "Applica";
    public bool CanApply => !_isBusy && _selectedVersion is not null && (!IsInstalled || _selectedVersion != _installedVersion);
    public bool CanRemove => !_isBusy && IsInstalled;

    public string StatusText
    {
        get
        {
            if (_isBusy) return _busyText;
            if (!IsInstalled) return Versions.Count == 0 ? "Non installata (nessuna versione taggata)" : "Non installata";
            return Latest is not null && Latest != _installedVersion ? "Aggiornamento disponibile" : "Aggiornata";
        }
    }

    public void SetBusy(bool busy, string text = "")
    {
        _busyText = text;
        IsBusy = busy;
    }

    /// <summary>Sostituisce l'elenco versioni mantenendo, se possibile, la selezione attuale.</summary>
    public void SetVersions(IEnumerable<string> versions)
    {
        Versions.Clear();
        foreach (var v in versions) Versions.Add(v);

        if (_selectedVersion is null || !Versions.Contains(_selectedVersion))
            _selectedVersion = _installedVersion is not null && Versions.Contains(_installedVersion)
                ? _installedVersion
                : Latest ?? _installedVersion;
        Notify();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // stringa vuota = "tutte le proprieta' sono cambiate"
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
