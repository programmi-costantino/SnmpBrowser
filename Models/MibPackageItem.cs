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

    /// <summary>Chiave con cui il pacchetto e' registrato (serve a ritrovare il plugin installato).</summary>
    public string? ActivationKey { get; init; }

    private string? _pluginState;
    private string _pluginActionText = "";
    private bool _pluginActionVisible;

    public string PluginActionText => _pluginActionText;
    public bool PluginActionVisible => _pluginActionVisible;

    public void SetPluginInfo(string? stateText, string actionText, bool actionVisible)
    {
        _pluginState = stateText;
        _pluginActionText = actionText;
        _pluginActionVisible = actionVisible;
        Notify(nameof(PluginActionText), nameof(PluginActionVisible), nameof(StatusText));
    }

    /// <summary>Id della sorgente personale; null per i pacchetti del repository centrale.</summary>
    public string? SourceId { get; init; }

    /// <summary>Plugin installato da una cartella o da un file .zip (non appartiene a nessun repository).</summary>
    public bool IsLocal { get; init; }

    private bool _pluginMqtt;
    public bool PluginMqttVisible => _pluginMqtt;

    public void SetPluginMqtt(bool visible)
    {
        _pluginMqtt = visible;
        Notify(nameof(PluginMqttVisible));
    }

    public bool IsPersonal => SourceId is not null;
    public string SourceText => IsLocal ? "File" : IsPersonal ? "Personale" : "Centrale";
    public ObservableCollection<string> Versions { get; } = new();

    public static bool IsBranchRef(string? version) => version?.EndsWith(BranchSuffix, StringComparison.Ordinal) == true;

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (_selectedVersion == value) return;
            _selectedVersion = value;
            Notify(nameof(SelectedVersion), nameof(ActionText), nameof(CanApply));
        }
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        set
        {
            _installedVersion = value;
            Notify(nameof(InstalledVersion), nameof(InstalledText), nameof(IsInstalled), nameof(ActionText),
                nameof(CanApply), nameof(CanRemove), nameof(StatusText));
        }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; Notify(nameof(Error), nameof(StatusText)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; Notify(nameof(IsBusy), nameof(CanApply), nameof(CanRemove), nameof(StatusText)); }
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
            if (IsLocal) return "-";
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
            string status;
            if (IsLocal) status = "Installato da file";
            else if (IsBranchRef(_installedVersion)) status = "Segue un branch: premi Aggiorna per l'ultima versione";
            else status = Latest is not null && Latest != _installedVersion ? "Aggiornamento disponibile" : "Aggiornata";
            return _pluginState is null ? status : $"{status} - plugin: {_pluginState}";
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
        Notify(nameof(SelectedVersion), nameof(LatestText), nameof(ActionText), nameof(CanApply), nameof(StatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Notifica solo le proprieta' indicate. Non uso la notifica "tutto e' cambiato": con ComboBox legati a
    /// ItemsSource/SelectedItem puo' far ricaricare l'elenco e perdere la versione scelta dall'utente.
    /// </summary>
    private void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
