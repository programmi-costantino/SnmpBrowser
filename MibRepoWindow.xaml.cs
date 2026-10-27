using System.Windows;
using SnmpBrowser.Services;

namespace SnmpBrowser;

/// <summary>Finestra con due schede: libreria MIB e libreria plugin (stessa meccanica, due cataloghi).</summary>
public partial class MibRepoWindow : Window
{
    private readonly CatalogPanel _mibPanel;
    private readonly CatalogPanel _pluginPanel;
    private readonly string _initialTab;

    public MibRepoWindow(string initialTab = "mib")
    {
        InitializeComponent();
        _initialTab = initialTab;

        _mibPanel = new CatalogPanel(RepoCatalog.Mib, PersonalCatalog.Mib, isPlugin: false,
            "Libreria condivisa di MIB. Scegli la versione di ogni pacchetto dal menu e premi Installa / Applica: l'app scarica solo quello che ti serve.");

        _pluginPanel = new CatalogPanel(RepoCatalog.Plugins, PersonalCatalog.Plugins, isPlugin: true,
            "Libreria di plugin (script JavaScript). Un plugin installato resta spento finche' non controlli autore, permessi e codice e premi Attiva.");

        GitText.Text = GitService.GitPath is { } git
            ? $"Git: {(GitService.IsBundled ? "incluso nell'app" : git)}"
            : "Git non trovato: copia MinGit nella cartella 'git' accanto all'app (tools/get-mingit.ps1) o installa Git for Windows.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialTab == "plugin") PluginTab.IsChecked = true;
        else ShowTab();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowTab();
    }

    private async void ShowTab()
    {
        var panel = PluginTab.IsChecked == true ? _pluginPanel : _mibPanel;
        Host.Children.Clear();
        Host.Children.Add(panel);
        await panel.EnsureInitializedAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
