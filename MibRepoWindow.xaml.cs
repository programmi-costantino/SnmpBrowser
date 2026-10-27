using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SnmpBrowser.Models;
using SnmpBrowser.Services;

namespace SnmpBrowser;

/// <summary>Converter booleano inverso (usato per disabilitare il menu mentre una riga e' occupata).</summary>
public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public partial class MibRepoWindow : Window
{
    private readonly ObservableCollection<MibPackageItem> _items = new();
    private bool _busy;

    public MibRepoWindow()
    {
        InitializeComponent();
        PackagesGrid.ItemsSource = _items;
        UrlBox.Text = MibRepoService.LoadUrl();

        GitText.Text = GitService.GitPath is { } git
            ? $"Git: {(GitService.IsBundled ? "incluso nell'app" : git)}"
            : "Git non trovato: copia MinGit nella cartella 'git' accanto all'app (tools/get-mingit.ps1) o installa Git for Windows.";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // elenco dal clone locale (senza rete); se non c'e' ancora, scarico il repository
        if (MibRepoService.IsCloned)
        {
            SetItems(await MibRepoService.LoadLocalAsync());
        }
        else if (MibRepoService.ValidateUrl(UrlBox.Text) is null)
        {
            await SyncAsync();
        }
        else
        {
            MessageText.Text = "Inserisci l'URL del tuo repository MIB e premi \"Aggiorna elenco\".";
            UpdateEmpty();
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncAsync();

    private async Task SyncAsync()
    {
        if (_busy) return;
        SetBusy(true);
        MessageText.Text = "Aggiornamento dell'elenco in corso...";

        try
        {
            var (items, error) = await MibRepoService.SyncAsync(UrlBox.Text);
            SetItems(items);

            MessageText.Text = error ?? $"{items.Count} pacchetti disponibili.";
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

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item) return;

        var tag = item.SelectedVersion;
        item.SetBusy(true, tag is null ? "Installazione..." : $"Installazione di {tag}...");
        SetBusy(true);

        try
        {
            var error = await MibRepoService.InstallAsync(item, tag);
            MessageText.Text = error ?? $"{item.Name} {item.InstalledVersion} installato.";
        }
        finally
        {
            item.SetBusy(false);
            SetBusy(false);
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item) return;

        item.SetBusy(true, "Rimozione...");
        SetBusy(true);

        try
        {
            var error = await MibRepoService.RemoveAsync(item);
            MessageText.Text = error ?? $"{item.Name} rimosso.";
        }
        finally
        {
            item.SetBusy(false);
            SetBusy(false);
        }
    }

    private void SetItems(IEnumerable<MibPackageItem> items)
    {
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        UpdateEmpty();
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SyncButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
