using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SnmpBrowser.Plugins;

namespace SnmpBrowser;

/// <summary>
/// Mostra il risultato di un plugin. Il plugin non disegna nulla: restituisce testo oppure
/// { "type": "table", "columns": [...], "rows": [[...]] } e l'app lo presenta con il proprio stile.
/// </summary>
public partial class PluginViewWindow : Window
{
    private readonly string _title;
    private readonly Func<Task<PluginCallResult>> _run;
    private string _copyText = "";
    private bool _busy;

    public PluginViewWindow(string title, Func<Task<PluginCallResult>> run)
    {
        InitializeComponent();
        _title = title;
        _run = run;
        Title = title;
        HeaderText.Text = title;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        StatusText.Text = "Esecuzione del plugin...";

        try
        {
            var result = await _run();
            if (!result.Ok) ShowText($"Errore del plugin:\n{result.Error}");
            else Render(result.Json);
            StatusText.Text = result.Ok ? $"Aggiornato alle {DateTime.Now:HH:mm:ss}" : "";
        }
        catch (Exception ex)
        {
            ShowText($"Errore: {ex.Message}");
            StatusText.Text = "";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            Cursor = null;
        }
    }

    private void Render(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);
            var root = doc.RootElement;

            switch (root.ValueKind)
            {
                case JsonValueKind.Null:
                    ShowText("(nessun risultato)");
                    return;
                case JsonValueKind.String:
                    ShowText(root.GetString() ?? "");
                    return;
                case JsonValueKind.Object when TryString(root, "type") == "table" && TryTable(root):
                    return;
                case JsonValueKind.Object when TryString(root, "type") == "text" && TryString(root, "text") is { } text:
                    ShowText(text);
                    return;
                default:
                    ShowText(JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
                    return;
            }
        }
        catch (Exception ex)
        {
            ShowText($"Risposta non valida: {ex.Message}");
        }
    }

    private static string? TryString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => e.ToString(),
    };

    private bool TryTable(JsonElement root)
    {
        if (!root.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array) return false;
        if (!root.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array) return false;

        var columns = cols.EnumerateArray().Take(30).Select(Cell).ToList();
        var rows = new List<string[]>();
        foreach (var r in rowsEl.EnumerateArray().Take(5000))
        {
            if (r.ValueKind != JsonValueKind.Array) continue;
            var cells = r.EnumerateArray().Select(Cell).ToList();
            while (cells.Count < columns.Count) cells.Add("");
            rows.Add(cells.Take(columns.Count).ToArray());
        }

        TableGrid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            TableGrid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"[{i}]"),
                Width = i == columns.Count - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            });
        }
        TableGrid.ItemsSource = rows;
        TableGrid.Visibility = Visibility.Visible;
        TextBody.Visibility = Visibility.Collapsed;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', columns));
        foreach (var row in rows) sb.AppendLine(string.Join('\t', row.Select(c => c.Replace('\t', ' ').Replace('\n', ' '))));
        _copyText = sb.ToString();
        return true;
    }

    private void ShowText(string text)
    {
        TableGrid.Visibility = Visibility.Collapsed;
        TextBody.Visibility = Visibility.Visible;
        TextBody.Text = text;
        _copyText = text;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_copyText);
            StatusText.Text = $"{_title}: copiato negli appunti";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copia non riuscita: {ex.Message}";
        }
    }
}
