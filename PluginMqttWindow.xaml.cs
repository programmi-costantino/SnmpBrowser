using System.Windows;
using SnmpBrowser.Plugins;

namespace SnmpBrowser;

/// <summary>L'utente inserisce utente e password del broker MQTT del plugin; lo script non le vede mai.</summary>
public partial class PluginMqttWindow : Window
{
    private readonly PluginInstance _plugin;

    public PluginMqttWindow(PluginInstance plugin)
    {
        InitializeComponent();
        _plugin = plugin;

        TitleText.Text = $"MQTT - {plugin.DisplayName}";
        var mqtt = plugin.Manifest.Mqtt!;
        BrokerText.Text = $"{(mqtt.Tls ? "mqtts" : "mqtt")}://{mqtt.Host}:{mqtt.Port}";

        var saved = PluginManager.Instance.GetMqttCredentials(plugin.Manifest.Id);
        UserBox.Text = saved?.Username ?? "";
        PassBox.Password = saved?.Password ?? "";
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        ResultText.Text = "Connessione in corso...";
        ResultText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");

        var mqtt = _plugin.Manifest.Mqtt!;
        var credentials = new MqttCredentials(UserBox.Text.Trim(), PassBox.Password);

        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                using var session = MqttSession.Connect(mqtt, credentials);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });

        ResultText.Text = error is null ? "Connessione riuscita." : $"Connessione non riuscita: {error}";
        ResultText.Foreground = (System.Windows.Media.Brush)FindResource(error is null ? "TextBrush" : "DangerBrush");
        TestButton.IsEnabled = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var error = PluginManager.Instance.SetMqttCredentials(_plugin.Manifest.Id, UserBox.Text.Trim(), PassBox.Password);
        if (error is not null)
        {
            ResultText.Text = $"Salvataggio non riuscito: {error}";
            ResultText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }
        DialogResult = true;
    }
}
