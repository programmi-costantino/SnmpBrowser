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
        // elenco dai cloni locali (senza rete); se non c'e' ancora nulla, scarico il repository centrale
        await ReloadAsync(centralRemote: false, personalRemote: false);

        if (_items.Count == 0 && !MibRepoService.IsCloned && MibRepoService.ValidateUrl(UrlBox.Text) is null)
            await ReloadAsync(centralRemote: true, personalRemote: true);
        else if (_items.Count == 0)
            MessageText.Text = "Inserisci l'URL del repository centrale e premi \"Aggiorna elenco\", oppure aggiungi una sorgente personale.";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync(centralRemote: true, personalRemote: true);

    private async void AddPersonalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (new PersonalSourceWindow { Owner = this }.ShowDialog() == true)
            await ReloadAsync(centralRemote: false, personalRemote: true);
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem { IsPersonal: true } item) return;

        var source = PersonalSourceService.Find(item.SourceId);
        if (source is null) return;

        if (new PersonalSourceWindow(source) { Owner = this }.ShowDialog() == true)
            await ReloadAsync(centralRemote: false, personalRemote: true);
    }

    /// <summary>Ricostruisce la tabella: repository centrale + sorgenti personali.</summary>
    private async Task ReloadAsync(bool centralRemote, bool personalRemote)
    {
        if (_busy) return;
        SetBusy(true);
        MessageText.Text = centralRemote || personalRemote ? "Aggiornamento dell'elenco in corso..." : "";

        try
        {
            var all = new List<MibPackageItem>();
            string? centralError = null;
            var urlError = MibRepoService.ValidateUrl(UrlBox.Text);

            if (centralRemote && urlError is null)
            {
                var (items, error) = await MibRepoService.SyncAsync(UrlBox.Text);
                all.AddRange(items);
                centralError = error;
            }
            else
            {
                if (MibRepoService.IsCloned) all.AddRange(await MibRepoService.LoadLocalAsync());
                if (centralRemote) centralError = urlError;
            }

            var personal = await PersonalSourceService.LoadItemsAsync(personalRemote);
            all.AddRange(personal);
            SetItems(all);

            // se usi solo sorgenti personali non ti ripeto che il repository centrale non e' configurato
            var skipCentralNag = personal.Count > 0 && UrlBox.Text.Contains("CHANGE-ME", StringComparison.Ordinal);
            var parts = new List<string>();
            if (centralError is not null && !skipCentralNag) parts.Add($"Repository centrale: {centralError}");
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

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not MibPackageItem item) return;

        var tag = item.SelectedVersion;
        item.SetBusy(true, tag is null ? "Installazione..." : $"Installazione di {tag}...");
        SetBusy(true);

        try
        {
            var error = item.IsPersonal
                ? await PersonalSourceService.InstallAsync(item, tag)
                : await MibRepoService.InstallAsync(item, tag);
            MessageText.Text = error ?? $"{item.Name} {item.InstalledVersion} installato.";
            if (error is not null) item.Error = null;
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
            var error = item.IsPersonal
                ? await PersonalSourceService.RemoveAsync(item)
                : await MibRepoService.RemoveAsync(item);
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
        AddPersonalButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
