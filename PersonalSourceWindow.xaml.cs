using System.Windows;
using SnmpBrowser.Services;

namespace SnmpBrowser;

public partial class PersonalSourceWindow : Window
{
    private readonly PersonalSource? _editing;

    public PersonalSourceWindow(PersonalSource? editing = null)
    {
        InitializeComponent();
        _editing = editing;

        if (editing is null)
        {
            TokenHint.Text = "Lascia vuoto se il repository e' pubblico.";
            return;
        }

        Title = "Modifica sorgente personale";
        TitleText.Text = "Modifica sorgente personale";
        NameBox.Text = editing.Name;
        UrlBox.Text = editing.Url;
        DeleteButton.Visibility = Visibility.Visible;

        if (PersonalSourceService.HasToken(editing))
        {
            TokenHint.Text = "Token salvato. Lascia vuoto per mantenerlo, oppure scrivine uno nuovo per sostituirlo.";
            ClearTokenBox.Visibility = Visibility.Visible;
        }
        else
        {
            TokenHint.Text = "Nessun token salvato.";
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageText.Text = text;
        MessageText.SetResourceReference(ForegroundProperty, error ? "DangerBrush" : "MutedBrush");
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        // se il campo token e' vuoto provo con quello salvato
        var typed = TokenBox.Password;
        var token = typed.Length > 0 ? typed : (_editing is null ? null : PersonalSourceService.SavedToken(_editing));

        TestButton.IsEnabled = false;
        ShowMessage("Verifica in corso...", error: false);

        try
        {
            var (ok, message) = await PersonalSourceService.TestConnectionAsync(UrlBox.Text, token);
            ShowMessage(message, error: !ok);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var error = _editing is null
            ? PersonalSourceService.Add(NameBox.Text, UrlBox.Text, TokenBox.Password)
            : PersonalSourceService.Update(_editing, NameBox.Text, UrlBox.Text, TokenBox.Password,
                ClearTokenBox.IsChecked == true);

        if (error is not null)
        {
            ShowMessage(error, error: true);
            return;
        }

        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;

        var confirm = MessageBox.Show(
            $"Eliminare la sorgente '{_editing.Name}'?\nVengono cancellati anche il clone locale e il token salvato.",
            "Elimina sorgente", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var error = PersonalSourceService.Delete(_editing);
        if (error is not null)
        {
            ShowMessage(error, error: true);
            return;
        }
        DialogResult = true;
    }
}
