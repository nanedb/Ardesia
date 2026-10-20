using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ardesia.Models;
using Ardesia.Services;
using Ardesia.Services.Google;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>
/// Schermata iniziale, mostrata quando non c'è un blocco appunti da riaprire:
/// si crea un nuovo file (scegliendo subito posizione e nome) o se ne apre uno esistente.
/// Da collegati a Google il nuovo blocco va su Drive e sotto compaiono i propri blocchi su Drive (docs/google-drive.md §7).
/// </summary>
public partial class StartWindow : Window
{
    sealed record RecentItem(string Name, string Folder, string Path, string Icon = "\uE8A5");

    readonly AppSettings _settings;

    public StartWindow(AppSettings settings, string? message = null)
    {
        _settings = settings;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20; // mai più alta dello schermo
        if (message != null)
        {
            MessageText.Text = message;
            MessageBorder.Visibility = Visibility.Visible;
        }
        Loaded += async (_, _) => await FillDriveAsync();
        FillRecent();
    }

    /// <summary>Il file scelto o appena creato (per un blocco su Drive: la sua copia in cache, anche se manca ancora).</summary>
    public string? ResultPath { get; private set; }

    void FillRecent()
    {
        bool signedIn = DriveSync.Current != null;
        var recent = _settings.RecentFiles
            .Where(p => DriveSync.TryParseCachePath(p, out _, out _) ? DriveSync.IsCurrentAccountPath(p) : File.Exists(p))
            .Take(signedIn ? 4 : 6)
            .Select(p => DriveSync.Describe(p) is { } d
                ? new RecentItem(d.Title, "Google Drive" + (d.Folder != null ? " · " + d.Folder : ""), p, "\uE753")
                : new RecentItem(System.IO.Path.GetFileNameWithoutExtension(p), System.IO.Path.GetDirectoryName(p) ?? "", p))
            .ToList();
        RecentList.ItemsSource = recent;
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task FillDriveAsync()
    {
        var g = GoogleAccountService.Current;
        if (!g.IsConfigured || !_settings.Persist) return;
        DrivePanel.Visibility = Visibility.Visible;
        var sync = DriveSync.Current;
        bool signedIn = sync != null;
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AllDriveButton.Visibility = NewLocalButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        NewHint.Text = signedIn
            ? "Su Google Drive, nella cartella Ardesia: scegli classe e materia."
            : "Scegli dove salvarlo e con che nome (ad esempio nella cartella di Google Drive della classe).";
        OpenTitle.Text = signedIn ? "Apri un blocco appunti dal computer" : "Apri un blocco appunti esistente";
        OpenHint.Text = signedIn ? "Un file .ardesia sul computer o su Google Drive per desktop (G:)." : "Cerca un file .ardesia sul computer o su Google Drive.";
        DriveList.ItemsSource = null;
        if (sync == null)
        {
            DriveText.Text = $"Accedi con l'account @{GoogleClientConfig.SchoolDomain} per salvare i blocchi appunti su Google Drive " +
                             "(anche senza Drive per desktop). Si apre il browser.";
            return;
        }
        DriveText.Text = $"Collegato come {sync.Email}. Leggo i tuoi blocchi appunti…";
        try
        {
            var listing = await sync.ListAsync(CancellationToken.None);
            if (!IsLoaded) return;
            DriveList.ItemsSource = listing.Notebooks.Take(5)
                .Select(n => new RecentItem(n.Title, string.Join(" · ", new[] { n.Folder, n.Modified is { } m ? "modificato " + DriveUi.When(m) : "" }
                    .Where(x => x.Length > 0)), sync.CachePath(n.Id)))
                .ToList();
            DriveText.Text = listing.Offline
                ? "Senza connessione: si possono aprire solo i blocchi già aperti su questo PC."
                : listing.Notebooks.Count == 0
                    ? $"Collegato come {sync.Email}. Non hai ancora blocchi appunti su Drive: creane uno qui sopra."
                    : $"Collegato come {sync.Email}.";
        }
        catch (Exception ex)
        {
            DriveText.Text = "Non riesco a leggere l'elenco da Google Drive: " + ex.Message;
        }
    }

    void New_Click(object sender, RoutedEventArgs e) => Finish(CreateNotebook(this, _settings));
    void NewLocal_Click(object sender, RoutedEventArgs e) => Finish(CreateNotebookFile(this, _settings));
    void Open_Click(object sender, RoutedEventArgs e) => Finish(PickNotebookFile(this, _settings));
    void Recent_Click(object sender, RoutedEventArgs e) => Finish((sender as Button)?.Tag as string);

    void AllDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DriveSync.Current is not { } sync) return;
        var dlg = new DriveBrowserWindow(sync, null) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            _ = FillDriveAsync(); // qualcosa può essere stato eliminato
            return;
        }
        Finish(dlg.ResultNew ? CreateNotebook(this, _settings) : dlg.ResultId is { } id ? sync.CachePath(id) : null);
    }

    async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        DriveText.Text = "Completa l'accesso nella pagina di Google che si è aperta nel browser…";
        var error = await DriveUi.SignInAsync(this);
        SignInButton.IsEnabled = true;
        await FillDriveAsync();
        if (error != null) DriveText.Text = error;
        FillRecent();
    }

    void Finish(string? path)
    {
        if (path == null) return;
        ResultPath = path;
        DialogResult = true;
    }

    /// <summary>
    /// Nuovo blocco appunti: su Google Drive se si è collegati (classe, materia e nome), altrimenti sul computer.
    /// Restituisce il percorso da aprire (per Drive la copia in cache).
    /// </summary>
    public static string? CreateNotebook(Window? owner, AppSettings settings)
    {
        if (!settings.Persist || DriveSync.Current is not { } sync) return CreateNotebookFile(owner, settings);
        var dlg = new NewDriveNotebookDialog(sync) { Owner = owner };
        if (owner == null) dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        return dlg.ResultLocal ? CreateNotebookFile(owner, settings) : dlg.ResultId is { } id ? sync.CachePath(id) : null;
    }

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ardesia");

    /// <summary>Chiede posizione e nome, crea il file con una sezione e una pagina vuota e ne restituisce il percorso.</summary>
    public static string? CreateNotebookFile(Window? owner, AppSettings settings)
    {
        string folder;
        if (settings.LastSaveFolder != null && Directory.Exists(settings.LastSaveFolder))
        {
            folder = settings.LastSaveFolder;
        }
        else
        {
            folder = DefaultFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
        }

        var dlg = new SaveFileDialog
        {
            Title = "Dove vuoi salvare il nuovo blocco appunti?",
            Filter = NotebookStore.FileFilter,
            FileName = "Nuovo blocco appunti" + NotebookStore.Extension,
            InitialDirectory = folder,
            OverwritePrompt = true,
        };
        DriveLocator.AddPlaces(dlg);
        if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return null;

        var nb = new Notebook { Title = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) };
        var section = NotebookNode.NewSection("Sezione 1", MainWindow.SectionColors[0]);
        nb.Insert(section, null);
        nb.Insert(NotebookNode.NewPage(), section);
        try
        {
            NotebookStore.Save(nb, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossibile creare il file:\n{dlg.FileName}\n\n{ex.Message}", "Ardesia",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        return dlg.FileName;
    }

    public static string? PickNotebookFile(Window? owner, AppSettings settings)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Apri blocco appunti",
            Filter = NotebookStore.FileFilter,
            InitialDirectory = settings.LastSaveFolder != null && Directory.Exists(settings.LastSaveFolder)
                ? settings.LastSaveFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        DriveLocator.AddPlaces(dlg);
        return (owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) == true ? dlg.FileName : null;
    }
}
