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

    public MainWindow()
    {
        InitializeComponent();

        ResultsGrid.ItemsSource = _results;
        OidTree.ItemsSource = _roots;

        _view = CollectionViewSource.GetDefaultView(_results);
        _view.Filter = FilterEntry;

        ThemeToggle.IsChecked = App.IsDark;
        UpdateCount();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        => App.ApplyTheme(ThemeToggle.IsChecked == true, save: true);

    private static Brush MakeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    // ---- Azioni -----------------------------------------------------------

    private async void GetButton_Click(object sender, RoutedEventArgs e)
    {
        var oid = OidBox.Text;
        await RunAsync("Get", async s =>
        {
            var entries = await SnmpService.GetAsync(s, oid);
            ShowResults(entries);
            return $"{entries.Count} valore/i letti";
        });
    }

    private async void WalkButton_Click(object sender, RoutedEventArgs e)
    {
        var oid = OidBox.Text;
        await RunAsync("Walk", async s =>
        {
            var entries = await SnmpService.WalkAsync(s, oid);
            ShowResults(entries);
            return $"{entries.Count} OID trovati";
        });
    }

    private async void SetButton_Click(object sender, RoutedEventArgs e)
    {
        var oid = OidBox.Text;
        var type = ((ComboBoxItem)SetTypeBox.SelectedItem).Content?.ToString() ?? "Integer32";
        var value = SetValueBox.Text;

        var confirm = MessageBox.Show(
            $"Scrivere '{value}' ({type}) su {oid}?\nL'operazione modifica il dispositivo.",
            "Conferma Set", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        await RunAsync("Set", async s =>
        {
            var entries = await SnmpService.SetAsync(s, oid, type, value);
            ShowResults(entries);
            return "valore scritto";
        });
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

    private async Task RunAsync(string label, Func<SnmpSettings, Task<string>> action)
    {
        SetBusy(true);
        try
        {
            var settings = ReadSettings();
            SetStatus($"{label} in corso...", BusyBrush);
            var sw = Stopwatch.StartNew();
            var message = await action(settings);
            SetStatus($"{label} completato: {message} ({sw.ElapsedMilliseconds} ms)", OkBrush);
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

    private SnmpSettings ReadSettings()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
            throw new FormatException("Host mancante");
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
            throw new FormatException("Porta non valida");
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 100)
            throw new FormatException("Timeout non valido (minimo 100 ms)");

        var version = VersionBox.SelectedIndex == 0 ? VersionCode.V1 : VersionCode.V2;
        return new SnmpSettings
        {
            Host = HostBox.Text.Trim(),
            Port = port,
            Community = CommunityBox.Text,
            Version = version,
            TimeoutMs = timeout,
        };
    }

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
    }
}
