using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ardesia.Models;
using Ardesia.Services;
using Microsoft.Win32;

namespace Ardesia;

/// <summary>
/// Schermata iniziale, mostrata quando non c'è un blocco appunti da riaprire:
/// si crea un nuovo file (scegliendo subito posizione e nome) o se ne apre uno esistente.
/// </summary>
public partial class StartWindow : Window
{
    sealed record RecentItem(string Name, string Folder, string Path);

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
        var recent = settings.RecentFiles.Where(File.Exists).Take(6)
            .Select(p => new RecentItem(System.IO.Path.GetFileNameWithoutExtension(p), System.IO.Path.GetDirectoryName(p) ?? "", p))
            .ToList();
        RecentList.ItemsSource = recent;
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Il file scelto o appena creato.</summary>
    public string? ResultPath { get; private set; }

    void New_Click(object sender, RoutedEventArgs e) => Finish(CreateNotebookFile(this, _settings));
    void Open_Click(object sender, RoutedEventArgs e) => Finish(PickNotebookFile(this, _settings));
    void Recent_Click(object sender, RoutedEventArgs e) => Finish((sender as Button)?.Tag as string);

    void Finish(string? path)
    {
        if (path == null) return;
        ResultPath = path;
        DialogResult = true;
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
