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

        // Icona per tutte le finestre dell'app.
        var icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Ardesia.ico"));
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
