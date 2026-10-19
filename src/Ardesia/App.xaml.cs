using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Ardesia.Services;

namespace Ardesia;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var it = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.DefaultThreadCurrentCulture = it;
        CultureInfo.DefaultThreadCurrentUICulture = it;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(it.IetfLanguageTag)));

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            var saved = (MainWindow as MainWindow)?.EmergencySave();
            MessageBox.Show(
                "Si è verificato un errore imprevisto:\n\n" + ex.Exception.Message +
                (saved != null ? $"\n\nUna copia di sicurezza del blocco appunti è stata salvata in:\n{saved}" : ""),
                "Ardesia", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Icona per tutte le finestre dell'app. BitmapFrame (non BitmapImage, che legge solo la prima immagine,
        // da 16 px) lascia a WPF la scelta della misura giusta per barra del titolo e barra delle applicazioni.
        var icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Ardesia.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && w.Icon == null) w.Icon = icon;
        }));

        // Solo lo strumento di prova del tocco (collegamento «Ardesia – Prova tocco LIM»).
        if (e.Args.Contains("--prova-tocco"))
        {
            var test = new TouchTestWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowState = WindowState.Maximized };
            MainWindow = test;
            test.Show();
            return;
        }

        // Prova di Google Drive: accesso, permessi e condivisione col progetto della scuola (docs/google-drive.md).
        int pd = Array.IndexOf(e.Args, "--prova-google");
        if (pd >= 0)
        {
            var client = pd + 1 < e.Args.Length && !e.Args[pd + 1].StartsWith("--") ? e.Args[pd + 1] : null;
            var win = new GoogleTestWindow(client);
            MainWindow = win;
            win.Show();
            return;
        }

        // Prova dell'accesso a Google (fase 1): accesso, account ricordato al riavvio, rinnovo, uscita.
        int pa = Array.IndexOf(e.Args, "--prova-accesso");
        if (pa >= 0)
        {
            var log = pa + 1 < e.Args.Length && !e.Args[pa + 1].StartsWith("--") ? e.Args[pa + 1] : null;
            var win = new AccessTestWindow(log);
            MainWindow = win;
            win.Show();
            return;
        }

        // Prova del grafico di Ardesia: parser e calcoli, senza finestre; scrive il registro e esce.
        int pf = Array.IndexOf(e.Args, "--prova-grafico");
        if (pf >= 0)
        {
            var log = pf + 1 < e.Args.Length && !e.Args[pf + 1].StartsWith("--") ? e.Args[pf + 1]
                : Path.Combine(Path.GetTempPath(), "ardesia-grafico.log");
            File.WriteAllText(log, Editor.Graph.GraphSelfTest.Run(out int failures));
            // accanto al registro, l'immagine di un grafico con formule difficili, per controllare la legenda a occhio
            Editor.Graph.GraphSelfTest.RenderLegendSample(Path.ChangeExtension(log, ".png"));
            Shutdown(failures == 0 ? 0 : 1);
            return;
        }

        // Prova di GeoGebra: apre solo la finestra del grafico e scrive nel registro indicato cosa succede.
        int pg = Array.IndexOf(e.Args, "--prova-geogebra");
        if (pg >= 0)
        {
            var log = pg + 1 < e.Args.Length && !e.Args[pg + 1].StartsWith("--") ? e.Args[pg + 1]
                : Path.Combine(Path.GetTempPath(), "ardesia-geogebra.log");
            // con un parametro: GeoGebra deve ricevere il cursore prima della funzione
            var win = new GeoGebraWindow(null, "y = x² - 3x + 2\ng(x) = 1,5x + a", new[] { new Editor.Graph.GraphParameter { Name = "a", Value = 2 } })
            {
                TraceFile = log,
                AutoCloseSeconds = 25,
                AppName = e.Args.Contains("--graphing") ? "graphing" : "classic",
            };
            MainWindow = win;
            win.Show();
            return;
        }

        string? file = null, snapshot = null, tab = null;
        bool demo = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--snapshot" when i + 1 < e.Args.Length: snapshot = e.Args[++i]; break;
                case "--tab" when i + 1 < e.Args.Length: tab = e.Args[++i]; break;
                case "--demo": demo = true; break;
                default:
                    if (File.Exists(e.Args[i])) file = e.Args[i];
                    break;
            }
        }

        var settings = AppSettings.Load();
        if (snapshot != null)
        {
            settings = new AppSettings { Persist = false };
        }
        else
        {
            // 1) si riapre l'ultimo blocco appunti; 2) se non c'è, prima di tutto si sceglie o si crea il file.
            if (file == null && settings.LastFile is { } last && File.Exists(last)) file = last;
            if (file == null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var message = settings.LastFile != null
                    ? $"Non trovo l'ultimo blocco appunti aperto:\n{settings.LastFile}\n\nSe si trova su Google Drive, controlla che Google Drive per desktop sia avviato."
                    : null;
                var start = new StartWindow(settings, message);
                if (start.ShowDialog() != true || start.ResultPath == null)
                {
                    Shutdown();
                    return;
                }
                file = start.ResultPath;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }

        var window = new MainWindow(file, settings);
        MainWindow = window;
        if (snapshot != null) window.ScheduleSnapshot(snapshot, demo, tab);
        window.Show();
    }
}
