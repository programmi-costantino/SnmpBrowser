using System.Windows;
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

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await CheckAsync();

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

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Download in corso...";

        var error = await UpdateService.DownloadAsync(new Progress<int>(p => Progress.Value = p));
        if (error is not null)
        {
            StatusText.Text = error;
            InstallButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            SetBusy(false);
            return;
        }

        StatusText.Text = "Download completato: l'app si riavvia per installare l'aggiornamento...";
        UpdateService.ApplyAndRestart();
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
