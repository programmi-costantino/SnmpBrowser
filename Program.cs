using Velopack;

namespace SnmpBrowser;

/// <summary>
/// Punto d'ingresso. Velopack deve girare per primo: gestisce l'installazione, l'aggiornamento
/// e la disinstallazione (le istruzioni --veloapp-* passate dall'installer) prima di aprire la finestra.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
