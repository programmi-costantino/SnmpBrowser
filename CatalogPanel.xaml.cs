using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SnmpBrowser.Models;
using SnmpBrowser.Plugins;
using SnmpBrowser.Services;

namespace SnmpBrowser;

/// <summary>Converter booleano inverso (usato per disabilitare il menu mentre una riga e' occupata).</summary>
public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Tabella di un catalogo (MIB o plugin): repository centrale + sorgenti personali, con menu versione per riga.
/// Per i plugin aggiunge il pulsante Attiva/Disattiva.
/// </summary>
public partial class CatalogPanel : UserControl
{
    private readonly RepoCatalog _central;
    private readonly PersonalCatalog _personal;
    private readonly bool _isPlugin;
    private readonly ObservableCollection<MibPackageItem> _items = new();
    private bool _busy;
    private bool _initialized;

    public CatalogPanel(RepoCatalog central, PersonalCatalog personal, bool isPlugin, string intro)
    {
        _central = central;
        _personal = personal;
        _isPlugin = isPlugin;

        InitializeComponent();
        IntroText.Text = intro;
        PackagesGrid.ItemsSource = _items;
        UrlBox.Text = central.LoadUrl();
        InstallFileButton.Visibility = isPlugin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isPlugin) PluginManager.Instance.Changed += OnPluginsChanged;
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isPlugin) PluginManager.Instance.Changed -= OnPluginsChanged;
    }

    private void OnPluginsChanged() => Dispatcher.BeginInvoke(RefreshPluginInfo);

    private static readonly Dictionary<string, DateTime> LastAutoSync = new();
    private static readonly TimeSpan AutoSyncEvery = TimeSpan.FromMinutes(15);

    /// <summary>Carica l'elenco la prima volta che la scheda viene mostrata e lo aggiorna da solo (al massimo ogni 15 minuti).</summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // prima l'elenco dai cloni locali e dalle versioni ricordate (istantaneo, anche senza rete)
        await ReloadAsync(centralRemote: false, personalRemote: false);

        // poi l'aggiornamento automatico dalla rete: non serve piu' premere "Aggiorna elenco" a ogni apertura
        var key = _central.Profile.Key;
        var canSync = RepoCatalog.ValidateUrl(UrlBox.Text) is null || _personal.All.Count > 0;
        var stale = !LastAutoSync.TryGetValue(key, out var last) || DateTime.UtcNow - last > AutoSyncEvery;

        if (canSync && stale)
        {
            LastAutoSync[key] = DateTime.UtcNow;
            await ReloadAsync(centralRemote: true, personalRemote: true, automatic: true);
        }
        else if (_items.Count == 0)
        {
            MessageText.Text = "Inserisci l'URL del repository centrale e premi \"Aggiorna elenco\", oppure aggiungi una sorgente personale.";
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        LastAutoSync[_central.Profile.Key] = DateTime.UtcNow;
        await ReloadAsync(centralRemote: true, personalRemote: true);
    }

    private async void AddPersonalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (new PersonalSourceWindow(_personal) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await ReloadAsync(centralRemote: false, personalRemote: true);
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem { IsPersonal: true } item) return;

        var source = _personal.Find(item.SourceId);
        if (source is null) return;

        if (new PersonalSourceWindow(_personal, source) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await ReloadAsync(centralRemote: false, personalRemote: true);
    }

    /// <summary>Ricostruisce la tabella: repository centrale + sorgenti personali.</summary>
    private async Task ReloadAsync(bool centralRemote, bool personalRemote, bool automatic = false)
    {
        if (_busy) return;
        SetBusy(true);
        MessageText.Text = centralRemote || personalRemote ? "Aggiornamento dell'elenco in corso..." : "";

        try
        {
            var all = new List<MibPackageItem>();
            string? centralError = null;
            var urlError = RepoCatalog.ValidateUrl(UrlBox.Text);

            if (centralRemote && urlError is null)
            {
                var (items, error) = await _central.SyncAsync(UrlBox.Text);
                all.AddRange(items);
                centralError = error;

                // senza rete il repository non si aggiorna, ma l'elenco gia' scaricato resta visibile
                if (error is not null && items.Count == 0 && _central.IsCloned)
                    all.AddRange(await _central.LoadLocalAsync());
            }
            else
            {
                if (_central.IsCloned) all.AddRange(await _central.LoadLocalAsync());
                if (centralRemote && !automatic) centralError = urlError;
            }

            var personal = await _personal.LoadItemsAsync(personalRemote);
            all.AddRange(personal);
            if (_isPlugin) all.AddRange(LocalPluginStore.ListItems());

            // se l'utente aveva scelto una versione nel menu, la mantengo anche dopo l'aggiornamento
            var previous = new Dictionary<string, string?>();
            foreach (var old in _items) previous.TryAdd(RowKey(old), old.SelectedVersion);
            foreach (var item in all)
            {
                if (previous.TryGetValue(RowKey(item), out var chosen) && chosen is not null &&
                    chosen != item.SelectedVersion && item.Versions.Contains(chosen))
                    item.SelectedVersion = chosen;
            }

            SetItems(all);

            // se usi solo sorgenti personali non ti ripeto che il repository centrale non e' configurato
            var skipCentralNag = personal.Count > 0 && UrlBox.Text.Contains("CHANGE-ME", StringComparison.Ordinal);
            var parts = new List<string>();
            if (centralError is not null && !skipCentralNag)
                parts.Add(automatic
                    ? $"Impossibile aggiornare l'elenco ({centralError}): mostro l'ultima copia salvata."
                    : $"Repository centrale: {centralError}");
            if (centralRemote || personalRemote) parts.Add($"{all.Count} pacchetti disponibili.");

            MessageText.Text = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Riporta subito nel modello la versione scelta dal menu (aggiorna pulsante e stato della riga):
    /// non mi affido solo al binding, che dentro la tabella non aggiornava le righe gia' installate.
    /// </summary>
    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: MibPackageItem item, SelectedItem: string version } && item.SelectedVersion != version)
            item.SelectedVersion = version;
    }

    private static string RowKey(MibPackageItem item) => $"{item.SourceId}|{item.IsLocal}|{item.Name}";

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item) return;

        var tag = item.SelectedVersion;
        item.SetBusy(true, tag is null ? "Installazione..." : $"Installazione di {tag}...");
        SetBusy(true);

        try
        {
            var error = item.IsPersonal
                ? await _personal.InstallAsync(item, tag)
                : await _central.InstallAsync(item, tag);

            if (error is not null) MessageText.Text = error;
            else if (_isPlugin) MessageText.Text = $"{item.Name} {item.InstalledVersion} installato. Premi \"Attiva\" per usarlo.";
            else MessageText.Text = $"{item.Name} {item.InstalledVersion} installato.";

            if (error is not null) item.Error = null;
        }
        finally
        {
            item.SetBusy(false);
            SetBusy(false);
            RefreshPluginInfo();
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item) return;

        item.SetBusy(true, "Rimozione...");
        SetBusy(true);

        try
        {
            var error = item.IsLocal
                ? await Task.Run(() => LocalPluginStore.Remove(item))
                : item.IsPersonal
                    ? await _personal.RemoveAsync(item)
                    : await _central.RemoveAsync(item);
            MessageText.Text = error ?? $"{item.Name} rimosso.";
            if (error is null && item.IsLocal) _items.Remove(item);
        }
        finally
        {
            item.SetBusy(false);
            SetBusy(false);
            RefreshPluginInfo();
        }
    }

    private async void PluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item || item.ActivationKey is null) return;

        var plugin = PluginManager.Instance.Find(item.ActivationKey);
        if (plugin is null) return;

        switch (plugin.State)
        {
            case PluginState.NeedsApproval:
                if (new PluginApprovalWindow(plugin) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
                {
                    SetBusy(true);
                    try { await PluginManager.Instance.ApproveAsync(plugin); }
                    finally { SetBusy(false); }
                }
                break;

            case PluginState.Disabled:
                SetBusy(true);
                try { await PluginManager.Instance.SetEnabledAsync(plugin, true); }
                finally { SetBusy(false); }
                break;

            case PluginState.Active:
                SetBusy(true);
                try { await PluginManager.Instance.SetEnabledAsync(plugin, false); }
                finally { SetBusy(false); }
                break;
        }
        RefreshPluginInfo();
    }

    private void InstallFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var menu = new ContextMenu();
        var folder = new MenuItem { Header = "Da una cartella..." };
        folder.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Cartella del plugin (con plugin.json)" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                await ImportAsync(() => LocalPluginStore.ImportFolderAsync(dialog.FolderName));
        };
        var zip = new MenuItem { Header = "Da un file .zip..." };
        zip.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Plugin compresso (*.zip)|*.zip", Title = "File zip del plugin" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                await ImportAsync(() => LocalPluginStore.ImportZipAsync(dialog.FileName));
        };
        menu.Items.Add(folder);
        menu.Items.Add(zip);
        menu.PlacementTarget = InstallFileButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task ImportAsync(Func<Task<string?>> import)
    {
        SetBusy(true);
        MessageText.Text = "Installazione del plugin...";
        try
        {
            var error = await import();
            MessageText.Text = error is null ? "Plugin installato. Premi \"Attiva...\" per usarlo." : $"Installazione non riuscita: {error}";
        }
        finally
        {
            SetBusy(false);
        }
        await ReloadAsync(centralRemote: false, personalRemote: false);
    }

    private void MqttButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MibPackageItem { ActivationKey: { } key }) return;
        var plugin = PluginManager.Instance.Find(key);
        if (plugin?.Manifest.Mqtt is null) return;

        new PluginMqttWindow(plugin) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>Aggiorna stato e pulsante dei plugin nella tabella (no-op per i MIB).</summary>
    private void RefreshPluginInfo()
    {
        if (!_isPlugin) return;

        foreach (var item in _items)
        {
            var plugin = item.IsInstalled && item.ActivationKey is not null
                ? PluginManager.Instance.Find(item.ActivationKey)
                : null;

            item.SetPluginMqtt(plugin is { State: not PluginState.Error, Manifest.Mqtt: not null });

            if (plugin is null)
            {
                item.SetPluginInfo(null, "", false);
                continue;
            }

            switch (plugin.State)
            {
                case PluginState.NeedsApproval:
                    item.SetPluginInfo("da approvare", "Attiva...", true);
                    break;
                case PluginState.Disabled:
                    item.SetPluginInfo("disattivato", "Attiva", true);
                    break;
                case PluginState.Active:
                    item.SetPluginInfo("attivo", "Disattiva", true);
                    break;
                default:
                    item.SetPluginInfo($"errore ({plugin.ErrorText})", "", false);
                    break;
            }
        }
    }

    private void SetItems(IEnumerable<MibPackageItem> items)
    {
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPluginInfo();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SyncButton.IsEnabled = !busy;
        AddPersonalButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
