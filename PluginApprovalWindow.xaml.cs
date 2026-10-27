using System.Windows;
using SnmpBrowser.Plugins;

namespace SnmpBrowser;

/// <summary>Mostra all'utente chi e' l'autore, cosa puo' fare il plugin e l'impronta del codice prima di attivarlo.</summary>
public partial class PluginApprovalWindow : Window
{
    public PluginApprovalWindow(PluginInstance plugin)
    {
        InitializeComponent();

        NameText.Text = plugin.DisplayName;
        DescText.Text = string.IsNullOrWhiteSpace(plugin.Manifest.Description) ? "(nessuna descrizione)" : plugin.Manifest.Description;
        AuthorText.Text = string.IsNullOrWhiteSpace(plugin.Manifest.Author) ? "(non indicato)" : plugin.Manifest.Author;
        IdText.Text = plugin.Manifest.Id;
        HashBox.Text = plugin.Hash;
        CodeBox.Text = plugin.MainSource;

        var lines = new List<string>
        {
            "- Aggiungere voci al menu Plugin, pannelli, formati di esportazione e decodifica dei valori.",
        };
        if (plugin.Manifest.Permissions.Contains("snmp.read"))
            lines.Add("- Leggere valori dal dispositivo selezionato (Get / Walk).");
        if (plugin.Manifest.Permissions.Contains("snmp.write"))
            lines.Add("- Scrivere valori sul dispositivo (Set): ogni scrittura ti chiede comunque conferma.");
        if (!plugin.Manifest.Permissions.Any(p => p.StartsWith("snmp.", StringComparison.Ordinal)))
            lines.Add("- Non ha accesso al dispositivo: lavora solo sui risultati che gli passa l'app.");
        PermText.Text = string.Join("\n", lines);
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
