using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Lextm.SharpSnmpLib;
using SnmpBrowser.Models;
using SnmpBrowser.Services;

namespace SnmpBrowser;

public partial class MainWindow : Window
{
    private static readonly Brush IdleBrush = MakeBrush("#94A3B8");
    private static readonly Brush BusyBrush = MakeBrush("#F59E0B");
    private static readonly Brush OkBrush = MakeBrush("#22C55E");
    private static readonly Brush ErrorBrush = MakeBrush("#EF4444");

    private readonly ObservableCollection<SnmpEntry> _results = new();
    private readonly ObservableCollection<OidNode> _roots = new();
    private readonly ICollectionView _view;
    private bool _updatingSelection;
    private readonly bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        ResultsGrid.ItemsSource = _results;
        OidTree.ItemsSource = _roots;

        _view = CollectionViewSource.GetDefaultView(_results);
        _view.Filter = FilterEntry;

        ThemeToggle.IsChecked = App.IsDark;
        ShowDetail(null);
        UpdateMibButton();
        UpdateCount();
        InitPlugins();
        InitUpdates();

        _ready = true;
        UpdateVersionUi();
        foreach (var box in new[] { HostBox, PortBox, TimeoutBox, OidBox }) ValidateField(box);
    }

    // ---- MIB --------------------------------------------------------------

    private async void MibButton_Click(object sender, RoutedEventArgs e)
    {
        new MibWindow { Owner = this }.ShowDialog();
        await RefreshNamesAsync();
    }

    private async void RepoButton_Click(object sender, RoutedEventArgs e) => await OpenCatalogWindowAsync("mib");

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;

        var result = MibStore.Instance.Import(files);
        await RefreshNamesAsync();
        SetStatus(result.Summary(), result.Loaded == 0 || result.Warnings.Count > 0 ? BusyBrush : OkBrush);
    }

    /// <summary>Dopo aver cambiato MIB o plugin aggiorna nomi e valori dei risultati gia' a video (i decodificatori dei plugin girano fuori dal thread dell'interfaccia).</summary>
    private async Task RefreshNamesAsync()
    {
        var current = _results.ToList();
        if (current.Count > 0)
        {
            var updated = await Task.Run(() => current
                .Select(r => r with
                {
                    Name = MibNames.Resolve(r.Oid),
                    Value = ValuePipeline.Format(r.Oid, r.Type, r.Raw ?? r.Value),
                })
                .ToList());
            ShowResults(updated);
        }
        UpdateMibButton();
    }

    private void UpdateMibButton() => MibButton.Content = $"MIB ({MibStore.Instance.Modules.Count})";

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        => App.ApplyTheme(ThemeToggle.IsChecked == true, save: true);

    private static Brush MakeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    // ---- Validazione ------------------------------------------------------

    private static string? ErrorFor(TextBox box) => box.Name switch
    {
        nameof(HostBox) => InputValidator.Host(box.Text),
        nameof(PortBox) => InputValidator.Port(box.Text),
        nameof(TimeoutBox) => InputValidator.Timeout(box.Text),
        nameof(OidBox) => InputValidator.Oid(box.Text),
        _ => null
    };

    /// <summary>Evidenzia il campo in rosso (con tooltip) se non valido.</summary>
    private string? ValidateField(TextBox box)
    {
        var error = ErrorFor(box);
        box.Tag = error is null ? null : "invalid";
        box.ToolTip = error;
        return error;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || sender is not TextBox box) return;

        var error = ValidateField(box);
        if (error is not null) SetStatus(error, ErrorBrush);
    }

    // ---- SNMPv3 -------------------------------------------------------------

    private bool IsV3 => VersionBox.SelectedIndex == 2;

    private string VersionText() => VersionBox.SelectedIndex switch { 0 => "v1", 1 => "v2c", _ => "v3" };

    private V3Level SelectedLevel => SecLevelBox.SelectedIndex switch
    {
        0 => V3Level.NoAuthNoPriv,
        1 => V3Level.AuthNoPriv,
        _ => V3Level.AuthPriv,
    };

    private static string ComboText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionUi();

    private void SecLevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionUi();

    /// <summary>Con v3 sparisce la community e compaiono utente, livello e (secondo il livello) password.</summary>
    private void UpdateVersionUi()
    {
        if (!_ready) return;

        var v3 = IsV3;
        CommunityPanel.Visibility = v3 ? Visibility.Collapsed : Visibility.Visible;
        V3Panel.Visibility = v3 ? Visibility.Visible : Visibility.Collapsed;

        var auth = SelectedLevel != V3Level.NoAuthNoPriv;
        var priv = SelectedLevel == V3Level.AuthPriv;
        AuthProtoPanel.Visibility = auth ? Visibility.Visible : Visibility.Hidden;
        AuthPassPanel.Visibility = auth ? Visibility.Visible : Visibility.Hidden;
        PrivProtoPanel.Visibility = priv ? Visibility.Visible : Visibility.Hidden;
        PrivPassPanel.Visibility = priv ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Errore nelle credenziali v3 (null se la versione non e' v3 o i dati sono validi).</summary>
    private string? V3Error()
    {
        if (!IsV3) return null;

        var error = InputValidator.V3User(UserBox.Text);
        if (error is not null) return error;
        if (SelectedLevel != V3Level.NoAuthNoPriv)
        {
            error = InputValidator.V3Password(AuthPassBox.Password, "autenticazione");
            if (error is not null) return error;
        }
        return SelectedLevel == V3Level.AuthPriv
            ? InputValidator.V3Password(PrivPassBox.Password, "cifratura")
            : null;
    }

    /// <summary>Controlla i campi richiesti; al primo errore lo mostra e porta il focus sul campo.</summary>
    private bool ValidateInputs(bool needOid)
    {
        var fields = needOid
            ? new[] { HostBox, PortBox, TimeoutBox, OidBox }
            : new[] { HostBox, PortBox, TimeoutBox };

        string? firstError = null;
        TextBox? firstField = null;
        foreach (var box in fields)
        {
            var error = ValidateField(box);
            if (error is not null && firstError is null)
            {
                firstError = error;
                firstField = box;
            }
        }

        if (firstError is null)
        {
            var v3Error = V3Error();
            if (v3Error is null) return true;

            SetStatus(v3Error, ErrorBrush);
            (UserBox.Text.Trim().Length == 0 ? UserBox : (UIElement)(AuthPassBox.Password.Length < 8 ? AuthPassBox : PrivPassBox)).Focus();
            return false;
        }

        SetStatus(firstError, ErrorBrush);
        firstField!.Focus();
        firstField.SelectAll();
        return false;
    }

    // ---- Azioni -----------------------------------------------------------

    private async void GetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(needOid: true)) return;
        var oid = OidBox.Text;
        await RunAsync("Get", s => SnmpService.GetAsync(s, oid), n => $"{n} valore/i letti");
    }

    private async void WalkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(needOid: true)) return;
        var oid = OidBox.Text;
        await RunAsync("Walk", s => SnmpService.WalkAsync(s, oid), n => $"{n} OID trovati");
    }

    private async void SetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(needOid: true)) return;

        var oid = OidBox.Text;
        var type = ((ComboBoxItem)SetTypeBox.SelectedItem).Content?.ToString() ?? "Integer32";
        var value = SetValueBox.Text;

        var valueError = InputValidator.SetValue(type, value);
        if (valueError is not null)
        {
            SetStatus(valueError, ErrorBrush);
            SetValueBox.Focus();
            SetValueBox.SelectAll();
            return;
        }

        var confirm = MessageBox.Show(
            $"Scrivere '{value}' ({type}) su {oid}?\nL'operazione modifica il dispositivo.",
            "Conferma Set", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await RunAsync("Set", s => SnmpService.SetAsync(s, oid, type, value), _ => "valore scritto");
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string oid }) OidBox.Text = oid;
    }

    private void OidBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WalkButton.IsEnabled) WalkButton_Click(sender, e);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(DetValue.Text)) return;
        try
        {
            Clipboard.SetText(DetValue.Text);
            SetStatus("Valore copiato negli appunti", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus($"Copia non riuscita: {ex.Message}", ErrorBrush);
        }
    }

    private async Task RunAsync(string label, Func<SnmpSettings, Task<SnmpResult>> action, Func<int, string> okText)
    {
        SetBusy(true);
        try
        {
            var settings = ReadSettings();
            SetStatus($"{label} in corso...", BusyBrush);
            var sw = Stopwatch.StartNew();

            var result = await action(settings);
            var elapsed = sw.ElapsedMilliseconds;

            // In caso di errore senza dati lascio a video i risultati precedenti
            if (result.Ok || result.Entries.Count > 0)
                ShowResults(result.Entries);

            if (!result.Ok)
            {
                var partial = result.Entries.Count > 0 ? $" (risultati parziali: {result.Entries.Count})" : "";
                SetStatus($"{label} fallito: {result.Error}{partial}", ErrorBrush);
            }
            else if (result.Entries.Count == 0)
            {
                SetStatus($"{label}: nessun risultato. L'OID potrebbe non esistere o non essere accessibile con questa community.",
                    BusyBrush);
            }
            else
            {
                SetStatus($"{label} completato: {okText(result.Entries.Count)} ({elapsed} ms)", OkBrush);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"{label} fallito: {ex.Message}", ErrorBrush);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Costruisce le impostazioni; i campi sono gia' stati validati da ValidateInputs.</summary>
    private SnmpSettings ReadSettings() => new()
    {
        Host = HostBox.Text.Trim(),
        Port = int.Parse(PortBox.Text.Trim()),
        Community = CommunityBox.Text,
        Version = VersionBox.SelectedIndex switch { 0 => VersionCode.V1, 1 => VersionCode.V2, _ => VersionCode.V3 },
        User = UserBox.Text.Trim(),
        SecurityLevel = SelectedLevel,
        AuthProtocol = ComboText(AuthProtoBox),
        AuthPassword = AuthPassBox.Password,
        PrivProtocol = ComboText(PrivProtoBox),
        PrivPassword = PrivPassBox.Password,
        ContextName = ContextBox.Text.Trim(),
        TimeoutMs = int.Parse(TimeoutBox.Text.Trim()),
    };

    private void SetBusy(bool busy)
    {
        GetButton.IsEnabled = !busy;
        WalkButton.IsEnabled = !busy;
        SetButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusDot.Fill = color;
    }

    // ---- Risultati, filtro e albero ---------------------------------------

    private void ShowResults(List<SnmpEntry> entries)
    {
        _results.Clear();
        foreach (var entry in entries) _results.Add(entry);
        RebuildTree(entries);
        ShowDetail(null);
        UpdateCount();
    }

    private bool FilterEntry(object item)
    {
        var text = FilterBox.Text.Trim();
        if (text.Length == 0 || item is not SnmpEntry e) return true;

        return e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || e.Oid.Contains(text, StringComparison.OrdinalIgnoreCase)
            || e.Type.Contains(text, StringComparison.OrdinalIgnoreCase)
            || e.Value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var shown = _view.Cast<object>().Count();
        CountText.Text = _results.Count == 0
            ? ""
            : shown == _results.Count ? $"{_results.Count} elementi" : $"{shown} di {_results.Count} elementi";
    }

    private void RebuildTree(IEnumerable<SnmpEntry> entries)
    {
        _roots.Clear();
        var index = new Dictionary<string, OidNode>();

        foreach (var entry in entries)
        {
            var parts = entry.Oid.Split('.');
            var level = _roots;
            var path = "";

            for (var i = 0; i < parts.Length; i++)
            {
                path = i == 0 ? parts[0] : path + "." + parts[i];
                var isLeaf = i == parts.Length - 1;

                if (!index.TryGetValue(path, out var node))
                {
                    var name = MibNames.Exact(path);
                    var header = name is null ? parts[i] : $"{name} ({parts[i]})";
                    if (isLeaf) header += $" = {entry.Value}";

                    node = new OidNode(path, header) { IsExpanded = i < 8 };
                    index[path] = node;
                    level.Add(node);
                }
                level = node.Children;
            }
        }
    }

    private void OidTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_updatingSelection || e.NewValue is not OidNode node) return;

        OidBox.Text = node.Oid;

        _updatingSelection = true;
        ResultsGrid.SelectedItem = _results.FirstOrDefault(r => r.Oid == node.Oid);
        if (ResultsGrid.SelectedItem is not null) ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem);
        _updatingSelection = false;

        ShowDetail(ResultsGrid.SelectedItem as SnmpEntry);
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection) return;

        var entry = ResultsGrid.SelectedItem as SnmpEntry;
        if (entry is not null) OidBox.Text = entry.Oid;
        ShowDetail(entry);
    }

    private void ShowDetail(SnmpEntry? entry)
    {
        DetName.Text = entry?.Name ?? "-";
        DetOid.Text = entry?.Oid ?? "-";
        DetType.Text = entry?.Type ?? "-";
        DetValue.Text = entry?.Value ?? "";
        CopyButton.IsEnabled = entry is not null;

        var info = entry is null ? null : MibStore.Instance.Describe(entry.Oid);

        // se il valore e' stato tradotto (scala, unita', nome) mostro anche quello originale
        if (entry?.Raw is { Length: > 0 } raw && raw != entry.Value)
            info = info is null ? $"Valore grezzo: {raw}" : $"Valore grezzo: {raw}\n{info}";

        DetInfo.Text = info ?? "";
        DetInfo.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
