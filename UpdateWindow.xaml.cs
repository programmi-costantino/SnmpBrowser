using System.Windows;
using System.Windows.Controls;
using SnmpBrowser.Services;

namespace SnmpBrowser;

public partial class UpdateWindow : Window
{
    private bool _busy;

    public UpdateWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versione attuale: {UpdateService.CurrentVersion}";
        AutoBox.IsChecked = UpdateService.AutoCheck;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckAsync();
        await LoadVersionsAsync();
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void AutoBox_Click(object sender, RoutedEventArgs e) => UpdateService.AutoCheck = AutoBox.IsChecked == true;

    private async Task CheckAsync()
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "Controllo in corso...";

        var error = await UpdateService.CheckAsync();
        if (error is not null)
        {
            StatusText.Text = error;
            InstallButton.IsEnabled = false;
        }
        else if (UpdateService.AvailableVersion is { } version)
        {
            StatusText.Text = $"Nuova versione disponibile: {version}.";
            InstallButton.IsEnabled = true;
        }
        else
        {
            StatusText.Text = "Hai gia' l'ultima versione.";
            InstallButton.IsEnabled = false;
        }
        SetBusy(false);
    }

    /// <summary>Carica l'elenco di tutte le versioni pubblicate, comprese quelle precedenti.</summary>
    private async Task LoadVersionsAsync()
    {
        if (_busy) return;
        SetBusy(true);
        VersionsHint.Text = "Carico l'elenco delle versioni...";

        var (versions, error) = await UpdateService.ListVersionsAsync();
        VersionsBox.ItemsSource = versions;
        VersionsBox.SelectedItem = versions.FirstOrDefault(v => v.IsCurrent) ?? versions.FirstOrDefault();
        VersionsHint.Text = error ?? "Scegli una versione per installarla, anche piu' vecchia di quella attuale (utile se una novita' da' problemi).";

        SetBusy(false);
    }

    private void VersionsBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVersionControls();

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await DownloadAndApplyAsync();
    }

    private async void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || VersionsBox.SelectedItem is not UpdateService.VersionChoice choice) return;

        var text = UpdateService.IsOlderThanCurrent(choice.Version)
            ? $"Tornare alla versione {choice.Version}?\n\nMIB, plugin e impostazioni restano al loro posto. Al prossimo controllo l'app potrebbe proporti di nuovo l'aggiornamento: se non vuoi, disattiva \"Controlla gli aggiornamenti all'avvio\"."
            : $"Installare la versione {choice.Version}?";

        if (MessageBox.Show(this, text, "Cambia versione", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var error = UpdateService.SelectVersion(choice.Version);
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        await DownloadAndApplyAsync();
    }

    /// <summary>Scarica la versione scelta (l'ultima trovata o quella selezionata dall'elenco) e riavvia l'app.</summary>
    private async Task DownloadAndApplyAsync()
    {
        SetBusy(true);
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Download in corso...";

        var error = await UpdateService.DownloadAsync(new Progress<int>(p => Progress.Value = p));
        if (error is not null)
        {
            Progress.Visibility = Visibility.Collapsed;
            SetBusy(false);
            await CheckAsync(); // riporta lo stato alla versione piu' recente trovata
            StatusText.Text = error;
            return;
        }

        StatusText.Text = "Download completato: l'app si riavvia per installare la versione...";
        UpdateService.ApplyAndRestart();
        SetBusy(false);
    }

    private void UpdateVersionControls() =>
        VersionButton.IsEnabled = !_busy && VersionsBox.SelectedItem is UpdateService.VersionChoice { IsCurrent: false };

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        VersionsBox.IsEnabled = !busy;
        UpdateVersionControls();
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
