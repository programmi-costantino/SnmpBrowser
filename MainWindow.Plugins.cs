using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnmpBrowser.Models;
using SnmpBrowser.Plugins;
using SnmpBrowser.Services;

namespace SnmpBrowser;

/// <summary>Parte della finestra principale che parla con i plugin: menu, azioni, pannelli, esportazione.</summary>
public partial class MainWindow
{
    private static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void InitPlugins()
    {
        PluginManager.Instance.Changed += OnPluginsChanged;
        PluginScheduler.Instance.Changed += OnSchedulesChanged;
        Closed += (_, _) =>
        {
            PluginManager.Instance.Changed -= OnPluginsChanged;
            PluginScheduler.Instance.Changed -= OnSchedulesChanged;
            PluginScheduler.Instance.StopAll();
        };
        UpdatePluginButton();
    }

    private async void OnPluginsChanged()
    {
        // Changed puo' arrivare da qualsiasi thread; i decodificatori attivi/spenti cambiano i valori mostrati
        await Dispatcher.InvokeAsync(async () =>
        {
            UpdatePluginButton();
            await RefreshNamesAsync();
        }).Task.Unwrap();
    }

    private void OnSchedulesChanged() => Dispatcher.BeginInvoke(UpdatePluginButton);

    private void UpdatePluginButton()
    {
        var active = PluginManager.Instance.Active.Count();
        var running = PluginScheduler.Instance.Running.Count;
        var text = active == 0 ? "Plugin" : $"Plugin ({active})";
        PluginButton.Content = running == 0 ? text : $"{text}  \u25CF {running}";
        PluginButton.ToolTip = running == 0
            ? "Pannelli e azioni dei plugin attivi, gestione plugin"
            : $"{running} esecuzione/i periodica/he in corso: apri il menu per fermarle";
    }

    private async Task OpenCatalogWindowAsync(string tab)
    {
        new MibRepoWindow(tab) { Owner = this }.ShowDialog();
        await RefreshNamesAsync();
    }

    // ---- Menu Plugin ------------------------------------------------------

    private void PluginButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var any = false;

