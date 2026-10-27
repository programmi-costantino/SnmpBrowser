using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SnmpBrowser.Models;

/// <summary>Una riga della tabella "Repository MIB": un pacchetto con le sue versioni (tag, e branch per le sorgenti personali).</summary>
public sealed class MibPackageItem : INotifyPropertyChanged
{
    public const string BranchSuffix = " (branch)";

    private string? _selectedVersion;
    private string? _installedVersion;
    private string? _error;
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

    /// <summary>Id della sorgente personale; null per i pacchetti del repository centrale.</summary>
    public string? SourceId { get; init; }

    public bool IsPersonal => SourceId is not null;
    public string SourceText => IsPersonal ? "Personale" : "Centrale";
    public ObservableCollection<string> Versions { get; } = new();

    public static bool IsBranchRef(string? version) => version?.EndsWith(BranchSuffix, StringComparison.Ordinal) == true;

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

    public string? Error
    {
        get => _error;
        set { _error = value; Notify(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; Notify(); }
    }

    /// <summary>Ultima versione taggata (i branch non contano come "ultima").</summary>
    public string? Latest => Versions.FirstOrDefault(v => !IsBranchRef(v));

    public bool IsInstalled => _installedVersion is not null;
    public string InstalledText => _installedVersion ?? "-";
    public string LatestText => Latest ?? "-";
    private bool BranchSelected => IsBranchRef(_selectedVersion);

    public string ActionText
    {
        get
        {
            if (!IsInstalled) return "Installa";
            if (_selectedVersion == _installedVersion) return BranchSelected ? "Aggiorna" : "Installata";
            return "Applica";
        }
    }

    public bool CanApply =>
        !_isBusy && _selectedVersion is not null &&
        (!IsInstalled || _selectedVersion != _installedVersion || BranchSelected);

    public bool CanRemove => !_isBusy && IsInstalled;

    public string StatusText
    {
        get
        {
            if (_isBusy) return _busyText;
            if (!string.IsNullOrEmpty(_error)) return _error;
            if (!IsInstalled) return Versions.Count == 0 ? "Non installata (nessuna versione)" : "Non installata";
            if (IsBranchRef(_installedVersion)) return "Segue un branch: premi Aggiorna per l'ultima versione";
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
                : Latest ?? Versions.FirstOrDefault();
        Notify();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // stringa vuota = "tutte le proprieta' sono cambiate"
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
