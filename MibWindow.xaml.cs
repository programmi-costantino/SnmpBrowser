using System.Windows;
using Microsoft.Win32;
using SnmpBrowser.Models;
using SnmpBrowser.Services;

namespace SnmpBrowser;

public partial class MibWindow : Window
{
    public MibWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var modules = MibStore.Instance.Modules;
        ModulesGrid.ItemsSource = null;
        ModulesGrid.ItemsSource = modules;

        EmptyText.Visibility = modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"{modules.Count} moduli - {MibStore.Instance.SymbolCount} simboli";
    }

    private void Import(IEnumerable<string> paths)
    {
        var result = MibStore.Instance.Import(paths);
        Reload();

        MessageText.Text = result.Warnings.Count == 0
            ? result.Summary()
            : result.Summary().Split(" - ")[0] + "\n" + string.Join("\n", result.Warnings);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona i file MIB",
            Multiselect = true,
            Filter = "File MIB|*.mib;*.txt;*.my;*.smi;*.asn;*.mi2|Tutti i file|*.*",
        };

        if (dialog.ShowDialog(this) == true) Import(dialog.FileNames);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is not MibModuleInfo module)
        {
            MessageText.Text = "Seleziona prima un modulo da rimuovere.";
            return;
        }

        if (module.Origin.Length > 0)
        {
            MessageText.Text = $"{module.Name} e' gestito dal repository MIB: rimuovilo da 'Repository MIB'.";
            return;
        }

        MibStore.Instance.Remove(module.Name);
        Reload();
        MessageText.Text = $"Modulo {module.Name} rimosso.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        Import(files);
    }
}
