using System.Windows;
using SnmpBrowser.Services;

namespace SnmpBrowser;

/// <summary>Controllo aggiornamenti nella finestra principale: pulsante nella barra di stato e controllo all'avvio.</summary>
public partial class MainWindow
{
    private void InitUpdates()
    {
        UpdateButton.Content = $"v{UpdateService.CurrentVersion}  -  Aggiornamenti";
        UpdateService.Changed += OnUpdateChanged;
        Closed += (_, _) => UpdateService.Changed -= OnUpdateChanged;
        Loaded += async (_, _) => await AutoCheckAsync();
    }

    private void OnUpdateChanged() => Dispatcher.BeginInvoke(RefreshUpdateButton);

    private void RefreshUpdateButton() =>
        UpdateButton.Content = UpdateService.AvailableVersion is { } version
            ? $"Aggiornamento {version} disponibile"
            : $"v{UpdateService.CurrentVersion}  -  Aggiornamenti";

    private async Task AutoCheckAsync()
    {
        if (!UpdateService.AutoCheck || !UpdateService.IsInstalled) return;

        await Task.Delay(TimeSpan.FromSeconds(3)); // prima lascio aprire l'app
        var error = await UpdateService.CheckAsync();

        // un errore di rete all'avvio non deve disturbare: si vede aprendo la finestra Aggiornamenti
        if (error is null && UpdateService.AvailableVersion is { } version)
            SetStatus($"Nuova versione {version} disponibile: apri Aggiornamenti in basso a destra.", OkBrush);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        new UpdateWindow { Owner = this }.ShowDialog();
        RefreshUpdateButton();
    }
}