        foreach (var plugin in PluginManager.Instance.Active)
        {
            if (plugin.Panels.Count == 0 && plugin.Actions.Count == 0 && plugin.Schedules.Count == 0) continue;
            any = true;

            menu.Items.Add(new MenuItem { Header = plugin.DisplayName, IsEnabled = false, FontWeight = FontWeights.SemiBold });

            foreach (var panel in plugin.Panels)
            {
                var item = new MenuItem { Header = "    " + panel.Title };
                var (p, id, title) = (plugin, panel.Id, panel.Title);
                item.Click += (_, _) => OpenPluginView(p, "panel", id, title);
                menu.Items.Add(item);
            }

            foreach (var action in plugin.Actions)
            {
                var item = new MenuItem { Header = "    " + action.Title };
                var (p, id, title) = (plugin, action.Id, action.Title);
                item.Click += (_, _) => OpenPluginView(p, "action", id, title);
                menu.Items.Add(item);
            }
            foreach (var schedule in plugin.Schedules)
            {
                var (p, s) = (plugin, schedule);
                var run = PluginScheduler.Instance.Find(p, s.Id);
                MenuItem item;
                if (run is null)
                {
                    item = new MenuItem { Header = $"    \u25B6  Avvia: {s.Title} (ogni {FormatEvery(s.EverySeconds)})" };
                    item.Click += (_, _) => StartSchedule(p, s);
                }
                else
                {
                    item = new MenuItem { Header = $"    \u25A0  Ferma: {s.Title} - {run.Status}" };
                    item.Click += (_, _) => PluginScheduler.Instance.Stop(p, s.Id);
                }
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        if (!any)
        {
            menu.Items.Add(new MenuItem { Header = "Nessun plugin attivo", IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        var manage = new MenuItem { Header = "Gestisci plugin..." };
        manage.Click += async (_, _) => await OpenCatalogWindowAsync("plugin");
        menu.Items.Add(manage);

        menu.PlacementTarget = PluginButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static string FormatEvery(int seconds) =>
        seconds % 3600 == 0 ? $"{seconds / 3600} h" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    /// <summary>Le esecuzioni periodiche usano la connessione indicata nella finestra al momento dell'avvio.</summary>
    private void StartSchedule(PluginInstance plugin, PluginSchedule schedule)
    {
        var settings = TryGetSettings();
        if (settings is null && (plugin.Manifest.Permissions.Contains("snmp.read") || plugin.Manifest.Permissions.Contains("snmp.write")))
        {
            SetStatus("Controlla host, porta, timeout e credenziali SNMP prima di avviare l'esecuzione periodica.", ErrorBrush);
            return;
        }

        PluginScheduler.Instance.Start(plugin, schedule, settings, HostBox.Text.Trim(), PortBox.Text.Trim(),
            VersionText());
        SetStatus($"{plugin.DisplayName}: '{schedule.Title}' avviato (ogni {FormatEvery(schedule.EverySeconds)}) su {HostBox.Text.Trim()}.", OkBrush);
    }

    private void OpenPluginView(PluginInstance plugin, string kind, string id, string title)
    {
        var window = new PluginViewWindow($"{plugin.DisplayName} - {title}", () =>
            plugin.CallAsync(kind, id, BuildContextJson(), TryGetSettings()))
        {
            Owner = this,
        };
        window.Show(); // non modale: si puo' continuare a lavorare con la finestra principale
    }

    // ---- Contesto passato ai plugin ---------------------------------------

    /// <summary>Impostazioni di connessione se i campi sono validi; altrimenti null (le chiamate SNMP del plugin daranno errore chiaro).</summary>
    private SnmpSettings? TryGetSettings()
    {
        foreach (var box in new[] { HostBox, PortBox, TimeoutBox })
            if (ErrorFor(box) is not null) return null;
        if (V3Error() is not null) return null;
        return ReadSettings();
    }

    private string BuildContextJson()
    {
        var selected = ResultsGrid.SelectedItem as SnmpEntry;
        var context = new
        {
            host = HostBox.Text.Trim(),
            port = PortBox.Text.Trim(),
            version = VersionText(),
            oid = OidBox.Text.Trim(),
            selected = selected is null ? null : Dto(selected),
            entries = VisibleEntries().Select(Dto).ToList(),
        };
        return JsonSerializer.Serialize(context, CamelJson);
    }

    private static object Dto(SnmpEntry e) => new { oid = e.Oid, name = e.Name, type = e.Type, value = e.Value, raw = e.Raw ?? e.Value };

    private List<SnmpEntry> VisibleEntries() => _view.Cast<SnmpEntry>().ToList();

    // ---- Esportazione -----------------------------------------------------

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var csv = new MenuItem { Header = "CSV (integrato)" };
        csv.Click += (_, _) => ExportCsv();
        menu.Items.Add(csv);

        var exporters = PluginManager.Instance.Active.SelectMany(p => p.Exporters.Select(x => (Plugin: p, Exporter: x))).ToList();
        if (exporters.Count > 0) menu.Items.Add(new Separator());

        foreach (var (plugin, exporter) in exporters)
        {
            var item = new MenuItem { Header = $"{exporter.Title} (.{exporter.Extension}) - {plugin.DisplayName}" };
            var (p, x) = (plugin, exporter);
            item.Click += async (_, _) => await ExportWithPluginAsync(p, x);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = ExportButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private string? AskSavePath(string extension, string filter)
    {
        if (_results.Count == 0)
        {
            SetStatus("Niente da esportare: esegui prima un Get o un Walk.", BusyBrush);
            return null;
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = $"snmp-{HostBox.Text.Trim().Replace(':', '_')}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ExportCsv()
    {
        var path = AskSavePath("csv", "File CSV (*.csv)|*.csv");
        if (path is null) return;

        try
        {
            var sb = new StringBuilder();
            sb.Append("Nome;OID;Tipo;Valore;Valore grezzo\r\n");
            foreach (var r in VisibleEntries())
                sb.Append(CsvCell(r.Name)).Append(';').Append(CsvCell(r.Oid)).Append(';')
                  .Append(CsvCell(r.Type)).Append(';').Append(CsvCell(r.Value)).Append(';')
                  .Append(CsvCell(r.Raw ?? r.Value)).Append("\r\n");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SetStatus($"Esportato in {path}", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus($"Esportazione non riuscita: {ex.Message}", ErrorBrush);
        }
    }

    /// <summary>Virgolette raddoppiate; i testi che Excel leggerebbe come formula vengono neutralizzati.</summary>
    private static string CsvCell(string text)
    {
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]) && !double.TryParse(text, out _))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private async Task ExportWithPluginAsync(PluginInstance plugin, PluginExporter exporter)
    {
        var path = AskSavePath(exporter.Extension, $"{exporter.Title} (*.{exporter.Extension})|*.{exporter.Extension}");
        if (path is null) return;

        SetStatus($"Esportazione con {plugin.DisplayName}...", BusyBrush);
        var result = await plugin.CallAsync("exporter", exporter.Id, BuildContextJson(), TryGetSettings());

        if (!result.Ok)
        {
            SetStatus($"Esportazione fallita: {result.Error}", ErrorBrush);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Json ?? "null");
            if (doc.RootElement.ValueKind != JsonValueKind.String)
            {
                SetStatus("Il plugin deve restituire il contenuto del file come testo.", ErrorBrush);
                return;
            }

            await File.WriteAllTextAsync(path, doc.RootElement.GetString() ?? "", new UTF8Encoding(false));
            SetStatus($"Esportato in {path}", OkBrush);
        }
        catch (Exception ex)
        {
            SetStatus($"Esportazione non riuscita: {ex.Message}", ErrorBrush);
        }
    }
}
